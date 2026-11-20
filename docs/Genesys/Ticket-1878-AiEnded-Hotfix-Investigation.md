# Ticket 1878 (TG-REG-20261119-0027) — hotfix investigation (19 Nov 2026)

> **Final rules (PR #292 review, 20 Nov 2026).** The ownership rules in §1.1, §6 and §7 apply to **every channel** (voice and WhatsApp alike), as recorded in
> [`Ownership-Rules-All-Channels.md`](Ownership-Rules-All-Channels.md), which is current where the two differ. A second pass had narrowed them to Phone and let a voice job deal
> unanswered calls by Round Robin; both were reconciled with the mandatory rules (no Round Robin after an AI conversation; unresolved tickets stay queued until valid acceptance or an authorised
> manual assignment). Voice-specific by nature: the first response is the real connect time and a call's end is concluded after a configurable window. Display times use `TicketDisplay.ToUae`.

**Read first — what was and was not verified.** No Production database, payload log, Genesys execution history or deployed build was reachable from this
environment. Everything below is either (a) read from the code and **reproduced on a real SQL Server with the real migrations and the real HTTP
endpoints**, or (b) listed under *"Needs Production confirmation"* with the exact query/command that settles it. Nothing here claims what the
Production payload contained.

## 1. Confirmed root causes

### 1.1 The "AI ended → human assignment" (the most serious defect) — CONFIRMED, reproduced
`GenesysTicketUpdateAppService.UpdateAsync` **manufactured** the signal `GenesysHumanSignal.AiEndedUnresolved`: any update carrying `ended` with no human
signal, no owner and no handler was *promoted* to "human work". That then ran `MarkHumanFollowUpAsync` →
`RouteExistingTicketToCallCenterAsync` → Call Center Round Robin, in the same request, one second after the end event. Exactly the 14:13:31 → 14:13:32 sequence of
ticket 1878 (mark human work · `AiEndedUnresolved` · transfer to Call Center · owner Bader El Almi).

* It required **no** transcript evidence, so it **bypassed `Genesys:AssignOnTranscriptEvidence`** completely.
* Handlers/jobs that can set the signal: **only** this one place. `AiEndedUnresolved` is not produced by the conversation-end service, the AI auto-close
  job, the chatbot-inactivity job or the reconciler. (Other paths that assign a person are unchanged and are legitimate: creation with an explicit
  `humanFollowUpRequired`, an explicit handoff, a *connected* agent, a mapped `HumanAgent` transcript line, and the reconciler's own rules for tickets that
  *already* need a person.)
* **Fix:** the promotion is removed. Ending an AI conversation never marks human work, moves the ticket or assigns anyone. A `HumanAgent` transcript line
  alone now also respects `AssignOnTranscriptEvidence=false` (no move, no assignment). The enum value stays only so old audit rows still read.
* Closed tickets: unchanged and re-proved — late events are audit-only (`GenesysEventIgnoredOnClosedTicket`), never reopen, transfer or assign.

### 1.2 "Next Step: AI is handling this customer" while the ticket is assigned and the conversation ended — CONFIRMED
`TicketInteractionQueryAppService.DescribeHandlingState` returned `"Ai"` for *every* Genesys interaction with no human signal — it ignored that the
conversation had **ended** and that **human follow-up was flagged**; the Web next-step then printed the AI sentence before it ever looked at the owner.
Now the read model distinguishes (and the UI words each differently):

| State | Meaning |
|---|---|
| `Ai` | AI conversation is live and is the only participant |
| `AiEnded` | AI conversation ended, no human need recorded |
| `HumanFollowUp` | a person is needed; nobody has accepted or replied — **whether or not an employee was assigned** |
| `WaitingForHuman` | an explicit Genesys handoff is waiting (unchanged) |
| `Human` | a person accepted / replied (unchanged) |

plus two honest flags on every interaction: `HumanAccepted` (handoff taken, recorded handler, answering phone agent) and `HumanResponded` (a `HumanAgent` line).
Assigning an employee sets neither — verified on SQL Server (no handler row, no `FirstHumanResponseAtUtc`).

### 1.3 Assignment History stopped at "Genesys Integration" — CONFIRMED
Round Robin (`AssignRoundRobinAsync`) wrote the `TicketAssignments` row, the audit entry and the timeline transfer event, but **never a
`TicketAssignmentHistory` row**; only the answering-agent path and Collections did. The history tab reads only that table. Fixed for Genesys-handled tickets
(those with a Genesys history entry): system Round Robin writes `Source=GenesysHumanFollowUp` (4, labelled *provisional — not yet accepted*) and a person's
manual assign/reassign writes `Source=GenesysManualAssignment` (5, `Kind=Manual`). Additive enum values on a `tinyint` column — **no migration**. Ordinary
tickets write nothing new.

### 1.4 The timestamp (AI conversation "started" 17:50:26 for a ticket created 13:51:04)
> **Superseded by §8:** with Genesys' confirmation that it sends UTC, the exact layer was reproduced and fixed. The paragraph below is the earlier, partial analysis.

**Mechanism proven:** stored `13:50:26` is the true start (09:50:26 UTC) **plus 4 h** — a wall-clock written as UTC. The UI converts UTC→UAE exactly once
(`TicketDisplay`, `AddHours(4)`), so a stored value 4 h ahead shows 17:50.

**Not provable from here:** *which* of these produced the stored value, because it depends on the exact text Genesys/TigerGroupWeb sent and on the build:

1. a build **without** PR #288 handled the call (an offset-bearing value stored as local wall-clock — the original bug), or
2. the sender wrote its **local** time with **no offset** (or labelled with `Z`). PR #288 reads zoneless text as UTC per the contract, which is *correct for a
   correct sender* and *wrong for this one* — and no code can tell them apart from the text.

Tested through the real contract types and JSON (`GenesysWireTimestampTests`, run under `TZ=UTC`, `Asia/Dubai`, `America/Los_Angeles`): `…Z`, `…+04:00`,
`…+00:00`, `…-04:00`, zoneless and fractional forms of the same instant **all store the same UTC instant** with PR #288 in place. So with a correct sender,
#288 is sufficient; ticket 1878 therefore points at **(1) an old build or (2) a mis-zoned sender** — Production data decides which (queries in §3).

**Fix (does not assume a zone, subtracts nothing):** an event cannot be later than the moment TigerCS received it. A new per-request `GenesysTimestampContext`
compares every instant with the server clock (tolerance `Genesys:FutureTimestampToleranceMinutes`, default 5):

* a **start time** in the future is **not stored** (the UI then shows when TigerCS recorded the interaction);
* a **connect / end / handoff time** in the future is limited to the receipt time;
* a **transcript line / customer confirmation** in the future is kept (they are compared by identity for idempotency) but reported;
* every case writes audit `GenesysTimestampAnomaly` with the **value as sent, its wire form (`Utc`/`Offset`/`Zoneless`) and the receipt time** — so the next
  occurrence tells you exactly what the sender wrote.

Applied at **every** Genesys entry: inquiry create, ticket update (start/routing/end/transcript/confirmation), conversation end, handoff request, handoff
assignment. Rows already stored wrong are **not rewritten** (the timeline keeps its "wrong time zone" note).

*Limit, stated plainly:* a mis-zoned value that lands in the **past** (sender zone west of UTC, or a start 4 h too early) cannot be detected by any receipt-time
check; it would still be stored as sent. The durable fix is upstream: Architect must send `ToString(Flow.StartDateTimeUtc)` with a `Z`.

## 2. Verifying the deployed build
`GET /health` returns `{"status":"healthy","version":"x.y.z+<full git commit>"}` (verified locally: it reports the commit it was built from). Compare with
`git rev-parse 5bb4a0e` / the merge commit of #288 (`9478ad7`) — `git merge-base --is-ancestor 9478ad7 <deployed commit>`. The repo also contains a committed
`publish/` folder; its binaries are stamped with commit `3e48b2f` (22 Oct), which is **222 commits behind `main` and does not contain #288** (file dates in the folder only
reflect checkout time and prove nothing). If that folder is what was deployed, #288 is **not** in Production — see "Stale `publish/` folder" in
`docs/releases/PR-292-Deployment-Checklist.md`. Database
evidence: `Diagnose_TigerCS_PROD_Ticket1878_GenesysTimelineOwnership_ReadOnly.sql` §0 (`AssignToAiAgent` audit row at creation exists only on a #288 build).

## 3. Needs Production confirmation
Run `Diagnose_TigerCS_PROD_Ticket1878_GenesysTimelineOwnership_ReadOnly.sql` (SELECT-only; executed here against the migrated schema and proven read-only by a test):
§0 build evidence · §2 start vs server clock · §3/§5 the audit trail incl. `HumanSignal=AiEndedUnresolved` · §4 ownership/history · §8 fleet-wide fingerprints
(other tickets with a future start, or assigned ≤5 s after an AI end with no human evidence).

## 4. Tests (all real, none skipped locally)
* `AiEndedHumanFollowUpSqlServerTests` — **real SQL Server + real migrations + real HTTP + the exact JSON**: future zoneless start rejected and audited; AI end →
  nothing assigned/moved/flagged; shown as `AiEnded`; explicit follow-up → Call Center + provisional agent + 2 history rows (also via the API) + no handler / no
  first response; redelivery idempotent; ticket closed → late end/follow-up/connect change nothing; a new conversation → a new ticket; the 1878 diagnostic runs.
* `GenesysOwnershipLifecycleTests` (rewritten + new), `GenesysWireTimestampTests`, `TicketNextActionTests` — scenarios above.
* **Mutation-checked:** re-inserting the old `AiEndedUnresolved` promotion makes both the SQL Server test and the in-memory regression test fail.
* Two pre-existing tests that asserted the old state names were updated deliberately (`Ai`→`AiEnded` for an ended conversation; leasing →`HumanFollowUp`).

## 5. Screenshots (`docs/Genesys/screenshots-1878`)
Real Api + Web processes on a real SQL Server, same HTTP flow, **before = the commit this branch is based on, after = this branch.**
`before-…`: assigned to Bader El Almi in Call Center, "Handled by: AI (virtual agent) — no human involved", start shown 19:05 vs first message 15:03.
`after-…`: nobody assigned after the AI end; and, after an *explicit* follow-up, the provisional agent with "Human follow-up required — no agent has accepted or
replied yet". The seeded database has no classification configured, so the Next Step banner shows the classify prompt (it outranks the AI line, which is why the
banner wording itself is pinned by `TicketNextActionTests` rather than by these captures).

