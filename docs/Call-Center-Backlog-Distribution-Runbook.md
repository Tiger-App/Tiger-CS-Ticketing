# One-time Call Center backlog distribution — plan, dry-run and rollback

**Database:** `TigerCS_Ticketing_Prod` · **Status: NOT EXECUTED. Nothing has been run against Production, UAT or any SQL Server.**
Every script defaults to a mode that changes nothing, and the execution script cannot commit without the approval gates below.

| File | Purpose | Writes? |
|---|---|---|
| `Preview_TigerCS_PROD_CallCenterBacklogDistribution_ReadOnly.sql` | **The dry run.** Ticket counts, eligible agents, per-agent distribution, exact ticket → agent plan | **No** (SELECT + session temp tables only) |
| `Execute_TigerCS_PROD_CallCenterBacklogDistribution.sql` | The distribution. `@DryRun = 1` by default → performs and validates everything, then rolls back | Only with `@DryRun = 0` **and** all gates |
| `Rollback_TigerCS_PROD_CallCenterBacklogDistribution.sql` | Undo only this batch's assignments | Only with `@DryRun = 0` |

## 0. What I could not do (honest status)

* **No dry-run numbers exist yet.** This session has no connection to Production (or any database), so I cannot report real ticket counts or per-agent
  distribution. Step 1 below produces them in minutes from a read-only login; paste the result sets back and they become the approval document.
* **The SQL has not been run on SQL Server** (none is available in this sandbox). I verified structure (balanced blocks/quotes) and reviewed it against the
  real EF schema and the engine source, but the first real execution must be the `@DryRun = 1` run on UAT / a restored Production copy.
* One thing needs a person: the **service-account ids** live in `appsettings` (`Authorization:ServiceIdentity:EmployeeIds`,
  `Collections:Authorization:IntegrationEmployeeIds`), not in SQL. Without them the scripts cannot exclude service accounts, so the verdict stays `BLOCKER` and commit is refused.

## 1. Scope rules (your 12 requirements → how each is enforced)

| # | Requirement | Enforcement |
|---|---|---|
| 1 | Identify unowned Open / InProgress / Pending | `TicketStatus IN (1,2,3,4)` (Open, InProgress, PendingCustomer, legacy PendingThirdParty) **and** `CurrentOwnerEmployeeId IS NULL` **and** no `TicketAssignments` row with `IsCurrent = 1` |
| 2 | Fair, via the existing Round Robin engine | Same pool rules, same persisted cursor (`DepartmentAssignmentRotations`), same lock, same order as `TicketAutoAssignmentService.AssignRoundRobinAsync`. Tickets dealt oldest-first → per-agent counts differ by ≤ 1 (validated; aborts otherwise) |
| 3 | Exclude deactivated / read-only / service accounts | `Employees.DeactivatedAtUtc IS NULL`; account not locked out; id not in the service list; must hold CS Agent, Department Employee or Department Head (Reporting User / Chairman-CEO-only, Supervisors, Managers, Admins are never handed work — same as the engine) |
| 4 | Never overwrite ownership / active assignments | Candidates must have no owner **and** no current assignment; the `UPDATE` re-checks `owner IS NULL` + unchanged `RowVersion`; one mismatch aborts the whole run. Tickets that already have an owner are never read into the plan. The script never supersedes an assignment row (validated) |
| 5 | Exclude Closed / Cancelled / Resolved | Status filter above. (There is no `Cancelled` status in this schema; Resolved = 5, Closed = 6 are excluded) |
| 6 | Exactly one current owner | One `Tickets.CurrentOwnerEmployeeId`; the filtered unique index `TicketAssignments(TicketId) WHERE IsCurrent = 1` makes a second current row impossible; post-write validation re-proves 1 owner = 1 current assignment per ticket |
| 7 | Appears under My Tickets | My Tickets is driven by `CurrentOwnerEmployeeId` (`TicketQueryFilters.VisibleTo`, covered by `MyTicketsVisibilityTests`) — setting the owner is sufficient |
| 8 | Owner can Resolve and Close | `TicketLifecycleAppService.IsCurrentOwner` — the current assignee may always Resolve and Close, unless they hold only read-only roles (excluded from the pool anyway) |
| 9 | Preserve history, SLA, audit, notifications | Status, SLA instances/pauses, history, interactions, notes, notifications/outbox are **never written**. Audit is append-only: +1 `AutoAssign` row per ticket, +1 batch row. SLA clocks keep running untouched |
| 10 | Transactions + concurrency protection | Single transaction, `XACT_ABORT ON`, `sp_getapplock` (no concurrent runs), cursor row `UPDLOCK/ROWLOCK` (live intake queues behind it, exactly as the app does), candidate rows `UPDLOCK/ROWLOCK` + `RowVersion` re-check, 20 s lock timeout, all-or-nothing |
| 11 | Dry-run preview | Preview script (read-only) and `@DryRun = 1` in the execute script |
| 12 | Rollback only the new assignments | Rollback script keyed by the batch's `CorrelationId` — see §4 |

