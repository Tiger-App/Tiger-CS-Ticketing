# PR #292 — Release verification and deployment checklist

Nothing in this document has been executed against Production. Every Production script is **dry-run by default** and needs your approval to commit.

## 1. Approved changes → where they live → how they are proven

| Approved change | Code | Proof (tests; all run in CI, SQL ones on a real SQL Server) |
|---|---|---|
| Correct UTC handling, host-independent | `GenesysUtcDateTimeConverter` (7 inbound fields) · `TigerCsDbContext.NormalizeLocalDateTimesToUtc` · `TicketDisplay.ToUae` (single +4h) | `GenesysWireTimestampTests` (every spelling `Z`/`+00:00`/`+04:00`/zoneless/fractions), run on a UTC host **and** with `TZ=Asia/Dubai` (CI step "Genesys timestamps on a UTC+4 host"); `AiEndedHumanFollowUpSqlServerTests.GenesysUtc_InEveryWireSpelling…` |
| Actual AI ownership | `Tickets.AiAgentOwnerEmployeeId` vs `CurrentOwnerEmployeeId`, `Ticket.TransferToDepartment(keepAiAgentOwner)` | `AiEndedHumanFollowUpSqlServerTests` (full lifecycle), `AgentHandoffTests` |
| No automatic Round Robin after AI conversation completion | removed from every Genesys path; `AssignStaleAi` report-only; unresolved tickets go to the Call Center queue with the AI Agent still owner | ~30 rewritten test classes, `CallCenterReconciliationSqlServerTests` |
| Human assignment only on valid acceptance / enabled transcript evidence / authorized manual assignment | `GenesysHumanHandlingEvidence`, `TicketAutoAssignmentService.ApplyAnsweringAgentAsync`, `TicketAssignmentAppService.RecordGenesysManualAssignmentAsync` | `AiEndedHumanFollowUpSqlServerTests`, `AnsweringAgentAssignmentSqlServerTests` |
| Complete Assignment History | `TicketAssignmentHistoryEntry` (+`PreviousDepartmentId`), queue-routing / department-transfer / follow-up rows | lifecycle test asserts every transition row; `EfModelAndMigrationConsistencyTests` |
| Correct SLA calculation | queue routing is never a human response and never resets SLA timers; explicit-workflow departments (Collections/Legal) preserved | `SlaAndEscalation` suite, `GenesysTimelineAndOwnershipTimingTests`, lifecycle SQL test |
| No automatic reopening of closed tickets | Closed is terminal for automated flows | `ClosedTicketIsTerminalTests` |

## 2. Review of the 30 `ToLocalTime()` replacements

All 30 (plus the `DateTime.Now.Date` on the Dashboard) are in `TigerCS.Web` **display** code and now call `TicketDisplay.ToUae(utc)` = `AsUtc(utc).AddHours(4)`.

* **Why it is the same or better:** `ToLocalTime()` used the *web server's* zone (identity on a UTC server, +4 only if the IIS host was UAE-zoned) and returned the value unchanged for `Kind=Unspecified` (what EF materialises). `ToUae` treats every stored value as UTC and converts exactly once, on any host. A test bans any new `ToLocalTime` in Web (`GenesysWireTimestampTests`).
* **Dashboard date filters, daily/monthly reports, SLA, Excel exports are unaffected:** none of them call `ToLocalTime`. Filters and the dashboard/report day boundaries are computed server-side in the Application layer on UTC (`DashboardAppService` uses `nowUtc.Date`); SLA working-hours use an explicit `TimeZoneInfo` (Asia/Dubai) in the Application layer; the Excel/CSV exports (`CollectionsCampaignReviewWorkbook`, `DispatchAdminReportService`) contain no zone conversion. The 30 sites are labels, tooltips and "today / yesterday" cell text.
* **Only visible change:** the Dashboard "today" comparison now uses the UAE day (`ToUae(UtcNow).Date`) instead of the server-local day, so a ticket created at 00:30 UAE is "today", also on a UTC server.
* **Pre-existing, not changed here (flag for a later decision):** dashboard/report *filters* bucket by UTC calendar day, so tickets created 00:00–04:00 UAE appear under the previous UTC date in date-range filters. Changing it alters report numbers, so it is deliberately not part of this hotfix.

## 3. Database scripts (all NOT executed; run in SSMS, one batch each, in this order)

| # | Script | Writes? | Notes |
|---|---|---|---|
| 0 | `Verify_TigerCS_PROD_AllMigrations_ReadOnly.sql` | no | before: expect `20261119120249_AddAssignmentHistoryPreviousDepartment` **pending**; after: nothing pending, `PreviousDepartmentId` **Present = 1** |
| 1 | `Deploy_TigerCS_PROD_AddAssignmentHistoryPreviousDepartment.sql` | **yes** | one nullable `int` column, no backfill, guarded (server + DB name), `@DryRun = 1` by default (applies, validates, ROLLS BACK). Set `@DryRun = 0` to commit. Rollback in the header. Tested on SQL Server (`AssignmentHistoryPreviousDepartmentScriptSqlServerTests`). |
| 2 | `Diagnose_TigerCS_PROD_Ticket1878_GenesysTimelineOwnership_ReadOnly.sql` | no | evidence for ticket 1878 |
| 3 | `Diagnose_TigerCS_PROD_GenesysHistoricalTimestamps_ReadOnly.sql` | no | classifies every Genesys conversation `CONFIRMED_PLUS4H` / `SUSPECT` / OK, shows stored → proposed values, transcript lines, connect times, a separate **SLA REVIEW** result set |
| 4 | `Remediate_TigerCS_PROD_GenesysHistoricalTimestamps.sql` | **yes, only after your approval** | `@DryRun = 1` default (returns before opening any transaction). See below. |

