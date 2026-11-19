# Customer Profile editing, properties and Change History

## What changed
| Area | Change |
|---|---|
| Name consistency | **Root cause:** each page collected its own candidate names. Ticket Details looked up a saved name under *one* phone key (this ticket's intake phone) and never read the customer's other numbers, other tickets' conversations or the profile edit, so it printed "Unnamed caller" while the profile found the name. `CustomerContactResolution` is now the one rule; the directory list, profile, Ticket Details, Related Tickets and ticket/global search all use it. Ticket snapshots are only ever read, never rewritten. |
| Edit Customer | Button on the profile. Name, mobile, landline, additional numbers, e-mail, nationality, country, emirate, location, preferred language. Local to Tiger Ticketing; CRM/PACT/Tasleeh are never written. Requires `Customers.Edit`. |
| Properties | Manual project/unit associations (Owner / Tenant / Representative), edit/remove, duplicate prevention (also against verified units). Always shown as "Manually added", never verified ownership. |
| UAE phones | Landlines `02/03/04/06/07/09` (971 + 8 digits) and mobiles (`5xx`, 971 + 9 digits) in local, `+971` and `00971` forms are one number and count as UAE in Customers by Location. No emirate is ever inferred; phone country is separate from residence. Stored keys are unchanged (no re-keying, no customer merging). |
| Audit | `CustomerProfileChanges`: one append-only row per changed field (old/new, employee id + name, UTC time, source, reason, related ticket, correlation id), written in the same transaction as the change. A DB trigger rejects UPDATE/DELETE. Idempotent via a unique `ChangeKey`. Stable `CustomerProfileId` survives phone/name changes. One summary row (field names only) also goes to `AuditEntries`. |
| Change History tab | Existing timeline design, grouped per operation. Requires the new `Customers.ViewHistory` permission (System Administrator and CS Manager by default). |

Closed tickets: the edit services have no ticket repository, so a customer edit cannot reopen a ticket or touch owner, SLA or closure data (`EditingACustomer_NeverTouchesAClosedTicket...` compares every stored column).

## Deployment (do not skip a step; nothing here has been run against Production)
Run in SSMS against `TigerCS_Ticketing_Prod` (10.10.10.124).

1. `Verify_TigerCS_PROD_AllMigrations_ReadOnly.sql` (read-only). Expect result 1 to list only `20261119081103_AddCustomerProfileAuditAndProperties` (and anything genuinely pending), result 2 to be empty, and every row of result 3 to have `Present = 1` except this migration's objects.
2. `Deploy_TigerCS_PROD_AddCustomerProfileAuditAndProperties.sql` with `@DryRun = 1` (default). It applies, validates and **rolls back**. Read the Messages: "DRY RUN SUCCESS".
3. Set `@DryRun = 0` and run to commit. Post-commit result sets confirm 25/14/11 columns, 0 rows in the new tables, one distinct `CustomerProfileId` per existing override, and the append-only check (error 51000).
4. Re-run the verify script: nothing pending, every `Present = 1`, trigger present.
5. Deploy the application. Grant `Customers.ViewHistory` to anyone else who must see history (Administration → permissions); until then only System Administrator and CS Manager see the tab.

The migration is additive: it adds columns/tables/indexes and gives each existing override row a fresh `CustomerProfileId`. Rollback SQL is in the script header (only while the new tables hold nothing you need).

**Order matters:** the new code reads the new columns, so the schema script must run **before** the application is deployed. (This is exactly the failure mode of the earlier `CustomerProfileOverrides` incident.)

## Guarding against a repeat
* `EveryMigrationIsListedInTheProductionVerifyScript` fails the build if the verify script misses any migration.
* `EveryMigrationFromTheAuditMigrationOn_ShipsBothProductionScripts` requires `<Name>.sql` and `Deploy_TigerCS_PROD_<Name>.sql` for every new migration.
* CI table list is now 87 tables and verifies the trigger.
* `CustomerProfileAuditSqlServerTests` (CI only) run the Production script (dry run, commit, re-run), EF's migration over existing rows, the trigger, and the retry / lost-update / duplicate-property races on a real SQL Server.

## Known limits
* **Verification status and CRM linkage** are CRM/ticket-owned and not editable on the profile, so they are not audited here; they change only when a ticket's verification changes (existing, separately audited paths).
* The contact-record save (`PUT /api/customers/contact`) records the *previous contact record's* values as "old", not what the page displayed.
* The directory list reads the same name sources as the profile (two extra batched reads per page; the query-count guard is raised to 10 and still independent of page size).
* Verifying the SQL Server tests and the Production script requires CI or a staging run; they were not executed in the authoring sandbox.
