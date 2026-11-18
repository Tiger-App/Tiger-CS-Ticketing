# Genesys timeline, ownership timing and closed-ticket immutability — investigation (18 Nov 2026)

Nothing here was verified against Production data: no payload log, database or Genesys execution history was available. Everything below is read from
the code and reproduced with tests; the items that only Genesys / TigerGroupWeb can confirm are listed in section 4.

## 1. Time zone — what happened

| Question | Finding (code) |
|---|---|
| Where is the UI conversion? | Once, in `TicketDisplay` (`SpecifyKind(Utc).AddHours(4)`). Not duplicated on the Web side. |
| Where was UTC not guaranteed? | At the Genesys boundary. `startedAtUtc`, `sentAtUtc`, `connectedAtUtc`, `endedAtUtc` are `DateTime` in the contract. System.Text.Json yields `Kind=Utc` for `…Z`, **`Kind=Local` (the server's zone, UTC+4 on the UAE host) for `…+04:00`**, `Unspecified` for no zone. EF persisted the raw ticks of whichever it got. |
| Effect | A start time carrying an offset was stored as **UAE wall-clock**; transcript lines and server-clock times were stored as UTC. The UI then added +4 to the first value again: "AI conversation started 19:18:24" next to a first human reply at 15:21:08 — the exact 4-hour inversion reported. |
| Not proven | Which exact string Genesys / TigerGroupWeb sent for `startedAtUtc`. The mechanism and the symptom match; the payload could not be inspected. |

**Fix.** `GenesysTimestamps.ToUtc` (Local → real instant, Unspecified → UTC per the contract) applied at the entry of every Genesys service
(ingest, update, end, transcript append, handoff request/assignment). Stored values are always UTC; conversion to UAE happens only in the UI.
Rows already stored wrong are **not rewritten**: the timeline flags a start time later than the conversation's first message (`wrong time zone`
note + evidence gap). Read-only check for affected rows:

```sql
-- Genesys conversations whose recorded start is later than their own first message (stored in the wrong zone before the fix)
SELECT i.TicketInteractionId, i.TicketId, i.GenesysConversationId, i.InteractionStartedAtUtc,
       MIN(m.SentAtUtc) AS FirstMessageAtUtc, DATEDIFF(MINUTE, MIN(m.SentAtUtc), i.InteractionStartedAtUtc) AS MinutesLater
FROM dbo.TicketInteractions i
JOIN dbo.TicketInteractionMessages m ON m.TicketInteractionId = i.TicketInteractionId
WHERE i.GenesysConversationId IS NOT NULL AND i.InteractionStartedAtUtc IS NOT NULL
GROUP BY i.TicketInteractionId, i.TicketId, i.GenesysConversationId, i.InteractionStartedAtUtc
HAVING i.InteractionStartedAtUtc > DATEADD(MINUTE, 2, MIN(m.SentAtUtc))
ORDER BY MinutesLater DESC;
```

## 2. Ownership timing — why the assignment came 13 minutes after the first reply

* The human reply (15:21:08) was only delivered to TigerCS inside the **end-of-conversation** PATCH (15:34:01). `UpdateAsync` resolves the
  `HumanAgent` line's `senderId` and applies the answering-agent rule — correctly, to the real person — but **stamped the transfer, the assignment and
  the audit with the processing time**. That is the 13-minute gap; the owner was right, the time was the delivery time.
