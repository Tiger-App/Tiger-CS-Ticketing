# Accounting (ACC) department retirement - findings and deployment plan

**Status: PLAN ONLY. Nothing has been applied to any database. Every step that changes data goes through an existing audited admin service
(API endpoint), never raw SQL, and each needs explicit approval.** The one new code change is a safety guard (section 7); it is unit-tested
on fakes but was **not compiled in the authoring session** (no `dotnet` available) - CI on the PR is the first build.

## 1. Verified facts (from the production read-only run you supplied)

| Item | Value |
|---|---|
| ACC | DepartmentId 6, active. Collections: DepartmentId 2 (COL), active |
| ACC tickets | 97, **all TicketStatus = 6** |
| `TicketStatus.Closed` | **6** (`src/TigerCS.Domain/Modules/Ticketing/TicketStatus.cs`: Open 1, InProgress 2, PendingCustomer 3, PendingThirdParty 4 (legacy), Resolved 5, Closed 6) |
| Pending/any TicketApprovals naming ACC | none |
| Approval requirements targeting ACC | 5 active: IDs 1, 16, 17, 18, 19 (RequestTypes 1, 2, 3, 4, 10), all `BlocksWorkUntilApproved = 1` |
| ACC's own request type / category / workflow | RequestType 48 "General", Category 48 "General", Workflow 38 (only used by RT 48) |
| Genesys queue mappings / UserTransferDepartments for ACC | none |

## 2. Tickets (requirements 1-5)

All 97 are Closed, which `TicketAssignmentAppService.TransferAsync` and every other mutation refuse (`TicketClosed`). **No transfer, no bulk tool.**
They keep `CurrentDepartmentId = 6` and their history. The Resolved/open bucket in PR #274's script returns 0 rows and is therefore moot; the
script stays only as a re-runnable pre/post check (run it again immediately before deactivation, in case a ticket arrived since).
Nothing about Collections auto-assignment or Tower/Unit validation is touched.

## 3. Approvals and the Send Receipts SLA (requirement 8) - apply NEITHER until you choose

How it works today (code-verified):
* `TicketApprovalAppService.RequestApprovalAsync` only opens a cycle when the ticket's request type has an **active** requirement of that type.
* An approval decision writes a typed `ApprovalReceived` workflow event (`AccountingApproval` approve; `CustomerServiceApproval` writes `CustomerServiceApproved`).
* Seeded SLA for Collections **Send Receipts** is `ApprovalReceived` + 1 day (`WorkflowReferenceData`): **the SLA clock starts only when that event exists.**
  No approval ever decided means no event, so the clock never starts and the ticket can never breach.
* After ACC is deactivated nobody can decide an ACC-targeted approval (its members are removed), and (new guard) no new cycle can open against it.

Which of RT 1, 2, 3, 4, 10 is Send Receipts vs. a NOC flow should be read from the pre-check output (section 9, check C). NOC request types
(from `NewRequestTypesBusinessReview`) have a workflow step `Accounting Approval` of kind `WaitingForApproval`.

### Option A - remove the Accounting approval step completely
1. Per requirement (5 calls): `PUT api/admin/request-types/{rt}/approval-requirements/AccountingApproval` with the current target and `IsActive = false`
   (soft-disable; the row and every historical `TicketApprovals` snapshot stay).
2. **SLA change is mandatory for Send Receipts** (and any other RT of the five that has an `ApprovalReceived` SLA row): change the trigger so a clock
   exists at all - `PUT api/admin/request-types/{rt}/sla-policies/{priority}` with `Trigger = TicketCreated` and the same duration (1 day). The business
   must accept that the post-approval-day semantics ("1 day after Accounting approval") becomes "1 day after creation".
3. **Workflow**: published versions that contain an `Accounting Approval` step must be superseded by a new version without it (`POST api/admin/workflows/{id}/versions`,
   edit steps, `publish`) and the request type pointed at it. Published versions are history and are not edited. Tickets already past that step keep their recorded path.
