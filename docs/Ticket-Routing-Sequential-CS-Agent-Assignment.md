# Ticket routing — one shared, sequential CS Agent rotation

## Root cause of `Facilities Management Queue (Unassigned)`

The department and the owner were **coupled**: every route that changed a ticket's department also *cleared its owner*, and then asked the **destination department** to find a new one.

| Path | Code | What went wrong |
|---|---|---|
| Transfer (CS Agent → Facilities) | `Ticket.TransferToDepartment` set `CurrentOwnerEmployeeId = null`; `TicketAssignmentAppService.TransferAsync` superseded the assignment | The agent lost the ticket the moment it moved |
| Re-assignment in the destination | `ApplyAsync` (rule) then `ApplyRoundRobinFallbackAsync` rotated **inside the destination department** | Facilities had no active, unlocked, role-eligible staff, **or** the request-type rule said *Department Queue*, **or** workflow settings disabled assignment ⇒ `NoEligibleEmployee` / queue |
| Request-type routing, category routing | `TicketRequestTypeRoutingService`, `TicketClassificationAppService` | same pattern |
| Reopen | `Ticket.Reopen` cleared the owner; `ApplyAsync` only | same; no rotation at all, so most reopens were unassigned |
| Failure | `QueueAsync` only wrote an audit line | Nothing flagged the ticket for staff; no retry; UI printed `"<Dept> Queue (Unassigned)"`, which read as a normal state |

The rotation itself (`DepartmentAssignmentRotation`, row-locked cursor) was sound; it was just keyed **per department**.

## What changed

**One rotation.** The pool is the Call Center's eligible CS Agents (active member, CS Agent role, not locked out, not a service identity — the existing eligibility rules). The persisted cursor is the existing `DepartmentAssignmentRotations` row of the Call Center department, so **no schema change** and the existing Call Center intake rotation continues seamlessly. Order is ascending employee id (`DepartmentAssignmentRotation.PickNext`), stable and deterministic; the cursor row is locked `UPDLOCK, HOLDLOCK` for the caller's transaction, so concurrent creations cannot take the same position.

**`TicketAutoAssignmentService`**
- `EnsureCsAgentAsync` — the single guarantee. Closed/Resolved → nothing. Already owned → nothing written (no duplicate rows/events). Else the shared rotation. No agent → failure recorded.
- `RestorePreviousCsAgentAsync` — reopen keeps the ticket's previous agent while still eligible.
- `ApplyRoundRobinFallbackAsync` now delegates to `EnsureCsAgentAsync` (this is what Transfer, category routing, request-type routing, creation without a Call Center, and reopen reach), so *"rule says Department Queue"* / *"assignment disabled"* can no longer strand an active ticket.
- Failure = audit `AutoAssign … Status=Unassigned` **plus** one `ManualAssignmentRequired` workflow event (marker `CsAgentAssignmentFailed`), deduplicated per ticket.

**Lifecycle**
- Transfer / request-type routing / category routing: `TransferToDepartment(…, preserveOwner: true)` — the agent is preserved unless the transfer names an assignee (`AssignToEmployeeId`). Call-Center-first system routing keeps the old release behaviour (it must clear a stale automatic pre-assignment). The transfer event's `ToEmployeeId` is set only when someone actually took the ticket.
- Reopen: previous agent if eligible → department rule → shared rotation → recorded failure. The reopen itself never fails because no agent exists.
- Retry: `POST /api/tickets/{id}/assignment/retry` (`Tickets.Assign` + CS Manager / administrator). Idempotent; 409 `no-eligible-cs-agent` when still nobody is available. The existing 5-minute reconciler continues to retry Open unowned tickets.
- UI: the label for a ticket with no agent is `Unassigned — awaiting CS Agent`; it is never presented as a department queue.

**Deliberate interpretations (please confirm)**
1. *Live AI conversations are not given an agent* until a person is requested (handoff) — `UnassignedTicketClassifier` already protects them, and rule 1's exception ("resolved and closed by AI") would otherwise be meaningless. They are not counted as failures.
2. After a transfer the department's own staff can no longer *claim* the ticket from a queue, because it has an owner. The receiving department gets it via an explicit reassignment (transfer + `AssignToEmployeeId`, or Assign).
3. With **no active Call Center department configured** there is no pool, so assignment fails *loudly* (recorded) rather than falling back to department staff.

## Files changed

Domain: `Ticket.cs`. Application: `TicketAutoAssignmentService.cs`, `TicketAssignmentAppService.cs`, `TicketLifecycleAppService.cs`, `TicketCreationAppService.cs`, `TicketRequestTypeRoutingService.cs`, `TicketClassificationAppService.cs`, `TicketOperationsDto.cs`. Api: `TicketsController.cs`. Web: `TicketDisplay.cs`. Scripts/docs: `Diagnose_TigerCS_ActiveTicketsMissingCsAgent_ReadOnly.sql`, this file. Tests: new `SharedCsAgentRotationTests`, additions to `TicketLifecycleAppServiceTests`, `CallCenterAllChannelsRegressionTests`; existing tests that encoded "transfer clears the owner" / department-scoped rotation / the old label were updated to the new rules. Collections, Campaigns and unrelated modules are untouched.

## Database migration

**None.** No schema, index or seed change. (`ManualAssignmentRequired` and the cursor table already exist.)

## Backfill of existing tickets — proposal (nothing has been modified)

1. Run `Diagnose_TigerCS_ActiveTicketsMissingCsAgent_ReadOnly.sql` on a read-only login. It separates `AI_IN_PROGRESS` and `PENDING_APPROVAL` (excluded) from `BACKFILL_CANDIDATE`, and shows the cursor and Call Center membership.
2. Review the candidate list with the business (especially tickets in `InProgress`/`Pending*`, which already had a working history without an agent).
3. Back up, then deal the candidates **oldest first** through the application, not raw SQL: `POST /api/tickets/{id}/assignment/retry` per ticket (or a throttled script calling it). That uses the same shared cursor, writes the assignment row + audit under the caller, and is idempotent, so a re-run or an interrupted run is safe. A direct SQL update is *not* recommended: it would bypass the cursor lock and the audit trail.
4. Re-run the diagnostic: `BACKFILL_CANDIDATE` must be 0 and section 5 must return no rows.

## Deployment and verification

1. Deploy the build (no migration step). 2. Confirm the Call Center has ≥1 active, unlocked CS Agent member. 3. Create tickets on several channels and from different departments; confirm owners go A, B, C, … in a fixed order. 4. Transfer one to Facilities Management: the department changes, the agent does not. 5. Reopen a closed ticket: it has an agent. 6. Temporarily lock out all agents, create a ticket: it shows `Unassigned — awaiting CS Agent`, a *Manual Assignment Required* event exists once; unlock, call `…/assignment/retry`: assigned. 7. Run the diagnostic.

## Verification status

See the PR description for the exact test run. SQL Server–gated tests (`*SqlServerTests`, including the parallel-cursor tests that exercise the real `UPDLOCK` row lock) are **skipped** unless `TIGERCS_TEST_SQL_SERVER` is set; the diagnostic SQL file was not executed against a database from this environment.