## 6. Behaviour change to approve
Previously an ended, unresolved AI conversation was *always* routed to a person. Now it is not (per the stated requirement). Such a ticket stays with the AI
(auto-close / inactivity rules apply) until a real signal arrives. If the business also wants a safety net for AI tickets that never get closed, that is the
existing, opt-in reconciler setting (`AssignStaleAi`) — unchanged.

## 7. Update — approved ownership rules (supersedes §1.1's "explicit follow-up routes to Round Robin", §6)

Decisions given on PR #292 after the first review: **no automatic human assignment anywhere for AI tickets; unresolved / waiting tickets sit in the Call Center
queue with the AI Agent staying the recorded owner; an explicit workflow department is preserved; queue routing is never a human response.**

| Situation | Behaviour now |
|---|---|
| AI receives the call | Ticket created and owned by the **AI Agent** (`Tickets.AiAgentOwnerEmployeeId`); never auto-assigned while the AI handles it. AI classification routes the department and applies SLA but **assigns nobody** (request-type rules and the default rotation no longer run for an AI-handled ticket). |
| AI resolves | `closedByAi` closes it under the AI Agent (unchanged). |
| AI asks for a human (handoff) / `humanFollowUpRequired` | Handling state **Waiting for Human** / **Human follow-up required**. The ticket moves to the **Call Center queue** (`OriginatingDepartmentId` untouched), **owner stays the AI Agent**, no `TicketAssignments` row, **no Round Robin**, rotation cursor untouched. A ticket whose department came from an **explicit workflow** (Collections, or any department different from its originating one — e.g. a request type routed it to Legal/Leasing) **keeps its department**; it is only flagged. |
| AI conversation ends unresolved | Interaction marked **requires follow-up** (`AiEndedUnresolved` is now a *flag + queue* signal, never an assignment trigger). Visible in the Call Center queue (claimable through Take Ownership); AI inactivity/auto-close can no longer make it vanish. |
| A person accepts | A mapped, eligible agent reported **connected** (`routing.agentId`, `handoff.assignedAgentId`, phone pickup at creation) is assigned the **existing** ticket directly; the AI owner is cleared; history `AI Agent → employee` (`GenesysCallTransfer`) or `GenesysDirectCall` when no AI was involved. Ringing / offered / queued / missed events are never sent and never assign. If several agents are offered, only the one reported connected is assigned. |
| The answering agent cannot take it | Unmapped / not a CS Agent / not in the Call Center → the ticket waits in the Call Center queue **flagged** (`ManualAssignmentRequired`), owner unchanged. The "next agent in the rotation" fallback is **removed**. |
| `AssignStaleAi` | Now **report-only**: a stale AI conversation is classified `StaleAiNoEndEvent` and left with the AI Agent. The reconciler's only action for Genesys tickets is *route to the Call Center queue* (`RouteToCallCenterQueue`); `RoundRobinCallCenter` remains for tickets with **no** Genesys interaction. |
| Direct human call (inbound/outbound) | The answering mapped employee owns it from creation; the AI Agent is never recorded as owner. |
| Closed ticket | Terminal; late/duplicate events are audit evidence only (unchanged, re-verified). |