4. Effect on in-flight tickets: those currently waiting at the approval step would be stuck - **pre-check D must return 0 open tickets on these request types**, else sequence per ticket.

### Option B - redirect the approval step to Collections
1. Per requirement (5 calls): same endpoint, `TargetKind = Department`, `TargetDepartmentId = 2`, keep `ApprovalType = AccountingApproval`,
   `BlocksWorkUntilApproved = true`, `IsActive = true`. (Optionally `TargetRoleName = "Department Head"` to narrow approvers.)
2. **No SLA change needed**: the approval type is unchanged, so the decision still emits `ApprovalReceived` and the Send Receipts clock starts as designed.
3. **Caveat to decide**: Collections *requests* Send Receipts and would now *approve* it (self-approval). Using a role target (e.g. `CS Supervisor`/`CS Manager`) avoids that; say which.
4. Workflows need no change. The label "Accounting Approval" stays in UI/history (it is the approval *type* name).
5. Default approver roles for a Department target are `Department Employee` / `Department Head` (`DepartmentTargetDefaultApproverRoles`); Collections members must hold one.

**Recommendation:** B with a role/narrowed target, because it keeps the SLA contract, workflow and history intact and needs the fewest changes.

## 4. RequestType 48 / Category 48 / Workflow 38 (requirements 6, 7)
* RT 48: `PATCH api/admin/request-types/48/activation {"isActive":false,"reason":"..."}` (audited). Pre-check: no ticket of RT 48 that is not Closed.
* Category 48: **no admin service exists for category deactivation** (only a read endpoint). It cannot be done through an audited service today. Options:
  (a) leave it - it is unreachable once ACC is inactive (`TicketCreationAppService` and routing reject categories whose department is inactive, and RT 48 inactive); or
  (b) approve a small audited admin action. I recommend (a).
* Workflow 38: `PATCH api/admin/workflows/38/activation {"isActive":false}` after RT 48 is deactivated. Inactive workflows cannot be newly selected; versions, steps and every ticket's recorded workflow reference remain.

## 5. Employees and memberships (requirements 9, 10)
Reported: one account with ACC primary, Collections 3 secondary, CS Manager secondary. `RemoveMemberAsync` refuses to remove a **primary** membership while
the user has others ("Make another department primary first"), so the order is:
1. Read-only check of each affected user: roles, active flag, all memberships (check E). Confirm the Accounting employee's intended home department.
2. Make a different department primary for the Accounting employee (existing admin membership API), then `DELETE api/admin/departments/6/members/{employeeId}`.
3. The "Collections 3" and "CS Manager" users: `DELETE api/admin/departments/6/members/{id}` - their other memberships are unaffected (secondary rows only).
4. **Visibility check before removing (requirement 10)**: the 97 tickets stay visible to roles with all-ticket scope (CS Manager, administrators) regardless of membership
   (`TicketVisibilityRule`). Department-scoped roles (Department Employee/Head, supervisors) see tickets via department membership, so after removal they will no longer see ACC's closed tickets;
   if anyone relies on that, keep a read-only role instead. Confirm with the user list from query E.

## 6. Preventing new work reaching ACC (requirement 11) - verified in code
| Path | Behaviour after deactivation |
|---|---|
| Transfer | `TransferAsync` -> `TargetDepartmentInactive` |
| Ticket creation / request-type routing / classification | reject inactive departments (`TicketCreationAppService`, `TicketRequestTypeRoutingService`, `DefaultClassificationService`) |
| Round Robin | skips inactive departments (`TicketAutoAssignmentService`) |
| Assign | targets must be active members of the ticket's current department; the 97 are Closed and immutable |
| New request types in ACC | rejected by `AdminRequestTypeAppService` validation |
| **Approval requests (GAP, now fixed)** | `RequestApprovalAsync` now refuses an approval whose requirement targets an inactive department (`ApprovalNotConfigured`) |
| **Approval requirement admin save (GAP, now fixed)** | an active requirement can no longer be saved against an inactive department |
| **Adding members to an inactive department (GAP, now fixed)** | `AddMemberAsync` returns Conflict |

