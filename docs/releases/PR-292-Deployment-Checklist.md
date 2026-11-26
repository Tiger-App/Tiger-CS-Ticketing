# PR #292 — Release verification and deployment checklist

Nothing in this document has been executed against Production. Every Production script is **dry-run by default** and needs your approval to commit.

## 1. Approved changes → where they live → how they are proven

| Approved change | Code | Proof (tests; all run in CI, SQL ones on a real SQL Server) |
|---|---|---|
| Correct UTC handling, host-independent | `GenesysUtcDateTimeConverter` (7 inbound fields) · `TigerCsDbContext.NormalizeLocalDateTimesToUtc` · `TicketDisplay.ToUae` (single +4h) | `GenesysWireTimestampTests` (every spelling `Z`/`+00:00`/`+04:00`/zoneless/fractions), run on a UTC host **and** with `TZ=Asia/Dubai` (CI step "Genesys timestamps on a UTC+4 host"); `AiEndedHumanFollowUpSqlServerTests.GenesysUtc_InEveryWireSpelling…` |
| **Scope: the ownership rules are PHONE-only** (second pass, 20 Nov) | gated on the interaction's channel in `GenesysTicketUpdateAppService`, `GenesysAgentHandoffAppService`, `TicketAutoAssignmentService` (`voiceOwnership`), `TicketCreationAppService`, `TicketRequestTypeRoutingService`, `UnassignedTicketClassifier`; digital channels keep their previous behaviour | `DigitalChannelsKeepTheirPreviousBehaviourTests`; the digital tests of the first pass restored from `main` |
| Actual AI ownership (voice) | `Tickets.AiAgentOwnerEmployeeId` vs `CurrentOwnerEmployeeId`, `Ticket.TransferToDepartment(keepAiAgentOwner)` | `VoiceOwnershipRulesTests`, `AiEndedHumanFollowUpSqlServerTests` (full lifecycle), `AgentHandoffTests` |
| AI closure: explicit `closedByAi` only; `ended` never closes / flags / assigns; `ended` may precede `closedByAi`; Call Center no longer blocks a voice closure | `GenesysTicketUpdateAppService.ApplyClosedByAiAsync`, `GenesysConversationEndAppService` | `VoiceOwnershipRulesTests`, `ClosedByAiTests` |
| Round Robin for a failed voice call only after the call ended, nobody answered and a configurable window passed — never at handoff time | `VoiceCallReconciler` + `VoiceCallReconciliationHostedService`, `VoiceCallReconciliation:*`, `TicketInteractions.EndReceivedAtUtc` | `VoiceCallReconcilerTests` (classifier + executor), `VoiceEvidenceSqliteTests`, `VoiceCallReconciliationRegistrationTests` |
| Human assignment only on valid acceptance (voice: first mapped person who answered; a later agent who actually answers a transferred call takes the ticket (history kept); one who is only offered / ringing does not; unmapped agent: queued + flagged, reconciled when the mapping is fixed) | `GenesysHumanHandlingEvidence`, `TicketAutoAssignmentService.ApplyAnsweringAgentAsync`, `TicketAssignmentAppService.RecordGenesysManualAssignmentAsync` | `VoiceOwnershipRulesTests`, `VoiceCallReconcilerTests`, `AiEndedHumanFollowUpSqlServerTests`, `AnsweringAgentAssignmentSqlServerTests` |
| Voice first response = the real connect time of the first mapped person who answered | `GenesysAnsweringAgentAssignmentService.TryRecordVoiceFirstResponseAsync`, inquiry `connectedAtUtc`, `TeamPerformanceQueryRepository.FirstResponseMetMoments` | `VoiceOwnershipRulesTests`, `VoiceFirstResponseAttributionSqliteTests` |
| Complete Assignment History | `TicketAssignmentHistoryEntry` (+`PreviousDepartmentId`), queue-routing / department-transfer / follow-up rows | lifecycle test asserts every transition row; `EfModelAndMigrationConsistencyTests` |
| Correct SLA calculation | queue routing is never a human response and never resets SLA timers; explicit-workflow departments (Collections/Legal) preserved | `SlaAndEscalation` suite, `GenesysTimelineAndOwnershipTimingTests`, lifecycle SQL test |
| No automatic reopening of closed tickets, **every channel**; the historical owner stays visible | Closed is terminal for automated flows; `GenesysAgentHandoffAppService.RequestAsync` refuses a closed ticket; `TicketQueryAppService` "last responsible" for every closed ticket | `ClosedTicketIsTerminalTests`, `ClosedTicketAllChannelsTests` |

