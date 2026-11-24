# Employee Reviews

Customers rate a Customer Service employee (1–5 stars, optional comment) on a **review tablet**. Independent of the ticket satisfaction
review (`TicketReviews`): nothing here touches tickets, SLA, closing or the existing dashboard metrics.

## Pieces

| Piece | Where | Who |
|---|---|---|
| Customer tablet | `/EmployeeReviews/Tablet` | nobody logs in; the tablet's own credential is its identity |
| Tablet activation | `/EmployeeReviews/Activate` | the person setting up the tablet, with the administrator's code |
| Tablet management | Administration → **Review Tablets** (`/Admin/EmployeeReviewDevices`) | System Administrator |
| Reviews dashboard | **Employee Reviews** (`/EmployeeReviews`, drill-down `/EmployeeReviews/Employee/{id}`) | `EmployeeReviews.View` |

## Register and activate a tablet

1. Administration → Review Tablets → **Register a tablet**: a name (e.g. “Reception desk 1”) and an existing, active Customer Service employee
   (a member of the *Customer Service* department, or holder of the *CS Agent* / *CS Supervisor* role — read from the existing `Employees` tables;
   nothing is hard-coded and no employee is created).
2. The page shows the **activation code and an activation link once**. Only a SHA-256 hash is stored; a lost code cannot be recovered — use
   **New credential**.
3. On the tablet open the activation link (the code travels in the `#fragment`, which no server or log sees) or open `/EmployeeReviews/Activate` and
   type the code. The tablet stores it in an HttpOnly, SameSite=Strict cookie and goes to `/EmployeeReviews/Tablet`. Put the browser in
   kiosk / full-screen mode on that URL.

Other actions per tablet: **Edit** (rename, assign another employee), **Deactivate / Activate**, **New credential** (old token stops at once),
**Revoke** (removes the credential and switches the tablet off; bring it back with *Issue credential*). Every change is audited (no tokens in the audit).

Reassigning a tablet only changes who **future** reviews go to: each review stores the employee it was given for.

## Customer experience

Employee name, Tiger logo, “How satisfied are you with our employee's service?”, five large stars, optional comment (1000 characters), Submit.
Arabic / English toggle (right-to-left layout, instant, no network call). After Submit a thank-you shows for 5 seconds and the screen resets
(also reset after 90 s of inactivity). A review is saved in the tablet's own storage **before** it is sent and is re-sent (same `submissionId`) until
the server confirms, so a network failure never loses it.

## API

| Route | Auth | Purpose |
|---|---|---|
| `POST /api/public/employee-reviews/session` | `X-Device-Token` | tablet session: status + tablet / employee **names** (no ids) |
| `POST /api/public/employee-reviews/submit` | `X-Device-Token` | `{ submissionId, rating, comment }` → 200 submitted / already recorded, 400, 401 (bad token), 403 (inactive), 429 |
| `GET/POST /api/admin/employee-review-devices`, `GET …/eligible-employees` | System Administrator | list, register, eligible employees |
| `PUT …/{id}`, `POST …/{id}/activation`, `POST` / `DELETE …/{id}/credential` | System Administrator | edit/assign, on/off, regenerate, revoke |
| `GET /api/employee-reviews`, `GET /api/employee-reviews/employees/{id}` | `EmployeeReviews.View` | dashboard, drill-down |

The employee is **never** taken from the request: it is read from the authenticated tablet's current assignment. Rate limit: 60 requests / minute per
device token (per caller address when none), far above real use.

## Visibility

New permission `EmployeeReviews.View` (module “Employee Reviews”; enforced on its own terms, an explicit deny always refuses). Baseline: CS Agent,
CS Supervisor, CS Manager, General Manager, Chairman/CEO, System Administrator (all permissions). Scope is always resolved from the caller's roles and department
memberships, never from the request:

* System Administrator, CS Manager, General Manager, Chairman/CEO — every employee;
* CS Supervisor / Department Head (when granted the permission) — employees who belong to one of their departments, plus themselves;
* everyone else — only their own reviews.

Dates are UAE calendar days (default: last 30 days); everything is stored in UTC and shown in UAE time.

## Database

Migration `20261124071552_AddEmployeeReviews`: `EmployeeReviewDevices` and `EmployeeReviews` (foreign keys to the existing `Employees`; unique
`SubmissionId`; unique filtered `TokenHash`). Production: `Deploy_TigerCS_PROD_AddEmployeeReviews.sql` (guarded, `@DryRun = 1` by default,
rollback in its header); EF-generated equivalent `AddEmployeeReviews.sql`; listed in `Verify_TigerCS_PROD_AllMigrations_ReadOnly.sql`. Apply the script
**before** deploying the new Api and Web builds.