**Assignment History** (`TicketAssignmentHistory`, new nullable `PreviousDepartmentId`, migration `AddAssignmentHistoryPreviousDepartment`, additive, no backfill; scripts
`AddAssignmentHistoryPreviousDepartment.sql` + `Deploy_TigerCS_PROD_AddAssignmentHistoryPreviousDepartment.sql`): initial AI Agent (1); direct human call (2);
AI→human / human→human connect (3); system Round Robin on a Genesys ticket outside transfers (4); manual assignment/reassignment (5); **queue routing** with previous
department, ownership unchanged (6); **department transfer** by a person with previous/new department and owner before/after (7). Every row carries previous/new
employee, department, reason, source, performed-by (null = system), timestamp, source-event time and the Genesys conversation id. Redelivery writes no duplicate.

**SLA and performance.** Queue routing and AI events never record a human first response: `FirstHumanResponseAtUtc` is set only by a real `HumanAgent` line or an
authorised human action; accepting (a connect event) is recorded separately as the interaction handler (`HumanAccepted`), a reply separately (`HumanResponded`). Moving
a ticket between departments does not restart SLA clocks. An AI-owned ticket has no human owner, so it is attributed to no employee in performance reports until a person
actually takes it. (Asserted in `AiEndedHumanFollowUpSqlServerTests` and the SLA/first-response unit tests.)

