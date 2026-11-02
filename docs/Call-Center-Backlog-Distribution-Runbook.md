# One-time Call Center backlog redistribution — plan, preview, execution and rollback

**Database:** `TigerCS_Ticketing_Prod` · **Status: NOT EXECUTED on Production (or any real environment).** Every script defaults to a mode that changes
nothing, and the execution script cannot commit without the approval gates in §4.

## Business rules (confirmed)

1. Move **all** existing *unassigned* Open / InProgress / Pending tickets from **every** department (active or inactive) into the Call Center (`CC`) and give each to exactly **one** eligible Call Center agent.
2. Assignment uses the **existing Round Robin algorithm** (same pool rules, same persisted cursor, same GUID order, same lock). **No workload balancing.**
3. **Held back — never moved, never assigned, never touched, listed separately in the preview:**
   * tickets with a **pending approval** (`TicketApprovals.Status = 1`);
   * tickets with an **active AI conversation** (definition below).
4. Preserved: existing owners (a ticket with an owner or a current assignment is never read), AI conversations (interaction rows untouched, and live AI tickets are left where they are), ticket status, SLA, `OriginatingDepartmentId`, request type / classification, audit history, workflow history.
5. Every transferred ticket ends with **exactly one** owner who is an eligible Call Center agent (re-verified inside the transaction).
6. One-time only. **No application code, configuration or workflow is changed; future tickets behave exactly as today.**

**Active AI conversation** (the interpretation used — please confirm at approval): the ticket has a Genesys-sourced interaction that is **not ended, not closed for inactivity, needs no human follow-up and has not been taken by a person**
(`Source = 2 AND EndedAtUtc IS NULL AND InactivityClosedAtUtc IS NULL AND RequiresHumanFollowUp = 0 AND HandledByUserId IS NULL`), **and** no interaction on the ticket is flagged `RequiresHumanFollowUp`, **and** there is no open human
handoff (`TicketAgentHandoffs.Status` Waiting / Assigned / InProgress). Anything that needs or already has a person (flagged for human follow-up, open handoff, taken by an agent, ended, inactivity-closed) is *human work* and is **eligible**.
This follows the application's own rule that an AI-handled ticket "stays where it was created" until a person is requested.

| File | Purpose | Writes? |
|---|---|---|
| `Preview_TigerCS_PROD_CallCenterBacklogDistribution_ReadOnly.sql` | **The dry run.** Eligible tickets, original-department counts, **held-back tickets (listed separately)**, agents, per-agent distribution, exact plan, the five approval numbers | **No** (SELECT + session temp tables) |
| `Execute_TigerCS_PROD_CallCenterBacklogDistribution.sql` | The redistribution. `@DryRun = 1` by default → does and validates everything, then rolls back | Only with `@DryRun = 0` **and** every gate |
| `Rollback_TigerCS_PROD_CallCenterBacklogDistribution.sql` | Reverts only this batch, only for tickets untouched since | Only with `@DryRun = 0` |
| `tests/backlog-distribution/` | Validation suite on a real SQL Server (see its README) | Disposable container only |

## 0. Preview — exact instructions (read-only, safe to run any time)

1. Open `Preview_TigerCS_PROD_CallCenterBacklogDistribution_ReadOnly.sql` in SSMS, connected to `TigerCS_Ticketing_Prod` with a **read-only** login (`db_datareader` is enough). It aborts on any other database name.
2. **Service accounts** (SQL cannot know them): collect every id from `appsettings` — `Authorization:ServiceIdentity:EmployeeIds` **and** `Collections:Authorization:IntegrationEmployeeIds` — and add one line each below the marker:
   ```sql
   INSERT INTO @ServiceIdentity VALUES ('xxxxxxxx-xxxx-xxxx-xxxx-xxxxxxxxxxxx');
   ```
   then set `DECLARE @ServiceIdsConfirmed bit = 1;`. (Only if you have verified that **no** service / integration account exists, leave the list empty and set `@NoServiceAccountsExist = 1` instead.)
   Until this is done the verdict is `BLOCKER`; a mistyped id is also a `BLOCKER` (`ServiceIdsNotFound` lists it).
