# Historical ticket backfill — classification and CS Agent assignment

Script: `Backfill_TigerCS_PUBLISHED_ClassificationAndAssignment.sql` (default `@DryRun = 1`, database guard `@ExpectedDb = TigerCS_Ticketing_Prod`, confirmed).
**Not executed by the application, CI or any scheduler. Production only after written approval.**

## Rules reused (no new business rule)

| Phase | Existing implementation the SQL transcribes |
|---|---|
| Classification state / candidates | `DefaultClassificationService.AssessAsync`, `IsDefaultAsync`, `DefaultClassificationBackfillClassifier.Decide` |
| Target classification | `ResolveForDepartmentAsync`: the *originating* department's own General / General + Published workflow; Customer Services / General (the human classification queue) when the department is unknown or inactive |
| Which tickets get a CS Agent | `CsAgentBackfillClassifier.Classify` (active, unowned; not pending approval, not Collections, not a live AI conversation, not younger than 3 min) |
| Who | `AssignConfirmedHandlerAsync` (handling agent, if an eligible Call Center CS Agent), else `EnsureCsAgentAsync` → shared Call Center rotation continuing the persisted cursor |

## AI conversations that ended without human intervention

Evidence (identical in the classification and assignment phases; a dry-run check proves they agree): a Genesys interaction exists, none is still live (every one has `EndedAtUtc` or `InactivityClosedAtUtc`), and there is no human evidence (no `RequiresHumanFollowUp`, open/accepted handoff, first human response, `HumanAgent` message, handler row or handled-by user).

| Situation | Result |
|---|---|
| No / invalid / inconsistent / request-type-only classification | `<originating department> / General / General` + that department's Published workflow |
| Originating department unknown or inactive | `Customer Services / General / General` |
| Valid classification (any status) | **Preserved**, never replaced (`ALREADY_VALID` / `ALREADY_DEFAULT`) |
| Category-only (partial) | **Converted for AI-ended tickets only**; every other ticket still follows `@IncludeCategoryOnly` (unchanged, default 0). Old values are in the audit row |
| Department | Never written. The target is the originating department's General; Customer Services General (classification only) when it is unknown or inactive |
| Valid department with no usable General | Reported `DEPT_GENERAL_MISSING`, nothing written (run `EnsureDefaultGeneralClassification.sql`) |
| Closed / Resolved | Label only: never reopened, never assigned, owner and status unchanged (proved by checks in report 8) |
| Active, unowned | Existing approved rules only, no AI-specific path (confirmed handler, else shared Call Center rotation; `@IncludeAiEndedWithoutHuman` keeps its Production default). Report 5b shows each AI-ended ticket's assignment class |
| Owned | Owner kept (every owned ticket is compared before / after) |

Dry-run counts: report 1 `ClassificationToApply_AiEndedWithoutHuman` (and `_Other`, `AiEndedWithoutHuman_TicketsTotal`), report 1b row **A1**, report 3b (outcome x stored state x status), 3c (what was converted, by stored state), 5b (assignment outcome), report 4 column `AiEndedWithoutHuman`. `@ApprovedClassificationCount` is still the **total** `ClassificationToApply`.

## Held back as ACTIVE_AI (live Genesys interaction, no end event)

The live-AI protection stays **on**. A ticket is `ACTIVE_AI` only when a Genesys interaction has neither `EndedAtUtc` nor `InactivityClosedAtUtc` **and** there is no human evidence. The human evidence is the application's: follow-up flag, open handoff, first human response, `HumanAgent` line, handler row, handled-by user, human workflow event (11/12/18/19) or a phone pick-up by an agent id that maps to no user (the last two were added to the SQL to match `UnassignedTicketEvidence`; before that, such tickets were wrongly held back).

**Find out whether they are really live:** run `Diagnose_TigerCS_PROD_BackfillActiveAi_ReadOnly.sql` (SELECT only). Result 2 shows whether end events arrive at all per channel and age, result 3 whether the inactivity job ever ran, result 4 classifies each held-back ticket:

