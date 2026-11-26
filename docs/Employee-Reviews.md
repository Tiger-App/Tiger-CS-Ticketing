# Employee Reviews

Customers rate a Customer Service employee (1–5 stars, optional comment) on a **review tablet**. Independent of the ticket satisfaction
review (`TicketReviews`): nothing here touches tickets, SLA, closing or the existing dashboard metrics.

## Pieces

| Piece | Where | Who |
|---|---|---|
| Customer tablet | `/EmployeeReviews/Tablet` | nobody logs in; the tablet's own credential is its identity |
| Tablet activation | `/EmployeeReviews/Activate` | the person setting up the tablet, with the administrator's 8-character code |
| Tablet management | Administration → **Review Tablets** (`/Admin/EmployeeReviewDevices`) | System Administrator |
| Reviews dashboard | **Employee Reviews** (`/EmployeeReviews`, drill-down `/EmployeeReviews/Employee/{id}`) | `EmployeeReviews.View` |

## Register and activate a tablet

1. Administration → Review Tablets → **Register a tablet**: a name (e.g. “Reception desk 1”) and an existing, active Customer Service employee
   (a member of the *Customer Service* department, or holder of the *CS Agent* / *CS Supervisor* role — read from the existing `Employees` tables;
   nothing is hard-coded and no employee is created).
2. The page shows an **8-character activation code and an activation link once**. The code is valid for **24 hours** and **one use**. A lost or
   expired code cannot be recovered — use **New activation code** (the table shows “Awaiting activation” / “Activation code expired”).
3. On the tablet open the activation link (the code travels in the `#fragment`, which no server or log sees) or open `/EmployeeReviews/Activate` and
   type the code. The server checks it and answers with the tablet's **own device token**; only that token is stored, in an HttpOnly,
   SameSite=Strict cookie, and the tablet goes to `/EmployeeReviews/Tablet`. Put the browser in kiosk / full-screen mode on that URL.

Other actions per tablet: **Edit** (rename, assign another employee), **Deactivate / Activate**, **Issue activation code** (for a tablet with no
credential: never activated, code expired, revoked), **Re-activate…** (for a tablet that is already activated and collecting reviews), **Revoke**
(removes the credential and any pending code and switches the tablet off). Every change is audited (no codes or tokens in the audit).

**Re-activating a working tablet is never silent.** Issuing a new code for a tablet that already has a credential switches that credential off
immediately — the tablet collects nothing until the new code is entered on it — so the page offers it only as **Re-activate…**, behind a warning and a
required confirmation tick, and the Api refuses (`409`) unless the request carries `confirmReplaceActiveCredential: true`; the domain refuses too.
A refused request changes nothing and writes no audit row; a confirmed one is audited as `AdminReactivateReviewDevice`. Issuing a code for a tablet
with no credential switches nothing off, so it needs no confirmation.

Reassigning a tablet only changes who **future** reviews go to: each review stores the employee it was given for.

## Activation code and device credential

Two different secrets, never the same value:

| | Activation code | Device token |
|---|---|---|
| Purpose | one-time hand-over from administrator to the person at the tablet | the tablet's permanent credential (`X-Device-Token`) |
| Shape | exactly **8** characters: upper-case letters and digits, **no hyphen** | `tgr-` + 43 base64url characters (256 bits) — **unchanged, not shortened** |
| Alphabet | `ACDEFGHJKMNPQRTUVWXY34679` — leaves out `0 O 1 I L 2 Z 5 S 8 B` (25 symbols, 25^8 ≈ 1.5 × 10^11) | |
| Created | by the Api (`RandomNumberGenerator`, one unbiased draw per character) | by the Api at the moment the code is redeemed |
| Lifetime | 24 hours, single use | until an administrator replaces or revokes it |
| Stored as | salted PBKDF2-SHA256 (100 000 iterations, 16-byte salt) hash, salt and expiry | SHA-256 hash |
| Shown to | the administrator, once | nobody — it goes straight from the Api to the tablet's cookie |

Typed codes are trimmed and upper-cased; a hyphen, an inner space or any other character makes the code invalid (nothing is quietly repaired). The
Web page and the Api both require exactly 8 alphabet characters; the Api is the authority.