## 8. Timezone — confirmed root cause, traced layer by layer (20 Nov 2026)

**Input from the Genesys integration team (Eman): Genesys sends UTC.** That removes the "sender wrote local time" hypothesis and leaves the pipeline. The ticket's
expected start is 09:50:26 UTC = **13:50:26 UAE**; the UI showed **17:50:26**, i.e. the database held **13:50:26** (the UAE wall-clock) in a column named `…Utc`.

### Where the extra four hours come from
Reproduced end to end with real processes (API + Web + SQL Server) **with the process zone set to `Asia/Dubai`, the production host's zone** (earlier local runs used a UTC
sandbox, which hides exactly this class of bug). The same true-UTC instant was sent in three spellings:

| Genesys payload for the instant 07:07:11 UTC | Pre-#288 build, UAE host: stored → shown on the page |
|---|---|
| `…T07:07:11Z` | 07:07:11 → 11:07:11 ✅ |
| `…T07:07:11` (no zone) | 07:07:11 → 11:07:11 ✅ |
| `…T07:07:11+00:00` (**explicit zero offset**) | **11:07:11 → 15:07:11 ❌** — start, end **and every transcript line** shifted +4 h |

| Layer | What happens to `…+00:00` on the UAE host |
|---|---|
| Genesys → TigerGroupWeb | Forwarded **byte-for-byte**: the controllers bind `JsonElement` and forward `GetRawText()`; TigerGroupWeb parses and rewrites nothing. **Not the cause.** |
| **TigerCS API, JSON deserialization into `DateTime`** | **`System.Text.Json` converts an offset-bearing string into the server's local zone** (`Kind=Local`, wall-clock 11:07:11 on a UTC+4 host). `Z` and zoneless strings are not converted. **This is where the four hours are introduced.** |
| Normalization / EF write | Nothing recognised the value as local. EF Core persists a `datetime2` by its **raw ticks**: 11:07:11 was stored in `InteractionStartedAtUtc` (a `GETDATE()`-style default is not involved; no DB-side local-time default exists). |
| EF materialization → Api JSON out | Read back as `Kind=Unspecified` (11:07:11); the Api writes it without a zone. Correct handling of what it was given. |
| Web deserialization → display | `TicketDisplay` treats stored values as UTC and adds +4 **exactly once**. 11:07:11 → 15:07:11. **The single legitimate conversion, applied to an already-wrong stored value.** |

