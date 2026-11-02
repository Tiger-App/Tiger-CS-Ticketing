# Call Center first — Production diagnosis and one-time backlog correction (ALL departments)

**Database:** `TigerCS_Ticketing_Prod` · **Status: NOT EXECUTED. Nothing has been run against Production, UAT or any SQL Server, and nothing has been deployed.**
Every script defaults to a mode that changes nothing; the execution script cannot commit without the approval gates in §5.

| File | Purpose | Writes? |
|---|---|---|
| `Diagnose_TigerCS_PROD_CallCenterFirst_ReadOnly.sql` | **Step 0.** Is Call Center-first live? How was `TG-ACC-20261102-0021` routed? Which new tickets skipped the Call Center and why? | **No** (SELECT only) |
| `Preview_TigerCS_PROD_CallCenterBacklogDistribution_ReadOnly.sql` | Dry run: counts per department, eligible agents in rotation order, per-agent distribution, exact ticket → agent plan | **No** (SELECT + session temp tables) |
| `Execute_TigerCS_PROD_CallCenterBacklogDistribution.sql` | Transfers unowned backlog from every department to the Call Center, then Round Robin assigns it. `@DryRun = 1` by default | Only with `@DryRun = 0` **and** all gates |
| `Rollback_TigerCS_PROD_CallCenterBacklogDistribution.sql` | Undoes only this batch (owner **and** department move). `@DryRun = 1` by default | Only with `@DryRun = 0` |

## 1. Diagnosis (from the source tree — Production itself was not reachable)

### 1.1 Why new tickets can still enter Accounting
A ticket whose number is `TG-ACC-…`, status **Open**, department **Accounting**, owner **Unassigned** was *not* routed Call Center-first. In the current code
(`TicketCreationAppService.CreateAsync`) every human-handled ticket is moved to the department with code `CC`, starts **InProgress** and is Round Robin
assigned. The Open/Accounting/Unassigned signature therefore means one of four things; the diagnose script tells them apart:

| # | Cause | Evidence (Diagnose result sets) | Remedy |
|---|---|---|---|
| A | **Production runs a build older than Call Center-first** (commits `bebcd3e`, `2f0c0ca`, 2 Nov). Old behaviour: category → department, no assignment rule → department queue | Verdict `NOT DEPLOYED` / `SCHEMA PRESENT, BUILD NOT LIVE`; zero `routed to Call Center first` audit rows | Deploy (§3) |
| B | **No single active department with `Code = 'CC'`.** `ResolveCallCenterAsync` returns null and the code *silently* falls back to the old routing | Verdict `MISCONFIGURED` | Fix the department row (DBA/Admin) |
| C | **Genesys ticket without `humanFollowUpRequired = true`.** `needsHuman = request.GenesysContext is null \|\| RequiresHumanFollowUp` — an AI-handled inquiry deliberately stays in the department it was created in, Unclassified (“Classification unavailable”), unowned | Result set 5: `Source = Genesys`, `RequiresHumanFollowUp = 0`; result set 9 groups by channel | **Business decision**, see §1.3 |
| D | Ticket created *before* the cut-over | Result set 6 timestamps vs `LastCallCenterFirstActivityUtc`; result set 10 | None for new tickets; backlog script covers it |

`appsettings.Production.json` ships `Genesys:Enabled = false`, so if Genesys tickets exist in Production the setting was overridden by environment variable;
the diagnose script reports the channel either way.

### 1.2 Is the latest code deployed? — What is missing
* The repository's tracked `publish/` folder is a **stale artifact** (built 31 Oct): `TigerCS.Application.dll` and `TigerCS.Infrastructure.dll` contain *no* `AssignCallCenterIntakeAsync`,
  `CallCenterIntake` or `DepartmentAssignmentRotation`. Nothing in the repository can prove what is on `TGAESHJAPP05`; only the diagnose script (data fingerprint) and the file versions on the server can.
* A complete Call Center-first release needs **all** of:
  1. DB: `Deploy_TigerCS_Prod_Migrations.sql` (adds `DepartmentAssignmentRotations`, `TicketInteractionHandlers`, transfer-tracking columns, `Tickets.CreatedByEmployeeId`),
  2. DB: `Deploy_TigerCS_PROD_AddUserPermissionOverrides.sql` — **separate script, not in the file above**; the current build reads `UserPermissionOverrides` on Transfer/Resolve/Close checks,
  3. App: a fresh `TigerCS.Api` **and** `TigerCS.Web` published from `main` ≥ `07271d7` (`tools/deploy/Publish-TigerCS.ps1`, not the tracked `publish/` folder),
  4. Config (API pool env vars): `Authorization__ServiceIdentity__EmployeeIds__n` and `Collections__Authorization__IntegrationEmployeeIds__n` — otherwise the Genesys service account can be dealt tickets,
  5. Data: one active department `CC` with eligible members (`Verify_TigerCS_CallCenter_Agents_ReadOnly.sql`).
