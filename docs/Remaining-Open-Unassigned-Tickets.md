# Remaining Open tickets with no employee owner — diagnosis, fix and rollout

Scope: Open tickets that, after the one-time Call Center backlog distribution, sit in a department queue with no employee
owner (e.g. TG-LCS-20261102-0021, TG-REG-20261102-0026, TG-ACC-20261102-0023). **Nothing here writes to Production
automatically.** The batch distribution is not re-run.

## 1. Why a ticket has no owner — taxonomy
One classifier (`UnassignedTicketClassifier`) and one read-only SQL (`Diagnose_TigerCS_PROD_RemainingOpenUnassigned_ReadOnly.sql`)
use the same classes, first match wins:

| Class | Meaning | Action |
|---|---|---|
| PENDING_APPROVAL | approval pending | held |
| TRANSFERRED_OUT_OF_CC | Call Center sent it onward | by design; check the destination has eligible members |
| RELEASED_BY_PERSON | person released it to the CC queue | left |
| HUMAN_ANSWERED_MAPPED | mapped Genesys agent answered (handler row) | assign to **that** agent |
| HUMAN_ANSWERED_UNMAPPED | phone call picked up by a Genesys id that maps to no user | Round Robin; map the id |
| HUMAN_REQUIRED_NOT_ROUTED | human flag / open handoff / human evidence, never reached the CC (failed or never-run routing) | route to CC + Round Robin |
| HUMAN_REQUIRED_IN_CC | in the CC, nobody picked (no eligible agent then, lost race) | Round Robin |
| MANUAL_OR_INTEGRATION | no Genesys conversation, stuck in a department queue | route to CC + Round Robin |
| AI_ACTIVE / AI_STALE_NO_END_EVENT / AI_ENDED | live / silent-too-long / finished AI conversation | preserved (stale is reported only) |

Code paths that leave a human-required ticket unowned (all before this change had no retry):
1. `AssignRoundRobinAsync` finds no eligible Call Center agent -> ticket queued "Unassigned"; nothing ever revisited it.
2. AI-created Genesys tickets are created in the category department (Call Center only when a human is asked for). If the
   human flag/handoff event is lost, fails, or the update that would call `RouteExistingTicketToCallCenterAsync` never arrives, the ticket stays in the queue.
3. A phone call picked up by an agent id not in `AspNetUsers.GenesysUserId` is treated as "may be a bot" at creation (signal `None`).
4. Tickets created after the batch were never in its scope.
5. Needs `BackgroundJobs:Enabled=true`; with it false (the shipped default) the chatbot-inactivity job never closes stale AI conversations.

## 2. Fix (code)
`CallCenterUnassignedWorkReconciler` + Hangfire job `call-center-unassigned-work` re-evaluate Open, **unowned** tickets only and
use the existing engine: the confirmed Genesys answering agent (`ApplyAnsweringAgentAsync`) else Call Center Round Robin restricted to
**CS Agents** (`RouteExistingTicketToCallCenterAsync` / `AssignCallCenterIntakeAsync`, rotation cursor row-locked). One transaction and DI scope per ticket;
owner re-checked inside the transaction; lost races reported, never retried blindly. It never touches owned tickets, active AI, completed AI,
pending approvals, tickets the CC transferred onward or a person released.

**Runs independently of Hangfire.** It is its own hosted service (`CallCenterUnassignedWorkHostedService`): it does NOT need
`BackgroundJobs:Enabled`, so enabling it never starts the SLA / Collections / outbox jobs. Runs never overlap: an in-process gate plus a
SQL Server `sp_getapplock` (session scoped, released automatically if the process dies) make a second run/worker/server skip.
Logs (ticket number + id only): `Reconciliation eligible`, `DRY RUN: would reconcile`, `Reconciliation ASSIGNED`, `... changed concurrently`,
`... no eligible Call Center CS Agent` (warning), `Reconciliation FAILED` (error), and a summary line with examined / eligible / assigned /
would-assign / no-agent / lost-race / failed / skipped-by-class / `MaxAssignmentsPerRun reached`.

Config (`CallCenterReconciliation`, all **off / dry-run** by default): `Enabled`, `DryRun`, `CsAgentRoleOnly` (true), `IntervalMinutes`, `BatchSize`,
`MaxAssignmentsPerRun`, `StaleAiAfterHours`, `AssignStaleAi`, `MinTicketAgeMinutes`.

## 3. Safe one-time correction (proposal — do not run until approved)
1. Run the read-only diagnostic from a `db_datareader` login; compare counts with item 2.
2. Fix data prerequisites it shows: eligible Call Center **CS Agents** exist (`CcMembers_EligibleAndCsAgentRole`), unmapped Genesys ids added to `AspNetUsers.GenesysUserId`. (`BackgroundJobs:Enabled` is NOT required.)
3. Deploy API with `CallCenterReconciliation:Enabled=true, DryRun=true`. The log line `Call Center reconciliation (DRY RUN ...)` lists class counts and the ticket ids it would act on; they must match `TotalWouldBeAssigned` in the diagnostic.
4. Approve, then set `DryRun=false` with `MaxAssignmentsPerRun` (e.g. 50). Each assignment writes the usual `AutoAssign`/`Transfer` audit rows and workflow event; superseded assignment rows are kept.
5. After the backlog is drained, leave `Enabled=true, DryRun=false` as the permanent safety net, or set `Enabled=false`.
Rollback: set `Enabled=false` (picked up within a minute, no restart; or recycle the app pool); individual tickets can be reassigned by a supervisor through the normal Assign action (history is preserved).

## 4. Deployment
API only (reconciler, hosted service, repository, config). Web is unaffected by this part. No database migration, no new table/column.
