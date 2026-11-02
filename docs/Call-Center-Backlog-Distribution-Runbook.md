# One-time Call Center backlog redistribution — plan, preview, execution and rollback

**Database:** `TigerCS_Ticketing_Prod` · **Status: NOT EXECUTED on Production (or any real environment).** Every script defaults to a mode that changes
nothing, and the execution script cannot commit without the approval gates in §4.

**Confirmed business decision:** move **all** existing *unassigned* Open / InProgress / Pending tickets from **every** department into the Call Center (`CC`)
and give each to exactly **one** eligible Call Center agent using the existing Round Robin rules. One-time only — **no application code, configuration or
workflow is changed; future tickets behave exactly as today.**

| File | Purpose | Writes? |
|---|---|---|
| `Preview_TigerCS_PROD_CallCenterBacklogDistribution_ReadOnly.sql` | **The dry run.** All eligible tickets, original-department counts, eligible agents, per-agent distribution, exact ticket → agent plan | **No** (SELECT + session temp tables) |
| `Execute_TigerCS_PROD_CallCenterBacklogDistribution.sql` | The redistribution. `@DryRun = 1` by default → does and validates everything, then rolls back | Only with `@DryRun = 0` **and** every gate |
| `Rollback_TigerCS_PROD_CallCenterBacklogDistribution.sql` | Reverts only this batch, only for tickets untouched since | Only with `@DryRun = 0` |
| `tests/backlog-distribution/` | Validation suite (real SQL Server) — see its README | Disposable container only |

## 1. Validation evidence (and its limits)

The three scripts were run against a **real SQL Server 2022 (Developer, Linux container)** whose schema was built from this repo's own migration scripts. **105 checks, 105 passed**
(re-run from a freshly rebuilt container with `tests/backlog-distribution/setup_test_db.sh`). The expected deal is recomputed by an independent Python oracle, not by the SQL under test.

Validation found and fixed four real defects in the earlier, never-run draft of this PR: a variable-name collision (`@n`/`@N` — the script did not compile on SQL Server), an illegal aggregate with an outer
reference in the rollback (Msg 8124), a one-time check that blocked behind a running batch instead of failing fast on the lock, and wrong reason precedence for tickets transferred out.
Deliberately breaking the ordering rule (SQL-native GUID order) is detected by the suite (47 of 62 tickets differ).

| Proven | |
|---|---|
| Preview | read-only (all table digests unchanged); plan equals the oracle for every ticket; blocks on unconfirmed / empty / mistyped service-account list |
| Dry run | performs and validates all writes, leaves the database byte-identical |
| Gates | every missing / wrong approval refuses and changes nothing; wrong database name aborts |
| Commit | owners = oracle; all in CC; originating dept / request type / category / priority / status / SLA state / created time preserved; exactly one current assignment each; audit + workflow events; non-eligible tickets byte-identical; spread ≤ 1; no ineligible account used |
| Rollback | untouched batch → exact pre-batch `Tickets` table; reassigned / worked / transferred tickets skipped byte-for-byte; history only grows |
| Concurrency | second run fails on the application lock; a concurrent assignment blocks then updates 0 rows; unique index rejects a duplicate current assignment; backlog drift between preview and run aborts |
| Scale | 20,062 tickets (16,056 moved) + 100,000 closed: preview 0.4 s, commit ≈ 12 s, rollback ≈ 2 s |

**Not proven — be honest about it:** this is *not* your Production server. Production's SQL Server version / compatibility level (scripts need `STRING_AGG` → level ≥ 130, SQL Server 2017+),
data volume and indexing, real Genesys traffic and real ticket shapes (e.g. tickets with a `RequestTypeId`, which the fixture does not seed — preserved fields are compared NULL-safe) are not covered.
Hence steps 4–5 in §3 (restored copy / UAT first) are mandatory, and the **service-account ids must come from `appsettings`** — SQL cannot know them.

## 2. Requirements → enforcement