3. Execute (F5) **as one batch**. Nothing is written; it takes about a second (0.5 s measured for 20k tickets).
4. Check the first result set: `Verdict` must be **`READY`**. Then read, in order — and send me **every result set** (text is fine; it contains ticket numbers and agent names only, no customer data):

   | Result set | What to check |
   |---|---|
   | 1 Verdict | `EligibleTickets_Total`, `TicketsMovedIntoCC`, `TicketsAlreadyInCC_AssignOnly`, `HeldBack_PendingApproval`, `HeldBack_ActiveAiConversation`, `EligibleAgents` |
   | 2 Eligible by **original department × status** | This *is* the move being approved: how many tickets leave each department (rows marked `MOVE to CC + assign`), including inactive departments |
   | 2b **HELD BACK** | Counts per department, then **every held-back ticket** with its reason (`PENDING APPROVAL` with approval type and since-date, or `ACTIVE AI CONVERSATION`; a ticket with both is listed once under the approval). These are left exactly as they are |
   | 3 Exclusions | Resolved/Closed unowned, already-owned (never touched), data-drift rows, **`TicketsWithMoreThanOneCurrentAssignment_MustBe0` must be 0** |
   | 4 Agents | Every Call Center member, `ELIGIBLE` or the reason excluded (deactivated / locked / service / no operational role). Anyone unexpected? Also read the "possible service account" list |
   | 5 Distribution | Tickets per agent (spread must be ≤ 1), from other departments vs already in CC, by status, load before → after; department × agent matrix |
   | 6 **Approval values** | The **five numbers** the execution script will demand: `ApprovedTicketCount`, `ApprovedMovedTicketCount`, `ApprovedAgentCount`, `ApprovedHeldBackPendingApprovalCount`, `ApprovedHeldBackActiveAiCount` |
   | 7 Exact plan | Every ticket → original department → receiving agent, oldest first |
5. Re-run it as often as you like; it never changes anything. The numbers move only as the live backlog moves.

## 1. Validation evidence (and its limits)

The scripts were run against a **real SQL Server 2022 (Developer, Linux container)** whose schema is built from this repo's own migration scripts. **152 checks, 152 passed**, re-run from a freshly rebuilt container
(`tests/backlog-distribution/setup_test_db.sh`). The expected deal and the hold-back rules are recomputed by an **independent Python oracle** from raw rows, not by the SQL under test.

| Proven | |
|---|---|
| Preview | read-only (all table digests unchanged); plan equals the oracle for every ticket; held-back tickets listed separately with reasons and absent from the plan; blocks on an unconfirmed / empty / mistyped service-account list |
| Dry run | performs and validates all writes, leaves the database byte-identical |
| Gates | every missing / wrong approval — including the two held-back counts — refuses and changes nothing; wrong database name aborts |
| Pending approvals | pending → held back; approved / rejected / cancelled → still eligible; a ticket that is also an AI conversation is counted once; held-back rows, their approvals, assignments, audit and events are byte-identical after commit; an approval decided or newly requested after the preview changes the counts and refuses the run |
| Active AI | live AI-only ticket → held back; ended / inactivity-closed / flagged for human / open handoff / taken-by-agent → eligible; `TicketInteractions`, `TicketAgentHandoffs`, `TicketApprovals` byte-identical after commit |
| Commit | owners = oracle; all in CC; originating dept / request type / category / priority / status / SLA / created preserved; exactly one current assignment each; audit + workflow events; non-eligible tickets byte-identical; spread ≤ 1; no ineligible account used |
| Rollback | untouched batch → exact pre-batch `Tickets` table; reassigned / worked / transferred / newly-approved / new-interaction tickets skipped byte-for-byte; history only grows; held-back tickets unreachable |
| Concurrency | see the table below |
| Scale | 20,072 eligible (16,066 moved) + 100,000 closed: preview 0.5 s, commit ≈ 13 s, rollback ≈ 2 s |

**Concurrent-assignment safeguards (each has a test, and a mutation test proves the test fails when the safeguard is removed):**

| Race | Outcome |
|---|---|
| Second run while one is running | Fails immediately on the application lock; nothing written |
| A human assigns a candidate ticket mid-run | Blocks until commit, then updates 0 rows — cannot overwrite the batch's owner |
| A duplicate *current* assignment | Rejected by the unique filtered index |
| Pending approval / AI conversation created on a planned ticket **between selection and the update** | The post-write re-check aborts the **whole** run (Msg 50340 / 50341); nothing changed |
| Same, **after** the update (ticket rows exclusively locked) | The competing insert waits on the ticket's foreign key and loses (deadlock victim, Msg 1205) or waits for commit; no batch ticket ends up with a pre-commit pending approval. *Rarely the batch itself can be chosen as victim: it rolls back completely — just re-run* |
| A new owner is locked out mid-run | Re-check aborts the whole run (Msg 50342) |
| An agent is deactivated mid-run | Blocked until the batch commits (agent rows are locked), so the pool cannot change under the run |
| Live Call Center intake (same cursor lock as the app) | Waits for the batch, then reads the advanced cursor |
| Backlog changes between preview and run | Counts differ → abort; re-preview and re-approve |

