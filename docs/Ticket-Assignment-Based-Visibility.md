# Ticket visibility — assignment-based access for employees, Genesys intake queue for CS Agents

**Status: implemented and tested in a build/test environment. NOT merged, NOT deployed. Nothing was run against UAT or Production.**
Needs: `TigerCS.Api` deploy (no Web change, **no database migration, no schema or data change, no new permission**).

## 1. Audit — what the code did before

One rule family decides "may this caller see this ticket": `TicketVisibilityRule` (per ticket) and its set-based twin `TicketQueryFilters.VisibleTo` (lists, search, totals, dashboard), reached by every read path through `TicketQueryAppService`, and by every `api/tickets/{ticketId}` route through the `[RequireTicketAccess]` filter.

| Caller | Before | Gap vs the requirement |
|---|---|---|
| CS Manager, System Administrator, CS Supervisor, General Manager, Chairman/CEO | every ticket | none |
| CS Agent (human) | own assigned tickets, plus the responsible-agent follow-up and inactivity-closure exceptions; the unassigned *Take Ownership* pool only in an explicit "Available to take" view | the Genesys intake queue was not in the default Queue |
| Genesys integration account | Genesys-created tickets only | none |
| **Department Employee** | **every ticket of every department they belong to** (assigned or not, a colleague's or not), plus their own tickets elsewhere | **the defect**: department membership exposed colleagues' and unassigned tickets |
| Department Head | every ticket of their departments, plus their own elsewhere | see open decision 1 |
| Reporting User | their departments' tickets (read-only) | none (preserved) |
| `[RequireTicketAccess]` filter | scoped only CS Agents and the Genesys account; for employees it relied on each service's own check | not a uniform server-side net |

## 2. What changed (smallest change set)

1. **`TicketVisibilityRule.IsAssignmentOnlyEmployee`** — a caller holding Department Employee *and no other role*. Their department-level view is removed (`CanViewDepartmentAsync` → false); `GetTicketAccessAsync` therefore returns Full only for the current owner. `HoldsDepartmentRole` now means Department Head only (an Agent who is also an Employee no longer gains a department view from the Employee role).
2. **`TicketQueryAppService.ResolveVisibleDepartmentIdsAsync`** returns an empty scope for such an employee, so every department-scoped surface (customer directory/history, handoff list, dashboard filters) *fails closed*; the surfaces that know the ownership rule add the caller's own tickets (`VisibleTo(..., ownerEmployeeId)`, now also passed by customer directory/history).
3. **`[RequireTicketAccess]`** now also scopes assignment-only employees, so **every** `api/tickets/{ticketId}` route (read and write) is refused with 403 *before* model binding for a ticket that is not theirs.
4. **Handoff actions** (`AgentHandoffAppService.CanViewHandoffAsync`): an employee may act on the work item of a ticket assigned to them; no department-level view and no claim route. The agent claim route now also accepts membership of the ticket's *current* department (the documented "transfer first, then a member of the new department accepts" route) — needed because an Agent+Employee lost the department view that used to cover it.
5. **Genesys intake queue** — `TicketQueryFilters.GenesysIntake` = *claimable* (`AvailableToTake`: active, unowned, no current assignment, no pending approval, no active AI conversation) **and** originating interaction from Genesys. An eligible Call Center CS Agent (`TicketClaimPolicy.IsEligibleClaimerAsync`: the same test as Take Ownership) also sees these, read-only, in the **default Queue, counts and dashboard**. Intake = claimable, so what is listed is exactly what can be taken. Employees, ineligible agents, other roles and unrelated workflows' unassigned tickets are not given it.

Nothing else was touched: routing, classification, SLA, Round Robin, Genesys ingestion/answer assignment, permissions and the service-identity restrictions are unchanged.

## 3. Authorization matrix (ticket visibility)

| Role | Own assigned tickets (any dept) | Colleague's / other's ticket | Unassigned ticket | Notes |
|---|---|---|---|---|
| Department Employee | ✔ Full | ✘ (list, detail, every sub-endpoint → 403) | ✘ | assignment-only |
| CS Agent (eligible Call Center member) | ✔ Full | ✘ (except the preserved follow-up exceptions) | ✔ read-only: **Genesys-originated, claimable** (default Queue) and the explicit "Available to take" pool | take ownership to work it |
| CS Agent (not Call Center / not eligible) | ✔ Full | ✘ | ✘ | |
| Department Head | ✔ | ✔ own departments (**unchanged, see decision 1**) | ✔ own departments | |
| CS Supervisor, General Manager, Chairman/CEO (read-only) | ✔ all | ✔ all | ✔ all | unchanged |
| **CS Manager, System Administrator** | ✔ all | ✔ all | ✔ all | |
| Reporting User | n/a | own departments, read-only | own departments | unchanged |
| Genesys integration account | Genesys-created tickets | ✘ manual tickets | — | unchanged; confined to the integration surface |

Permission overrides (`Tickets.View/Edit/Assign/Close/Transfer…`) are *action* rights: a Grant never widens visibility (tested). CRM/customer-document permissions are separate and untouched; the customer directory/profile an employee can open is limited to customers of tickets assigned to them.

## 4. Affected endpoints

* Gated by `[RequireTicketAccess]` (now including assignment-only employees): all of `TicketsController` (`GET/POST api/tickets/{id}` — detail, history, activity-timeline, interactions, notes, customer-history, customer-profile, assignment-history, reviews, approvals, classification, assignment, transfer, ownership, status, resolution, close, reopen, workflow-events, reconciliation…), `TicketSlaController`, `PriorityDowngradeRequestsController`.
* List-shaped, scoped in the repository: `GET api/tickets` (Queue, My Tickets via `ownerEmployeeId`, Closed via `ticketStatus`, search, totals), `GET api/dashboard`, `GET api/dashboard/overview`.
* Customer lookups: customer directory list/profile, customer-history (`CustomerHistoryAppService`) — callers pass the caller as owner scope.
* `api/pending-customer-interactions/*` — per work item (`CanViewHandoffAsync`).
* Unchanged by design: `PATCH api/genesys/tickets/{id}` and the other `api/genesys/*` routes (service identity, own authorization).

## 5. Reassignment and transfer

Visibility is recomputed from the ticket's *current* owner on every request (no cache, no session state). Tested through the real endpoints: after a manager reassigns, the former owner immediately gets 403 on detail/notes/list and the new owner gets access; after a transfer **without** an assignee the owner keeps the ticket (My Tickets after a transfer) and the receiving department's staff do **not** gain access; after a transfer with an assignee, access moves to the assignee.

## 6. Unassigned tickets — secure claim mechanism

The mechanism already exists and is kept: **Take Ownership** (`POST api/tickets/{id}/ownership`, `TicketClaimPolicy`) — CS Agents who are active, unlocked Call Center members only, honouring `Tickets.Edit/Assign` Deny; one transaction, row-version + unique current-assignment guarded, audited. This change only makes the *Genesys-originated* part of the pool visible in the default Queue. Unassigned tickets are never shown to employees. `TicketClaim__Enabled=false` switches the whole feature (list scope, details flag, endpoint, intake queue) off together.

## 7. Test results

`dotnet test src/TigerCS.Tests` → **7,428 passed, 0 failed, 90 skipped** (the skipped tests are the SQL Server suites this environment cannot run; CI ran them and caught one assertion in `TakeOwnershipSqlServerTests` that encoded the old "personal list never holds the pool" rule, now updated to "holds only the Genesys-originated part").

New: `AssignmentBasedVisibilityEndpointsTests` (18, real HTTP pipeline) and `GenesysIntakeSqliteTests` (2, real SQL): own ticket; own ticket from another department; colleague's and unassigned tickets (list/detail/search/totals/department queue); direct access by id on 13 read and 9 write endpoints; dashboard counts; Closed tab; reassignment and transfer; CS Manager/System Administrator/Supervisor/GM see all; Department Head and Reporting User unchanged; a permission Grant never widens visibility; Genesys intake (eligible agent sees unassigned Genesys work in the default Queue, read-only; manual/other-department/active-AI/assigned/closed excluded; leaves the queue once assigned or taken; employees and ineligible agents never see it). Existing Genesys service-account (`TicketVisibilityByRoleTests`, `ReadOnlyRolesAndServiceIdentityEndpointTests`), Supervisor and reporting tests pass unchanged.
A mutation check (disabling `IsAssignmentOnlyEmployee`) makes 9 of the new tests fail.
**~25 existing tests asserted the old "employee sees the department" rule and were updated** to the new rule (they now assign the ticket to the employee and assert a colleague/unassigned ticket is refused) — please review that diff deliberately.

## 8. Workflow implications and decisions needed (nothing below was changed beyond the confirmed rule)

1. **Department Head** keeps their department-wide view. The approved matrix (Solution-Analysis §4.1: "All dept tickets") and Head's assign/approve/escalation duties depend on it; the brief says "own tickets unless an approved management rule applies". **Confirm.** If Head should be assignment-only, make `HoldsDepartmentRole` return false and exempt Head's assign/approval screens.
2. **Transfer without an assignee** now leaves the ticket invisible to the receiving department's employees until a Head/Supervisor/Manager assigns it (employees have no self-claim — unchanged). Department work is therefore pushed to people by assignment. If you want employees to pull from a department queue, that is a new, separately approved claim mechanism (mirror `TicketClaimPolicy`, department-scoped).
3. **Reopen Approval (BR-020)**: any department member could request it on a closed ticket. A non-assignee employee can no longer open the ticket, so only the assigned (closing) employee can request. Confirm, or approve a narrow exception.
4. **Pool breadth (your rule 7)**: the existing explicit "Available to take" pool shows eligible Call Center agents *every* department's unassigned human-handling tickets, including manual ones. It is limited to eligible CS Agents (never employees), so it was preserved; the **default Queue** gets only the Genesys-originated subset. Confirm whether the explicit pool should also be narrowed to Genesys-originated work.
5. **CS Agent "own assigned only"**: the existing responsible-agent follow-up/closure and inactivity-closure exceptions are preserved (CS Agents close tickets after department work). Confirm they stay.
6. A Department Employee's Close/Transfer/Resolve **permission Grants** are inert on colleagues' tickets (they still work on the employee's own tickets).
7. Employees no longer see the department work-item list (pending customer interactions); they can still act on the item of a ticket assigned to them.
8. `AuthorizationOverride` (System Administrator) and the Genesys service identity paths are unchanged.

## 9. Rollout

Deploy API only; verify in UAT with one Department Employee, one colleague, one CS Manager and one eligible Call Center agent (see tests for the exact scenarios); roll back by redeploying the previous API build (no data change).