**Remediation rules.** It never subtracts four hours blindly. A conversation is changed only when two independent server-recorded facts prove it (start later than the ticket's own server-clock creation; end/transcript later than the audit-recorded receipt of the end event; each by more than 5 min and at most 4 h + 5 min) and the corrected values are internally consistent. Single-evidence or mixed conversations are **SUSPECT** and never touched. It changes only `InteractionStartedAtUtc`, `EndedAtUtc`, `SentAtUtc`, `ConnectedAtUtc`. Ticket status, owner, department, closure, resolution, assignments, status history, SLA rows and all existing audit rows are checksum-verified unchanged inside the transaction (it rolls back otherwise); one new append-only `GenesysTimestampCorrected` audit row per corrected ticket records old and new values; a `ROLLBACK STATEMENTS` result set restores the exact old values; re-running finds nothing. SLA is **not** recalculated: affected SLA flags are listed separately for a separate decision. Pilot with `@TicketId = 1878`, set `@OnlyCreatedBeforeUtc` to the go-live moment of the new build. Tested on SQL Server (`GenesysHistoricalTimestampScriptsSqlServerTests`: proven / correct / two suspect conversations, dry run writes nothing, only timestamp columns change, protected state byte-identical, idempotent, pilot scoping, wrong-database abort).

## 4. Stale `publish/` folder — findings

* `publish/` is tracked in git (230 files, 320 MB). Its binaries are stamped with commit **`3e48b2f` (22 Oct 2026)**, which is **222 commits behind `main`** and does **not** contain #288 (nor #292). *(An earlier note in this PR said the folder was "dated 18 Nov 05:32"; that was only the checkout time of the files and is withdrawn.)*
* If Production was deployed from it, Production runs a pre-#288 build, which is exactly what reproduced the +4h on 1878. Confirm with `GET /health` on the Production API: `version` ends with `+<commit>`; run `tools/verify-deployed-fixes.sh <version>`.
* **Security:** the committed `publish/TigerCS.Api/appsettings.json` and `appsettings.Development.json` contain non-empty secret settings (an SMTP `Password` and a `ClientSecret`). Treat them as exposed: **rotate** them. Values are intentionally not repeated here. See the `security/remove-committed-*` branches.
* This PR adds `/publish/` to `.gitignore` (stops new commits). It does **not** delete the 320 MB from the repository (that needs your approval); when approved: `git rm -r --cached publish && git commit -m "Stop tracking build output"` (history purge is a separate decision, see the security branches).
* **Clean build and deploy (never from the repo's `publish/`):**
  1. On the build machine, from a clean clone of the merged `main` commit: `.\tools\deploy\Publish-TigerCS.ps1 -Output C:\build\TigerCS` (refuses output inside the repo or a dirty tree; runs the tests; stamps `-p:SourceRevisionId=<commit>`; blocks secrets/Development/UAT settings; writes `MANIFEST.txt` with the commit). Linux/CI equivalent: `tools/build-release.sh`.
  2. Back up Production (full DB backup + the current IIS site folders), then `.\tools\deploy\Deploy-TigerCS.ps1` with the new stamped folder; roll back with `tools/deploy/Rollback-TigerCS.ps1`.
  3. Production secrets live only in the server's environment / secret store, never in the package.

## 5. Verifying the deployed version

* API: `GET https://<api>/health` → `{"status":"healthy","version":"1.0.0+<commit>"}`
* Web: `GET https://<web>/health` → `{"status":"healthy","version":"1.0.0+<commit>"}` (**new in this PR**)
* `PR292_COMMIT=<final commit> tools/verify-deployed-fixes.sh <api-version> <web-version>` — exit 0 only if every required fix (incl. #288) is in the build **and** Api and Web are the same commit. `unknown` as a version means the package was not built by the release script.

## 6. Deployment checklist (in order; each step needs your go-ahead)

1. [ ] CI green on the final commit; PR approved and merged by you.
2. [ ] Full DB backup; copy of the current IIS Api and Web folders.
3. [ ] Run script 0 (before) and save the output.
4. [ ] Script 1 with `@DryRun = 1`; read the output; then `@DryRun = 0`.
5. [ ] Clean publish from the merged commit (section 4); deploy Api then Web.
6. [ ] `GET /health` on both; `verify-deployed-fixes.sh` passes (same commit).
7. [ ] Script 0 again: nothing pending, column present.
8. [ ] Create a test Genesys conversation with `+00:00` timestamps; check start/end/transcript match the Genesys clock (UTC) and show +4h exactly once in the UI.
9. [ ] Script 2 (ticket 1878) and script 3 (all history); review CONFIRMED vs SUSPECT.
10. [ ] **Only with your approval:** script 4 pilot (`@TicketId = 1878`, dry run, then real), review, then the rest in batches.
11. [ ] Rotate the secrets that were committed under `publish/`.

Rollback: DB column is additive (header of script 1 has the `DROP COLUMN`); application via `Rollback-TigerCS.ps1`; timestamp corrections via the generated `ROLLBACK STATEMENTS`.