## 2. Review of the 30 `ToLocalTime()` replacements

All 30 (plus the `DateTime.Now.Date` on the Dashboard) are in `TigerCS.Web` **display** code and now call `TicketDisplay.ToUae(utc)` = `AsUtc(utc).AddHours(4)`.

* **Why it is the same or better:** `ToLocalTime()` used the *web server's* zone (identity on a UTC server, +4 only if the IIS host was UAE-zoned) and returned the value unchanged for `Kind=Unspecified` (what EF materialises). `ToUae` treats every stored value as UTC and converts exactly once, on any host. A test bans any new `ToLocalTime` in Web (`GenesysWireTimestampTests`).
* **Dashboard date filters, daily/monthly reports, SLA, Excel exports are unaffected:** none of them call `ToLocalTime`. Filters and the dashboard/report day boundaries are computed server-side in the Application layer on UTC (`DashboardAppService` uses `nowUtc.Date`); SLA working-hours use an explicit `TimeZoneInfo` (Asia/Dubai) in the Application layer; the Excel/CSV exports (`CollectionsCampaignReviewWorkbook`, `DispatchAdminReportService`) contain no zone conversion. The 30 sites are labels, tooltips and "today / yesterday" cell text.
* **Only visible change:** the Dashboard "today" comparison now uses the UAE day (`ToUae(UtcNow).Date`) instead of the server-local day, so a ticket created at 00:30 UAE is "today", also on a UTC server.
* **Pre-existing, not changed here (flag for a later decision):** dashboard/report *filters* bucket by UTC calendar day, so tickets created 00:00–04:00 UAE appear under the previous UTC date in date-range filters. Changing it alters report numbers, so it is deliberately not part of this hotfix; it is tracked as a separate follow-up task (UAE calendar boundaries for every business-day filter, daily/monthly report, performance/SLA report and export, with UTC storage unchanged).

## 3. Database scripts (all NOT executed; run in SSMS, one batch each, in this order)

