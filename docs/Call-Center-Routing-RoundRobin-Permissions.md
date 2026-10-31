# Call Center routing, Round Robin assignment, Resolve/Close/Reopen permissions

## Flow
| Step | Behavior |
|---|---|
| AI resolved (`closedByAi`) | Unchanged: existing closure validations apply (Resolved → Closed, "Closed by AI"). |
| AI asks for a human (Genesys handoff) | `GenesysAgentHandoffAppService.RequestAsync` routes a ticket that nobody owns to the **Call Center** department (code `CC`) before the work item is recorded. Skipped when already in Call Center, already owned by a person, closed, or the trigger is an explicit `AgentTransfer`. Audited (`Transfer`) + `TicketTransferred` workflow event (system routing). |
| Call Center selects a category | Category → Department. `TicketClassificationAppService` lets a ticket sitting in Call Center be routed to the category's (active) department; manual ticket creation by a Call Center user for another department does the same. Category Type/Request Type still drives workflow, SLA and the assignment rule. |
| Assignment | `TicketAutoAssignmentService`: a configured request-type rule decides first. If none decided, the department's **Round Robin** picks the next eligible employee. |

## Round Robin
* `AssignmentMode.RoundRobin` can also be configured per request type (Admin → Request type → Assignment).
* Eligible = active members of the ticket's **current** department (stable id order).
* Cursor persisted in `DepartmentAssignmentRotations` (one row per department; migration `AddDepartmentAssignmentRotation`), so it survives restarts and is shared by all instances.
* Concurrency: on SQL Server the cursor row is created if missing and read `WITH (UPDLOCK, ROWLOCK)` inside the assignment transaction, so concurrent assignments serialize and never pick the same slot. `rowversion` is a second line of defence (surfaces as the existing concurrency conflict).
* No eligible employee (or assignment disabled for the department) → ticket stays in the department queue, shown as **"<Department> Queue (Unassigned)"**; the reason is audited.
* Every assignment/reassignment writes a `TicketAssignments` row and an `AutoAssign`/`Assign` audit entry (system actions have a null actor).

## Permissions
* **Resolve / Close:** the ticket's current assignee may always do both, regardless of role or department. Existing role rules (Department Head, CS layer, ADR-0024 override) still apply to everyone else. Read-only roles never qualify through ownership. All lifecycle validations and audit are unchanged.
* **Reopen:** unchanged rule — Call Center users (CS Agent/Supervisor/Manager) with ticket access; a non-blank reason is mandatory (service + controller); audit stores user, timestamp, previous status (`Closed`) and new status (`InProgress`) plus the reason. Inactivity-closure and reopen-window rules are untouched.