Audit/engine parity: each ticket gets the same record the engine writes (`AutoAssign`, null actor = system, `Mode=RoundRobin`) with
`Trigger=BacklogDistribution;BatchId=<guid>` appended, and `TicketAssignments.AssigningActorEmployeeId = NULL`.
.NET `Guid` order equals the ordinal order of the canonical GUID text, so the scripts sort on `CONVERT(char(36), id)` with a binary collation
(SQL Server's native `uniqueidentifier` ordering is different and is deliberately not used).

## 2. Decisions I need from you before execution

1. **Unowned tickets that are *not* in the Call Center department (default: excluded).** The engine's pool is the ticket's *current* department, and an
   assignee must belong to it. Giving a Finance/Legal/etc. ticket to a Call Center agent would either break that invariant or require a department
   transfer (changing department, audit, SLA routing). The preview's *Scope reconciliation* table shows exactly how many such tickets exist per
   department. If you want them moved to Call Center first, that is a separate, explicit change — tell me and I'll plan it.
2. **Fairness definition.** Round Robin deals evenly *from the cursor*; it does **not** level out agents who already hold different numbers of tickets
   (the preview shows `ActiveOwnedBefore/After` so you can see the effect). If you want workload-levelling instead, that deviates from "the existing engine".
3. **Notifications.** An assignment never created a notification in this app, and the script sends none (no bulk burst to agents). Say so if you want one.
4. **Automatic-assignment semantics are inherited.** Because the actor is the system (as with every Round Robin assignment), an *Open* ticket with no human
   response counts as "automatic pre-assignment" — a Genesys-reported handler can still take it over. That is existing engine behaviour, not new.

## 3. Execution plan (exact, gated)

| Step | Who | Action | Gate |
|---|---|---|---|
| 1 | You (read-only login) | Fill `@ServiceIdentity` in the **Preview** script, `@ServiceIdsConfirmed = 1`, run it. Return all result sets | Verdict must be `READY` |
| 2 | You + me | Review: agent list (anyone unexpected? run the "possible service account" heuristic list), per-agent table (spread ≤ 1), scope table (decision 1), anomalies (`NoOwnerButHasCurrentAssignment`, existing duplicate current rows must be 0) | **Written approval of counts** |
| 3 | DBA | Full backup / snapshot of `TigerCS_Ticketing_Prod`. Record the backup name | Backup verified |
| 4 | DBA | Run **Execute** with defaults on UAT / a restore of the backup (`@DryRun = 1`): all validations must pass, "DRY RUN … ROLLED BACK" | Clean dry run |
| 5 | DBA, approved window (low traffic) | Run **Execute** on Prod, still `@DryRun = 1`, then compare its counts to step 1 | Matches the approved numbers |
| 6 | DBA, on explicit go | Set `@ServiceIdsConfirmed = 1`, `@ApprovedTicketCount`, `@ApprovedAgentCount` from the approved preview, `@DryRun = 0`. Run. **Save the printed `BatchId`** | Counts must equal the live computation or it aborts |
| 7 | You | Post-check (§5) and spot-check 2–3 agents' My Tickets; Resolve/Close one test ticket if desired | Sign-off |

Runtime/locking note: the script locks the candidate tickets and the CC cursor for a few seconds. Lock escalation on a very large candidate set could briefly block writers to
`Tickets` — hence the low-traffic window and the 20 s lock timeout (it fails fast and changes nothing rather than waiting).
`AuditEntries` has no useful index on `Action`, so the one-time-batch check scans it once (the repo's other deploy scripts accept the same).

## 4. Rollback strategy

*Before commit:* the whole run is one transaction — any error, validation failure or mismatch rolls it all back; `@DryRun = 1` always does.
*After commit:* **`Rollback_TigerCS_PROD_CallCenterBacklogDistribution.sql`** with the saved `BatchId` (also found as `CorrelationId` of the batch's audit rows).

* Working set = exactly the tickets in that batch's `AutoAssign` audit rows (count cross-checked against the batch row; mismatch aborts). No other ticket can be touched.
* A ticket is released only if it is **still as the batch left it**: same owner, still Call Center, still active, batch row still the single current assignment, no later
  assignment, and nobody has worked it (no status change, note or first response after the batch). Anything else is **skipped and reported with the reason** — we never pull
  work out from under an agent. (`@IncludeWorked = 1` relaxes only the "worked" test, only with explicit approval.)
* Per released ticket: `CurrentOwnerEmployeeId → NULL`, the batch's assignment row → `IsCurrent = 0` (history kept, **never deleted**), +1 `AutoAssignRollback` audit row.
* Round Robin cursor restored to its pre-batch value only if no live ticket has been dealt since; otherwise left alone (a cursor position is harmless).
* Never deletes/edits audit, history, SLA, notes, notifications or interactions. Dry-run by default; second rollback of the same batch is refused.
* Last resort for a catastrophic problem is the step-3 backup — not needed for the expected failure modes above.

## 5. Post-execution verification (read-only)

```sql
-- every batch ticket: exactly one owner, exactly one current assignment, same person
SELECT COUNT(*) AS BatchTickets,
       SUM(CASE WHEN t.CurrentOwnerEmployeeId = a.AssignedEmployeeId THEN 1 ELSE 0 END) AS OwnerMatchesAssignment
FROM dbo.AuditEntries ae
JOIN dbo.Tickets t ON t.TicketId = TRY_CONVERT(bigint, ae.EntityId)
JOIN dbo.TicketAssignments a ON a.TicketId = t.TicketId AND a.IsCurrent = 1
WHERE ae.CorrelationId = '<BatchId>' AND ae.Action = N'AutoAssign';

-- must return 0 rows
SELECT TicketId FROM dbo.TicketAssignments WHERE IsCurrent = 1 GROUP BY TicketId HAVING COUNT(*) > 1;
-- must return 0: unowned active Call Center tickets that remain (other than any created after the run)
```

## 6. Residual risks

* Scripts are unexecuted; first run must be the UAT/restore dry run (step 4).
* Backlog drift between preview and execution is possible on a live system — that is why execution aborts unless counts equal the approved numbers; re-preview and re-approve.
* Service-account detection depends on the ids you supply; the preview also lists CC members whose names look like integration accounts for manual review.
