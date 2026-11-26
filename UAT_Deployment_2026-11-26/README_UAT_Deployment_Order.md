# TigerCS UAT deployment package (database TigerCsTicketing)

Generated from origin/main `beff5b6`. Every script aborts unless connected to `TigerCsTicketing`. Nothing here was run against UAT or PROD.
All deploy scripts are **dry-run by default** (`DECLARE @DryRun bit = 1;` = apply, validate, ROLL BACK). Set it to `0` to commit.
Run each file in SSMS as one batch (SQLCMD mode OFF). Stop at the first FAIL / error and send me the output.

## Exact execution order

| Step | File | Mode | Expect |
|---|---|---|---|
| 0 | Take a full backup of TigerCsTicketing | manual | pre-flight warns if none in 24 h |
| 1 | `00_PreFlight_UAT_ReadOnly.sql` | read-only | `PRE-FLIGHT PASSED`, 55 applied, 5 chain migrations pending |
| 2 | `01_Deploy_UAT_AddCustomerProfileOverrides.sql` | dry run, then `@DryRun = 0` | `DRY RUN SUCCESS`, then `COMMITTED` |
| 3 | `02_Deploy_UAT_AddCustomerProfileAuditAndProperties.sql` | dry run, then commit | same |
| 4 | `03_Deploy_UAT_AddAssignmentHistoryPreviousDepartment.sql` | dry run, then commit | same |
| 5 | `04_Deploy_UAT_AddInteractionEndReceivedAt.sql` | dry run, then commit | same |
| 6 | `05_Deploy_UAT_AddEmployeeReviews.sql` | dry run, then commit | same |
| 7 | `06_Deploy_UAT_Collections_fn_CollectionsUnitFloor.sql` | dry run, then commit | same (13-case self-test) |
| 8 | `90_PostDeploy_Checks_UAT_ReadOnly.sql` | read-only | `POST-DEPLOY CHECKS PASSED (47 checks)` |
| 9 | `99_Verify_TigerCS_UAT_ReadOnly.sql` | read-only | verdict `UP TO DATE...`; 60 of 60 migrations PASS; 0 FAIL / DRIFT |

Order is enforced by the scripts themselves: file 02 refuses to run before 01, 03 before 02, 04 before 03, 05 before 04. Files 01-05 are one chain
(ids 20261118120000 -> 20261119081103 -> 20261119120249 -> 20261120074059 -> 20261124071552). File 06 is independent of the chain.

## Safety properties (all scripts)
- Database-name guard first; nothing is touched if it fails.
- Prerequisite checks (history table, previous migration recorded, target objects absent, required tables/keys present).
- One transaction per script; validation of column shapes, keys, indexes, history row (exactly once, ProductVersion 10.0.11) and
  row counts of existing tables BEFORE commit; any failure rolls back.
- The migration-history row is written only in the same transaction as the schema change. Never insert history rows by hand.
- Additive only: no DROP, no data rewrite. The one data write is file 02's `NEWID()` back-fill of the new `CustomerProfileId` column.
- Idempotent: re-running a committed script changes nothing.

## Not part of this package (and why)
- `V001__reconcile_CollectionsTowers.sql`: UAT already has one of its two alternative indexes (it creates either `UX_` or `IX_CollectionsTowers_Company_TowerNumber`, never both). Only re-run it if step 8 reports "neither".
- Any `*_PROD_*`, `Remediate_*`, `Backfill_*`, `Execute_*` script: production only.
- `ApplyQueueingTigerPropertiesMappings.sql`, `EnsureDefaultGeneralClassification.sql`: data scripts, run only if step 9 shows the corresponding WARN rows are unwanted.
