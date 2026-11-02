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
Apply EF migration `20261102065459_AddUserPermissionOverrides` or run `AddUserPermissionOverrides.sql` (idempotent, additive).
