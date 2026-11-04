# Production release — commit `1b62286` (2026-11-04)

**Prepared, not deployed.** No SQL was run, no site was stopped, restarted or copied to, and no Production setting was read or changed.
Target database: `TigerCS_Ticketing_Prod`. Companion to [Production-Deployment.md](Production-Deployment.md) (IIS, secrets, health checks, rollback).

## 1. What this build is

| | |
|---|---|
| Commit | `1b6228618911e69b0795bfb9f65e4924fadeaac9` (`main`, includes #158 #161 #163 #164 #169 — `tools/verify-deployed-fixes.sh` prints all five PRESENT) |
| Package | `TigerCS-1b622861.zip` (`api/`, `web/`, idempotent migration SQL, config + deployment-order docs) |
| SHA-256 zip | `b6691fc048afeb73ea10066aa760317b6d81fd75130a72eb0bced3e1bd08c6cf` |
| SHA-256 migrations | `3710ad61a85b968e1f4cd140cb4f17a55402e44eb207630f287456887c2639a7` |
| Built with | `tools/build-release.sh` on .NET SDK 10.0.111: **7,402 tests passed, 0 failed, 90 skipped**; EF model-drift check clean |
| Version stamp | `GET /health` on the API returns `1.0.0+1b6228618911e69b0795bfb9f65e4924fadeaac9` (verified inside `TigerCS.Api.dll`) |
| `MANIFEST.txt` | commit, UTC build time, SDK, SHA-256 of the zip and the SQL |

## 2. Database compatibility with `TigerCS_Ticketing_Prod`

Run **`Verify_TigerCS_PROD_PendingMigrations_ReadOnly.sql`** first (SELECT only). It lists the repository's 43 migrations that are missing from `__EFMigrationsHistory`, history rows this build does not know (a newer/different build was deployed - stop), and whether the tables the app and the backfill need exist.

Expected pending set (Production's last recorded migration was `20261022090000` when `Deploy_TigerCS_Prod_Migrations.sql` was written; confirm with the report):

| Migration | Production-hardened script | Change |
|---|---|---|
| `20260912110119_AddExternalCustomerSnapshotToTickets` (only if unrecorded) | `Deploy_TigerCS_Prod_Migrations.sql` | 2 nullable `Tickets` columns |
| `20261030061255_AddTicketInteractionHandlers` | same | new table + copy of existing handlers |
| `20261030062642_AddTicketTransferTracking` | same | 4 nullable columns + index on `TicketWorkflowEvents` |
| `20261030073857_AddTicketCreatedBy` | same | `Tickets.CreatedByEmployeeId` + backfill from the first Create audit row |
| `20261031094748_AddDepartmentAssignmentRotation` | same script (standalone alternative: `Deploy_TigerCS_PROD_AddDepartmentAssignmentRotation.sql`) | new table (Round Robin cursor) |
| `20261102065459_AddUserPermissionOverrides` | `Deploy_TigerCS_PROD_AddUserPermissionOverrides.sql` | new table |
| `20261104130835_AddCollectionsTowerAssignments` | **`Deploy_TigerCS_PROD_AddCollectionsTowerAssignments.sql` (new, this release)** | 4 new empty tables |

* Every Up() step is additive (CreateTable / nullable AddColumn); the only data write is the `CreatedByEmployeeId` backfill. The `DROP`s in the full idempotent script belong to migrations older than `20261022090000`, which a guarded script skips when they are recorded.
* The hardened scripts verify objects that were created by hand earlier (a plain EF script would fail on them) and all default to **`@DryRun = 1`**. Prefer them over `TigerCS-all-migrations.idempotent.sql`, which is shipped for reference and for a clean database.
* **The Collections script is new**: the repository only had a UAT-only version. It is that file with the database guard changed to `TigerCS_Ticketing_Prod` and a dry-run switch. Review it like the others.
* The app does **not** migrate on start-up. **Order: database first, then the application.** The new build reads the new tables; the old build tolerates the additive schema, so a database-only step is safe to do early.

## 3. Settings that must survive the deployment

* The package's `appsettings.json` / `appsettings.Production.json` are **placeholders** (empty password, `BackgroundJobs`, `Genesys` and `Collections` all `false`). **Do not copy them over the server's files** - that would switch the Genesys integration and background jobs off and blank the connection string.
* Secrets and overrides live in **application-pool environment variables** (Production-Deployment.md §4); they are not in the package and are not touched by a file copy.
* Copy with `robocopy <package>\api C:\inetpub\TigerCS\Api /E /XF appsettings.json appsettings.Production.json web.config /XD logs` (same for `web`). Keep the server's `web.config`; the packaged one is the standard in-process handler.
* New switches in this build all default OFF in code and in the packaged file: `DefaultClassificationBackfill:Enabled`, `CsAgentBackfill:Enabled`, `CallCenterReconciliation:Enabled` (+ `DryRun=true`). **Leave them off**: the historical backfill is the SQL script, not these app jobs.
* After the copy, diff the server's `appsettings.Production.json` against the packaged one only to learn about **new keys**; do not take values from the package.

## 4. Deployment steps (you run these; nothing here is automated)

1. Full backup of `TigerCS_Ticketing_Prod`; confirm it restores.
2. Run `Verify_TigerCS_PROD_PendingMigrations_ReadOnly.sql`; send the result back.
3. Apply the pending scripts from §2 **in migration order**, each first with `@DryRun = 1`, then `0`. Quiesce the API/Web/background workers while `Deploy_TigerCS_Prod_Migrations.sql` runs (it takes schema locks).
4. Back up the current `Api` and `Web` folders (rollback copies, Production-Deployment.md §11).
5. Stop the sites; copy the package per §3; start the sites.
6. `curl http://localhost:8091/health` -> `version` must be `1.0.0+1b6228618911e69b0795bfb9f65e4924fadeaac9`; `curl http://localhost:8092/health` for the Web; sign in, open Tickets and Admin; check the Application log for start-up guard errors.
7. `tools/verify-deployed-fixes.sh 1b6228618911e69b0795bfb9f65e4924fadeaac9` (or the `/health` value): all five PRESENT.
8. Only then: run the historical backfill dry run ([Historical-Ticket-Backfill-Runbook.md](Historical-Ticket-Backfill-Runbook.md)). Run `EnsureDefaultGeneralClassification.sql` first if its report 2 shows a missing General.

**Rollback:** redeploy the saved `Api`/`Web` folders (application releases do not change the schema); every migration here is additive, so the old build keeps working against the new schema. A schema rollback is a DBA restore from the step-1 backup.
