# PR #292 — consolidated final report (review of both sessions' work)

Nothing was merged, deployed or executed against Production. Final commit SHA, CI and test numbers: see the PR description and the reviewer message (they change with every push; the numbers below are for the last locally verified code).

## 1. Review of the second pass (d2bd4137, d147651a, fc3d2fa6, ebac7a43, 26e449ee) against the approved implementation (5d794db7)

| New in the second pass | Business behaviour | Verdict |
|---|---|---|
| **Voice-only ownership rules** | The AI-ownership / answering-agent rules applied to Phone only; WhatsApp and other channels kept "Call Center first + Round Robin at creation / handoff / follow-up / end of conversation, next-agent fallback, `AssignStaleAi`". | **Conflicts with the mandatory rules** (AI conversation ending must not Round Robin; never assign another employee because the accepting one is unavailable) and with the instruction to apply voice and WhatsApp consistently. **Reconciled: one rule set for every channel** (`GenesysOwnershipRules`). The ~28 test classes it had reverted to old behaviour were restored to the approved versions; its digital "previous behaviour" tests were replaced by channel-parity tests. |
| **No-answer reconciliation (`VoiceCallReconciler`)** | After an ended call nobody answered and a window passed, deal the ticket by Round Robin. | **Conflicts** (unresolved ticket must stay queued until valid acceptance or manual assignment). **Changed:** it now *flags "waiting for a person" and queues in the Call Center; AI Agent stays owner; nobody is assigned.* The useful half is kept: a person who actually answered but was unmapped gets the ticket once the mapping is valid. |
| **`VoiceCallReconciliation` settings** | Enabled/DryRun/window/lookback/interval/batch/max-actions. | Kept. Production now ships `DryRun = true` (observe-only) until reviewed. |
| **`EndReceivedAtUtc` migration** | Records when TigerCS received a conversation's end; anchors the window. | Required by the reconciler. Additive, nullable, no backfill. **Defect found and fixed:** its deploy script failed to compile on a dry run (static reference to the column in the post-commit section). |
| **Display-time helper** | A separate `DubaiTime` helper. | Folded into the single `TicketDisplay.ToUae` (merge fc3d2fa6); doc references corrected. |
| Voice first response at the connect time | `GenesysCallAnswer`, write-once, receipt time audited if no connect time. | Kept (voice-specific by nature). |
| Closed-ticket history ("last responsible"), AI closure attribution | Display only. | Kept. |
| "No owner change when a later agent continues a voice call" | Described as approved 20 Nov; not verifiable. | **Not kept in code**: `Genesys:ReassignOnTransfer` (default true, as on `main`) governs every channel; set `false` for "first answerer keeps it". Needs your confirmation. |

Other adjustments needed for consistency: request-type classification by the AI assigns nobody on every channel; the read-model shows "Waiting for human"/"AI ended" on every channel; transcript-line assignment obeys `AssignOnTranscriptEvidence` on every channel. Kept as voice-specific (documented): call end is concluded after the window (answering agent can still arrive after `ended`), and AI closure is not refused for a call ticket queued in the Call Center.

