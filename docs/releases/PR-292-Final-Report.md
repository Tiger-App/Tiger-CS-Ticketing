# PR #292 — consolidated final report (review of both sessions' work)

Nothing was merged, deployed or executed against Production.

**Requirement verification matrix:** `docs/releases/PR-292-Requirement-Verification-Matrix.md`. **Closed is terminal on every channel** (domain `Ticket.Reopen` always throws; application policy, approvals, API and Genesys events refuse); the ~90 deleted tests are restored as terminal-state rejection tests. **Test results on the integrated code** (docs-only commits follow): UTC host 9460 passed / 0 failed; `TZ=Asia/Dubai` 9460 / 0; real SQL Server 9569 / 0 failed (56 Collections RCV tests skipped: they need `TIGERCS_PERF_SQL`). Final commit SHA, CI and test numbers: see the PR description and the reviewer message (they change with every push).

## 1. Scope decision (approved): Voice and Digital are separate
The agent-identification lifecycle differs: for a call the answering agent is reported at or just after the call; for WhatsApp / other digital channels the receiving agent id can arrive up to 72 hours later. Therefore:
* **Voice (Phone):** the approved voice assignment changes apply (`docs/Genesys/Voice-Ownership-Rules.md`): AI Agent owns the call ticket; no Round Robin at handoff time; the answering employee owns it; an unmapped or ineligible answerer leaves it queued and flagged; a failed call (ended, nobody answered, window passed) is **queued and flagged, not assigned** (no last-offered agent, no Round Robin; a later confirmed connection assigns the answerer) by `VoiceCallReconciler`; first response = the real connect time; a later agent on the same call is history only.
* **Digital channels (WhatsApp, web chat, Instagram, Facebook, social media, e-mail):** the assignment behaviour **from `main`** is unchanged (Call Center first, Round Robin at creation / handoff / follow-up, end-of-conversation promotion, next-agent fallback, reassignment on transfer per `Genesys:ReassignOnTransfer`, `AssignStaleAi`, Call-Center guard on AI closure).
* **Universal (all channels):** Closed / Resolved tickets are terminal; late or duplicate events are audit evidence only; the timestamp guard; Assignment History rows; read-model handling states; the single `TicketDisplay.ToUae` conversion.

History: a review round briefly unified the two lifecycles under one rule set. That overrode this approved separation and was **reverted** (`git revert 419899e3`); the code is the second pass's voice-only implementation again, plus the fixes below.

## 2. What this review changed on top of the two sessions' work
* Fixed a defect in `Deploy_TigerCS_PROD_AddInteractionEndReceivedAt.sql` (a dry run failed to compile because the post-commit section referenced the new column statically) and added real-SQL-Server tests for it and for the verification script.
* The `PreviousDepartmentId` migration test now accepts the later `EndReceivedAtUtc` migration as the only pending one.
* `VoiceCallReconciliation:DryRun = true` is shipped in **every** environment (observe-only) so the automatic assignment of unanswered voice calls (last offered agent) is enabled deliberately after reviewing the logs.
* Repository hygiene / secret guard, Web `/health` version, clean-publish tooling, historical-timestamp scripts, secret-rotation guide (earlier rounds) are preserved.

## 3. Migrations (apply in this order, before deploying the build)
| # | Migration | Why it is needed | Effect on existing production data | Script |
|---|---|---|---|---|
| 1 | `20261119120249_AddAssignmentHistoryPreviousDepartment` | Assignment History must show the department a ticket came from when it is only queue-routed (department changes, owner does not). | One nullable `int` column on `TicketAssignmentHistory`; **no existing row touched, no backfill** (old rows show no previous department). | `Deploy_TigerCS_PROD_AddAssignmentHistoryPreviousDepartment.sql` |
| 2 | `20261120074059_AddInteractionEndReceivedAt` | The no-answer window must be measured from when TigerCS *received* the end, not the sender's (possibly delayed or wrong) event time. | One nullable `datetime2` on `TicketInteractions`; **no row touched, no backfill** (older calls fall back to their `EndedAtUtc`, and the job's look-back is 24 h). | `Deploy_TigerCS_PROD_AddInteractionEndReceivedAt.sql` |

Both scripts: guard on server + database name, `@DryRun = 1` default (applies, validates, **rolls back**), single transaction, idempotent (a column that exists with the right shape is kept, a wrong shape aborts), schema validation, row counts unchanged, registers the migration in `__EFMigrationsHistory` with the EF product version. #2 refuses to run unless #1 is recorded. `Verify_TigerCS_PROD_AllMigrations_ReadOnly.sql` lists both. EF-generated idempotent equivalents: `AddAssignmentHistoryPreviousDepartment.sql`, `AddInteractionEndReceivedAt.sql`. All covered by real-SQL-Server tests (dry run, commit, idempotency, wrong database, missing dependency, verify script).
Historical timestamp remediation is separate and not part of this order (`Diagnose_…GenesysHistoricalTimestamps_ReadOnly.sql`, `Remediate_…GenesysHistoricalTimestamps.sql`, dry-run by default, **needs your approval**).

