# 17. Release package, stale artifacts, credential rotation and migration/configuration checklist

> Part of the [documentation set](README.md). Nothing here deploys anything. **Evidence levels used throughout:** *Local* = run in the review environment (SQLite/fakes, no external systems); *CI* = GitHub Actions on the pull request (real SQL Server for the migration workflow); *UAT* = run against a UAT environment. As of this writing only *Local* evidence exists; CI and UAT columns are explicitly empty until someone fills them.

## 17.1 Are `publish/` and the root patches current? — No

| Artifact | Finding (method) | Verdict |
|---|---|---|
| `publish/TigerCS.Api`, `publish/TigerCS.Web`, `publish/TigerCS.zip` (≈96 MB, 287 files) | Metadata search of the shipped `TigerCS.Application.dll`: types from before PR #72 are present (`TeamPerformance…`, `GenesysScreenPopAppService`, `CollectionsReminder…`); types from #72–#77 and later are **absent** (`CollectionsCampaignAppService`, `PactReceivablesController`, `GenesysCustomerUnitDetailsAppService`, `ChatbotInactivityCloseJob`, `CustomerOtpAppService`). Zip `appsettings.json` is dated 2026‑10‑06 and has no `PactReceivables` section. | **Stale** — built before the campaigns/unit-details/inactivity/OTP work. Do not deploy it. |
| `TigerCS_Customer_Cards_Payments.patch` | Does not apply forward or in reverse on the current tree (diverged). | Superseded leftover |
| `collections-due-overdue.patch`, `collections-campaigns.patch`, `TigerCS_Collections_Campaigns/collections-campaigns.patch` (identical copies), `TigerCS_Collections_Campaigns/files/**` | Diverged: the code they add exists in `main` and was changed afterwards (#75, #77). | Superseded leftover |
| `TigerCS_Remaining_Tests_Fix.patch`, `TigerCS_Test_Analyzer_Fix.patch` | Reverse-apply cleanly, i.e. already contained in the tree. | Already applied — redundant |
| Root `*.sql` | Hand-apply copies of migrations; kept current: `AddCustomerOtpVerification.sql`, `AddSlaPauseAndPriorityDowngrade.sql` (generated from the EF model). | Keep (see 17.3) |

**Nothing was deleted.** Recommendation (decision D15): remove `publish/`, the six `.patch` files and `TigerCS_Collections_Campaigns/` in a separate reviewed commit, since a fresh package is now produced by `tools/build-release.sh` / the manual *Release package* workflow.

## 17.2 Fresh package from the tested commit

`tools/build-release.sh [out]` (also `.github/workflows/release-package.yml`, manual): refuses a dirty tree → `dotnet test` → `dotnet ef migrations has-pending-model-changes` → `dotnet publish` Api and Web (Release) → `dotnet ef migrations script --idempotent` → zip + `MANIFEST.txt` (commit, UTC build time, SDK, sha256). The exact commit and hashes of the package built for this review are in the pull-request description and the *Release commit* line below.

**Release commit:** `e4059c56c45406d80151053425c7c9c6e34ac3b1` (build date 2026‑10‑08, .NET SDK 10.0.111). The package was produced by `tools/build-release.sh` on that exact commit after 3,779 passing tests and a clean EF drift check (*Local* evidence). Later commits on the branch are documentation-only (this file, PR text) and do not change the package; the package must be rebuilt if any file under `src/` changes.

| File | sha256 |
|---|---|
| `TigerCS-e4059c5.zip` (api/, web/, idempotent migration SQL, config + deployment notes; 65 MB) | `683defdbf1049a5d95f5c07ef83066a8f6fc6fe48955bd02339759e273eaa7ea` |
| `TigerCS-all-migrations.idempotent.sql` (all migrations, idempotent) | `1b485fa8b50f586f7709c7fd2b36105a43bdf3e22da22596599825ef3c31c67e` |

Checked in the package: it contains the post-#72 types (`CollectionsCampaignAppService`, `CustomerOtpAppService`, `PriorityDowngradeAppService`, `SlaPauseService`, service-identity code); committed `appsettings.json` has an empty password and no secrets; the SQL creates `CustomerOtpChallenges`, `TicketSlaPausePeriods`, `PriorityDowngradeRequests`. A byte-identical rebuild is not promised (timestamps); verify the hash of the file you deploy, or run the manual *Release package* workflow for a CI-built artifact.

## 17.3 Migration checklist (in order; take a backup first)

| # | Migration | Hand-apply script | Creates |
|---|---|---|---|
| 1 | `20261005072511_AddCollectionsReminders` | `AddCollectionsReminders.sql` | reminder tables |
| 2 | `20261007093644_AddChatbotInactivityAndCrmDocumentCopies` | `AddChatbotInactivityAndCrmDocumentCopies.sql` | `CrmDocumentDeliveryRequests`, inactivity columns |
| 3 | `20261007103153_AddResolutionClosedForCustomerInactivity` | `AddResolutionClosedForCustomerInactivity.sql` | resolution lookup row |
| 4 | `20261007180009_AddCustomerOtpVerification` | `AddCustomerOtpVerification.sql` | `CustomerOtpChallenges` |
| 5 | `20261008184722_AddSlaPauseAndPriorityDowngrade` | `AddSlaPauseAndPriorityDowngrade.sql` | `TicketSlaPausePeriods`, `PriorityDowngradeRequests`, 6 columns on `TicketSlaInstances` |

Checks: `SELECT MigrationId FROM __EFMigrationsHistory ORDER BY 1 DESC` shows #5 first; model drift check — *Local*: `dotnet ef migrations has-pending-model-changes` → "No changes" (run before and after generating #5); *CI*: the *DB Migration Validation* workflow applies all migrations to SQL Server and compares the exact table list (updated in this PR to 60 tables) — **result: passed on PR #78 (run 37828357971)**; *UAT*: not run.

## 17.4 Configuration checklist

Set (names only — values come from the secret store): `ConnectionStrings__TigerCsDatabase`, `ConnectionStrings__PACTRPT`, `ConnectionStrings__CrmDatabase`, `Jwt__SigningKey`, `Crm__SecretKey`, `PactApi__ApiKey`, `EmailNotifications__Password`, `CrmDocuments__OtpCodePepper` (required before enabling documents), `Genesys__ScreenPopWebBaseUrl`, `Authorization__ServiceIdentity__EmployeeIds__0=<Genesys service account employee id>`, `BackgroundJobs__Enabled=true`, `Tasleeh__Provider=Unavailable`. Leave off until their blockers clear: `CrmDocuments__Enabled`, Collections reminder channels, `NextPayment`, `Collections__Campaigns__FinancialSourceValidated`. Full reference: [12](12-configuration-reference.md). Order: [13](13-deployment-order.md).

## 17.5 Credential rotation (by secret name and affected service — values are never reproduced here)

History scan (only key names inspected) found non-empty values committed for the following settings. The working tree is clean, but git history, forks and any copy of old `publish/` zips still carry them, so **all must be treated as exposed and rotated**:

| Secret (config key) | Where it was committed (file) | Service / system affected by rotation | Who rotates |
|---|---|---|---|
| `ConnectionStrings:TigerCsDatabase` (SQL login password) | `src/TigerCS.Api/appsettings.json` (history) | TigerCS SQL Server `TigerCsTicketing`; API, Web-to-API, Hangfire | DBA / IT |
| `ConnectionStrings:PACTRPT` (SQL login password) | same | PACT reporting SQL Server; Collections receivables and campaigns | PACT DBA |
| `ConnectionStrings:CrmDatabase` (SQL login password) | same | CRMDB SQL Server; receivables contact enrichment | CRM DBA |
| `Crm:SecretKey` (`X-SECRET-KEY`) | same | Tiger CRM `TicketingSystem/*` API — **CRM and TigerCS must change together** | CRM team |
| `PactApi:ApiKey` | same | PACT customer API | PACT team |
| `EmailNotifications:Password` (SMTP mailbox password) | `src/TigerCS.Api/appsettings*.json` (2026-10-21 to 2026-10-23) and `publish/TigerCS.Api/appsettings*.json` | Microsoft 365 `no_reply` mailbox; customer e-mail, OTP and document delivery. See [20](20-smtp-credential-exposure-and-rotation.md) | M365 administrator |
| Dev fallback in `TigerCsDbContextFactory.cs` and CI SQL password in `.github/workflows/db-migration-validation.yml` | current tree | local/CI containers only — confirm they are not reused anywhere real | Repo owner |

Procedure: create the new secret in the secret store → update the dependent system and TigerCS configuration in the same window → restart → run the *UAT* checks for lookup, receivables and CRM calls → disable the old credential → (optional, separate decision) purge history with `git filter-repo` and force-push after notifying all clones. The JWT signing key was not found in history, but rotate it if it was ever shared in the same files. The SMTP password **was** committed (see 20) and must be rotated.

## 17.6 What was verified where

| Claim | Local | CI | UAT |
|---|---|---|---|
| Solution builds; 3,7xx tests pass (exact count in the PR description) | ✔ | ✔ `build-and-test` passed on PR #78 | — |
| EF model has no drift vs migrations | ✔ | — | — |
| Migrations apply to SQL Server and tables match | — | ✔ `validate-migration` passed on PR #78 (run 37828357971) | — |
| Data-action templates render valid JSON (string, null, Arabic, special characters) | ✔ (simulator, not Genesys's engine) | — | — |
| Forwarding code against real TigerGroupWeb / Genesys | — | — | not run |
| OTP e-mail, CRM document files, SMTP delivery | fakes only | — | not run |
| PACT/EDSM data, campaign amounts | fakes only | — | not run |