* The application never migrates itself; if only the DLLs were copied, the new build fails on the missing table (500 on ticket creation) — tickets then stop being created rather than silently skipping, so a *working* old-looking ticket points to cause A, B or C.

### 1.3 Gap that code alone cannot close (decision required)
The AI-handled Genesys path (cause C) is intentional: moving such a ticket into the Call Center switches off `closedByAi` and the chatbot-inactivity rules (both exclude Call Center tickets).
Options: **(1)** keep as is and have the Genesys flow send `humanFollowUpRequired = true` whenever a person is needed (recommended, no code change); **(2)** route every Genesys ticket to the
Call Center and give up AI closure for them; **(3)** add a timeout-based escalation of unflagged tickets. No C# was changed in this PR because the build/test toolchain is not available in the authoring environment.

## 2. What the backlog procedure does (requirement → enforcement)

| # | Requirement | Enforcement |
|---|---|---|
| 1 | Unowned Open / InProgress / Pending, **all departments** | `TicketStatus IN (1,2,3,4)` **and** `CurrentOwnerEmployeeId IS NULL` **and** no `TicketAssignments` row with `IsCurrent = 1`; any `CurrentDepartmentId` |
| 2 | Transfer to Call Center | `CurrentDepartmentId → CC` only. Written exactly as the live intake writes it: `Transfer` audit (system actor, `Reason=Backlog correction - routed to Call Center first`) + `TicketTransferred` workflow event (`SystemRouting`, not an escalation) |
| 3 | Distribute fairly via the existing engine | Same pool rules, same persisted cursor, same row lock, same order as `AssignRoundRobinAsync`; dealt oldest-first so per-agent counts differ by ≤ 1 (validated; aborts otherwise) |
| 4 | Eligible agents only | not deactivated, not locked out, **not a service account**, holds **CS Agent** (`@CsAgentRoleOnly = 1`, see §2.1), member of CC |
| 5 | Preserve human owners | Tickets with an owner or a current assignment are never read into the plan; the `UPDATE` re-checks `owner IS NULL`, department, status and `RowVersion` per row, one mismatch aborts everything |
| 6 | Preserve closed/resolved, SLA, original department, history | Status 5/6 excluded. `OriginatingDepartmentId`, `TicketNumber`, status, priority, SLA instances/pauses, status history, interactions, notes, notifications/outbox are **never written** (validated + row-count snapshot). Audit is append-only |
| 7 | One owner, one current assignment | `Tickets.CurrentOwnerEmployeeId` + filtered unique index `TicketAssignments(TicketId) WHERE IsCurrent = 1`; post-write validation re-proves it per ticket |
| 8 | Concurrency / atomicity | One transaction, `XACT_ABORT ON`, `sp_getapplock`, CC cursor `UPDLOCK/ROWLOCK` (live intake queues behind it), candidate rows `UPDLOCK/ROWLOCK`, 20 s lock timeout, all-or-nothing |
| 9 | Dry run / approvals | Preview (read-only) + `@DryRun = 1`; commit needs approved ticket, transfer and agent counts + `@ServiceIdsConfirmed = 1` |
| 10 | Rollback | Rollback script, keyed by the batch `CorrelationId`; restores owner **and** department; skips anything touched since (§6) |

### 2.1 Decisions baked into the defaults (change only with approval)
* **`@CsAgentRoleOnly = 1`.** The live Round Robin pool also admits *Department Employee* / *Department Head* members of CC, but only **CS Agent / Supervisor / Manager** hold the `Transfer` role
  (`TicketRoleSets.Transfer`). A Department Employee who received a Call Center ticket could Resolve and Close it (owner right) but **could not Transfer it** — which breaks the Call Center workflow. The
  preview lists any such member as “engine-eligible but excluded”. (The same gap exists for *new* tickets in the live engine: `TicketRoleSets.RoundRobinEligible` vs `TicketRoleSets.Transfer`; restricting
  Call Center intake to Transfer-capable roles is a small engine change that is proposed here but not applied — it needs a build and the test suite.)
* **`@IncludeLiveAiConversations = 0`.** An unowned ticket that is a *live* Genesys AI conversation (originating interaction has a conversation id, no human follow-up, not ended, not inactivity-closed) is held back and
  listed by the preview, because moving it would disable AI closure/inactivity handling. Tickets with a human requested, ended conversations and all manual tickets are in scope.
