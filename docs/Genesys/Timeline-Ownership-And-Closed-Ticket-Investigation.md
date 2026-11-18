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

**Fix.** The ownership change now carries *when it happened* (`TicketAssignment.AssignedAtUtc`, the transfer event) = the first human line's time
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