**Not proven — be honest about it:** this is *not* your Production server. Production's SQL Server version / compatibility level (scripts need `STRING_AGG` → level ≥ 130), data volume and indexing, real Genesys traffic, and real ticket shapes (e.g. tickets with a
`RequestTypeId`, which the fixture does not seed — preserved fields are compared NULL-safe) are not covered. Hence steps 4–5 in §3 (restored copy first) are mandatory, and **the service-account ids must come from `appsettings`**.
The "active AI conversation" definition is an interpretation of the schema (above) — the preview's held-back list is where you confirm it matches reality.

## 2. Requirements → enforcement

| # | Requirement | How |
|---|---|---|
| 1 | Exclude pending approvals, list separately | Candidate query has `NOT EXISTS (… TicketApprovals … Status = 1)`; the preview's result set 2b lists each held-back ticket with approval type and date; count is a commit gate |
| 2 | Existing Round Robin, no workload balancing | Pool / order / cursor / lock identical to `TicketAutoAssignmentService`; tickets dealt oldest first; the preview shows load before / after for information only |
| 3 | All unassigned Open / InProgress / Pending, every department | Status `IN (1,2,3,4)`, owner NULL, no `IsCurrent = 1` assignment, any `CurrentDepartmentId` (inactive departments included) |
| 4 | Preserve owners, AI conversations, status, SLA, original dept, audit, workflow history | Owned tickets / current assignments never read; live AI tickets held back; in-transaction `EXCEPT` proof that originating dept, request type, category, priority, status, SLA state, escalation, verification, created time, number are unchanged; no history / SLA / interaction / approval / notification table is written; earlier audit rows and workflow events verified unchanged |
| 5 | Exactly one eligible CC owner per transferred ticket | One guarded statement sets department + owner; the filtered unique index and a post-write validation prove one owner = one current assignment; a **fresh** eligibility re-evaluation (not deactivated, not locked, not service, CC member, operational role) runs after the writes; agent rows are locked for the run |
| 6 | Preview / Execute / Rollback / Runbook consistent | Same hold-back predicates in all three (same text), same five numbers, same batch markers; tests drive all three through one fixture |
| 7 | Tests for approval exclusions and concurrent safeguards | `tests/backlog-distribution` T11, T11b, T12, T13 (+ mutation checks) |
| 8 | Approval counts + service-account safety | §4 |

