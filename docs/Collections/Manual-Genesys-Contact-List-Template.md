# Manual Genesys contact list: the official eleven-column template

The Genesys team confirmed the template of the dedicated **Manual** contact list. **Manual Export for Genesys** and **Manual Send to Genesys** both write exactly this payload (one definition: `GenesysContactTemplate.ValuesOf`).

| # | Column | Source |
|---|--------|--------|
| 1 | `Phone` | CRM first, PACT completing (`CollectionsContactLinker`), E.164 |
| 2 | `Email` | CRM first, PACT completing. **`Email`, not `Email Address`** (Manual only) |
| 3 | `CustomerName` | CRM first, PACT completing |
| 4 | `ReminderType` | The stage label (unchanged) |
| 5 | `AmountDue` | PACT, the Manual filtered amount, two decimals (unchanged) |
| 6 | `UnitNumber` | The unit as the Manual page lists it (the PACT unit code of the review record) |
| 7 | `ProjectName` | The project held by the review record (`ProjectCode`); empty when none |
| 8 | `TowerName` | The tower's real name (`CollectionsTowerName`); **empty** when the tower has no real name, a number is never passed off as a name |
| 9 | `DueDate` | PACT, `yyyy-MM-dd` (unchanged) |
| 10 | `DueMonthAr` | Arabic name of the due date's month (يناير ... ديسمبر), fixed table, independent of the server culture |
| 11 | `TenantId` | The **PACT tenant** of the unit. Never a CRM `CustomerId` |

* One row per unit, even when phone numbers repeat. Nothing is merged, de-duplicated or invented; a missing value stays empty.
* Auto contact lists keep their six columns (`Phone, CustomerName, Email Address, ReminderType, AmountDue, DueDate`), their per-stage lists and their routing.
* The Manual list must carry all eleven columns by exact name; a list that lacks one (or still has `Email Address`) refuses the send and the export, and the Genesys connection check reports it. Extra list columns are tolerated for the check, but a Manual file is exactly the eleven columns.
* All send protections (authorization, approval, reconciliation, freshness, idempotency, destination checks, suppression) are unchanged. A suppression `PUT` of a Manual contact carries the same eleven keys.
* `ProjectName` and `TowerName` are frozen on the dispatch item at approval (migration `AddManualGenesysDispatchItemFields`, script `AddManualGenesysDispatchItemFields.sql`, idempotent, not executed by the build). Items approved before the migration have them empty.
