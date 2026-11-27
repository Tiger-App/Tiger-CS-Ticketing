# P0 — Genesys tickets stuck on the AI Agent in the Call Center queue

## Symptom
Open Genesys (phone) tickets stay **assigned to the AI Agent** in the Call Center queue. Employees cannot edit, update, follow up or close them, and the only way in is *Take Ownership*.

## Root cause (three independent defects, all in the voice path)
1. **The job that hands a failed-AI call to a person never wrote anything in production.** `VoiceCallReconciliation` shipped `DryRun = true` in `appsettings.json` and `appsettings.Production.json`
   ("observe-only until the operator reviews a day of logs", `docs/releases/PR-292-Deployment-Checklist.md` step 7d). It was never switched to `false`, so every run only logged `DRY RUN: would reconcile …`.
2. **Even when live it deliberately assigned nobody.** After the PR #292 "waiting-state" decision `VoiceCallReconciler.ConcludeNoAnswerAsync` only *flagged* the ticket and queued it in the Call Center with
   the AI Agent as owner: "no assignment without a confirmed Genesys connection, no Round Robin". A call that ended with nobody answering therefore stayed on the AI Agent until a person used Take Ownership.
   (`Tickets.AiAgentOwnerEmployeeId` is deliberately separate from `CurrentOwnerEmployeeId`, so the AI Agent as owner means "no human owner": nobody can edit the ticket.)
3. **Old stuck tickets were never retried.** The job only looked at calls that ended in the last `LookbackHours` (24), so a ticket that was flagged but unassigned for longer than that was abandoned for good; and when
   no agent was eligible it recorded the failure once and never tried again.

The Genesys events, the Hangfire/background-job host and the Round Robin engine were verified and are **not** at fault: the reconciler runs as its own hosted service (independent of `BackgroundJobs:Enabled`),
and `TicketAutoAssignmentService`'s Round Robin (row-locked persisted cursor, CS Agent role, active, not locked out, not a service identity, Call Center member) works. Digital channels already deal to a CS Agent at handoff time and are unchanged.

## Required behaviour → implementation
| # | Requirement | How |
|---|---|---|
| 1 | AI resolves → stays on the AI Agent and is closed | unchanged (`ClosedByAi`); the job never touches a call the AI closed, a call with no human need, or a live call |
| 2 | AI transfers and a human answers → that agent | unchanged (`GenesysAnsweringAgentAssignmentService`, `routing.agentId` + `connectedAtUtc`); a late connect replaces a provisional Round Robin owner |
| 3 | Human needed, nobody answered → eligible Call Center employee by the existing Round Robin | `VoiceCallReconciler.ConcludeNoAnswerAsync` → `AssignCallCenterIntakeAsync` (explicit workflow department → `AssignDepartmentRoundRobinAsync`). `Ticket.AssignTo` clears `AiAgentOwnerEmployeeId`, so the employee owns the ticket outright |
| 4 | Nobody eligible → stay queued, retry automatically | flagged + routed to the Call Center once; a read-only eligibility pre-check means each retry (every `IntervalSeconds`, 60 s) writes nothing; the ticket is dealt the moment somebody is eligible |
| 5 | Employee can edit/update/follow up/close without Take Ownership | the employee is `CurrentOwnerEmployeeId`; the normal owner rights apply. No permission was added or widened |

Eligibility / safety (all pre-existing, reused): active employee, not locked out, not the Genesys service identity, **CS Agent** role and **Call Center** department membership (other departments: operational roles of that department),
read-only roles never. Availability/workload = the existing shared Round Robin cursor (one persisted cursor, consecutive slots). Duplicates and races: ticket `RowVersion`, the unique "one current assignment" index, the
row-locked cursor, an owner re-check inside the transaction (`LostRace` is reported, not retried blindly), and idempotence (an owned ticket is never a candidate). SLA clocks are untouched (they run from creation);
the previous assignment row is superseded, never deleted; Assignment History records "Human follow-up … Provisional"; audit entries `GenesysVoiceNoAnswerConcluded` (once) and `AutoAssign`.

Never touched: Closed/Resolved tickets, tickets with a human owner, pending approvals, calls still live, a ticket a person **released** to the queue (new `ReleasedByPerson` class), a call a person answered
(flagged answers wait for the mapping fix, unchanged), Collections tower tickets, AI-handled calls with no human need, digital channels.