**Single use.** Redeeming a code stores the new token hash and erases the code in one conditional write (the code's hash is a concurrency token), so of
two simultaneous requests with the same code exactly one succeeds. Wrong, malformed, expired, used and replaced codes all get the same `401`.

**Expiry.** A code is never redeemable at or after its expiry (`now < expiresAt`), even while its hash still sits in the row.

**Attempt limits (server side, in the database, so they survive restarts and hold across Api instances).** Each activation request is counted
*before* its code is looked at: per client address (10 attempts per 15 minutes) and across all clients (100 attempts per hour). Over either limit the
code is not checked at all — even the correct one gets `429` — and a refused attempt does not use the code up. Malformed input counts like any guess.
A successful activation clears the client's count and gives the shared budget back, so several tablets set up one after another from the same office
address (with typos along the way) never lock each other out; only ten failures in a row from one address do, for the rest of that 15-minute window. Counters are keyed by a hash of the address (no address is
stored). In front of this sits an ASP.NET rate limiter (30 requests / minute per client address) — a flood guard, deliberately above what an office needs. TigerCS.Web forwards the visitor's address in
`X-Client-Address`; the shared limit is the backstop if that is ever spoofed or missing. Consequence: someone who deliberately burns the shared
budget can delay *new* activations for up to an hour; tablets that are already activated are never affected.

**Existing tablets.** The migration only adds nullable columns and one table. A tablet activated before this change keeps its token (and its cookie),
its reviews and its employee; nothing is re-issued. A new code is only needed to set up a new tablet or to re-provision one.

## Customer experience

Employee name, Tiger logo, “How satisfied are you with our employee's service?”, five large stars, optional comment (1000 characters), Submit.
Arabic / English toggle (right-to-left layout, instant, no network call). After Submit a thank-you shows for 5 seconds and the screen resets
(also reset after 90 s of inactivity). A review is saved in the tablet's own storage **before** it is sent and is re-sent (same `submissionId`) until
the server confirms, so a network failure never loses it.

## API

| Route | Auth | Purpose |
|---|---|---|
| `POST /api/public/employee-reviews/activate` | none (the code is the credential) | `{ code }` → 200 `{ outcome: Activated, deviceToken }` (once, `no-store`), 401 (unusable code), 403 (tablet off), 429 (too many attempts) |
| `POST /api/public/employee-reviews/session` | `X-Device-Token` | tablet session: status + tablet / employee **names** (no ids) |
| `POST /api/public/employee-reviews/submit` | `X-Device-Token` | `{ submissionId, rating, comment }` → 200 submitted / already recorded, 400, 401 (bad token), 403 (inactive), 429 |
| `GET/POST /api/admin/employee-review-devices`, `GET …/eligible-employees` | System Administrator | list, register (returns the activation code once), eligible employees |
| `PUT …/{id}`, `POST …/{id}/activation`, `POST` / `DELETE …/{id}/credential` | System Administrator | edit/assign, on/off, issue a new activation code, revoke |
| `GET /api/employee-reviews`, `GET /api/employee-reviews/employees/{id}` | `EmployeeReviews.View` | dashboard, drill-down |

The employee is **never** taken from the request: it is read from the authenticated tablet's current assignment. Rate limit: 60 requests / minute per
device token (per caller address when none), far above real use; activation has its own, much stricter limits (above).

## Visibility

New permission `EmployeeReviews.View` (module “Employee Reviews”; enforced on its own terms, an explicit deny always refuses). Baseline: CS Agent,
CS Supervisor, CS Manager, General Manager, Chairman/CEO, System Administrator (all permissions). Scope is always resolved from the caller's roles and department
memberships, never from the request:

* System Administrator, CS Manager, General Manager, Chairman/CEO — every employee;
* CS Supervisor / Department Head (when granted the permission) — employees who belong to one of their departments, plus themselves;
* everyone else — only their own reviews.

Dates are UAE calendar days (default: last 30 days); everything is stored in UTC and shown in UAE time.

## Database

1. `20261124071552_AddEmployeeReviews`: `EmployeeReviewDevices` and `EmployeeReviews` (foreign keys to the existing `Employees`; unique `SubmissionId`;
   unique filtered `TokenHash`). Production: `Deploy_TigerCS_PROD_AddEmployeeReviews.sql` (guarded, `@DryRun = 1` by default, rollback in its header);
   EF-generated equivalent `AddEmployeeReviews.sql`; listed in `Verify_TigerCS_PROD_AllMigrations_ReadOnly.sql`.
2. `20261126154428_AddEmployeeReviewActivationCodes` (additive; every new column is nullable, so existing rows and tablets are untouched):
   `ActivationCodeHash`, `ActivationCodeSalt`, `ActivationCodeExpiresAtUtc` on `EmployeeReviewDevices`, and the `EmployeeReviewActivationThrottles`
   counter table. Production: `Deploy_TigerCS_PROD_AddEmployeeReviewActivationCodes.sql` (guarded, `@DryRun = 1` by default, checks that activated tablets are unchanged, rollback in
   its header); EF-generated equivalent `AddEmployeeReviewActivationCodes.sql`; listed in `Verify_TigerCS_PROD_AllMigrations_ReadOnly.sql`.

Apply each script **before** deploying the Api and Web builds that need it. Rollback of (2) is safe once the new builds are stopped: drop the three
columns and the counter table and delete its `__EFMigrationsHistory` row; tablets that already hold a device token keep working on the old builds.