* **No notifications** are emitted (assignments never emitted any in this app; avoids a bulk burst to agents).
* **Status is preserved**, not forced to InProgress as new Call Center tickets are.
* Round Robin deals evenly from the cursor; it does not level existing workloads (the preview shows `ActiveOwnedBefore/After`).

## 3. Approved procedure (gated — do not skip or reorder)

| Step | Who | Action | Gate |
|---|---|---|---|
| 0 | DBA, read-only login | Run **Diagnose**. Record the Verdict and result sets 4–10 for `TG-ACC-20261102-0021` | Cause identified (A/B/C/D) |
| 1 | DBA + release owner | If cause A/B: fix the cause first — apply both migration scripts (§1.2 items 1–2), set service-account env vars (item 4), publish & deploy **Api and Web** per `docs/Production-Deployment.md`, fix the `CC` department. Verify with **Diagnose** again + `Verify_TigerCS_CallCenter_Agents_ReadOnly.sql` (`READY`) | Verdict `LIVE`; one new test ticket routes CC-first and lands in an agent's My Tickets |
| 2 | You | Fill `@ServiceIdentity` (both settings) in **Preview**, `@ServiceIdsConfirmed = 1`, run it. Return all result sets | Verdict `READY` |
| 3 | You + release owner | Review: agent list, per-agent spread ≤ 1, scope table per department, held-back AI conversations, anomalies (`NoOwnerButHasCurrentAssignment`, duplicate current rows must be 0) | **Written approval of ticket / transfer / agent counts** |
| 4 | DBA | Full backup / snapshot of `TigerCS_Ticketing_Prod`; record its name | Backup verified |
| 5 | DBA | Run **Execute** with defaults (`@DryRun = 1`) on UAT or a restore of the backup. All validations must pass: “DRY RUN … ROLLED BACK” | Clean dry run |
| 6 | DBA, approved low-traffic window | Run **Execute** on Prod, still `@DryRun = 1`; compare counts with step 2 | Matches the approved numbers |
| 7 | DBA, on explicit go | Fill `@ServiceIdentity`, `@ServiceIdsConfirmed = 1`, `@ApprovedTicketCount`, `@ApprovedTransferCount`, `@ApprovedAgentCount` from the approved preview, `@DryRun = 0`. Run. **Save the printed `BatchId`** | Counts must equal the live computation or it aborts |
| 8 | You | Post-checks (§7) and the receiving-employee checks (§8) | Sign-off |

Locking note: the script locks the candidate tickets and the CC cursor for a few seconds; with a very large backlog, lock escalation could briefly block writers to `Tickets` — hence the low-traffic window and the 20 s lock timeout (fails fast, changes nothing).

## 4. Service-account exclusion list
Service accounts are **not in SQL**; the application builds the list from two settings (`ServiceIdentityRegistry`): `Authorization:ServiceIdentity:EmployeeIds` and `Collections:Authorization:IntegrationEmployeeIds`
(Production: API-pool environment variables `Authorization__ServiceIdentity__EmployeeIds__0..n`, `Collections__Authorization__IntegrationEmployeeIds__0..n`; the committed value is an empty list).
Both scripts take the same ids in `@ServiceIdentity`; the Preview/Execute **refuse to commit** while `@ServiceIdsConfirmed = 0`, and the Execute re-validates that no ticket went to a listed id.
The preview also prints a name-based heuristic (genesys / integration / service / bot / api) of CC members for manual review. **The ids are not available to this session — see Blockers.**

## 5. Execution gates (all enforced by `Execute`)
`DB_NAME() = TigerCS_Ticketing_Prod` · schema columns/indexes present (incl. the unique filtered index) · exactly one active `CC` · assignment enabled for CC · no earlier `BacklogDistributionBatch` ·
`@ServiceIdsConfirmed = 1` · approved counts (tickets, transfers, agents) equal the live computation · app lock held · every candidate row still unowned/same department/same status/same `RowVersion` ·
post-write validations (one owner = one current assignment; all in CC; status and originating department unchanged; no deactivated/service owner; spread ≤ 1; audit/assignment/event counts exact; no assignment ever superseded).

## 6. Rollback strategy
*Before commit:* one transaction — any error or mismatch rolls everything back; `@DryRun = 1` always does.
*After commit:* `Rollback_…sql` with the saved `BatchId` (also the `CorrelationId` of the batch's audit rows).
* Working set = exactly the batch's `AutoAssign` audit rows (count cross-checked with the batch row, as are the batch's `Transfer` rows); no other ticket can be touched.
* A ticket is released only if it is **still as the batch left it**: same owner, still in Call Center, still active, batch row still the single current assignment, no later assignment, nobody worked it
  (no status change, note or first response after the batch). Otherwise it is **skipped and reported** — human work in progress is never taken away (`@IncludeWorked = 1` relaxes only the “worked” test).
