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
4. Category-only tickets are kept (`@IncludeCategoryOnly = 0`); stale-AI rule off, as in Production configuration.

## Run

1. Backup, or restore a copy. 2. Run with defaults: a real transaction that performs every write and check, prints reports 1–10, then **rolls back**.
3. Review: 1 and 1b (counts, 1b by Open / InProgress / Pending / Resolved / Closed), 2 (configuration), 3–4 (classification), 5–7 (assignment), 8 (all `PASS`), 9 (review only), 10 (modification log).
4. After sign-off: copy `ClassificationToApply` / `AssignmentToApply` into `@ApprovedClassificationCount` / `@ApprovedAssignmentCount`, settle the service-account gate, set `@DryRun = 0`. A changed count aborts the run.

**Rerun:** safe. Fixed tickets no longer match a candidate rule; unfixable ones are reported again. **Rollback:** each `ApplyDefaultClassification` audit row holds the previous category / request type / workflow template; each assignment is a superseded + new `TicketAssignments` pair under `CorrelationId = BatchId`; the batch row holds the cursor pre-image. No rollback script is shipped yet.

**Run the dry run on a restored copy of Production, not on the live server:** it takes row locks (the Call Center rotation cursor, agent rows, planned tickets) inside its transaction before rolling back.