## 4. Configuration (no secret involved)
| Setting | Default | Purpose | Recommended production value |
|---|---|---|---|
| `Genesys:AssignOnAnswer` | true | The employee Genesys reports *connected* owns the ticket. | **true** |
| `Genesys:ReassignOnTransfer` | true | A different employee connected to a live **digital** conversation takes over (voice too: a human who actually answers a transferred call takes the existing ticket; ringing / offered agents never do). | **true** (unchanged from `main`) |
| `Genesys:AssignOnTranscriptEvidence` | true | A human transcript line from a mapped, eligible agent proves who received it. | **true**; false = assign only on the connect event (stricter) |
| `Genesys:AutoRaiseHandoffOnConversationEnd` | false | Raise a handoff work item from an end event alone. | **false** (it never assigns now, but would create work items) |
| `Genesys:FutureTimestampToleranceMinutes` | 5 | An event time further in the future than this is impossible (clock skew / local-as-UTC) and is not stored as sent; audited. | **5** |
| `VoiceCallReconciliation:Enabled` | true | Registers the job (independent of Hangfire). **Voice only.** | **true** |
| `VoiceCallReconciliation:DryRun` | **true** (shipped in every environment) | true = classify and log only. When live it assigns an unanswered ended voice call to the last agent Genesys offered it to (never Round Robin; nobody offered → unassigned), and gives a ticket to a person who answered once their id is mapped. | **true for the first day, then false** after reviewing the log |
| `VoiceCallReconciliation:NoAnswerWindowSeconds` | 300 | Seconds after the end was *received* before "nobody answered" is concluded (flag + queue). Re-read every run. | **300** (60 flags sooner but risks flagging a call whose answer event is late) |
| `VoiceCallReconciliation:LookbackHours` | 24 | Older ended calls are never swept up. | **24** |
| `VoiceCallReconciliation:IntervalSeconds` / `BatchSize` / `MaxActionsPerRun` | 60 / 100 / 50 | Cadence and write limits. | defaults |
| `StalePhoneAssignment:Enabled` (existing) | false | Legacy backlog tool. | **false** |
| `AiOnlyAutoClose` (existing) | off | AI-only ticket auto-close. | **off** |
| `EmailNotifications:Password` | (empty) | SMTP secret — **server-side only**, rotate (see `PR-292-Secret-Rotation.md`). | environment variable `EmailNotifications__Password` |

The voice reconciler no longer assigns anyone for a failed call (it flags and queues; the former last-offered assignment was removed). Production ships it observe-only, which Production ships switched to observe-only.

## 5. Clean API and Web publish
From a clean clone at the **approved merge commit**: `.\tools\deploy\Publish-TigerCS.ps1 -Output C:\build\TigerCS` (refuses a dirty tree or output inside the repo; runs the tests; stamps `-p:SourceRevisionId=<commit>`; blocks secrets / Development / UAT settings; writes `MANIFEST.txt`), or the manual *Release package* workflow. Deploy with `Test-IisReadiness.ps1` then `Deploy-TigerCS.ps1 -Source …` (Api, then Web); roll back with `Rollback-TigerCS.ps1`. Verify with `GET /health` on both (same `+<commit>`) and `PR292_COMMIT=<sha> tools/verify-deployed-fixes.sh <api-version> <web-version>`. The old tracked `publish/` folder is removed (it was a 22 Oct build without #288 and contained a committed SMTP password).

## 6. Smoke tests and ordered deployment
`docs/releases/PR-292-Deployment-Checklist.md` §6 (A–G; each step waits for your approval).

## 7. Remaining risks and upstream dependencies
* **Genesys must send UTC.** The converter is host-independent and handles every offset spelling, but a flow that writes *local* time with a `Z` is only detected when it is in the future (tolerance 5 min). Upstream fix: Architect sends `ToString(Flow.StartDateTimeUtc)`.
* **Voice:** `routing.agentId` / `connectedAtUtc` identify the answering agent; an unmapped id leaves the ticket queued and flagged until mapped; a failed call is dealt ≈ 5–6 min after the end (window + interval) once the reconciler is live.
* **Digital:** unchanged from `main`; the late (up to 72 h) agent id is handled by the existing behaviour. Any future digital change needs its own approved requirement.
* **Resolved:** the earlier tension between "Round Robin for a failed call" and "stays queued until acceptance" is replaced by the final rule: a failed call goes to the **last offered agent** (never Round Robin); nobody offered → unassigned for follow-up.
* Historical wrong timestamps are **not** corrected by the deployment (separate, approved remediation; SLA flags are reported, not recalculated).
* The 56 Collections RCV real-SQL tests need a separate fixture database (`TIGERCS_PERF_SQL`) and are not executed in CI or here.
* The earlier SMTP password remains in Git history until a separately approved rewrite; rotate it.
* UAE calendar-day filtering of dashboards/reports/exports is a separate follow-up task (not in this PR).