* Per released ticket: owner → NULL, **department → the one it came from** (read from the batch's own `Transfer` audit row), batch assignment row → `IsCurrent = 0` (history kept), + `AutoAssignRollback` and
  `Transfer` audit rows and a `TicketTransferred` event going back. Nothing is deleted. Cursor restored only if no live ticket was dealt since.
* Last resort for a catastrophic problem is the step-4 backup.

## 7. Post-execution verification (read-only)
```sql
-- every batch ticket: now in CC, exactly one owner, exactly one current assignment, same person
SELECT COUNT(*) AS BatchTickets,
       SUM(CASE WHEN t.CurrentOwnerEmployeeId = a.AssignedEmployeeId AND t.CurrentDepartmentId = a.AssignedDepartmentId THEN 1 ELSE 0 END) AS OwnerAndDeptMatchAssignment
FROM dbo.AuditEntries ae
JOIN dbo.Tickets t ON t.TicketId = TRY_CONVERT(bigint, ae.EntityId)
JOIN dbo.TicketAssignments a ON a.TicketId = t.TicketId AND a.IsCurrent = 1
WHERE ae.CorrelationId = '<BatchId>' AND ae.Action = N'AutoAssign';

-- must return 0 rows
SELECT TicketId FROM dbo.TicketAssignments WHERE IsCurrent = 1 GROUP BY TicketId HAVING COUNT(*) > 1;
-- must return 0 (other than tickets created/held back after the run): active unowned tickets anywhere
SELECT COUNT(*) FROM dbo.Tickets t WHERE t.TicketStatus IN (1,2,3,4) AND t.CurrentOwnerEmployeeId IS NULL
  AND NOT EXISTS (SELECT 1 FROM dbo.TicketAssignments a WHERE a.TicketId = t.TicketId AND a.IsCurrent = 1);
```

## 8. Receiving-employee verification (requirement 6) — what the code guarantees and what must be seen live
| Capability | Basis in code | Existing automated coverage (could not be re-run here — no .NET SDK) |
|---|---|---|
| Appears in **My Tickets** | driven by `CurrentOwnerEmployeeId` (any department, any status) | `MyTicketsVisibilityTests`, `DashboardOverviewTests.MyTickets_…` |
| **Resolve** and **Close** | the current assignee may always do both (`IsCurrentOwner`), unless read-only | `TicketLifecycleAppServiceTests.Assignee_InAnyDepartmentAndRole_CanResolveAndClose` |
| **Transfer** | requires a `Transfer` role (CS Agent / Supervisor / Manager) **and** to be the current owner; target must match the request type once classified (`422 transfer-target-must-match-request-type`) | `CallCenterFirstRoutingTests.CallCenterOwner_CanTransfer_…`, `OnlyTheAssignedCallCenterAgent_MayTransfer_…` |
| Destination re-assignment | destination department rule / Round Robin; previous owner loses ownership | `TicketTransferTrackingTests`, `TransferredOwnerAccessTests` |

Live check after execution (UI, as one real receiving CS Agent): My Tickets lists the batch tickets; open one → Resolve; open another → Close; open a third → Transfer to the request-type department
(an unclassified ticket may go to any active department) and confirm the destination Round Robin picked one employee. Use test/low-risk tickets agreed in advance.

## 9. Blockers before execution
1. **Service-account ids** (both settings) are not available → the scripts refuse to commit; also decides whether the Genesys integration account is a CC member.
2. **Production state is unverified** — nothing here could connect to Production. Step 0 (Diagnose) must be run and, if the Call Center-first build/schema/`CC` department is missing, step 1 completed first.
3. **No real counts** — the Preview must be run by someone with Production read access; the approval document is its output.
4. **The SQL has never been run on SQL Server** (none available here). The first run must be the `@DryRun = 1` on UAT or a restored backup (step 5). Treat any error there as a defect to fix before Production.
5. **Decisions:** (a) `@CsAgentRoleOnly` default, (b) holding back live AI conversations, (c) Genesys unflagged-ticket policy (§1.3), (d) backup/window owner.
6. **No automated tests were run** for this change (no .NET SDK in the authoring environment); C# is unchanged.

## 10. Residual risks
* Backlog drift between preview and execution — execution aborts unless counts equal the approved numbers; re-preview and re-approve.
* Transferred tickets leave their department's queue: department heads will see them disappear; communicate before the window. A source department whose settings forbid transfers out is not consulted (system routing, as in live intake).
* Service-account detection depends on the ids supplied.