## 7. Code changes in this PR (requirements 12, 14)
* `AdminDepartmentAppService.SetActivationAsync`: **deactivation guard.** Refused (Conflict, nothing changes) while the department has non-Closed tickets,
  active request types, active approval requirements targeting it, pending approvals targeting it, active Genesys queue mappings or user transfer-destination grants.
  Implemented by `IDepartmentDeactivationDependencyReader` (read-only, EF implementation `DepartmentDeactivationDependencyReader`). Reactivation is never blocked.
  **This means the five requirements and RT 48 must be handled first, which is the correct order.**
* `NewRequestTypesImporter`: treats an **inactive** Accounting department as absent, so a re-run never wires a new approval to ACC (and never reactivates it).
* Seeds: `WorkflowReferenceData`/`ProductionReferenceData` only ever *insert missing* departments and are skipped for populated tables; they never flip `IsActive`. Verified, no change needed.
  `ProductionSeeder` checks that code `ACC` *exists* (inactive still satisfies it) - correct for history.
* Tests added in `AdminDepartmentAppServiceTests` (guard blocks / allows, reactivation, inactive membership).

## 8. Reporting and dashboards (requirement 13)
* Dashboard department filter (`DashboardQueryRepository`): active-only list unless history is requested; inactive departments still resolve by name (`DashboardOptionRow.IsActive`).
* Team performance (`TeamPerformanceQueryRepository`): keeps a department visible when inactive but still having assignment history.
* Admin department list shows inactive ones under "All departments". Historical tickets keep `CurrentDepartmentId = 6` and still display "Accounting".
* To verify on UAT after deactivation: a filter by department "Accounting" on a historical range still returns the 97 Closed tickets.

## 9. Idempotent deployment plan

Each step is safe to re-run (it checks state first and the API calls are PUT/PATCH to a target state). Run on **UAT first**, then production after backup.
Read-only checks (A-F) live in `Preview_TigerCS_PROD_AccountingToCollectionsTransfer_ReadOnly.sql` (result sets 1-7 plus C-F).

**Pre-checks (all must pass):** (A) ACC and COL exist and are active; (B) ACC tickets with status <> 6 = 0 (re-run PR #274 script); (C) list of the five
requirements with request-type name and its SLA trigger; (D) non-Closed tickets on RT 1,2,3,4,10 sitting at an `Accounting Approval` step = 0 or handled;
(E) members of ACC with all their memberships and roles; (F) pending `TicketApprovals` targeting ACC = 0; database backup taken and restore point recorded.

**Order:**
1. Decide Option A/B; apply approval requirements (+ SLA/workflow for A) - five audited calls.
2. Deactivate RT 48, then Workflow 38.
3. Membership changes (section 5).
4. `PATCH api/admin/departments/6/activation {"isActive":false,"reason":"..."}` - the guard will refuse if any pre-check was missed.

**Post-checks:** ACC `IsActive = 0`; 97 tickets still `CurrentDepartmentId = 6`, status 6, row counts unchanged for Tickets/TicketAssignments/TicketApprovals/TicketWorkflowEvents/AuditEntries;
no active requirement/request type/queue mapping targets ACC; transfer to ACC returns `TargetDepartmentInactive`; creating a ticket for each of the five request types works and the approval flow behaves as the chosen option intends
(B: approval request opens against Collections; A: no approval offered and SLA clock starts at creation); audit shows one entry per change.

**Rollback (all through the same audited endpoints, nothing deleted):** reactivate department 6, RT 48 and Workflow 38 (`activation` endpoints, `isActive:true`; reactivation is not blocked by the guard);
restore each requirement (original target Department 6, `BlocksWorkUntilApproved = 1`, `IsActive = 1`) and, for Option A, the SLA trigger `ApprovalReceived` and the previous published workflow version;
re-add memberships with `POST api/admin/departments/6/members` using the memberships captured in pre-check E. The pre-check output is therefore the rollback manifest - save it.
