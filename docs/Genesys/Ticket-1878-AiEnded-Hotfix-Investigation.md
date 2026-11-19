# Ticket 1878 (TG-REG-20261119-0027) — hotfix investigation (19 Nov 2026)

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
`publish/TigerCS.Api` folder dated **18 Nov 05:32 — before #288 merged (20:49)**; if that folder is what was deployed, #288 is **not** in Production. Database
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