| # | Requirement | How |
|---|---|---|
| 1 | All departments | Candidates are every ticket with status 1–4, no owner, no current assignment — any `CurrentDepartmentId`, including inactive departments |
| 2 | Move to CC + exactly one agent | One statement sets `CurrentDepartmentId = CC` and `CurrentOwnerEmployeeId = agent`, guarded by *still unowned*, *still in the planned department* and *unchanged `RowVersion`*; one mismatch aborts the whole run |
| 3 | Preserve originating dept, request type, classification, status, SLA, history, audit | Never written. After the writes an in-transaction `EXCEPT` compares originating dept, request type, category, priority, status, SLA state, escalation level, verification status, created time, number against the pre-write values (abort on any difference); history / SLA / interactions / notes / approvals / notifications are not written at all; all earlier audit rows and workflow events are verified unchanged |
| 4 | Audit trail + workflow events | Per moved ticket: `Transfer` audit (system actor, `DepartmentId=<from>` → `<CC>`, `OriginatingDepartmentId=… (unchanged)`) and a `TicketTransferred` workflow event (reason `SystemRouting`, From → CC, `ToEmployeeId` = agent). Per ticket: `AutoAssign` audit (engine format + `Trigger=BacklogDistribution;BatchId=…`) and the `TicketAssignments` row. The `SystemRouting` reason is the app's own "system moved it" marker: not counted as an agent transfer or escalation and it does not affect a Genesys handler's rights. A ticket already in CC gets the assignment records only (no department move to record) |
| 5 | Exclude Closed / Resolved / Cancelled | Status filter `IN (1,2,3,4)`; Resolved = 5, Closed = 6 excluded. The schema has no `Cancelled` ticket status |
| 6 | Don't overwrite assigned tickets | Owner must be NULL **and** no `IsCurrent = 1` assignment row; owned tickets, legacy owned tickets and "no owner but a current assignment" data-drift rows are never read into the plan (the preview counts each) |
| 7 | Exclude deactivated / locked / read-only / service | Employee not deactivated; account not locked out; id not in the service list; must hold CS Agent, Department Employee or Department Head (Reporting User / Chairman-CEO-only, Supervisors, Managers, Admins never get work — identical to the engine) |
| 8 | Concurrency, no duplicate current assignments | `sp_getapplock` (one run at a time, fail-fast); CC rotation row `UPDLOCK/ROWLOCK` exactly as the live engine (live intake queues behind it); candidate rows locked + `RowVersion` re-check; filtered unique index `TicketAssignments(TicketId) WHERE IsCurrent = 1` verified present before running; `XACT_ABORT`, 20 s lock timeout, single transaction |
| 9 | Update scripts + runbook | This change |
| 10 | Preview content | Result set 2: eligible by **original department × status** with `MOVE` / `assign only`; result set 5: per-agent tickets (from other departments / already CC / by status / load before → after), department × agent matrix, spread, the exact ticket → agent plan |
| 11 | Rollback | §5 — restores original department **and** unowned / no-current-assignment state, only for tickets untouched since the batch |
| 12 | Approval counts + service-account confirmation | §4 |

Round Robin (identical to `TicketAutoAssignmentService`): pool = active CC members who pass the exclusions above; order = ascending .NET `Guid` order (equal to ordinal order of the GUID text — SQL Server's native
`uniqueidentifier` order is different and deliberately not used); next = first eligible id after the persisted cursor (`DepartmentAssignmentRotations`), wrapping; tickets are dealt **oldest first** across all
departments, so per-agent counts differ by at most 1. The cursor is advanced as the engine does.

## 3. Execution plan (exact, gated)

| Step | Who | Action | Gate |
|---|---|---|---|
| 1 | You, read-only login | In the **Preview**: paste every id from `Authorization:ServiceIdentity:EmployeeIds` **and** `Collections:Authorization:IntegrationEmployeeIds` into `@ServiceIdentity`, set `@ServiceIdsConfirmed = 1` (or `@NoServiceAccountsExist = 1` only if there are none). Run. Return **all** result sets | Verdict `READY` |
| 2 | You + me | Review: agents (anyone unexpected? check the "possible service account" list), **result set 2** (counts per original department — this *is* the move being approved), exclusions / anomalies (data-drift and duplicate-current counts), per-agent table (spread ≤ 1), the three approval numbers | **Written approval** of the counts |
| 3 | DBA | Full backup / snapshot of `TigerCS_Ticketing_Prod`; record its name | Backup verified |
| 4 | DBA | Restore that backup to a scratch database **named `TigerCS_Ticketing_Prod` on a non-production server**; run **Execute** with defaults (`@DryRun = 1`), then a real run (`@DryRun = 0`, gates filled), then **Rollback** (dry run, then real). All validations must pass; compare counts with step 1 | Clean on the copy |
| 5 | DBA, approved low-traffic window | **Execute** on Production, still `@DryRun = 1`; counts must equal the approved numbers | Matches |
| 6 | DBA, explicit go | Fill the gates (§4), `@DryRun = 0`, run as **one batch in SSMS** (or `sqlcmd -I`, since filtered indexes need `QUOTED_IDENTIFIER ON`). **Save the printed `BatchId`** | Counts must equal the live computation or it aborts |
| 7 | You | Post-checks (§6); spot-check 2–3 agents' *My Tickets*; Resolve/Close one test ticket if desired; sign off | Sign-off |