| # | Script | Writes? | Notes |
|---|---|---|---|
| 0 | `Verify_TigerCS_PROD_AllMigrations_ReadOnly.sql` | no | before: expect `20261119120249_AddAssignmentHistoryPreviousDepartment` and `20261120074059_AddInteractionEndReceivedAt` **pending**; after: nothing pending, `PreviousDepartmentId` and `TicketInteractions.EndReceivedAtUtc` **Present = 1** |
| 1 | `Deploy_TigerCS_PROD_AddAssignmentHistoryPreviousDepartment.sql` | **yes** | one nullable `int` column, no backfill, guarded (server + DB name), `@DryRun = 1` by default (applies, validates, ROLLS BACK). Set `@DryRun = 0` to commit. Rollback in the header. Tested on SQL Server (`AssignmentHistoryPreviousDepartmentScriptSqlServerTests`). |
| 1b | `Deploy_TigerCS_PROD_AddInteractionEndReceivedAt.sql` | **yes** | one nullable `datetime2` column on `TicketInteractions` (when TigerCS received a call's end — the anchor of the voice no-answer window), no backfill, guarded, `@DryRun = 1` by default. Run **after** 1 and **before** the application. Rollback in the header. EF-generated equivalent: `AddInteractionEndReceivedAt.sql`. Tested on SQL Server (`InteractionEndReceivedAtScriptSqlServerTests`: dry run, commit, idempotent re-run, wrong-database abort, EF has nothing left to apply). |
| 2 | `Diagnose_TigerCS_PROD_Ticket1878_GenesysTimelineOwnership_ReadOnly.sql` | no | evidence for ticket 1878 |
| 3 | `Diagnose_TigerCS_PROD_GenesysHistoricalTimestamps_ReadOnly.sql` | no | classifies every Genesys conversation `PROVEN` (`PROVEN_START_OFFSET` / `PROVEN_FULL_OFFSET`) / `SUSPECT` / `CORRECT`, counts each class, reason and type of inconsistency, shows a DryRun preview (ticket, conversation, column, old UTC, proposed UTC, reason) for PROVEN only, and a separate **SLA REVIEW** result set; reports ZERO safe corrections when no conversation has independent evidence |
| 4 | `Remediate_TigerCS_PROD_GenesysHistoricalTimestamps.sql` | **yes, only after your approval** | `@DryRun = 1` default (returns before opening any transaction). See below. |

**Remediation rules.** It never subtracts four hours blindly, and it corrects **only `PROVEN_START_OFFSET`** conversations (start later than the ticket's own server-clock creation AND, minus 4 h, landing just before the conversation's own first transcript line; end and lines plausible), re-validated against the current data on every run. It writes exactly one column, `TicketInteractions.InteractionStartedAtUtc`: never `EndedAtUtc`, transcript `SentAtUtc`, `ConnectedAtUtc`, ticket creation / First Human Response dates, SLA, status, ownership, assignments, history or resolutions (all checksum-verified unchanged inside the transaction, otherwise it rolls back). `PROVEN_FULL_OFFSET`, `SUSPECT` and `CORRECT` conversations are counted (result set `REVALIDATION`, next to the approved diagnostic numbers) and never touched. Original values are preserved in one new append-only `GenesysTimestampCorrected` audit row per conversation (original and new start, interaction id, conversation id, proof), a `ROLLBACK STATEMENTS` result set, and `@RollbackRunId` (restores from the audit rows, optimistic, audited). Idempotent: a corrected start no longer matches the proof, and a conversation with a correction audit row is never planned again. A real run over all tickets aborts if more conversations are PROVEN than `@ApprovedProvenStart` (1693). `@DryRun = 1` is the default. Pilot one ticket from the PLAN with `@TicketId`. Tested on SQL Server (`GenesysHistoricalTimestampScriptsSqlServerTests`).

## 4. Stale `publish/` folder — resolved in this PR

* The tracked `publish/` folder (230 files, 320 MB) was a build of commit **`3e48b2f` (22 Oct 2026)**, **222 commits behind `main`**, without #288 or #292. *(An earlier note said "dated 18 Nov 05:32"; that was only checkout time and is withdrawn.)* If Production was deployed from it, Production runs a pre-#288 build, which is what reproduced the +4h on 1878; confirm with `GET /health` on the Production API (`version` ends with `+<commit>`; `tools/verify-deployed-fixes.sh <version>`).
* **Removed from the current tree** (`git rm -r publish`); `/publish/` stays in `.gitignore`. **History is not rewritten** (separate approval).
* **Secrets:** see `docs/releases/PR-292-Secret-Rotation.md` (key `EmailNotifications:Password`; rotation steps).
* **Guardrails:** `tools/check-repo-hygiene.sh` + `RepositoryHygieneTests` + CI step block tracked binaries, build output and non-empty secret settings.
* **Artifacts come only from an approved commit:** `Publish-TigerCS.ps1` and `tools/build-release.sh` refuse a dirty tree, run the tests, stamp `-p:SourceRevisionId=<commit>`, write `MANIFEST.txt`; the manual *Release package* workflow builds from the exact commit it runs on and uploads the package.

## 5. Verifying the deployed version

* API: `GET https://<api>/health` → `{"status":"healthy","version":"1.0.0+<commit>"}`; Web: `GET https://<web>/health` → same shape (new in this PR).
* `PR292_COMMIT=<approved commit> tools/verify-deployed-fixes.sh <api-version> <web-version>` — exit 0 only if every required fix (incl. #288 and #292) is in the build **and** Api and Web are the same commit. `unknown` means the package was not built by the release script.

## 6. Ordered deployment checklist (application only; each step waits for your go-ahead; nothing is executed yet)

Historical timestamp remediation (script 4 above) is **not part of this deployment**. It is a separate change with its own approval, run only after the application is verified.

**A. Before anything**
1. [ ] PR #292 approved and merged **by you**; note the merge commit SHA = `<APPROVED>`; CI green on it.
2. [ ] Full database backup (verify it restores) and a copy of the current IIS Api and Web folders (`Deploy-TigerCS.ps1` also snapshots; keep your own).
3. [ ] Rotate the committed SMTP secret (`docs/releases/PR-292-Secret-Rotation.md`) and set it as a server-side setting.

**B. Production migration verification**
4. [ ] SSMS on `TGAESHJDBP02`, database `TigerCS_Ticketing_Prod`: run `Verify_TigerCS_PROD_AllMigrations_ReadOnly.sql`. Expect: `20261119120249_AddAssignmentHistoryPreviousDepartment` and `20261120074059_AddInteractionEndReceivedAt` listed as **pending**; nothing "unknown"; no other pending. Save the output. (Stop if anything else is pending/unknown.)

**C. `PreviousDepartmentId` migration**
5. [ ] **Dry run:** `Deploy_TigerCS_PROD_AddAssignmentHistoryPreviousDepartment.sql` exactly as committed (`@DryRun = 1`). Expect: guard passed, "DRY RUN SUCCESS … ROLLED BACK", column not present afterwards.
6. [ ] **Actual execution (after your approval):** set `@DryRun = 0`, run once as one batch. Expect "COMMITTED", migration recorded, row counts unchanged.
7. [ ] Re-run `Verify_…AllMigrations_ReadOnly.sql`: only `AddInteractionEndReceivedAt` still pending, `TicketAssignmentHistory.PreviousDepartmentId` **Present = 1**.

**C2. `EndReceivedAtUtc` migration (voice reconciliation window anchor)**
7a. [ ] **Dry run:** `Deploy_TigerCS_PROD_AddInteractionEndReceivedAt.sql` exactly as committed (`@DryRun = 1`). Expect: guard passed, "DRY RUN SUCCESS … ROLLED BACK".
7b. [ ] **Actual execution (after your approval):** `@DryRun = 0`, one batch. Expect "COMMITTED", row counts unchanged, no backfill.
7c. [ ] Re-run `Verify_…AllMigrations_ReadOnly.sql`: nothing pending, `TicketInteractions.EndReceivedAtUtc` **Present = 1**.

**C3. Configuration (no deployment needed to change later)**
7d. [ ] `VoiceCallReconciliation` (shipped in `appsettings*.json`; **every environment ships `DryRun = true`** = classify and log, write nothing, so the automatic assignment of an unanswered ended **voice** call to its last offered agent is switched on deliberately): leave it observe-only for the first day, read the `DRY RUN: would reconcile …` log lines, then set `VoiceCallReconciliation:DryRun=false` (environment variable `VoiceCallReconciliation__DryRun`, no deployment). Window/impact: `docs/Genesys/Voice-Ownership-Rules.md` §3. Digital channels are not affected by this setting.

**D. Clean build and IIS deployment**
8. [ ] On the build machine, from a clean clone checked out at `<APPROVED>`: `.\tools\deploy\Publish-TigerCS.ps1 -Output C:\build\TigerCS` (runs tests, stamps the commit, blocks secrets / Development / UAT settings, writes `MANIFEST.txt`). Alternative: run the *Release package* workflow on `<APPROVED>` and download the artifact.
9. [ ] Confirm `MANIFEST.txt` commit == `<APPROVED>`.
10. [ ] IIS: `.\tools\deploy\Test-IisReadiness.ps1`, then `.\tools\deploy\Deploy-TigerCS.ps1 -Source C:\build\TigerCS\<stamp>` (Api then Web). Rollback path: `.\tools\deploy\Rollback-TigerCS.ps1` (DB column is additive; the old build ignores it).

**E. Build-version verification**
11. [ ] `GET <api>/health` and `GET <web>/health`: both `healthy`, both `version` end with `+<APPROVED>`.
12. [ ] `PR292_COMMIT=<APPROVED> tools/verify-deployed-fixes.sh <api-version> <web-version>` → exit 0 (all PRESENT, same commit).

**F. Production smoke tests** (use a test customer/ticket; record results)
13. [ ] Login to Web as Manager and as a CS Agent; dashboard and a ticket list load; dates look right.
14. [ ] Genesys test conversation with `…T..:..:..+00:00` timestamps: stored start/end/transcript equal the Genesys UTC time (check in SQL) and the UI shows them +4h exactly once (UAE).
15. [ ] **Voice**, AI-handled test call: owner = AI Agent, status "AI is handling"; no person assigned automatically; Assignment History shows the AI Agent row.
16. [ ] Same call, AI asks for a human (handoff naming an agent = an **offer**): "Waiting for human", in the Call Center queue, AI Agent still owner, **no assignment at handoff time**, SLA timers not reset. Hang up with nobody answering: after ≈ 5–6 minutes (window + one interval; only once `VoiceCallReconciliation:DryRun=false`) the ticket is assigned to the **last agent the call was offered to** (history source "last offered agent", audit `AssignLastOfferedAgent`; **not Round Robin**, no first response). Repeat with a call **nobody was ever offered**: it stays **unassigned** in the Call Center queue for follow-up. An agent who answers inside the window (or later) owns it instead.
17. [ ] A mapped Call Center agent answers (`routing.agentId` + `connectedAtUtc`): the ticket is assigned to that agent, history shows AI → employee, `FirstHumanResponseAtUtc` = the connect time; a second agent connecting later does **not** take it (handler only). A call answered by an **unmapped** agent id: queued + flagged, not Round Robin; map the id and the next run assigns it.
17a. [ ] **Digital** (WhatsApp / web chat) test conversation: behaviour unchanged from before this PR (Call Center first, one Round Robin agent).
18. [ ] Closed ticket stays closed after a late Genesys event (any channel) and still names its last responsible; a Collections/Legal-routed ticket keeps its department.
19. [ ] Run `Diagnose_TigerCS_PROD_Ticket1878_GenesysTimelineOwnership_ReadOnly.sql` and `Diagnose_TigerCS_PROD_GenesysHistoricalTimestamps_ReadOnly.sql` (both read-only); give the output to the reviewer.
20. [ ] Send a test email notification (new SMTP secret) and confirm delivery.

**G. Separate, later, only with explicit approval — historical timestamp remediation**
21. [ ] Review step 19 output (PROVEN vs SUSPECT vs CORRECT, counts per reason, SLA REVIEW). Then, only on your written approval: `Remediate_TigerCS_PROD_GenesysHistoricalTimestamps.sql` dry run (check `REVALIDATION`) → pilot one ticket from the PLAN with `@TicketId` → remainder in batches, with `@OnlyCreatedBeforeUtc` set to the go-live moment of step 10.

Rollback: DB column additive (`DROP COLUMN` in the script header); application via `Rollback-TigerCS.ps1`; timestamp corrections via the generated `ROLLBACK STATEMENTS`.
