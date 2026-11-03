# Ticket Activity Timeline & Closure Audit

Covers: the root-cause analysis for ticket **TG-FM-20261103-0001 (TicketId 187)**, the full chronological Activity timeline, the
inactivity-closure guard, tests, migration requirements and the production deployment steps.

> **Nothing here touches Production data, and nothing deploys automatically.** The change is read-only on every historical record, needs **no
> database migration**, and ships through the normal PR → UAT → Production path.

---

## 1. Root-cause analysis — ticket 187

### What was observed

| Time (Asia/Dubai) | Fact |
| --- | --- |
| 08:15 | Ticket created (Instagram) |
| 08:26 | System: Open → Resolved → Closed, "customer inactivity over 10 minutes" |
| ~09:05 | Conversation History shows human agent Kawther Benbrahim replying |
| now | Ticket still assigned to Facilities Management Queue (Unassigned) |

### What the code proves (read from the repository)

1. **The 08:26 closure is exactly what the Website/Chatbot rule does** (`ChatbotInactivityCloseAppService`, default 10 minutes, measured
   from the customer's last interaction). Instagram is a Genesys non-voice channel, so the rule applies. The closer excludes a ticket only
   if it has an **owner**, a recorded **handler** (`HandledByUserId`), an **open handoff**, `RequiresHumanFollowUp`, or is a Call Center
   ticket. Ticket 187 "remains in the Facilities queue (Unassigned)", so none of those held at 08:26 — **TigerCS had no record of any human
   being involved when it closed the ticket.** The 08:26 closure was a correct application of the rule to what TigerCS knew.
2. **Nothing ever checked whether a person had actually spoken.** A `HumanAgent` line in the transcript was not an exclusion in the closer.
   The update path (`GenesysTicketUpdateAppService`) does treat a human line as a signal and marks human follow-up — but only on that
   path, and only *after* the line arrives; the transcript-only path (`AppendTranscriptAsync`) did nothing, and a customer message after a
   human line restarted the inactivity clock because no owner/handler existed.
3. **A human reply on an already-Closed ticket is accepted and silently stored.** `AppendTranscriptAsync` has no status check, so the 09:05
   reply is saved to the transcript (hence it appears in Conversation History). The follow-up routing ("route to Call Center, assign one
   agent") skips closed tickets, so nothing was assigned and no event recorded the arrival. That is why the ticket stayed
   *Closed + unassigned in the Facilities queue* while Conversation History shows a human reply. **This is a real gap, independent of how the
   timing turns out.**
4. The old Activity tab only drew creation + status changes + notes + transfers, so none of this was visible.

### What the code cannot prove — and how to settle it

Whether the 09:05 reply was (a) a **separate interaction session**, (b) a **delayed/out-of-order event**, or (c) a genuine **premature
closure** depends on production rows that were not available when this analysis was written. They are all stored, so it can be settled in
minutes, read-only:

```bash
# run in SSMS / sqlcmd against TigerCS_Ticketing_Prod (db_datareader is enough) — SELECT only
Diagnose_TigerCS_PROD_Ticket187_ClosureTimeline_ReadOnly.sql
```

It prints one chronological result set across every store (transcript *sent* times next to audit *received* times) and a verdict per
interaction:

| Verdict | Means | Evidence |
| --- | --- | --- |
| **SEPARATE SESSION** | The human activity belongs to a conversation that started *after* 08:26 | A second `TicketInteractions` row (new `GenesysConversationId`) starting after the closure |
| **DELAYED EVENT** | A human acted *before* 08:26 but TigerCS learned of it later | Handler `ConnectedAtUtc` ≤ closure < `RecordedAtUtc`, or a human line *sent* ≤ closure whose `GenesysTranscriptAppended` audit entry is *after* it |
| **PREMATURE CLOSURE** | A human replied in the **same** conversation that had been closed for inactivity | One interaction, human line *sent* after 08:26, no earlier human evidence |

The same classification is now computed automatically on every ticket (see §2) and appears on the **Ticket closed** entry as *Human activity
after closure*. If 187 turns out to be (c), the underlying cause is that a human joined an Instagram DM the AI had gone quiet in, without
TigerCS being told (no handoff, no connect event, no mapped agent) before the clock ran out — which is the evidence gap the guard below closes
for the future, and the timeline now exposes for the past. **Historical rows are not changed.**

---

## 2. What changed

### API (`TigerCS.Api` / `TigerCS.Application` / `TigerCS.Infrastructure`)

| Change | Where |
| --- | --- |
| **`GET /api/tickets/{ticketId}/activity-timeline`** — read-only, `Tickets.View`, same visibility as the ticket | `TicketsController`, `TicketActivityTimelineAppService` |
| **`TicketActivityTimelineBuilder`** — pure function from loaded stores to the timeline (no I/O, fully unit-tested) | `Application/Modules/Ticketing/ActivityTimeline/` |
| Read-only audit port `IAuditEntryReader` (+ `AuditEntryReader`) and `ITicketAssignmentRepository.ListByTicketIdAsync` | Application / Infrastructure |
| **Closure guard:** the inactivity closer never closes a conversation in which a `HumanAgent` line exists; it marks the ticket human work and writes audit `ChatbotInactivityCloseSkippedHumanActive` | `ChatbotInactivityCloseAppService` |
| A `HumanAgent` line stops both inactivity clocks the moment it is stored | `GenesysConversationEndAppService.AppendTranscriptAsync` |
| A human reply reaching an already-**Closed** ticket writes audit `TicketHumanActivityAfterClosure` (forward-compatible evidence of *when TigerCS received it*). **Nothing is reopened or rewritten.** | same |

**Sources used (requirement 4):** `TicketInteractions`, `TicketInteractionMessages` (transcript), `TicketAgentHandoffs`,
`TicketInteractionHandlers` (Genesys-ID → user mapping), `TicketWorkflowEvents`, `TicketStatusHistory`, `TicketAssignments`, `AuditEntries`.

**Event coverage (requirement 1):** ticket created (source, channel, creator) · conversation / AI conversation started · first & last AI
response · last customer message · customer requested human · handoff requested/queued · offered/assigned · accepted · completed/cancelled ·
human agent connected (name + Genesys ID mapping) · first & latest human response · routing changes · marked as human work · owner
assigned/changed/released · department transfer/escalation (previous → receiving owner & department) · conversation ended (actor derived from the
reported end reason, said so in the text) · Open/InProgress/Resolved/Closed/Reopened · resolution outcome (incl. Cancelled) · SLA state ·
verification · human activity after closure.

**Every closure (requirement 2)** carries: *Closed by* (Human Agent / AI / System Automation / Supervisor), actual actor name, timestamp, exact
reason + reason code (CustomerInactivity, AiResolved, DocumentDelivery, ResolvedByAgent, ManualClose, NotRecorded), the evidence source
(audit entry or lifecycle note), whether the customer interaction had already ended, whether a human was connected/responding, and the
after-closure assessment (`NoHumanActivityAfterClosure`, `SeparateInteractionSession`, `DelayedEvent`, `PrematureClosure`, `Inconclusive`).

**Never invented (requirement 5):** missing facts read `Unknown` / `Not recorded`, and a list of *evidence gaps* (unmapped Genesys agent,
missing audit entry, unrecorded creator…) is shown under the timeline. Timestamps are UTC in the API and contain no clock times inside text.

**Avoids flooding (requirement 8):** the transcript contributes ≤ 5 milestones per interaction (start, first/last AI reply, last customer
message, first/latest human reply), each linking to Conversation History.

### Web (`TigerCS.Web`)

- Activity tab renders the Api timeline: name, actor + actor type, description, reason, *previous → new*, delayed-delivery note, closure facts
  block, link to Conversation History. Notes, escalations and approval-cycle events are merged in as before.
- **All timestamps are Asia/Dubai (UTC+4)**, shown with seconds and labelled once in the heading
  (`TicketDisplay.DubaiTimestamp`). This also fixes the old tab, which used the *server's* local time.
- If the timeline call fails the page falls back to the previous basic feed and says so — it never breaks the ticket page.

### Behaviour decisions (deliberately not made for you)

- **A human reply on a closed ticket does not reopen it.** Reopening is a business decision (approval, SLA, ownership). It is now *visible*
  (timeline warning + audit entry) so a supervisor can reopen deliberately. If you want auto-reopen or auto-routing to the Call Center, say so
  and it becomes a separate, reviewed change.
- "Offered" and "assigned" are not stored separately by Genesys; the timeline shows the recorded assignment and says so.
- No ticket is ever closed by a "Genesys end event" today (ending a conversation never closes a ticket), so that reason cannot appear.
- "Supervisor" is shown when the closing user holds CS Supervisor / CS Manager / General Manager; otherwise "Human Agent".

---

## 3. Tests

`dotnet test src/TigerCS.Tests` (all green). New:

| Requirement | Test |
| --- | --- |
| AI-only resolution and closure | `AiOnlyResolution_ShowsAiMilestones_AndClosedByAi_WithNoHumanInvolved` |
| AI → human handoff | `AiToHumanHandoff_ShowsRequestQueuedOffered…`, `HandoffWithoutAStatedTrigger_IsNotAttributedToAnyone` |
| Agent accepted and replied | `AgentAcceptedAndReplied_ShowsConnected_Accepted_FirstAndLatestHumanResponse_WithoutFloodingMessages` |
| Automatic inactivity closure | `InactivityClosure_ShowsSystemAutomation_Reason_EndedNo_HumanNo` (ticket 187 as recorded) |
| Delayed / out-of-order Genesys events | `DelayedGenesysConnectEvent…`, `DelayedTranscriptDelivery_IsRecognisedFromTheAuditReceiptTime` |
| Human replies after an earlier closure | `HumanReplyInTheSameConversationAfterClosure_IsAPrematureClosure…`, `…NewConversation…SeparateInteractionSession`, `ReplyAfterAReopen…` |
| Multiple interactions on one ticket | `MultipleInteractions_AreLabelledAndKeptSeparate…` |
| Missing audit evidence | `MissingAuditEvidence_IsShownAsUnknownOrNotRecorded_NeverInvented`, `MissingCreatorAndInteractions…`, `ClosureWithoutAnAuditEntry…` |
| Timezone | `Timestamps_AreConvertedToAsiaDubai_AndLabelled`, `NoDescriptionOrNoteCarriesAClockTime…` |
| Closure guard | `HumanActiveInactivityGuardTests` (6 tests: control, VirtualAgent line, human line stops clock, customer message after human line, closed-ticket evidence, customer line on closed ticket) |
| SQL translation / read-only | `ActivityTimelineQueriesSqliteTests` |
| API endpoint, auth inventory, OpenAPI | `GetTicketActivityTimeline_Returns200`, inventory + Swagger lists updated |

## 4. Migration requirements

**None.** No EF migration, no schema change, no data backfill. New audit actions are ordinary rows written going forward; historical rows are
read as they are. Optional (only if the Activity tab is slow on a large `AuditEntries` table): `Optional_TigerCS_PROD_AuditEntries_EntityIndex.sql`
— a non-clustered index, idempotent, easily dropped, deliberately **not** part of the release.

## 5. Example full Activity timeline

*Illustrative — synthetic, generated from the regression-test scenario that mirrors ticket 187 and assuming the same-conversation outcome (§1 (c)).
It is not production data. Real output for 187 comes from the new endpoint after deployment.*

All times Asia/Dubai (UTC+4).

| Time | Event | Actor | Details |
| --- | --- | --- | --- |
| 3 Nov 2026 08:15:00 | Ticket created | System — Genesys integration (svc-genesys) | Source: Genesys · Channel: Instagram |
| 3 Nov 2026 08:15:00 | AI conversation started | AI (AI virtual agent) | Genesys: the Instagram conversation, Inbound, queue Facilities queue |
| 3 Nov 2026 08:15:03 | Last customer message | Customer (Layla) | Last of 1 customer message(s) |
| 3 Nov 2026 08:15:08 | First AI response | AI (Tiger Bot) | First AI reply |
| 3 Nov 2026 08:26:00 | Ticket resolved | System | Open → Resolved · Reason: Automatically closed — customer inactive for more than 10 minutes. |
| 3 Nov 2026 08:26:00 | **Ticket closed** | System Automation (Chatbot inactivity job) | Resolved → Closed · Reason: customer inactive for more than 10 minutes (CustomerInactivity, source: Audit entry) · Customer interaction already ended? **No** — the conversation had not ended · Human agent connected? **No** — none recorded at closure time · **Human activity after closure: PrematureClosure** — a human replied in the same conversation after the ticket was closed; no human was recorded when it closed ⚠ |
| 3 Nov 2026 09:05:00 | First human response | Agent (Kawther Benbrahim) | First reply by a human agent in the Instagram conversation → Open Conversation History |
| 3 Nov 2026 09:05:00 | Human activity after closure ⚠ | Agent (Kawther Benbrahim) | A human agent replied after the ticket was closed. Assessment: PrematureClosure |

Evidence gaps listed beneath: *(none in this example)*; for older tickets e.g. "The ticket's creator is not recorded", "Genesys agent … is
not mapped to a TigerCS user".

## 6. Production deployment instructions (manual — nothing is deployed for you)

1. **Review & merge** the PR after CI is green (`dotnet test src/TigerCS.Tests`, including `EfModelAndMigrationConsistencyTests` — no model change).
2. **Deploy to UAT first** (normal publish of `TigerCS.Api` then `TigerCS.Web`). No `dotnet ef` / SQL step.
3. **UAT smoke test**
   - Open any ticket → *Activity*: header says "All times in Asia/Dubai (UTC+4)", events are chronological, closed tickets show the closure block.
   - `GET /api/tickets/{id}/activity-timeline` returns 200 for a visible ticket and 404 for a ticket the caller cannot see.
   - Send a Genesys update with a `HumanAgent` transcript line, advance past the timeout → ticket is **not** closed; audit shows
     `ChatbotInactivityCloseSkippedHumanActive`.
4. **Production, in order:** (a) **API** first, (b) **Web**. Web tolerates an older API (falls back to the basic feed with a notice), so a
   brief gap between the two is safe. Take the usual DB backup beforehand; this release does not write schema.
5. **Verify** in Production: run `Diagnose_TigerCS_PROD_Ticket187_ClosureTimeline_ReadOnly.sql` (SELECT only) and compare with the
   Activity tab of ticket 187; record which verdict (SEPARATE SESSION / DELAYED EVENT / PREMATURE CLOSURE) it is.
6. **Watch** for `TicketHumanActivityAfterClosure` and `ChatbotInactivityCloseSkippedHumanActive` audit entries after go-live — each is a case the
   old behaviour would have mishandled.
7. **Rollback:** redeploy the previous Web/API build. No data to undo: the only writes this release introduces are new audit rows and the
   sticky `RequiresHumanFollowUp` flag on conversations a human actually spoke in (the same flag the update path already sets for human lines).
8. *(Optional, only if the Activity tab is slow)* run `Optional_TigerCS_PROD_AuditEntries_EntityIndex.sql` in a quiet period.

**Do not** change production data for ticket 187. If it should be reopened, a supervisor does that through the normal Reopen action, which
writes its own history.
