# Staff provisioning (7 users) and department coverage

**Status: implemented and unit-tested. NOT deployed. No UAT or Production data was read or changed.** Production data changes go through the dry-run/apply tool below, with approval.
No database migration, no schema change, no new role, no new permission, no change to login/authentication.

## 1. Existing structure (reviewed before changing anything)

| Concern | Where |
|---|---|
| Identity account | `AspNetUsers` (`ApplicationUser`), created via `IUserAccountManager.CreateAccountAsync` (Identity validates the password policy) |
| Staff profile | `Employees` (`EmployeeId` = `AspNetUsers.Id`; `DeactivatedAtUtc` = inactive) |
| Roles | `AspNetRoles` / `AspNetUserRoles`; fixed set in `Roles.cs` (9 roles) |
| Department | `Departments` (`Code` is the stable key) |
| Membership | `UserDepartmentAssignments` (many-to-many, one primary) |
| User Management page | Administration → Users, `POST api/admin/users` → `AdminUserAppService.CreateAsync` (account + employee + role + primary department + audit) |
| Round Robin | `TicketAutoAssignmentService` + `DepartmentAssignmentRotations` (row-locked cursor) |

## 2. Decisions and findings

* **Confirmed:** "CUSTOMERSERVICES" = department `CS` "Customer Service" (not `LCS`).
* **Customer Services → `CS Agent`** (as instructed).
* **Registration → `Department Employee`.** It is the existing non-administrative role that works and resolves tickets assigned to the employee's department. It has no assign/close/transfer/admin authority and is in the Round Robin eligible set for every department except the Call Center. No role was invented; no administrative privilege is granted. **Confirmed.**
* **The Call Center rotation (decision: leave unchanged).** Genesys/phone and manual Call Center intake are dealt by the *shared CS Agent rotation*, whose pool is the members of the department with code `CC`, CS Agents only. The three new CS Agents are placed in `CS` (as requested), so they will **not** receive Call Center rotation work unless they are also made members of `CC`. **Confirmed: the new CS users are not added to `CC`; Genesys/Call Center routing is unchanged.**
* **No hardcoded passwords.** Passwords are generated at run time (CSPRNG, 20 chars, policy-compliant), passed to Identity, and written only to an owner-only file you name. They are never printed or logged. Each user can be reset from User Management as usual.
* **Defect fixed:** manual reassignment (`AssignAsync`) and transfer-with-assignee (`TransferAsync`) only checked department membership, so a *deactivated* employee could be named as assignee. Both now require an *active* member (Collections already did).

## 3. How to provision (idempotent)

`TigerCS.ProductionSeeder provision-staff`, using the same safety rules as the existing seeder (connection string from the environment only, server/database verified against the server itself, exact migration match).

```bash
# 1. Dry run: read-only. Prints the plan (would-create / already-exists / skipped) and the department coverage.
dotnet run --project src/TigerCS.ProductionSeeder -- provision-staff --target production

# 2. Apply (only after approval and a backup). One transaction; rolled back on any failure.
dotnet run --project src/TigerCS.ProductionSeeder -- provision-staff --target production --apply \
  --confirm-production TGAESHJDBP02/TigerCS_Ticketing_Prod --backup-confirmed \
  --actor-email <active System Administrator email> --credentials-out ./one-time-credentials.txt
```

Target check: the connection string's Data Source must be `TGAESHJDBP02` or its approved IP `10.10.10.124` (exact; any other host, FQDN or IP is refused), and the server itself must also report `@@SERVERNAME` = `TGAESHJDBP02` and `DB_NAME()` = the expected database. A refusal prints the reported values with invisible characters escaped.

Behaviour: users are created through `AdminUserAppService.CreateAsync` (same code as User Management, audited under the named administrator). Matching is by email/user name, case-insensitive, so a re-run creates nothing. An existing account is **never modified**: if it exists with another role/department, is deactivated, or is incomplete, it is reported as *Skipped* for a person to review (exit code 2). Departments are found by code; a missing or inactive one skips the user. Nothing is created in `Departments` or `AspNetRoles`.

| Full name | Email | Department | Role |
|---|---|---|---|
| Zakarya Azzouz | customerservice1@tigergroup.ae | Customer Service (CS) | CS Agent |
| Mohammad Al Kazzaz | Customerservice3@tigergroup.ae | Customer Service (CS) | CS Agent |
| Assel Azhibayeva | customerservice2@tigergroup.ae | Customer Service (CS) | CS Agent |
| Nada Emad | n.eid@tigergroup.ae | Registration (REG) | Department Employee |
| Gyl Quintana | Gyl.Quintana@tigergroup.ae | Registration (REG) | Department Employee |
| Marielle De Lara | Marielle.Bulanadi@tigergroup.ae | Registration (REG) | Department Employee |
| Nickole Gayle | Nickole.Dejesus@tigergroup.ae | Registration (REG) | Department Employee |

Created / existing / skipped counts for a real database are **not known yet**: the dry run has not been executed against any database (no database access from this session). The unit tests prove the behaviour on fakes.