## 2. Final business rules
`docs/Genesys/Ownership-Rules-All-Channels.md` (all 9 mandatory rules, each with its code location; the 3 deliberate voice differences).

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
| `Genesys:AssignOnAnswer` | true | The employee Genesys reports *connected* owns the ticket (never the next Round Robin name). | **true** |
| `Genesys:ReassignOnTransfer` | true | A different employee connected to a live conversation takes over; false = the first person who answered keeps it. | **true**, unless the business wants "first answerer keeps it" (decision needed) |
| `Genesys:AssignOnTranscriptEvidence` | true | A human transcript line from a mapped, eligible agent proves who received it. | **true**; false = assign only on the connect event (stricter) |
| `Genesys:AutoRaiseHandoffOnConversationEnd` | false | Raise a handoff work item from an end event alone. | **false** (it never assigns now, but would create work items) |
| `Genesys:FutureTimestampToleranceMinutes` | 5 | An event time further in the future than this is impossible (clock skew / local-as-UTC) and is not stored as sent; audited. | **5** |
| `VoiceCallReconciliation:Enabled` | true | Registers the job (independent of Hangfire). | **true** |
| `VoiceCallReconciliation:DryRun` | false (Production file: **true**) | true = classify and log only. | **true for the first day, then false** after reviewing the log |
| `VoiceCallReconciliation:NoAnswerWindowSeconds` | 300 | Seconds after the end was *received* before "nobody answered" is concluded (flag + queue). Re-read every run. | **300** (60 flags sooner but risks flagging a call whose answer event is late) |
| `VoiceCallReconciliation:LookbackHours` | 24 | Older ended calls are never swept up. | **24** |
| `VoiceCallReconciliation:IntervalSeconds` / `BatchSize` / `MaxActionsPerRun` | 60 / 100 / 50 | Cadence and write limits. | defaults |
| `StalePhoneAssignment:Enabled` (existing) | false | Legacy backlog tool. | **false** |
| `AiOnlyAutoClose` (existing) | off | AI-only ticket auto-close. | **off** |
| `EmailNotifications:Password` | (empty) | SMTP secret — **server-side only**, rotate (see `PR-292-Secret-Rotation.md`). | environment variable `EmailNotifications__Password` |

No new setting enables automatic assignment. The only automatic ownership change left is the one the business rules allow: the actual accepting / answering employee.

## 5. Clean API and Web publish
From a clean clone at the **approved merge commit**: `.\tools\deploy\Publish-TigerCS.ps1 -Output C:\build\TigerCS` (refuses a dirty tree or output inside the repo; runs the tests; stamps `-p:SourceRevisionId=<commit>`; blocks secrets / Development / UAT settings; writes `MANIFEST.txt`), or the manual *Release package* workflow. Deploy with `Test-IisReadiness.ps1` then `Deploy-TigerCS.ps1 -Source …` (Api, then Web); roll back with `Rollback-TigerCS.ps1`. Verify with `GET /health` on both (same `+<commit>`) and `PR292_COMMIT=<sha> tools/verify-deployed-fixes.sh <api-version> <web-version>`. The old tracked `publish/` folder is removed (it was a 22 Oct build without #288 and contained a committed SMTP password).

## 6. Smoke tests and ordered deployment
`docs/releases/PR-292-Deployment-Checklist.md` §6 (A–G; each step waits for your approval).

## 7. Remaining risks and upstream dependencies
* **Genesys must send UTC.** The converter is host-independent and handles every offset spelling, but a flow that writes *local* time with a `Z` is only detected when it is in the future (tolerance 5 min); a value in the past cannot be detected. Upstream fix: Architect sends `ToString(Flow.StartDateTimeUtc)`.
* **`routing.agentId` / `connectedAtUtc` are the acceptance signal.** Without them a person is only recognised by a human transcript line (`AssignOnTranscriptEvidence`). An agent id that maps to no Ticketing user leaves the ticket queued and flagged until the mapping is corrected — by design, never another employee.
* **Decision needed:** `Genesys:ReassignOnTransfer` (transfer takes ownership vs first answerer keeps it); whether the voice end-window difference is acceptable.
* **Voice end timing:** an unanswered ended call is flagged ≈ 5–6 min after the end (window + interval) instead of instantly; it is invisible as "waiting for a person" until then.
* Historical wrong timestamps are **not** corrected by the deployment (separate, approved remediation; SLA flags are reported, not recalculated).
* The 56 Collections RCV real-SQL tests need a separate fixture database (`TIGERCS_PERF_SQL`) and are not executed in CI or here.
* The earlier SMTP password remains in Git history until a separately approved history rewrite; rotate it.
* **UAE calendar-day filtering** of dashboards/reports/exports is a separate follow-up task (not in this PR).