Round Robin (identical to the engine): pool = active CC members who are not deactivated, not locked out, not a service identity and hold CS Agent / Department Employee / Department Head (read-only roles, supervisors, managers, admins never get work); order = ascending
.NET `Guid` order (the ordinal order of the GUID text — SQL Server's native `uniqueidentifier` order differs and is deliberately not used); next = first eligible id after the persisted cursor, wrapping. The cursor is advanced as the engine does.

Per moved ticket: `Transfer` audit (system actor, `DepartmentId=<from>` → `<CC>`, `OriginatingDepartmentId=… (unchanged)`) and a `TicketTransferred` workflow event (reason `SystemRouting` — the app's own "system moved it" marker: not counted as an agent transfer or escalation,
no effect on a Genesys handler's rights; From → CC; `ToEmployeeId` = agent). Per ticket: `AutoAssign` audit (engine format + `Trigger=BacklogDistribution;BatchId=…`) and the `TicketAssignments` row. A ticket already in CC gets the assignment records only.

## 3. Execution plan (exact, gated)

| Step | Who | Action | Gate |
|---|---|---|---|
| 1 | You, read-only login | Run the Preview exactly as in §0. Return all result sets | Verdict `READY` |
| 2 | You + me | Review §0 item 4. Confirm the held-back list matches your understanding of "pending approval" and "active AI conversation" | **Written approval** of the five numbers |
| 3 | DBA | Full backup / snapshot of `TigerCS_Ticketing_Prod`; record its name | Backup verified |
| 4 | DBA | Restore that backup on a **non-production** server under the name `TigerCS_Ticketing_Prod`; run **Execute** with defaults (`@DryRun = 1`), then a real run (`@DryRun = 0`, gates filled), then **Rollback** (dry run, then real). All validations must pass; compare counts with step 1 | Clean on the copy |
| 5 | DBA, approved low-traffic window | **Execute** on Production, still `@DryRun = 1`; its counts must equal the approved numbers | Matches |
| 6 | DBA, explicit go | Fill the gates (§4), `@DryRun = 0`, run as **one batch in SSMS** (or `sqlcmd -I` — filtered indexes need `QUOTED_IDENTIFIER ON`). **Save the printed `BatchId`** | Counts must equal the live computation or it aborts |
| 7 | You | Post-checks (§6); spot-check 2–3 agents' *My Tickets*; Resolve / Close one test ticket if desired; sign off | Sign-off |

**Lock window:** candidate tickets, the Call Center agents' `Employees` rows and the CC rotation row are locked until commit — measured ≈ 0.6 ms per ticket (20k tickets ≈ 13 s). Live writers to those rows (and new Call Center intake) wait; the 20 s lock timeout makes the script
fail with nothing changed instead of waiting. Run in a quiet window.

## 4. Commit gates (all required for `@DryRun = 0`)

* `@ApprovedTicketCount`, `@ApprovedMovedTicketCount`, `@ApprovedAgentCount`, `@ApprovedHeldBackPendingApprovalCount`, `@ApprovedHeldBackActiveAiCount` = Preview result set 6, and **each must equal** what the run computes at that moment (any drift → abort, nothing written; re-preview and re-approve).
* `@ServiceIdsConfirmed = 1`; the service-account list must be non-empty **or** `@NoServiceAccountsExist = 1`; every listed id must be a real employee.
* No earlier `BacklogDistributionBatch` audit row (one-time; `@AllowSecondBatch` only with explicit approval — e.g. to process held-back tickets later, after a fresh preview and approval).
* Database `TigerCS_Ticketing_Prod`; CC exactly one active department with assignment enabled; rotation table, hold-back source tables / columns and the unique filtered index must exist.

## 5. Rollback strategy

*Before commit:* one transaction — any error, failed validation or mismatch rolls everything back; `@DryRun = 1` always does.
*After commit:* `Rollback_…sql` with the saved `BatchId` (also the `CorrelationId` of everything the batch wrote). Dry-run it first.

* Working set = the batch's own per-ticket audit rows (counts cross-checked with the batch row; mismatch aborts). Held-back tickets were never part of the batch, so rollback cannot reach them.
* A ticket is **rolled back** only if still exactly as the batch left it: same agent owner, still in CC, still Open / InProgress / Pending, the batch row is the single current assignment, no later assignment, and **nobody worked it** — no first response, status-history row, note,
  Genesys handler record, **new interaction, new approval request**, or other workflow event after the batch. Everything else is **skipped, listed with the reason, left byte-for-byte as found**. (`@IncludeWorked = 1` relaxes only the "worked" test, only with approval.)
* Per rolled-back ticket: owner → NULL; department → the original department (from the batch's own `TicketTransferred` event; unchanged for tickets already in CC); the batch assignment row → `IsCurrent = 0` (**kept, never deleted**); `Transfer` + `AutoAssignRollback` audit rows and a reversing
  `TicketTransferred/SystemRouting` event (CC → original). Originating department, request type, status, SLA, history are never touched; no audit row is ever edited or deleted.
* Round Robin cursor restored to its pre-batch value only if nobody was dealt a ticket since. Dry-run by default; a batch can be rolled back once. Last resort for a catastrophic problem is the step-3 backup.

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

## 7. What the business should expect afterwards

* Tickets of every department (except the held-back ones) appear in the Call Center agents' *My Tickets*; the owner can Resolve / Close them (owner right). A classified ticket can be transferred back, but the app offers **only the request type's department** as the target
  (`transfer-target-must-match-request-type`) — expected, and how tickets return to specialists.
* **Held-back tickets stay in their current department, unowned**, and carry on in their own approval / AI flow. They are *not* picked up automatically afterwards; once an approval is decided or an AI conversation ends they are ordinary unassigned tickets that need a follow-up decision (a second, separately approved batch).
* SLA clocks and breach state are untouched. Tickets in an **inactive** department are included (otherwise stranded) and shown separately in the preview.
* The assignment is a system action (null actor), like every Round Robin assignment: an *Open* ticket with no human response still counts as "automatic pre-assignment", so a Genesys-reported handler can take it over — existing engine behaviour.
* No notification is sent (an assignment never produced one in this app).
* Round Robin spreads evenly **from the cursor**; it does not level agents who already hold different workloads (decision: no workload balancing).