* `Previous ownership: Registration / Unassigned`: the timeline derived the previous owner from `TicketAssignments` only; the AI Agent is recorded
  in `TicketAssignmentHistory` (history-only by design, #261), so it never appeared.
* "Call answered by a Call Center agent": a literal used for every channel.
* `SLA Running → Running`: `FirstHumanResponseRecorder` writes a same-value `SlaState` history row as its ledger entry; the timeline rendered it as a state change.

> **Revised after review.** Back-dating does not make an assignment real-time, so the transcript fallback no longer back-dates: it assigns when
> TigerCS *receives* the evidence and is labelled `AcceptanceEvent=false` (section 5). Only a genuine acceptance event with its own
> `routing.connectedAtUtc` is dated at that time, because that is the real acceptance time, not a simulation.

**Original fix (superseded for the transcript path).** The ownership change now carries *when it happened* (`TicketAssignment.AssignedAtUtc`, the transfer event) = the first human line's time
(or `routing.connectedAtUtc`), clamped to [ticket creation, the assignment it replaces, now]; the audit row and the history entry keep the processing
time separately (`OccurredAtUtc=` / `RecordedAtUtc=`, `SourceEventAtUtc` / `ChangedAtUtc`). Wording is channel-specific (`AnswerWording`): voice "answered",
everything else "accepted" ("WhatsApp conversation accepted by a Call Center agent"). The timeline shows the AI Agent as previous owner and turns the
same-value SLA row into a **First response recorded (SLA)** milestone at the real reply time (processing time shown separately). First Response
SLA itself was already measured from the first human line's `SentAtUtc` and is unchanged; accepting a conversation never satisfies it.

## 3. Closed is terminal

* **Reopen paths.** `Ticket.Reopen` has exactly one production caller, `TicketLifecycleAppService.ReopenAsync` (explicit, authorized, reopen window and
  outcome rules). No job, Genesys handler, AI workflow, WhatsApp/Queueing handler or ticket matcher calls it; every other `Ticket` mutator
  (`ChangeStatus`, `AssignTo`, `TransferToDepartment`, `ReleaseOwner`, `Resolve`, `RecordFirstHumanResponse`, `Classify…`) already throws on a Closed
  ticket. A new conversation id creates a new ticket; a replay of an old conversation id returns the existing ticket (`AlreadyIngested`).
  A test fails if any second caller of `ticket.Reopen(` appears.
* **What a late event still did to a Closed ticket (found and fixed):** it could apply the default classification, write a ticket-level "agent
  connected" workflow event (and widen handler access), and write a post-closure AI ownership-history row. A Closed ticket now takes none of these
  (`ClosedTicketGuard`); the late evidence is recorded once as audit `GenesysEventIgnoredOnClosedTicket` (shown on the timeline as *Late event
  recorded after closure*), the transcript/interaction keep their facts. No notification is raised.
* **Concurrency.** Closure vs. an in-flight event: the domain guards run on the loaded ticket and the unit of work's row version rejects the stale
  writer (`ConcurrencyConflict`, nothing applied). The SQL Server concurrency tests for this exist but are skipped without a database in this
  environment (119 skipped) — run them before release.
* **Was the reported ticket reopened?** The code cannot reopen a ticket automatically, and the timeline shows "Ticket reopened" only from a
  status-history row `Closed → <not Closed>`, which only the manual Reopen writes. If the timeline you reviewed shows AI events after the closure with
  no `Closed →` status row, it was a **delayed AI event added to the history, not a reopen** — the late transcript/handler/classification writes fixed
  above explain how it looked like one. The timeline you refer to was not attached to this request, so this is not confirmed for that ticket: run
  `SELECT * FROM dbo.TicketStatusHistory WHERE TicketId = <id> AND Dimension = 1 AND OldValue = 6 ORDER BY OccurredAtUtc` (6 = Closed) — no row means it was never reopened.

## 4. Upstream events — what TigerCS receives, and what is missing

* Human acceptance reaches TigerCS only via (a) `routing.agentId` from the **agent script's Update Routing** on script load, (b) a `HumanAgent`
  transcript line (live `transcript` PATCH or the end PATCH). It is real-time **only if Genesys sends those during the conversation**; the
  data actions and triggers in `docs/Genesys/data-actions` send the transcript only on end, and *Update Routing* sends no `connectedAtUtc`.
  For this WhatsApp ticket the first evidence was the end PATCH.
* Offers/ringing are deliberately never sent (`docs/Employee-Response-Time-Reporting.md`); an offered agent never owns a ticket (test added).
* **Missing upstream (Genesys / TigerGroupWeb, not invented here):** a real-time "agent accepted" signal for messaging (script load may not fire
  for WhatsApp after a bot transfer — confirm in Genesys execution history); `connectedAtUtc` in the *Update Routing* template; live transcript
  PATCHes while the conversation runs. Until then TigerCS can only date the acceptance by the first human line.

## 5. Interaction states — what each one is, and what TigerCS does with it

| State | Genesys source today | Reaches TigerCS as | Assigns a person? |
|---|---|---|---|
| **Offered** (ringing / alerting a specific agent) | `user.start` trigger | **Never sent** (by contract; "a ringing call never reaches TigerCS") | **No** — an offered agent never owns the ticket |
| **Accepted / Connected** | Agent script load (*Update Routing* data action 03) → `routing.agentId`; phone pickup on the create call | `routing.agentId` (+ optional `routing.connectedAtUtc`) | **Yes, immediately** (answering-agent rule); the only real-time trigger |
| **First human response** | A `HumanAgent` transcript line | `transcript` (live PATCH) or `ended.transcript` | Only as *late evidence* (switchable: `Genesys:AssignOnTranscriptEvidence`), at receipt time, flagged `AcceptanceEvent=false`. Always sets First Response SLA from the line's own `sentAtUtc` |
| **Disconnected** | `customer.end` trigger → `disconnectType` | `ended.endReason` | No |
| **Conversation ended** | same trigger, data action 04 | `ended.endedAtUtc` | No (ownership already settled; a transfer is only honoured while the conversation is live) |

**What was verified.** TigerCS: all of the above, by tests (connect assigns before the end; offered never assigns; a late transcript assigns at receipt, never
back-dated; the switch). TigerGroupWeb: only the repo copy of the forwarding patch (`docs/Genesys/TigerGroupWeb-Patch`) — it forwards `PATCH
/api/genesys/tickets/{id}` bodies byte-for-byte via `GetRawText()`, so **the forwarder cannot be what delays acceptance**; the TigerGroupWeb repository
itself was not reachable from this environment and the deployed build is unverified.

**Why the 15:21 acceptance was only known at 15:34 (cause, not simulated).** For this WhatsApp conversation no `routing.agentId` update reached TigerCS
when the person accepted; the first evidence was the end-of-conversation PATCH. Real-time assignment therefore needs these **upstream changes, in
Genesys Cloud / Architect and TigerGroupWeb (none can be made from this repository)**:

1. **A signal fired when an agent *connects* to a messaging interaction (WhatsApp / web messaging), not when it is offered.** The agent-script *Update
   Routing* call is the documented one; confirm in Genesys flow-execution history that the script actually loads for a WhatsApp conversation transferred
   from the bot (a script attached only to the voice flow's *Transfer to ACD* would explain it). If the script cannot fire for messaging, a Genesys
   trigger on the conversation's connect event for the `message` media type must call data action 03. The exact topic name must be taken from your org's
   trigger schema — it is not asserted here.
2. **Send `routing.connectedAtUtc`** from that call (data action 03's request template does not carry it today). Without it TigerCS can only time the
   acceptance by when the call arrives.
3. **Optionally stream the transcript** (`transcript` PATCH) while the conversation runs, so First Response and the human sender are known live
   instead of at the end.
4. **TigerGroupWeb:** deploy the forwarding patch (it was *specified, not applied* per `TigerGroupWeb-Proxy-Change.md`) and confirm `PATCH
   /api/genesys/tickets/{id}` is routed and its service-account token renews; nothing in the body may be dropped.

Until (1) exists, a person who accepts is assigned when the first update naming them arrives, and that is stated honestly in the audit and timeline.

## 6. The AI Agent as the actual owner

PR #261 made the AI Agent history-only because "an ownerless AI ticket" is how ~45 files recognise AI handling (Round Robin, Take Ownership claim pool,
reconciler, AI auto-close, inactivity closer, visibility, dashboard buckets, SLA, reporting). Putting the AI account into `CurrentOwnerEmployeeId` would
silently change every one of them (an "owned" ticket is skipped by the reconciler, excluded from the claim pool, refused by the AI/inactivity closers and
counted as assigned on the dashboard).

**Implemented:** a persisted `Tickets.AiAgentOwnerEmployeeId` (migration `AddTicketAiAgentOwner`, additive nullable column, no backfill; script
`AddTicketAiAgentOwner.sql`). It is the ticket's *actual assigned owner* while the AI handles the conversation and no person owns it:

* Set at creation of an AI-first Genesys ticket on every channel (WhatsApp, chat, voice), audited `AssignToAiAgent`, with the existing history entry.
* Cleared by every person-assignment (`Ticket.AssignTo`: acceptance, direct pickup, Round Robin, manual assign, take ownership) and by a move to a
  department queue (`TransferToDepartment`). A person's ownership can never be taken back by the AI (`AssignToAiAgent` refuses).
* Kept on closure, so an AI-handled and AI-closed ticket still names the AI Agent. Never changed on a Closed ticket.
* `CurrentOwnerEmployeeId` stays "the human owner", so **routing, permissions, SLA, reconciler, auto-close, dashboards and reports behave exactly as before**
  (regression suite unchanged and green). Screens read `AssignedOwnerEmployeeId` / `IsOwnedByAiAgent`; the ticket list/detail show "AI (virtual agent)"
  from the persisted owner instead of inferring it from a 6/12-hour activity window. Tickets created before the column existed keep the evidence rule.
* Historical tickets are **not** back-filled (that would be a production data change needing approval).
