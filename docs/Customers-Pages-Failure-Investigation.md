# Customers pages failing — investigation and fix

Reported (localhost:7219):

1. `GET /Customers` → "Tiger Ticketing System could not load the customer directory."
2. `GET /Customers/phone:…?fromTicket=440` → "This customer profile could not be loaded."

## Trace

| Step | `/Customers` | `/Customers/{key}?fromTicket=440` |
|---|---|---|
| Razor Page | `Customers.cshtml.cs` | `CustomerProfile.cshtml.cs` |
| Web client | `CustomersApiClient.ListAsync` → `GET api/customers` | `CustomersApiClient.GetProfileAsync` → `GET api/customers/profile/{key}` |
| Api | `CustomersController.List` | `CustomersController.GetProfile` |
| App service | `CustomerDirectoryAppService.ListAsync` | `CustomerDirectoryAppService.GetProfileAsync` |
| Query | `CustomerDirectoryRepository.ListAsync` | `CustomerDirectoryRepository.GetProfileAsync` |
| **Shared** | **`CustomerDirectoryRepository.Identified()`** | **`CustomerDirectoryRepository.Identified()`** |

Both pages show their generic error for any non-success `ApiOutcome` other than 401/403/404/400; a 500 (unhandled SQL
exception, including a command timeout) is exactly that case. `ApiClientBase` logs every such call
(`TigerCS.Api call failed: GET … -> 500`), and the Api's `UseExceptionHandler` logs the exception itself — those two log lines are
where the failing statement shows up.

## CRM / PACT / external calls — ruled out

* The directory list makes **no** external call (it is computed from persisted tickets; there is no CRM list API).
* The profile calls CRM only for a `crm:` key (`TicketsApiClient.GetCustomerProfileAsync`), and a failure there is
  swallowed into `CrmProfile = null` — it can never produce "could not be loaded". A `phone:` profile (the reported one) makes
  no CRM, PACT or Tasleeh call at all. Payment and Documents panels are deferred until their tab is opened.

So both failures are local database reads, and they share one query.

## Root cause (code defect, shared by both pages)

`Identified()` resolved each ticket's captured phone with a correlated `SELECT TOP(1) PhoneNumber FROM IntakeRecords WHERE
LinkedTicketId = t.TicketId ORDER BY IntakeRecordId` **inside the projection**. EF Core inlines a projected subquery at every
reference, and the identity `Kind`, `Phone`, the `WHERE Kind <> 0` and the canonicalizing `REPLACE()` chain reference it, so the
generated T-SQL contained **14 correlated `TOP(1)` look-ups per ticket** (captured with the SQL Server provider; see
`CustomerDirectoryQueryShapeTests`). That ran:

* for **every visible ticket** in the `COUNT(*)` query and again in the page query of `/Customers`, and
* for **every visible ticket** in the phone-keyed profile query, because the profile can only filter on the phone *after* the
  identity is computed (`… END = @identity_PhoneNumber`).

Cost therefore grows with the total ticket table (Call Center phone tickets dominate), against SqlClient's default 30 s
command timeout (no `CommandTimeout` is configured for `TigerCsDbContext`) — hence both pages failing together once the
table is large enough, while a CRM-keyed profile (whose filter short-circuits on `CrmBuyerCustomerId`) can still open.

## Fix

`CustomerDirectoryRepository.Identified()` now resolves each ticket's first non-empty linked intake phone **once**, as a single
pre-aggregated derived table (`MIN(IntakeRecordId)` per `LinkedTicketId`, joined back to `IntakeRecords`) LEFT JOINed to
`Tickets`. Same row selected as before (lowest `IntakeRecordId` with a non-empty phone), same canonicalization, same
identity precedence (CRM Buyer id → external id → phone), same search/filter/department/visibility predicates. The generated SQL
has no correlated `TOP(1)` left. No timeout was raised, no exception is caught, nothing is cached, and no empty list is
substituted.

Unchanged by design: customer identity rules, CRM/PACT matching, ticket-linking (`/Tickets/{id}/Customer` and the
`?fromTicket=` Back-to-ticket link), the Web error states.

## Second thing to rule out on the affected database

Both pages also read `CustomerContactDetails` (added today by migration `20261105094644_AddCustomerContactDetails`, #199) —
the list after its page query, the profile after its ticket reads. The app does **not** migrate on start-up, so a database that
has not had that migration applied fails both pages with `Invalid object name 'CustomerContactDetails'` (SQL 208), a different
cause with the same symptoms. `Verify_TigerCS_PROD_PendingMigrations_ReadOnly.sql` now lists it. For a dev database:
`dotnet ef database update --project TigerCS.Infrastructure --startup-project TigerCS.Infrastructure`; for Production:
`Deploy_TigerCS_PROD_AddCustomerContactDetails.sql`. The Api log line to look for is the `SqlException` under
`GET /api/customers`; "Invalid object name" = this, "Execution Timeout Expired" = the query above.

## Tests

* `CustomerDirectoryQueryShapeTests` — SQL Server provider, no database: list (count + page, with and without search/filters),
  phone-keyed and CRM-keyed profile all resolve the phone with one join and contain no `TOP(1)` (fails on the old query).
* `CustomerDirectorySqliteTests.PhoneIdentity_IsTheFirstNonEmptyLinkedIntake_ForTheListAndTheProfile` — the join keeps the
  "first non-empty intake" rule for list and profile.
* `CustomerPagesRegressionTests` — both reported requests through Razor: render when the Api answers (profile still leads
  back to ticket 440); when the Api read fails each page says so, shows no empty/fabricated data, and the failed call is logged.