## 4. Department coverage

`GET api/admin/departments/coverage` (System Administrator, read-only) and the dry run's final section list every **active** department with: active employees, employees *eligible for automatic assignment* (same test as Round Robin: not a service account, not locked out, an operational role — CS Agents only in the Call Center — not read-only), and a status:

* `Staffed`
* `Unstaffed` — no active employee: needs an employee assignment
* `NoAssignableEmployee` — has members but none can receive automatic assignment

Computed from the existing relationships, not stored (no schema change); nothing is auto-assigned or invented. The existing department list/detail pages already show member counts and members. **I did not render the new coverage in the Web UI** — the endpoint and tool output are the source of truth for now. The list of departments needing staff is whatever the production dry run prints with a status other than `Staffed`.

## 5. Ticket permissions and assignment: what already exists (inspected, not rebuilt)

Most of the second brief is already implemented; the documents `Ticket-Assignment-Based-Visibility.md`, `Auto-Assignment-Audit-Final.md` and `Call-Center-Routing-RoundRobin-Permissions.md` describe it.

| Requirement | Existing mechanism |
|---|---|
| Employee sees only own tickets (dashboard, list, search, details, API, export) | `TicketVisibilityRule` / `TicketQueryFilters.VisibleTo` and `[RequireTicketAccess]` on every `api/tickets/{id}` route (backend). `IsAssignmentOnlyEmployee` for Department Employee. |
| Admin/Supervisor keep management view | unchanged (all-ticket roles) |
| Department on every ticket | `Tickets.CurrentDepartmentId` is a required column |
| Round Robin, eligible active employees only, own department only, concurrency-safe | `TicketAutoAssignmentService.AssignRoundRobinAsync`: active members only, `FilterEligibleAsync`, cursor row-locked in the transaction |
| Genesys routing | unchanged: answering agent owns the ticket; otherwise shared CS Agent rotation |
| No eligible employee | ticket stays unassigned in the department queue, audit + `ManualAssignmentRequired` event; visible to supervisors/managers/admin |
| Employees cannot reassign | Assign = CS Supervisor/Department Head (own dept) + CS Manager (+ administrator override); `AssignAsync` |
| Reassignment audit + department rule | `Assign` audit entry, `TicketAssignments` history, transfer-reason required, target must be a member of the ticket's department |

**Not done / needs your decision** (changing these would alter live Genesys behaviour, so I did not):
1. Department Head keeps a department-wide view (open decision 1 in `Ticket-Assignment-Based-Visibility.md`).
2. **Export:** there is no ticket export endpoint in the code. The only exports are the Collections dispatch/campaign CSV/workbook (permission `Collections.View`, not granted to Department Employee) and `api/reports/team-performance*` (CS Manager / General Manager / Chairman only; Department Employee gets 403, tested). Neither returns an employee's ticket list. If a ticket export is added later it must go through `TicketQueryAppService`.
2b. **Transfer behaviour (needs your decision):** by an existing, documented business rule a department transfer *keeps the CS Agent as owner* (`preserveOwner`); the department rule / Round Robin runs only for a ticket with *no* owner, and the fallback pool is the shared CS Agent rotation. So a transferred ticket is not automatically handed to a member of the receiving department when a CS Agent already owns it, and with assignment-only visibility the receiving department's employees do not see it until a Supervisor/Manager assigns it (`TransferTicketRequestDto.AssignToEmployeeId` does that atomically). I did not change this: it contradicts the Genesys/Call Center ownership model you asked to keep. When the ticket has no owner and the request type's rule is Round Robin, only active, eligible members of the *target* department are chosen (tested).
3. "Check coverage before activating automatic assignment" is reported (tool + endpoint) but does not *block* activation of a rule or department; blocking would change admin behaviour. Say if you want a guard.
4. A persisted "Unstaffed" flag on departments (needs a column) was not added; the status is derived.
5. The Registration and other departments' *request-type assignment rules* were not inspected per department; rules and Round Robin are unchanged.

## 6. Verification

Run in this session: new `StaffProvisioningTests` (16 cases: roster, dry-run is read-only, create-once/idempotent, case-insensitive match, existing user untouched, deactivated user skipped, missing department creates nothing, duplicate roster email, coverage statuses, Call Center CS-Agent-only counting, locked-out accounts, password generator, CLI safety); `AssignAsync_DeactivatedDepartmentMember_NeverReceivesTheTicket`; `RoundRobin_DeactivatedEmployeeNeverReceivesATicket`. Existing tests that cover the brief's scenarios: `AssignmentBasedVisibilityEndpointsTests` (A cannot see B's, A sees own), `RoundRobinAndOwnerPermissionTests` (fair rotation, no eligible employee, no other-department), Genesys suites (`GenesysAnsweringAgentRoutingTests`, `CallCenterFirstRoutingTests`), SLA/history/notification suites. See the PR for the full test result.

Rollback: redeploy the previous API; users created by the tool are deactivated in User Management (never deleted).