## Changed files
* `src/TigerCS.Application/Modules/GenesysIntegration/Services/VoiceCallReconciler.cs` — Round Robin on no-answer, retry, old-ticket recovery, `ReleasedByPerson`, new `AssignedRoundRobin` outcome.
* `src/TigerCS.Infrastructure/BackgroundJobs/VoiceCallReconciliationJob.cs` — new outcome, a no-eligible retry does not use the write budget.
* `src/TigerCS.Infrastructure/Modules/Ticketing/Repositories/TicketUnassignedWorkRepository.cs` — candidate query also returns old unowned calls flagged `RequiresHumanFollowUp`.
* `src/TigerCS.Api/appsettings.json`, `appsettings.Production.json` — `VoiceCallReconciliation:DryRun = false`.
* Tests: `VoiceCallReconcilerTests`, `VoiceEvidenceSqliteTests`, `VoiceCallReconciliationRegistrationTests`. Docs: this file, `Voice-Ownership-Rules.md`.
* `Diagnose_TigerCS_PROD_AiOwnedStuckTickets_ReadOnly.sql` (new, read-only).

## Production deployment
No migration and no schema change.
1. Run `Diagnose_TigerCS_PROD_AiOwnedStuckTickets_ReadOnly.sql` (db_datareader is enough). Section 1 `EligibleCsAgentsInPool` must be ≥ 1 and `ActiveCallCenterDepartments` exactly 1; note section 3's count (the backlog).
2. Check any **environment override**: `VoiceCallReconciliation__DryRun`, `VoiceCallReconciliation__Enabled`, `VoiceCallReconciliation__LookbackHours` must not be set to `true` / `false` / small values in the App Service or pipeline variables (an override of `DryRun=true` defeats the fix).
3. Deploy `TigerCS.Api` from the merged commit and recycle. Startup logs `Voice call reconciliation: Enabled=True, DryRun=False …`.
4. **Hot-fix without a deploy (optional, immediate for the existing build's classification only):** setting `VoiceCallReconciliation__DryRun=false` makes the old build flag-and-queue for real, but it does **not** assign anyone — deploy the build.
5. Watch the logs: `Voice call: ticket … dealt to employee … by Round Robin` and the run summary (`Round Robin n; left queued (retry) n; nothing written, no eligible agent n`). Warnings `no eligible Call Center CS Agent` mean the pool is empty — fix staffing (role/department/active/lock-out), the tickets are dealt on the next run.
6. Re-run the diagnostic: section 3 drains (≤ 50 tickets per minute, `MaxActionsPerRun`), section 5 lists the assignments.

## Recovery of existing stuck tickets
Automatic: the live job recovers every Open, unowned, AI-owned **phone** ticket whose call ended and whose interaction needs a person (section 3 of the diagnostic), however old, throttled to 50 per minute, through the same Round Robin as new work.
Closed/Resolved tickets and tickets that already have a human owner are not candidates (the job lists only Open tickets with no `CurrentOwnerEmployeeId`) and are never redistributed. No SQL write is needed or provided: a SQL update would bypass the cursor lock,
assignment history, audit and notification.
Tickets the job will **not** assign appear in section 4 with the reason: `CALL_STILL_LIVE` (end event never arrived: use the existing `StalePhoneAssignment` tooling, off by default), `AI_HANDLED_NO_HUMAN_NEED` (nobody asked for a person),
`HUMAN_ANSWERED_…` (fix the agent's `GenesysUserId` mapping; the job then assigns that agent), `RELEASED_BY_PERSON`, `PENDING_APPROVAL`. Anything else can be given to a person from the ticket page by an authorised user.
To pause the recovery: `VoiceCallReconciliation__DryRun=true` (no restart needed; re-read every run).

## Rollback
Set `VoiceCallReconciliation__DryRun=true` (stops all writes immediately) or `VoiceCallReconciliation__Enabled=false`; or redeploy the previous build. Assignments already made stay valid (they are ordinary Round Robin assignments with history and audit).

## Not covered (out of scope, report)
* `CallCenterReconciliation` (digital backlog) stays disabled/dry-run by default as asserted by `ProductionConfigurationTests`; enabling it is a separate, previewed operation (`Call-Center-Backlog-Distribution-Runbook.md`).
* A phone ticket whose **end event never arrives** is not concluded by this job (a human may still answer); `StalePhoneAssignment` covers it when enabled.
