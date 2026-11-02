# User Permissions Management

Administration → Users → **Manage Permissions** (`/Admin/Users/{id}/Permissions`).

## How it fits the existing security model
No second security system. The feature adds one layer to ASP.NET Core Identity + the existing policies:

| Piece | Where |
|---|---|
| Permission catalog (module → action) | `TigerCS.Domain/Modules/IdentityAndAccess/Permissions/PermissionCatalog.cs` |
| Role baseline (what each fixed role carries) | `RolePermissionDefaults.cs` — restates today's role policies, widens nothing |
| Per-employee overrides (Grant / Deny) | table `UserPermissionOverrides` (migration `AddUserPermissionOverrides`, script `AddUserPermissionOverrides.sql`) |
| Effective = not denied AND (granted OR role) | `EffectivePermissions.cs` |
| Enforcement | `UserPermissionAuthorizationHandler` — a bare `IAuthorizationHandler`, same pattern as the System Administrator override; endpoints declare `[RequirePermission(...)]` |
| Management service | `AdminUserPermissionAppService` + `GET/PUT api/admin/users/{id}/permissions` |

Roles are still edited through the existing `PUT api/admin/users/{id}/roles` (also reachable on the Manage Permissions page).

## Enforcement semantics (backend)
* **Deny** — fails the request with 403 on every endpoint that declares that permission, regardless of the role policy and even for System Administrators (`Fail()` beats `Succeed()`).
* **Grant** — satisfies the *role* requirement of a role-gated policy (e.g. `ReportsRead`, `SystemAdministrator`). It never lifts the identity gate (active employee), service-identity confinement, the read-only write guard, or department scoping.
* No override / no attribute → behaviour is exactly as before. Existing users, roles and Genesys accounts are untouched; the migration only creates a table.
* Where an endpoint is `AuthenticatedStaff` and the application service decides by role/ownership/status (most ticket actions), a *grant* does not bypass those business rules; a *deny* still blocks the endpoint.
* The test `Every_controller_action_declares_a_permission_or_an_explicit_exemption` fails if a controller action has neither `[RequirePermission]` nor `[NoPermissionRequired("reason")]`.

## Ticket actions: Resolve, Close, Transfer
| Rule | Behaviour |
|---|---|
| **Deny** | Always blocks - endpoint (403) and service - for the assignee and the System Administrator too. |
| **Grant** | Replaces only the *role* requirement. Resolve: holder must belong to the ticket's department (and, as a CS Agent, be able to modify the ticket). Close / Transfer: holder must be able to modify the ticket (own department, or a role that sees all departments). Status, closed-ticket, department-workflow and concurrency rules run unchanged. |
| **Assignee** | The employee a ticket is assigned to can Resolve and Close it (not read-only roles) unless explicitly denied. |
| **Baselines** | Resolve = Department Employee/Head; Close = CS Agent/Supervisor/Manager; Transfer = CS Manager; Reopen = CS layer; Assign = CS Supervisor/Dept Head (own department) + CS Manager. A unit test fails if these drift from `TicketRoleSets`. |

Other permissions: **Deny** works on every endpoint that declares them; **Grant** only opens role-gated policies (Reports, Administration view/config, Customer-verification/Create). Services that decide by role/ownership/status (e.g. Assign, Reopen, Collections financial roles) do not honour grants yet.

## My Tickets
`GET /api/tickets?ownerEmployeeId=<me>` lists every ticket currently assigned to the caller. Department-scoped roles (Department Employee/Head) also see tickets assigned to them that sit outside their department memberships (e.g. after a Call Center transfer); CS Agents already did. Ownership adds only the caller's own tickets.

## UI
* `GET api/users/me` now also returns `effectivePermissions`, `grantedPermissions`, `deniedPermissions`.
* Navigation items, the `/Reports` and `/Admin` page gates and the Ticket Details actions (Resolve, Close, Transfer, Reopen, Assign, Change status) use the same decision as the Api: not denied AND (role rule OR grant) AND ticket-level authorization. If `/me` cannot be read the Web falls back to the role rules. Hiding is presentation; the Api enforces.

## Safeguards (enforced in the service/API, not the UI)
* Only callers with `Administration.Permissions.Manage` (System Administrator baseline) reach the endpoints.
* Nobody edits their own overrides.
* A permission can only be granted by someone who holds it.
* Administrator-only permissions (`Users.Create`, `Users.Edit`, `Roles.Assign`, `Permissions.Manage`) can be denied but never granted outside the System Administrator role (the handler also ignores such rows if inserted by hand).
* Those permissions cannot be denied to the last active System Administrator who holds them.
* Read-only roles (Chairman/CEO, Reporting User) cannot be granted mutating permissions.
* Configured integration (Genesys) service accounts cannot be given overrides.

## Audit
One `AuditEntries` row per changed permission, written in the same transaction: action `AdminSetUserPermission`, `ActorEmployeeId` = administrator, `EntityId` = employee, `BeforeValue`/`AfterValue` = `Permission=<key>;Override=<Inherit|Grant|Deny>;Effective=<bool>` (+ `;Reason=`), `OccurredAtUtc` = timestamp. Role changes keep their existing `AdminSetUserRoles` audit.

## Deploy
Apply EF migration `20261102065459_AddUserPermissionOverrides` or run `AddUserPermissionOverrides.sql` (idempotent, additive; creates `UserPermissionOverrides` with an FK to `Employees` and a unique index; touches no existing table). It has no dependency on the other pending migrations, but EF orders it last. Applied migrations must still match the build by identity (`validate --target production`).