**Lock window:** the run holds update locks on the candidate tickets and the CC rotation row until commit — measured ≈ 0.6 ms per ticket (20,062 tickets ≈ 12 s). Live writers to those tickets (and new Call
Center intake) wait; the 20 s lock timeout makes the script fail with nothing changed instead of waiting indefinitely. Run in a quiet window; the application lock stops a second run.

## 4. Commit gates (all required for `@DryRun = 0`)

* `@ApprovedTicketCount`, `@ApprovedMovedTicketCount`, `@ApprovedAgentCount` = the preview's result set 6, and they must **equal** what the run computes at that moment (any drift → abort, nothing written; re-preview and re-approve).
* `@ServiceIdsConfirmed = 1`; the service-account list must be non-empty, **or** `@NoServiceAccountsExist = 1`; every listed id must be a real employee (typos abort).
* No earlier `BacklogDistributionBatch` audit row (one-time; `@AllowSecondBatch` only with explicit approval).
* Database name must be `TigerCS_Ticketing_Prod`; CC must be exactly one active department with assignment enabled; the rotation table and the unique filtered index must exist.

## 5. Rollback strategy

*Before commit:* one transaction — any error, failed validation or mismatch rolls everything back; `@DryRun = 1` always does.
*After commit:* `Rollback_…sql` with the saved `BatchId` (also the `CorrelationId` of every audit row / workflow event the batch wrote). Run its dry run first.

* Working set = the batch's own per-ticket audit rows (count and moved-count cross-checked with the batch row; mismatch aborts) — no other ticket can be touched.
* A ticket is **rolled back** only if still exactly as the batch left it: same agent owner, still in CC, still Open/InProgress/Pending, the batch row is the single current assignment, no later assignment, and **nobody worked it** (no first
  response, status-history row, note, Genesys handler record, or other workflow event after the batch). Everything else is **skipped, listed with the reason, and left byte-for-byte as found**. (`@IncludeWorked = 1` relaxes only the "worked" test, only with approval.)
* Per rolled-back ticket: owner → NULL; department → the original department (from the batch's own `TicketTransferred` event; unchanged for tickets that were already in CC); the batch assignment row → `IsCurrent = 0` (**kept, never deleted**);
  `Transfer` + `AutoAssignRollback` audit rows and a reversing `TicketTransferred/SystemRouting` event (CC → original). Originating department, request type, status, SLA, history are never touched; no audit row is ever edited or deleted.
* Round Robin cursor restored to its pre-batch value only if nobody was dealt a ticket since; otherwise left (harmless).
* Dry-run by default; a batch can be rolled back once. Last resort for a catastrophic problem is the step-3 backup.

## 6. Post-execution verification (read-only)

```sql
-- every batch ticket: one owner, one current assignment, same person, in CC
SELECT COUNT(*) AS BatchTickets,
       SUM(CASE WHEN t.CurrentOwnerEmployeeId = a.AssignedEmployeeId AND t.CurrentDepartmentId = a.AssignedDepartmentId THEN 1 ELSE 0 END) AS Consistent
FROM dbo.AuditEntries ae
JOIN dbo.Tickets t ON t.TicketId = TRY_CONVERT(bigint, ae.EntityId)
JOIN dbo.TicketAssignments a ON a.TicketId = t.TicketId AND a.IsCurrent = 1
WHERE ae.CorrelationId = '<BatchId>' AND ae.Action = N'AutoAssign';

-- must return 0 rows
SELECT TicketId FROM dbo.TicketAssignments WHERE IsCurrent = 1 GROUP BY TicketId HAVING COUNT(*) > 1;
```

## 7. What the business should expect afterwards (risks)

* Tickets of every department land in the Call Center agents' *My Tickets*; the owner can Resolve / Close them (owner right). The Call Center can transfer a classified ticket back, but the app offers **only the request type's department** as target (`transfer-target-must-match-request-type`) — expected, and it is how the tickets get back to specialists.
* **Department-specific workflow in flight:** tickets with a pending approval keep that approval record (preview counts them: `WithPendingApproval_ApprovalKept`); department-scoped approvers (e.g. Department Heads) may no longer see the ticket in their own department queue — confirm how your approval flow should treat these before approving the counts. SLA clocks and breach state are untouched (a breached ticket stays breached).
* Tickets in an **inactive** department are included (they are otherwise stranded) and are shown separately in the preview.
* The assignment is a system action (null actor), like every Round Robin assignment: an *Open* ticket with no human response still counts as "automatic pre-assignment", so a Genesys-reported handler can take it over — existing engine behaviour.
* No notification is sent (an assignment never produced one in this app).
* Round Robin spreads evenly **from the cursor**; it does not level agents who already hold different workloads — the preview shows load before / after per agent.