| Cause | Meaning | What to do |
|---|---|---|
| `GENUINELY_RECENT` | activity within 60 minutes | stays protected |
| `CLOSE_RULE_OVERDUE` | a 10 min customer-inactivity / 5 min awaiting-reply clock is overdue outside the Call Center: the approved job should have closed the ticket | the inactivity job is not running (`BackgroundJobs:Enabled`); fix the configuration, do not write data |
| `EXCLUDED_CALL_CENTER` | Call Center (voice): the inactivity rule never applies | only a Genesys end event can end it |
| `NO_CLOCK_NO_END_EVENT` | no clock, no end | the Genesys end event never reached TigerCS |

TigerCS cannot query Genesys (Genesys only calls TigerCS), so "still live" can only be proven from Genesys itself. **Reconciliation, only for tickets the diagnostic shows as stale and only after approval:** export the conversation ids (result 6) and their real end times from Genesys, then replay them through the existing, idempotent conversation-end operation (`GenesysConversationEndAppService`): write-once `EndedAtUtc`/`EndReason`, audited as `GenesysConversationEnded`, nothing on the ticket changes, safe to repeat. No end time is ever invented. A conversation Genesys reports as still connected stays excluded. The opt-in `CsAgentBackfill:StaleAiAfterHours` rule (null today, 24 h only proposed) is the alternative that writes nothing, but it needs a separate business approval.

**Production finding (all 181 held-back tickets are live *Phone* interactions with no end event):** see [Genesys/Phone-End-Event-Investigation.md](Genesys/Phone-End-Event-Investigation.md) and run `Diagnose_TigerCS_PROD_PhoneEndEvents_ReadOnly.sql` (SELECT only; section F compares with a Genesys export). They stay protected until each conversation's real state is verified; nothing is closed and BackgroundJobs stays off.

## What is and is not written

| Written | Never written |
|---|---|
| `Tickets.CategoryId / RequestTypeId / WorkflowTemplateId` (classification candidates only) | ticket status, department, priority, SLA, escalation, timestamps |
| `Tickets.CurrentOwnerEmployeeId` + one superseded and one new `TicketAssignments` row (unowned active tickets only) | status history, SLA instances / pauses, interactions, Genesys fields, messages, notes, approvals, workflow events |
| `AuditEntries`: `ApplyDefaultClassification`, `AutoAssign`, `BackfillAssign`, one `HistoricalBackfillBatch` | notifications / outbox (so no Genesys call or customer notification) |
| `DepartmentAssignmentRotations` cursor (only when the rotation dealt a ticket) | any owned ticket's owner, any Closed / Resolved ticket's owner |

Closed and Resolved tickets can be re-classified (a label only) but are never assigned or reopened.

## Decisions (approved)

1. **Department is not changed.** Only an explicit transfer / request-type routing event moves a department in the existing workflow; report 9b lists mismatches for review.
2. **A valid department with no usable General of its own** is reported (`DEPT_GENERAL_MISSING`) and left alone — run `EnsureDefaultGeneralClassification.sql` first. It is never swapped for Customer Services'.
3. **Owned tickets are never reassigned**, even if the owner is deactivated (report 9a lists them). Reassignment would be a new rule.
4. Category-only tickets are kept (`@IncludeCategoryOnly = 0`) except AI-ended-without-human ones; stale-AI rule off, as in Production configuration.

## Run

1. Backup, or restore a copy. 2. Run with defaults: a real transaction that performs every write and check, prints reports 1–10, then **rolls back**.
3. Review: 1 and 1b (counts, 1b by Open / InProgress / Pending / Resolved / Closed), 2 (configuration), 3–4 (classification), 5–7 (assignment), 8 (all `PASS`), 9 (review only), 10 (modification log).
4. After sign-off: copy `ClassificationToApply` / `AssignmentToApply` into `@ApprovedClassificationCount` / `@ApprovedAssignmentCount`, settle the service-account gate, set `@DryRun = 0`. A changed count aborts the run.

**Rerun:** safe. Fixed tickets no longer match a candidate rule; unfixable ones are reported again. **Rollback:** each `ApplyDefaultClassification` audit row holds the previous category / request type / workflow template; each assignment is a superseded + new `TicketAssignments` pair under `CorrelationId = BatchId`; the batch row holds the cursor pre-image. No rollback script is shipped yet.

**Run the dry run on a restored copy of Production, not on the live server:** it takes row locks (the Call Center rotation cursor, agent rows, planned tickets) inside its transaction before rolling back.
