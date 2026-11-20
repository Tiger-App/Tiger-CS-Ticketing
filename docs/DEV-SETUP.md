# Tiger CS Ticketing — Local Development Setup (Identity and Access)

Scope: this covers the Identity and Access increment only (authentication,
roles, departments, department assignments). No ticketing/SLA/Genesys setup
is described here — those arrive with their own increments.

---

## 1. Prerequisites

- .NET 10 SDK — pinned by `global.json` (`10.0.111`).
- A local SQL Server instance. Docker is the easiest route and is what's
  assumed below; a native local SQL Server install works identically as
  long as the connection string in step 2 matches it.
- The `dotnet-ef` CLI tool: `dotnet tool install --global dotnet-ef --version 10.0.11`
  (add `/root/.dotnet/tools` or `~/.dotnet/tools` to `PATH` if prompted).

## 2. Start a local SQL Server

```bash
docker run -e "ACCEPT_EULA=Y" -e "MSSQL_SA_PASSWORD=YourStrong!Passw0rd" \
  -p 1433:1433 --name tigercs-sql -d mcr.microsoft.com/mssql/server:2022-latest
```

`YourStrong!Passw0rd` is Microsoft's own widely-published example password
for local SQL Server containers (used verbatim in Microsoft's official
`mssql-server` Docker documentation) — not a real secret, and this
connection targets `localhost` only. It's the value in
`TigerCsDbContextFactory`'s design-time fallback, which is what
`dotnet ef` uses when `TIGERCS_DESIGN_TIME_CONNECTION` is unset.

The committed `appsettings*.json` do **not** carry it, and do not point at
this container: both `src/TigerCS.Api/appsettings.json` and
`appsettings.Development.json` ship a shared-server connection string whose
`Password=` is empty, so neither can connect as it stands. Supply the whole
connection string yourself — user-secrets locally (step 3), or
`ConnectionStrings__TigerCsDatabase` in UAT/Production — rather than editing
the committed file. To point at the Docker container above, set the
connection string in user-secrets to
`Server=localhost,1433;Database=TigerCsTicketing_Dev;User Id=sa;Password=<the MSSQL_SA_PASSWORD from the command above>;TrustServerCertificate=True;`.

## 3. Configure secrets (never committed)

Two values must **not** come from `appsettings*.json`: the JWT signing key,
and (optionally) a development administrator's credentials. Both are read
from ASP.NET Core user-secrets or environment variables.

From `src/TigerCS.Api`:

```bash
dotnet user-secrets init   # only needed once; sets <UserSecretsId> (already set)

dotnet user-secrets set "Jwt:SigningKey" "a-random-string-at-least-32-characters-long"
# ^ must be at least 32 bytes (256 bits) — the app now validates this at
#   startup and refuses to run on a missing or too-short key, rather than
#   accepting a default/fallback signing secret.

# Optional: creates a System Administrator on first run. Omit these two and
# no development administrator is created — roles/departments still seed.
dotnet user-secrets set "DevAdmin:Username" "admin@tigercs.local"
dotnet user-secrets set "DevAdmin:Password" "Choose-Your-Own-Strong-Password-1!"
```

Equivalent environment variables (e.g. for CI or a container), using the
`__` double-underscore separator ASP.NET Core configuration expects:

```bash
export Jwt__SigningKey="a-random-string-at-least-32-characters-long"
export DevAdmin__Username="admin@tigercs.local"
export DevAdmin__Password="Choose-Your-Own-Strong-Password-1!"
```

The password policy enforced at signup (Security-Architecture.md §1's
placeholder, made concrete for this increment): minimum 8 characters, at
least one digit, one lowercase, one uppercase, one non-alphanumeric
character, and at least 4 unique characters — the same rule applies to the
`DevAdmin:Password` value above, to an administrator's password reset
(`POST /api/admin/users/{employeeId}/password`) and to a user's own change
(`POST /api/auth/change-password`); both answer `422` listing Identity's
reasons when the new password falls short. Lockout: 5 failed attempts locks
the account for 15 minutes (Security-Architecture.md §13); an
administrator's reset lifts the lockout as well.

These are **pilot defaults, not hardcoded final values** — override any of
them via configuration without a code change:

```bash
dotnet user-secrets set "Identity:Password:RequiredLength" "10"
dotnet user-secrets set "Identity:Lockout:MaxFailedAccessAttempts" "3"
```

Only the keys you actually set are overridden; anything you don't set keeps
the pilot default. The app validates these at startup regardless of source
(`Identity:Password:RequiredLength` must be ≥ 8, `Identity:Lockout:MaxFailedAccessAttempts`
must be between 1 and 10, `Identity:Lockout:DefaultLockoutTimeSpan` must be
at least 1 minute) and refuses to start if a configured value violates
that floor.

**No production deployment is authorized at this stage of the pilot** — this
is enforced through release governance and documentation
([ADR-0022's "Pilot-Stage Production Restriction"](architecture/adr/0022-deployment-strategy.md),
`MVP-Implementation-Backlog.md` §0), **not** by the application refusing to
start under `ASPNETCORE_ENVIRONMENT=Production`. The app runs the same way
regardless of environment name, given valid configuration — whoever
controls the deployment pipeline is responsible for not pointing it at a
production environment until that's actually authorized.

### Secrets inventory — what is never committed

Every committed `appsettings*.json` (in `src/`)
carries **empty placeholders** for these values. Each real environment must
supply them through user-secrets (Development) or environment variables
(UAT / Production, `__` separator):

| Configuration key | Environment variable | Purpose |
|---|---|---|
| `ConnectionStrings:TigerCsDatabase` | `ConnectionStrings__TigerCsDatabase` | SQL Server connection string. Both shipped Api `appsettings*.json` carry `Password=` empty — supply the whole connection string here (§2). Development does **not** point at the local Docker SQL Server; set it to that yourself if you want it |
| `Jwt:SigningKey` | `Jwt__SigningKey` | JWT signing key (§3) |
| `Crm:SecretKey` | `Crm__SecretKey` | CRM Buyer Lookup / customer-documents shared secret (§3a) |
| `CrmDocuments:OtpCodePepper` | `CrmDocuments__OtpCodePepper` | Secret mixed into stored OTP code hashes (chatbot document verification) — set a long random value; never commit it |
| `PactApi:ApiKey` | `PactApi__ApiKey` | PACT customer lookup API key (§3b) |
| `EmailNotifications:Password` | `EmailNotifications__Password` | Microsoft 365 SMTP mailbox password (§3d) |
| `DevAdmin:Password` | `DevAdmin__Password` | Development-only seeded administrator (§3) |

`publish/` is **not** committed any more (it is git-ignored; a stale tracked copy was removed in PR #292).
Build deployable output only with `tools/deploy/Publish-TigerCS.ps1` / `tools/build-release.sh` from a clean, approved commit,
and never put the values above in any file inside it. `tools/check-repo-hygiene.sh` (CI) fails the build if build output, binaries or a non-empty secret setting is tracked.

## 3a. Configure CRM Buyer Lookup (`Crm:BaseUrl` / `Crm:SecretKey`)

The CRM Buyer Lookup integration (`CrmBuyerHttpGateway`, `GET /api/crm/buyers`)
calls the legacy CRM MVC 4.7 application's own
`GET /TicketingSystem/GetBuyerByPhone` endpoint directly — it is a real
integration from day one and is not governed by `Crm:Provider`.

`Crm:Provider` governs the other two CRM ports — `ICrmGateway` (unit/contact
lookup) and `ICrmCustomerLookupGateway` (the CRM leg of phone-based customer
search) — for which Tiger CRM publishes no endpoint yet:

- **`Http`** — the standard for Development, UAT and Production
  (`appsettings.json`, `appsettings.Development.json`). Until the CRM team
  publishes those endpoints the two ports fail closed
  (`UnimplementedCrmHttpGateway`): `GET /api/crm/units…` answers 502
  `crm-unavailable` and the customer lookup's Crm source reports `Failed`.
  These two ports never serve fixture data in a real environment.
- **`Mock`** — `MockCrmGateway` fixture data. The automated test host pins it
  (`TigerCsApiFactory`); `CrmGatewaySafety` refuses it outside
  Development/Testing. To use the fixtures locally instead of the real CRM:
  `dotnet user-secrets set "Crm:Provider" "Mock"` from `src/TigerCS.Api`.

Two configuration values under the same `Crm` section:

- **`Crm:BaseUrl`** — the CRM application's base URL. Not a secret: safe to
  commit per environment in `appsettings.{Environment}.json`, the same way
  `TigerCsApi:BaseUrl` is committed in `src/TigerCS.Web/appsettings.json`.
- **`Crm:SecretKey`** — the shared secret CRM validates via the
  `X-SECRET-KEY` request header (the same value CRM reads from
  `ConfigurationManager.AppSettings["TicketingSecretKey"]` on its own side).
  **Never committed** — configure it the same way as `Jwt:SigningKey` above.

**Development** (user-secrets, from `src/TigerCS.Api`):

```bash
dotnet user-secrets set "Crm:BaseUrl" "https://crm-dev.tigergroup.internal/"
dotnet user-secrets set "Crm:SecretKey" "<the value CRM's TicketingSecretKey app setting holds>"
```

**UAT / Production** (environment variables, e.g. in the deployment
pipeline/container — same `__` separator as `Jwt__SigningKey` above):

```bash
export Crm__BaseUrl="https://crm-uat.tigergroup.internal/"
export Crm__SecretKey="<the value CRM's TicketingSecretKey app setting holds>"
```

If `Crm:SecretKey` (or `Crm:BaseUrl`) is left unconfigured, `GET /api/crm/buyers`
does not crash the app or fail startup — `CrmBuyerHttpGateway` returns a 502
(`CrmBuyerLookupOutcome.Unavailable`) for every request until both are set,
consistent with every other CRM port never blocking the rest of the
application. This is also why local development works without a real CRM
connection: the endpoint simply reports CRM as unavailable until you opt in
by setting the two values above.

## 3b. Configure PACT customer lookup (`Pact:Provider` / `PactApi:BaseUrl` / `PactApi:ApiKey`)

The PACT customer/contract lookup (`PactCustomerHttpGateway`, the Pact leg of
`GET /api/intake-records/{id}/customer-lookup`) calls PACT's
`GET v1/contracts/{mobile}` and `GET v1/contracts/{mobile}/customer-type`
endpoints. Unlike CRM Buyer Lookup it sits behind a provider switch:

- **`Pact:Provider`** — `"Mock"` (the default: `MockPactGateway`'s fixture
  data, deterministic and offline — what dev and the test suite use) or
  `"Http"` (the real PACT integration).

With `"Http"` selected, two values under the `PactApi` section:

- **`PactApi:BaseUrl`** — PACT's base URL. Not a secret: safe to commit per
  environment in `appsettings.{Environment}.json`, same as `Crm:BaseUrl`.
- **`PactApi:ApiKey`** — the API key PACT validates via the `X-API-KEY`
  request header. **Never committed** — configure it the same way as
  `Crm:SecretKey` above.

**Development** (user-secrets, from `src/TigerCS.Api`):

```bash
dotnet user-secrets set "Pact:Provider" "Http"
dotnet user-secrets set "PactApi:BaseUrl" "http://pact-dev.tigergroup.internal:5020/"
dotnet user-secrets set "PactApi:ApiKey" "<the API key PACT issued for this system>"
```

**UAT / Production** (environment variables, same `__` separator):

```bash
export Pact__Provider="Http"
export PactApi__BaseUrl="http://pact-uat.tigergroup.internal:5020/"
export PactApi__ApiKey="<the API key PACT issued for this system>"
```

If `PactApi:ApiKey` (or `PactApi:BaseUrl`) is left unconfigured while
`Pact:Provider` is `"Http"`, nothing crashes or fails startup —
`PactCustomerHttpGateway` reports `PactCustomerLookupOutcome.Unavailable`,
which customer lookup surfaces as a `Failed` Pact source entry; ticket
creation is never blocked and the agent falls back to manual customer/unit
entry, consistent with every other lookup source.

### Troubleshooting: PACT says "no match" for a customer that exists

`PactCustomerHttpGateway` sends PACT the mobile number trimmed and with
every `+` removed (`NormalizePactPhone` — PACT-request-side only; the
stored intake/ticket number is never changed). If a known customer still
comes back not-found, capture the gateway's Debug diagnostics — they show
the exact absolute URL sent and the raw status, which separates the three
realistic causes:

```jsonc
// appsettings.{Environment}.json (or user-secrets) — temporary, remove after
"Logging": { "LogLevel": { "TigerCS.Integrations": "Debug" } }
```

- `GET http://…/v1/contracts/9715… -> HTTP 200` + "EMPTY data array":
  the route is right; PACT matched nothing for the number **as sent** —
  a number-format mismatch (see the curl matrix below).
- `-> HTTP 404`: the customer might not exist, **or the URL is wrong** —
  check the absolute URL in the same line. A `PactApi:BaseUrl` carrying a
  path prefix must survive into the URL (`ResolveBaseAddress` guarantees
  the trailing `/` that relative-URI resolution needs).
- `-> HTTP 401/403`: the API key — surfaces as a Failed source, never as
  "no match".

To determine the exact number format PACT expects, run this matrix from a
machine inside the Tiger network against the current PACT base URL with a
mobile known to exist in PACT, and see which variant returns rows:

```bash
BASE="<current PactApi BaseUrl>"; KEY="<current PactApi ApiKey>"
for m in 971501234567 00971501234567 0501234567 +971501234567; do
  echo "== $m =="
  curl -sS -H "X-API-KEY: $KEY" "${BASE%/}/v1/contracts/$m" | head -c 300; echo
done
```

Whichever variant returns the customer defines the target format;
`NormalizePactPhone` is the single place to adjust (PACT-only — CRM and
persistence are untouched by design).

## 3c. Configure the Genesys integration (`Genesys:Enabled`)

The Genesys integration ships **switched off** and stays off until it is
deliberately enabled:

```jsonc
// src/TigerCS.Api/appsettings.json — the shipped default
"Genesys": { "Enabled": false }
```

With `Enabled: false`, all three Genesys endpoints (`POST /api/genesys/tickets`,
`GET /api/genesys/customers/lookup`, `PATCH /api/genesys/tickets/{ticketId}`)
answer `503` and write nothing.
**Everything else is unaffected** — manual/Face-to-Face ticket creation, the
New Ticket wizard, and every existing flow behave exactly as before, because
nothing in the normal ticketing path reads the flag.

To switch it on locally (from `src/TigerCS.Api`):

```bash
dotnet user-secrets set "Genesys:Enabled" "true"
```

**The JWT service account below is the UAT mechanism, not the confirmed final
Genesys authentication design** — see
`docs/architecture/Genesys-API-Contracts.md`. No new authentication scheme has
been built.

**There is no Genesys credential or endpoint to configure.** No Genesys API
base URL, OAuth client or webhook signing secret exists in this system,
because none has been confirmed by the Genesys team (see
`docs/architecture/Genesys-Integration-Phase1.md` §10). Genesys calls
TigerCS's own inbound endpoints as an ordinary authenticated **service
account**, using the same JWT authentication every other API client uses:

1. Create a staff account for Genesys under Administration
   (`POST /api/admin/users`) and give it the **CS Agent** role — the same role
   that may create tickets, which is exactly what ingestion does.
2. Genesys signs in with `POST /api/auth/login` and sends
   `Authorization: Bearer {token}` on every call.

### Genesys routing configuration (required before any inquiry can be routed)

One thing must be configured, System Administrator-only, and it is **not
seeded** — the real Genesys queue ids are not known to this repository and are
never invented:

```bash
# Which department a Genesys queue's inquiries belong to.
PUT/POST /api/admin/genesys/queue-mappings
```

An inquiry whose queue is unmapped is refused with a `422` naming the gap — it
is never routed to a guessed department.

Nothing configures **pending human work** either. Genesys reports that an
interaction needs a human agent (`PATCH /api/genesys/tickets/{ticketId}` with a
`handoff` block) on
whatever channel it happened, and TigerCS records it as a work item on the
existing ticket. Agents see it under **Pending Interactions**. TigerCS performs
no dialling, chat transport, WhatsApp/social sending or queue routing — Genesys
owns all of that.

Nothing configures a **category**. A Genesys ticket is created *Unclassified*
(`CategoryId` and `RequestTypeId` `NULL`) on the default priority
(`Genesys:DefaultTicketPriority`, "Normal" = Medium) with its SLA running from
creation — nobody has read the request yet, but it is measured from the first
moment. A request type supplied by the bot (at creation or later) routes it; an
agent can still add the category via
`POST /api/tickets/{ticketId}/classification`, which does not restart the clock.
How quickly a human first responded is measured separately and is
unaffected by any of this.

## 3d. Configure customer email notifications (`EmailNotifications`)

Customer-facing email (ticket received / resolved / closed / reopened) ships
**switched off** and is delivered through the Microsoft 365 SMTP account when
enabled. Full reference: `docs/Customer-Email-Notifications.md`.

```jsonc
// src/TigerCS.Api/appsettings.json — the shipped default (password deliberately empty)
"EmailNotifications": { "Enabled": false, "Provider": "Smtp", "Password": "", ... }
```

Locally the Development override uses the in-memory `Recording` adapter, so
deliveries are logged (`NOTHING WAS SENT`) and nothing leaves your machine. To
exercise real SMTP from a developer machine (from `src/TigerCS.Api`):

```bash
dotnet user-secrets set "EmailNotifications:Provider" "Smtp"
dotnet user-secrets set "EmailNotifications:Password" "<the no_reply_tiger mailbox password>"
dotnet user-secrets set "BackgroundJobs:Enabled" "true"   # the Outbox dispatcher does the sending
```

UAT / Production supply the same keys as environment variables
(`EmailNotifications__Enabled=true`, `EmailNotifications__Password=...`,
`BackgroundJobs__Enabled=true`). **Never put the password in any committed
file.** Startup refuses to run when delivery is enabled without
`SmtpHost`/`Username`/`Password`/`FromEmail`, naming the missing key.

## 4. Apply the database migration

From `src/`:

```bash
dotnet ef database update --project TigerCS.Infrastructure --startup-project TigerCS.Infrastructure
```

This creates `TigerCsTicketing_Dev` with Identity's own tables plus
`Employees`, `Departments`, and `UserDepartmentAssignments` — nothing else
(no ticketing/SLA/Genesys tables exist yet). If you don't have `dotnet ef`
on `PATH`, run it via `dotnet tool run dotnet-ef ...` instead.

To generate a new migration after changing an entity in this module:

```bash
dotnet ef migrations add <Name> --project TigerCS.Infrastructure --startup-project TigerCS.Infrastructure -o Persistence/Migrations
```

To preview the SQL a migration would run, without needing a live database:

```bash
dotnet ef migrations script --project TigerCS.Infrastructure --startup-project TigerCS.Infrastructure --idempotent
```

## 5. Run the API

From `src/TigerCS.Api`:

```bash
dotnet run
```

In the `Development` environment, `Program.cs` seeds the fixed role set,
four sample departments, and — only if `DevAdmin:Username`/`DevAdmin:Password`
are set per step 3 — one System Administrator account. Nothing here ever
runs against a non-development database; there's no production deployment
path in this increment.

### Swagger UI

With the API running, browse to:

```
https://localhost:PORT/swagger
```

The generated OpenAPI document itself is at
`/swagger/v1/swagger.json` (and, unchanged from before Swagger UI was added,
at `/openapi/v1.json`).

To call an authenticated endpoint from the UI:

1. Expand **Authentication → POST /api/auth/login** and execute it with a
   seeded account (step 3).
2. Copy the `accessToken` value out of the response.
3. Press **Authorize** at the top right and paste **only the token** — no
   `Bearer ` prefix. Swagger UI sends `Authorization: Bearer {token}` on
   every request from then on.

Swagger is served in the `Development` and `Testing` environments only
(`OpenApiDocumentation.EnabledEnvironments`). In any other environment —
Production included — neither the UI nor the JSON document is mapped, so
both paths behave exactly like a route this application does not have.

## 6. API examples (no real credentials)

Log in (replace with your own dev admin or a seeded test account):

```bash
curl -s -X POST https://localhost:PORT/api/auth/login \
  -H "Content-Type: application/json" \
  -d '{"username":"<your-dev-username>","password":"<your-dev-password>"}'
```

```json
{
  "accessToken": "<jwt>",
  "expiresAtUtc": "2026-08-19T13:00:00Z",
  "employeeId": "00000000-0000-0000-0000-000000000000",
  "displayName": "Development Administrator",
  "roles": ["System Administrator"],
  "primaryDepartmentId": null
}
```

Call an authenticated endpoint:

```bash
curl -s https://localhost:PORT/api/users/me \
  -H "Authorization: Bearer <jwt-from-login>"
```

List roles (System Administrator only):

```bash
curl -s https://localhost:PORT/api/roles \
  -H "Authorization: Bearer <jwt-from-login>"
```

Deactivate a staff account (System Administrator only):

```bash
curl -s -X PATCH https://localhost:PORT/api/users/<employeeId>/activation \
  -H "Authorization: Bearer <jwt-from-login>" \
  -H "Content-Type: application/json" \
  -d '{"isActive": false, "reason": "Left the company"}'
```

Log out:

```bash
curl -s -X POST https://localhost:PORT/api/auth/logout \
  -H "Authorization: Bearer <jwt-from-login>"
```

**Logout semantics — stated explicitly, not implied.** MVP authentication is
a stateless JWT (`AuthenticationAppService`'s own doc comment says this
too): there is no server-side token-revocation list. `POST /api/auth/logout`
returns `204 No Content` and does nothing else — it does **not** invalidate
the token. The token the client was holding remains cryptographically valid
and will keep passing *authentication* until it expires (`Jwt:ExpirationMinutes`,
60 by default) or the client discards it. What logout does not protect
against — a stolen still-valid token being replayed after "logout" — is a
known, accepted limitation of this MVP increment, not an oversight; adding a
revocation mechanism was explicitly out of scope ("do not invent
refresh-token functionality unless already approved"). The one thing that
*does* stop a token from working immediately, regardless of logout, is
**deactivation** (`PATCH .../activation`) — see
`Token_ReusedAfterDeactivation_IsRejectedOnEveryProtectedEndpoint` in
`AuthEndpointsTests.cs` — and, since the password-management increment,
**a password reset or change** (next paragraph).

**Password changes end every session.** Each JWT carries an `sst` claim: the
user's ASP.NET Core Identity *security stamp* at the moment the token was
issued (`JwtTokenService`; the Screen Pop redeem path issues it the same
way). `ActiveEmployeeHandler`, which already queries the database on every
request to confirm the employee is still active, also compares that claim
with the account's current stamp and refuses a token whose stamp is stale or
missing — the same 403 a deactivated employee's token gets. Both
`POST /api/admin/users/{employeeId}/password` (System Administrator reset:
replaces the password, lifts any lockout, resets the failed-attempt count)
and `POST /api/auth/change-password` (the caller's own change: current
password verified, policy applied) rotate the stamp, so every token issued
before the change — on every device — stops working at once, while a fresh
`POST /api/auth/login` with the new password works immediately. There is
still no revocation list; the stamp is the only state consulted, and it is
state Identity maintains anyway. Both actions are audited
(`AdminResetUserPassword` with the reason, `ChangeOwnPassword`) without the
password ever being written to the audit trail, the logs or a response. The
Web app's "Change my password" page (account menu) signs the cookie session
out after a success and sends the user back to `/Login`.

## 7. SQL Server validation checklist

This sandbox has no Docker daemon available to run these steps itself (the
`docker` CLI is present but `docker info` fails to reach a daemon socket).
Since a real SQL Server was needed, `.github/workflows/db-migration-validation.yml`
runs the migration against an actual SQL Server 2022 service container in
GitHub Actions instead — automated, not a manual local run, and not merely
`dotnet ef migrations script` reviewed by eye.

**Confirmed by that workflow.** The pinned expected-table list is refreshed with
every schema-adding increment, which is the point of it: an unannounced table is
a workflow failure, not a silent pass. It stood at 11 tables after Identity and
Access, 31 after SLA and Escalation, and **33 as of the Notifications increment**
(`OutboxMessages` and `Notifications`, MVP-Data-Dictionary.md §2.21/§2.23).
`TicketSlaPausePeriods` and `PriorityDowngradeRequests` remain deliberately
absent (MVP-Implementation-Backlog.md §0/§0.2), as do all Genesys tables.

- [x] `dotnet ef database update` against a real SQL Server 2022 container completes with no error.
- [x] `SELECT name FROM sys.tables ORDER BY name;` returns exactly the expected table list — nothing else, confirmed by the workflow's own table-list diff, not eyeballed.
- [x] Every filtered unique index is present **with its exact `filter_definition`** — the primary-department index, the CRM verification-session idempotency index, the current-assignment and current-resolution indexes, the current-SLA-period and one-auto-breach-per-ticket indexes, and the Notifications increment's `UX_Notifications_OutboxMessageId_NotificationType`. The EF Core InMemory provider ignores filtered indexes entirely, so this is the only place they are actually exercised.
- [x] The `OutboxMessages` dispatcher and dead-letter indexes carry their `[Status]` filters, so `Processed` rows — which nothing deletes — never grow the indexes the hot path reads.
- [x] Column nullability on `Notifications`/`OutboxMessages` matches §2.21/§2.23, including `RecipientAddress` being nullable: a ticket with no deliverable requester address still gets a dead-lettered, visible row rather than a fabricated address.
- [x] Every foreign key on the SLA, Escalation and Notifications tables is `NO_ACTION` (Restrict).
- [x] `dotnet ef migrations has-pending-model-changes` against the live, migrated database reports "No changes have been made to the model since the last migration." — no drift between the model and the applied schema.

**Not covered by that workflow — still only verified via the InMemory-backed integration tests, not against a real SQL Server:**

- [ ] A full application login round-trip (seed → `POST /api/auth/login` → authenticated request) against a real SQL-Server-backed database, run locally per steps 1-6 above. Worth doing before this increment is considered fully production-schema-verified, since InMemory doesn't exercise the same SQL Server-specific behavior (e.g. the filtered index, real transaction/locking semantics) that a login-and-request flow would touch end-to-end.

Re-run the workflow any time via `workflow_dispatch` (Actions tab → "DB Migration Validation" → "Run workflow") to re-verify after a schema change.

## 8. Running the tests

```bash
cd src
dotnet test --configuration Release
```

The integration tests (`TigerCS.Tests/IdentityAndAccess/Integration`) run
the real Api in-process against a per-test EF Core InMemory database — no
SQL Server is needed to run `dotnet test`. Only the migration itself (step 4)
needs a real SQL Server, since InMemory doesn't exercise the filtered
unique index or real T-SQL constraints.

## 9. SLA and escalation background jobs (ADR-0015)

The SLA and Escalation increment adds Hangfire, backed by the same SQL Server
database (ADR-0003). It runs two things:

- a **scheduled (delayed) job per due timestamp**, enqueued the moment the
  timestamp is computed — the primary breach-detection mechanism
  (`SLA-Architecture.md` §13);
- a **recurring safety sweep**, every 1–5 minutes, that re-scans due deadlines
  solely to catch a scheduled job lost to a deploy or restart (§14). It is
  never the primary path, and on a healthy system it records nothing.

Neither depends on an open browser or a SignalR client: both are server-side
background work against the stored `TicketSlaInstances.*DueAtUtc` columns.

### Configuration

```jsonc
"BackgroundJobs": {
  "Enabled": true,            // default; set false to run the API with no Hangfire server
  "SweepIntervalMinutes": 5   // clamped to the approved 1-5 range
}
```

Hangfire provisions its own tables in a separate `HangfireSla` schema on first
start, so they are never confused with — or migrated alongside — the
application schema EF Core owns. `ConnectionStrings:TigerCsDatabase` is
required whenever `Enabled` is `true`; the API refuses to start otherwise
rather than silently running without SLA breach detection.

**`Enabled: false` disables job *execution*, never job *logic*.** Breach
detection stays fully wired and reachable — the deadline scheduler simply
becomes a no-op, so a breach is detected on the next sweep instead of at the
exact due moment, and detected exactly once either way (both paths share one
idempotency key, `SLA-Architecture.md` §15). The integration-test hosts use
this, since they have no SQL Server behind them.

### Reference data

`SlaPolicies` and the default `BusinessCalendars`/`BusinessCalendarWorkingDays`
rows are seeded alongside `Priorities` by `DevSeedData` in Development, from
`SlaReferenceData` — the same source the integration-test host seeds from, so
the approved per-tier targets exist in exactly one place. The seeded calendar
is ISSUE-017's approved Option A: **Saturday–Thursday working, Friday off,
08:00–18:00, `Asia/Dubai`**.

**No `Holidays` rows are seeded.** ADR-0010 makes holiday entry a manual
annual process with a named business owner (ISSUE-012), and every row requires
an `EnteredByEmployeeId` — so holiday dates are entered per year, not shipped.
A missing holiday entry silently produces an SLA deadline that runs through a
day the business was closed; that is ADR-0010's own stated operational risk,
and it is the one piece of this calculation a code change cannot protect.

The host must be able to resolve the calendar's `TimeZone` (`Asia/Dubai`). On
Linux that needs `tzdata` installed. A zone that cannot be resolved fails
loudly rather than falling back to UTC, which would silently shift every
business-hours deadline by four hours.

## 11. Running TigerCS.Web locally

TigerCS.Web is a separate ASP.NET Core app — it never touches the database
directly; every page that needs data calls TigerCS.Api over HTTP through a
handful of typed `HttpClient`s (`src/TigerCS.Web/Services/Api/*ApiClient.cs`).
All of them share **one** configuration source: the `TigerCsApi:BaseUrl`
value `Program.cs` binds into `TigerCsApiOptions` and hands to every
`AddHttpClient<...>()` registration. There is no per-client override and
nothing here is ever hard-coded — if a page can't reach the Api, this is the
one setting to check.

```jsonc
// src/TigerCS.Web/appsettings.Development.json
"TigerCsApi": {
  "BaseUrl": "https://localhost:7283"
}
```

**This must match whatever port TigerCS.Api is actually listening on for
you.** `src/TigerCS.Api/Properties/launchSettings.json`'s `https` profile
binds `https://localhost:7283` (and, unless you've overridden it, also
`http://localhost:5179`) — if you start the Api a different way (a custom
`--urls`, a different launch profile, IIS Express, a container port
mapping), update `TigerCsApi:BaseUrl` to match instead of assuming the
default. A stale or mismatched value here is exactly what an "Unable to load
the department list" / "Could not record this interaction" pair on the New
Ticket page means — the Api itself can be completely healthy (confirmed via
Swagger) while Web is still pointed at the wrong address.

Both apps run over HTTPS in Development with the standard ASP.NET Core
localhost dev certificate. Trust it once, machine-wide, if you haven't
already — this is what lets the Web app's server-side `HttpClient` complete
TLS to the Api without any certificate-validation code change:

```bash
dotnet dev-certs https --trust
```

Never work around a certificate error here by disabling TLS validation
(`ServerCertificateCustomValidationCallback`, `HttpClientHandler.DangerousAcceptAnyServerCertificateValidator`,
etc.) — that reintroduces the exact risk the dev certificate exists to avoid,
for a problem `dotnet dev-certs https --trust` already solves.

TigerCS.Web authenticates its own users with a cookie
(`TigerCS.Web.Auth`, `Program.cs`) that never reaches the browser as
JS-readable data. Server-side, `BearerTokenHandler` reads the Api access
token back out of that cookie's claims and attaches it as
`Authorization: Bearer {token}` on every outgoing call — every typed client
except `AuthApiClient` (which signs in/out, before any token exists) is
registered with `.AddHttpMessageHandler<BearerTokenHandler>()` in
`Program.cs`. A page failing with an authorization-specific message ("Your
session is not authorized to perform this action.") rather than a
connectivity one means you're signed into Web but the forwarded token is
being rejected — check that you're signed in with an account TigerCS.Api
still considers active, not that the Api address is wrong.

In Development, every non-success Api call (a mapped HTTP failure or a
connection failure) is logged from `ApiClientBase` — method, relative
endpoint, status code, the Api's own "detail"/"title" if it sent one, and
the exception type on a connection failure. Never a token, a cookie, or a
full response body. Check the Web app's own console/log output first when a
page reports a generic failure — the specific cause is there even when the
page itself only shows a safe, generic message.

Run it from `src/TigerCS.Web`:

```bash
dotnet run
```

then open `https://localhost:7219`.