The `AddHours(4)` in the UI is not duplicated anywhere; the second four hours were baked into the stored value. Ticket 1878 therefore ran on **a build without PR #288** (the
repository's committed `publish/` folder is a build of `3e48b2f`, which predates #288 — to be confirmed with `GET /health` on Production) **and** Genesys wrote UTC with an explicit offset. (Any explicit-offset spelling reproduces it.)
The #288 build, run through the identical experiment, is correct — but only because `ToUtc` reverses the host-zone conversion after the fact, i.e. it still *depends on* the
host zone being the same one the reader used.

### What changed (nothing subtracts four hours; no warning is the fix)
1. **JSON boundary** — `GenesysUtcDateTimeConverter` on every inbound Genesys timestamp (start, end, transcript `sentAtUtc`, `connectedAtUtc` (agent acceptance),
   `confirmedAtUtc`): parsed with `DateTimeOffset`, an offset names the real instant, **no offset = UTC (the contract)**, always `Kind=Utc`, **never consulting
   `TimeZoneInfo.Local`**. A reflection test fails if a new inbound `DateTime` is added without it. Invalid text is a 400, not a guess.
2. **Persistence** — `TigerCsDbContext.SaveChanges` converts any `DateTimeKind.Local` value to its real UTC instant: **server-local time can no longer reach a column from any path**
   (not only Genesys).
3. **Presentation** — `TicketDisplay.ToUae` = `AsUtc` (a `Local` value is converted by its instant, **never relabelled UTC**) + a fixed +4 (Asia/Dubai has no DST), used by every
   helper. **All 30 `.ToLocalTime()` calls in the Web project** (which gave UAE time on the UAE host and UTC anywhere else) now use it; a test fails if `.ToLocalTime()` returns.
4. **Other Genesys path with the opposite trap** — `CollectionsReminderOutcomeAppService` used `DateTime.ToUniversalTime()`, which reads a *zoneless* value as host-local (four hours
   early on the UAE host); it now uses the shared normalizer.
5. **CI** — the timestamp tests run again with `TZ=Asia/Dubai`.

SLA calculation reads these same columns: conversation start/end, agent acceptance (`connectedAtUtc`) and every transcript time (which sets First Response) were all shifted by the
same defect, so they are corrected by the same change; the SLA calculator itself already requires UTC and rejects `Kind=Local`.

### Evidence
* Real-process runs under `TZ=Asia/Dubai`, three payload spellings, layer dumps (DB ticks → API JSON → page): see `docs/Genesys/screenshots-1878/tz-before-pre288-offset-payload-uae-host.png`
  (start 15:07:11 for a true 11:07:11 UAE, later than the ticket's own creation) and `tz-after-fixed-offset-payload-uae-host.png` (11:05:18 for a true 11:05:18 UAE; start → messages → end → ticket order correct).
* `GenesysWireTimestampTests` (46 tests incl. the matrix `Z`, `.000Z`, `+00:00`, `+04:00`, `-05:00`, zoneless, fractions; ticket 1878's exact `+00:00` value → "19 Nov 2026 13:50:26")
  pass under `TZ=UTC`, `Asia/Dubai` and `America/Los_Angeles`; **mutation-checked** — removing the converter from one field fails four of them under `Asia/Dubai`.

### Still needs Production confirmation / a decision
* **Which spelling Genesys uses** is inferred by reproduction (an explicit offset is the only UTC form that reproduces the symptom); the audit no longer records the raw spelling because the
  boundary now normalizes first. The read-only diagnostic (§8a, rows whose start is later than their own creation/first message) lists every affected conversation.
* **Rows already stored wrong are not rewritten** (a Production data change needs approval). They are identifiable (`InteractionStartedAtUtc`/`EndedAtUtc`/message `SentAtUtc` exactly +4 h
  from the true instant, usually later than `CreatedAtUtc`); a corrective script can be prepared for approval.
* Deploy **this** build (not just #288): #288 is correct only while the API host's zone matches the one the JSON reader used.
