# Call Center first — Production diagnosis and one-time backlog correction (ALL departments, pending approvals & live AI conversations held back)

**Database:** `TigerCS_Ticketing_Prod` · **Status: NOT EXECUTED on Production, and nothing has been deployed.** The Preview / Execute / Rollback scripts **were validated on a real SQL Server 2022 container** (§11) — not on your Production server.
Every script defaults to a mode that changes nothing; the execution script cannot commit without the approval gates in §5.

**Confirmed business rules:** (1) move **all** unassigned Open / InProgress / Pending tickets from **every** department to the Call Center and give each exactly **one** eligible CC agent; (2) the **existing Round Robin**, no workload balancing; (3) **held back and never touched** — tickets with a **pending approval** (`TicketApprovals.Status = 1`) and tickets with an **active AI conversation** — listed separately by the Preview; (4) existing owners, AI conversations, status, SLA, original department, audit and workflow history are preserved; (5) no application code, configuration or workflow changes — future tickets behave as today.

| File | Purpose | Writes? |
|---|---|---|
| `Diagnose_TigerCS_PROD_CallCenterFirst_ReadOnly.sql` | **Step 0.** Is Call Center-first live? How was `TG-ACC-20261102-0021` routed? Which new tickets skipped the Call Center and why? | **No** (SELECT only) |
| `Preview_TigerCS_PROD_CallCenterBacklogDistribution_ReadOnly.sql` | Dry run: eligible tickets per original department, **held-back tickets listed separately**, eligible agents in rotation order, per-agent distribution, exact ticket → agent plan, the five approval numbers (§3a) | **No** (SELECT + session temp tables) |
| `Execute_TigerCS_PROD_CallCenterBacklogDistribution.sql` | Transfers unowned backlog from every department to the Call Center, then Round Robin assigns it. `@DryRun = 1` by default | Only with `@DryRun = 0` **and** all gates |
| `Rollback_TigerCS_PROD_CallCenterBacklogDistribution.sql` | Undoes only this batch (owner **and** department move). `@DryRun = 1` by default | Only with `@DryRun = 0` |
| `tests/backlog-distribution/` | Validation suite on a real SQL Server (see its README and §11) | Disposable container only |

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
| 1 | Unowned Open / InProgress / Pending, **all departments** | `TicketStatus IN (1,2,3,4)` **and** `CurrentOwnerEmployeeId IS NULL` **and** no `TicketAssignments` row with `IsCurrent = 1`; any `CurrentDepartmentId` (inactive departments included) |
| 1b | **Pending approvals are held back** | `NOT EXISTS (TicketApprovals … Status = 1)` in Preview and Execute; each held-back ticket is listed by the Preview (type, since-date); the held-back count is a commit gate. Approved / rejected / cancelled approvals do **not** block |
| 1c | **Active AI conversations are preserved** | held back by default (§2.1) and listed by the Preview; interaction, handoff and approval tables are never written; count is a commit gate |
| 2 | Transfer to Call Center | `CurrentDepartmentId → CC` only. Written exactly as the live intake writes it: `Transfer` audit (system actor, `Reason=Backlog correction - routed to Call Center first`) + `TicketTransferred` workflow event (`SystemRouting`, not an escalation) |
| 3 | Distribute fairly via the existing engine | Same pool rules, same persisted cursor, same row lock, same order as `AssignRoundRobinAsync`; dealt oldest-first so per-agent counts differ by ≤ 1 (validated; aborts otherwise) |
| 4 | Eligible agents only | not deactivated, not locked out, **not a service account**, holds **CS Agent** (`@CsAgentRoleOnly = 1`, see §2.1), member of CC. Re-evaluated **fresh after the writes** and the agents' `Employees` rows are locked for the run, so exactly one *currently eligible* owner per ticket is guaranteed |
| 5 | Preserve human owners | Tickets with an owner or a current assignment are never read into the plan; the `UPDATE` re-checks `owner IS NULL`, department, status and `RowVersion` per row, one mismatch aborts everything |
| 6 | Preserve closed/resolved, SLA, original department, history | Status 5/6 excluded. `OriginatingDepartmentId`, `TicketNumber`, status, priority, SLA instances/pauses, status history, interactions, notes, notifications/outbox are **never written** (validated + row-count snapshot). Audit is append-only |
| 7 | One owner, one current assignment | `Tickets.CurrentOwnerEmployeeId` + filtered unique index `TicketAssignments(TicketId) WHERE IsCurrent = 1`; post-write validation re-proves it per ticket |
| 8 | Concurrency / atomicity | One transaction, `XACT_ABORT ON`, `sp_getapplock`, CC cursor `UPDLOCK/ROWLOCK` (live intake queues behind it), candidate rows and CC agent rows `UPDLOCK/ROWLOCK`, post-write re-checks (no planned ticket gained a pending approval or an active AI conversation; every owner still eligible), 20 s lock timeout, all-or-nothing. Full race table in §11 |
| 9 | Dry run / approvals | Preview (read-only) + `@DryRun = 1`; commit needs the **five** approved counts (tickets, transfers, agents, held-back pending approvals, held-back active AI) + `@ServiceIdsConfirmed = 1` (or `@NoServiceAccountsExist = 1`), every listed service id a real employee |
| 10 | Rollback | Rollback script, keyed by the batch `CorrelationId`; restores owner **and** department; skips anything touched since (§6) |

### 2.1 Decisions baked into the defaults (change only with approval)
* **`@CsAgentRoleOnly = 1`.** The live Round Robin pool also admits *Department Employee* / *Department Head* members of CC, but only **CS Agent / Supervisor / Manager** hold the `Transfer` role
  (`TicketRoleSets.Transfer`). A Department Employee who received a Call Center ticket could Resolve and Close it (owner right) but **could not Transfer it** — which breaks the Call Center workflow. The
  preview lists any such member as “engine-eligible but excluded”. (The same gap exists for *new* tickets in the live engine: `TicketRoleSets.RoundRobinEligible` vs `TicketRoleSets.Transfer`; restricting
  Call Center intake to Transfer-capable roles is a small engine change that is proposed here but not applied — it needs a build and the test suite.)
* **Pending approvals — always held back** (no switch). A ticket waiting on an approval cycle keeps its approval and stays where it is; moving it would pull it out from under its approvers. Once the approval is decided it is an ordinary unassigned ticket and needs a follow-up decision (a second, separately approved batch).
* **`@IncludeLiveAiConversations = 0`.** An unowned ticket with an **active AI conversation** is held back and listed by the Preview, because moving it would disable AI closure / chatbot-inactivity handling. *Active AI conversation* here means: a Genesys-sourced interaction that is **not ended, not closed for inactivity,
  needs no human follow-up and was not taken by a person** (`Source = 2 AND EndedAtUtc IS NULL AND InactivityClosedAtUtc IS NULL AND RequiresHumanFollowUp = 0 AND HandledByUserId IS NULL`), **and** no interaction on the ticket is flagged `RequiresHumanFollowUp`, **and** there is no open human handoff
  (`TicketAgentHandoffs.Status` Waiting / Assigned / InProgress). Anything that needs or already has a person is human work and **is** in scope. This is a deliberate, slightly narrower reading than a plain "live conversation" check (a conversation a person has already taken, or for which a human was requested,
  is not an AI-only ticket). **Confirm the Preview's held-back list matches your understanding before approving.** Setting `@IncludeLiveAiConversations = 1` (in *both* Preview and Execute) includes them only with explicit approval; pending approvals stay held back either way.
* **No notifications** are emitted (assignments never emitted any in this app; avoids a bulk burst to agents).
* **Status is preserved**, not forced to InProgress as new Call Center tickets are.
* Round Robin deals evenly from the cursor; it does not level existing workloads (the preview shows `ActiveOwnedBefore/After`).

## 3. Approved procedure (gated — do not skip or reorder)

| Step | Who | Action | Gate |
|---|---|---|---|
| 0 | DBA, read-only login | Run **Diagnose**. Record the Verdict and result sets 4–10 for `TG-ACC-20261102-0021` | Cause identified (A/B/C/D) |
| 1 | DBA + release owner | If cause A/B: fix the cause first — apply both migration scripts (§1.2 items 1–2), set service-account env vars (item 4), publish & deploy **Api and Web** per `docs/Production-Deployment.md`, fix the `CC` department. Verify with **Diagnose** again + `Verify_TigerCS_CallCenter_Agents_ReadOnly.sql` (`READY`) | Verdict `LIVE`; one new test ticket routes CC-first and lands in an agent's My Tickets |
| 2 | You | Run the **Preview** exactly as in §3a. Return all result sets | Verdict `READY` |
| 3 | You + release owner | Review: agent list, per-agent spread ≤ 1, eligible tickets per original department, **the held-back list (pending approvals and active AI conversations)**, anomalies (`NoOwnerButHasCurrentAssignment`, duplicate current rows must be 0) | **Written approval of the five counts** |
| 4 | DBA | Full backup / snapshot of `TigerCS_Ticketing_Prod`; record its name | Backup verified |
| 5 | DBA | On UAT or a **restore of the backup under the name `TigerCS_Ticketing_Prod` on a non-production server**: run **Execute** with defaults (`@DryRun = 1`), then a real run (`@DryRun = 0`, gates filled), then **Rollback** (dry run, then real). All validations must pass | Clean on the copy |
| 6 | DBA, approved low-traffic window | Run **Execute** on Prod, still `@DryRun = 1`; compare counts with step 2 | Matches the approved numbers |
| 7 | DBA, on explicit go | Fill `@ServiceIdentity`, `@ServiceIdsConfirmed = 1`, `@ApprovedTicketCount`, `@ApprovedTransferCount`, `@ApprovedAgentCount`, `@ApprovedHeldBackPendingApprovalCount`, `@ApprovedHeldBackActiveAiCount` from the approved preview, `@DryRun = 0`; leave `@CsAgentRoleOnly` / `@IncludeLiveAiConversations` equal to the Preview's. Run as **one batch in SSMS** (or `sqlcmd -I` — filtered indexes need `QUOTED_IDENTIFIER ON`). **Save the printed `BatchId`** | Counts must equal the live computation or it aborts |
| 8 | You | Post-checks (§7) and the receiving-employee checks (§8) | Sign-off |

Locking note: the candidate tickets, the Call Center agents' `Employees` rows and the CC cursor are locked until commit — measured ≈ 0.6 ms per ticket (20k tickets ≈ 13 s). Live writers to those rows (and new Call Center intake) wait; the 20 s lock timeout makes the script fail with nothing changed instead of waiting. Run in a quiet window.

## 3a. Preview — exact instructions (read-only, safe to run any time)

1. Open `Preview_TigerCS_PROD_CallCenterBacklogDistribution_ReadOnly.sql` in SSMS, connected to `TigerCS_Ticketing_Prod` with a **read-only** login (`db_datareader` is enough — tested). It aborts on any other database name.
2. **Service accounts** (SQL cannot know them): take every id from `appsettings` — `Authorization:ServiceIdentity:EmployeeIds` **and** `Collections:Authorization:IntegrationEmployeeIds` (Production: API-pool environment variables) — and add one line each below the marker:
   ```sql
   INSERT INTO @ServiceIdentity VALUES ('xxxxxxxx-xxxx-xxxx-xxxx-xxxxxxxxxxxx');
   ```
   then set `DECLARE @ServiceIdsConfirmed bit = 1;`. Only if you have verified that **no** service / integration account exists, leave the list empty and set `@NoServiceAccountsExist = 1` instead. Until then the verdict is `BLOCKER`; a mistyped id is also a `BLOCKER` (`ServiceIdsNotFound` lists it).
3. Leave `@CsAgentRoleOnly = 1` and `@IncludeLiveAiConversations = 0` (the approved defaults). If anyone changes them, the **same** values must be used in Execute.
4. Execute (F5) **as one batch**. Nothing is written; about half a second for 20k tickets.
5. `Verdict` (first result set) must be **`READY`**. Then read — and send me **every result set** (ticket numbers and agent names only, no customer data):

   | Result set | What to check |
   |---|---|
   | 1 Verdict | `EligibleTickets_Total`, `TicketsMovedIntoCC`, `TicketsAlreadyInCC_AssignOnly`, `HeldBack_PendingApproval`, `HeldBack_ActiveAiConversation`, `EligibleAgents` |
   | 2 Eligible by **original department × status** | The move being approved: how many tickets leave each department (rows marked `MOVE to CC + assign`), inactive departments included |
   | 2b **HELD BACK** | Counts per department, then **every held-back ticket** with its reason: `PENDING APPROVAL` (approval type and since-date) or `ACTIVE AI CONVERSATION`; a ticket with both is listed once under the approval. These are left exactly as they are |
   | 3 Exclusions | Resolved / Closed unowned, already-owned (never touched), data-drift rows; **`TicketsWithMoreThanOneCurrentAssignment_MustBe0` must be 0** |
   | 4 Agents | Every Call Center member: `ELIGIBLE` or the reason excluded (deactivated / locked / service account / excluded by `@CsAgentRoleOnly` / no operational role). Anyone unexpected? Also read the "possible service account" list |
   | 5 Distribution | Tickets per agent (spread must be ≤ 1); from other departments vs already in CC; by status; load before → after (information only — no balancing); department × agent matrix |
   | 6 **Approval values** | The **five numbers** Execute will demand: `ApprovedTicketCount`, `ApprovedTransferCount`, `ApprovedAgentCount`, `ApprovedHeldBackPendingApprovalCount`, `ApprovedHeldBackActiveAiCount` |
   | 7 Exact plan | Every ticket → original department → receiving agent, oldest first |
6. Re-run as often as you like; it never changes anything. The numbers move only as the live backlog moves.

## 4. Service-account exclusion list
Service accounts are **not in SQL**; the application builds the list from two settings (`ServiceIdentityRegistry`): `Authorization:ServiceIdentity:EmployeeIds` and `Collections:Authorization:IntegrationEmployeeIds`
(Production: API-pool environment variables `Authorization__ServiceIdentity__EmployeeIds__0..n`, `Collections__Authorization__IntegrationEmployeeIds__0..n`; the committed value is an empty list).
Both scripts take the same ids in `@ServiceIdentity`; the Preview/Execute **refuse to commit** while `@ServiceIdsConfirmed = 0`, and the Execute re-validates that no ticket went to a listed id.
The preview also prints a name-based heuristic (genesys / integration / service / bot / api) of CC members for manual review. **The ids are not available to this session — see Blockers.**

## 5. Execution gates (all enforced by `Execute`)
`DB_NAME() = TigerCS_Ticketing_Prod` · schema columns / indexes present (incl. the unique filtered index and the hold-back source tables) · exactly one active `CC` · assignment enabled for CC · no earlier `BacklogDistributionBatch` (checked after the app lock) ·
`@ServiceIdsConfirmed = 1`, list non-empty **or** `@NoServiceAccountsExist = 1`, every listed id a real employee · **five approved counts** (tickets, transfers, agents, held-back pending approvals, held-back active AI) equal the live computation · app lock held ·
every candidate row still unowned / same department / same status / same `RowVersion` · **post-write re-checks** (no planned ticket gained a pending approval — Msg 50340 — or an active AI conversation — Msg 50341; every new owner still an eligible agent — Msg 50342) ·
post-write validations (one owner = one current assignment; all in CC; status and originating department and every preserved field unchanged; no deactivated / service owner; spread ≤ 1; audit / assignment / event counts exact; no assignment ever superseded).

## 6. Rollback strategy
*Before commit:* one transaction — any error or mismatch rolls everything back; `@DryRun = 1` always does.
*After commit:* `Rollback_…sql` with the saved `BatchId` (also the `CorrelationId` of the batch's audit rows).
* Working set = exactly the batch's `AutoAssign` audit rows (count cross-checked with the batch row, as are the batch's transfer events); no other ticket can be touched.
* A ticket is released only if it is **still as the batch left it**: same owner, still in Call Center, still active, batch row still the single current assignment, no later assignment, nobody worked it
  (no status change, note, first response, Genesys handler record, **new interaction, new approval request** or other workflow event after the batch). Otherwise it is **skipped and reported, left byte-for-byte as found** — human work in progress is never taken away (`@IncludeWorked = 1` relaxes only the “worked” test). Held-back tickets were never part of the batch, so rollback cannot reach them.
* Per released ticket: owner → NULL, **department → the one it came from** (read from the batch's own `TicketTransferred` event), batch assignment row → `IsCurrent = 0` (history kept), + `AutoAssignRollback` and
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
4. **Validated on a SQL Server 2022 test container, not on Production** (§11). Production's version / compatibility level (needs `STRING_AGG`, level ≥ 130), volume, indexes and real ticket shapes are untested; the first Production-like run must be the restored-copy run (step 5). Treat any error there as a defect to fix before Production.
5. **Decisions:** (a) `@CsAgentRoleOnly` default, (b) the held-back definitions (pending approval; *active AI conversation* as defined in §2.1 — confirm against the Preview's list), (c) Genesys unflagged-ticket policy (§1.3), (d) backup/window owner.
6. **The SQL has an automated suite (§11); the C# test suite was not run** (no .NET SDK in the authoring environment) and C# is unchanged.

## 9a. Interaction with "Take Ownership" (manual claim of the same tickets)
Call Center agents can now take ownership of unassigned tickets themselves ([Call-Center-Take-Ownership.md](Call-Center-Take-Ownership.md)). It uses the **same eligibility** as this batch (active, no owner, no current assignment, no pending approval, no active AI conversation), so the two never double-assign: a claimed ticket is owned and simply drops out of the plan, and **Rollback never touches a claim** (it is keyed on this batch's `AutoAssign` audit rows).
One thing to plan: Execute re-checks every planned row and aborts the whole run if any ticket changed after the Preview, so a claim made between Preview and Execute aborts the run (safe, rolled back — re-preview and re-approve). **Set `TicketClaim__Enabled=false` (config + API recycle, no redeploy) for the Preview → Execute window** and re-enable afterwards.

## 10. Residual risks
* Backlog drift between preview and execution — execution aborts unless counts equal the approved numbers; re-preview and re-approve.
* Transferred tickets leave their department's queue: department heads will see them disappear; communicate before the window. A source department whose settings forbid transfers out is not consulted (system routing, as in live intake).
* **Held-back tickets stay in their department, unowned**, and carry on in their own approval / AI flow; they are *not* picked up automatically afterwards (a second, separately approved batch is needed once an approval is decided or an AI conversation ends).
* Rarely a competing writer can make SQL Server choose the batch as a **deadlock victim** (Msg 1205): it rolls back completely and nothing changes — re-run.
* Service-account detection depends on the ids supplied.

## 11. Validation on SQL Server (what is proven, and what is not)
The three scripts were run against a **real SQL Server 2022 (Developer, Linux container)**, schema built from this repository's own migration scripts. **160 checks, 160 passed**, repeated from a freshly rebuilt container (`tests/backlog-distribution/setup_test_db.sh`, then `run_tests.py`). The expected deal and the hold-back rules are recomputed by an **independent Python oracle** from raw rows, not by the SQL under test.
Validation found and fixed defects in the earlier, never-run drafts — including a variable collision (`@n`/`@N`, case-insensitive) that stopped the script compiling on SQL Server, and an illegal aggregate with an outer reference in Rollback (Msg 8124) — that **are still present in the version merged to `main` by #143/#144**; this change replaces them.

| Proven | |
|---|---|
| Preview | read-only (table digests unchanged); plan = oracle for every ticket; runs under a plain `db_datareader` login that cannot write; held-back tickets listed separately and absent from the plan; blocks on an unconfirmed / empty / mistyped service-account list |
| Dry run / gates | `@DryRun = 1` leaves the database byte-identical; each missing or wrong approval — incl. both held-back counts — and a wrong database name refuse and change nothing |
| Pending approvals | pending → held back; approved / rejected / cancelled → still eligible; pending + AI counted once; held-back rows, approvals, interactions, handoffs byte-identical after commit; an approval decided or newly requested after the preview changes the counts and refuses the run |
| Active AI | live AI-only → held back; ended / inactivity-closed / flagged for human / open handoff / taken by an agent → eligible; AI tables untouched; `@IncludeLiveAiConversations = 1` includes them only when Preview and Execute agree and the counts match |
| Pool | `@CsAgentRoleOnly` 1 and 0 both equal the oracle; deactivated / locked / service / read-only / supervisory accounts never receive tickets |
| Commit | owners = oracle; all in CC; originating dept / request type / category / priority / status / SLA / created preserved; exactly one current assignment each; audit + `TicketTransferred/SystemRouting` events; non-eligible tickets byte-identical; spread ≤ 1 |
| Rollback | untouched batch → the exact pre-batch `Tickets` table; reassigned / worked / transferred / newly-approved / new-interaction tickets skipped byte-for-byte; history only grows |
| Scale | 20,072 eligible (16,066 moved) + 100,000 closed: preview 0.5 s, commit ≈ 13 s, rollback ≈ 2 s |

**Concurrent-assignment safeguards** (each has a test; a mutation check proves the test fails when the safeguard is removed):

| Race | Outcome |
|---|---|
| Second run while one is running | Fails immediately on the application lock; nothing written |
| A human assigns a candidate ticket mid-run | Blocks until commit, then updates 0 rows — cannot overwrite the batch's owner |
| A duplicate *current* assignment | Rejected by the unique filtered index |
| Pending approval / AI conversation created on a planned ticket **between selection and the update** | Post-write re-check aborts the **whole** run (Msg 50340 / 50341); nothing changed |
| Same, **after** the update (ticket rows exclusively locked) | The competing insert waits on the ticket's foreign key and loses (deadlock victim, Msg 1205) or waits for the commit; no batch ticket ends up with a pre-commit pending approval |
| A new owner is locked out mid-run | Re-check aborts the whole run (Msg 50342) |
| An agent is deactivated mid-run | Blocked until the batch commits (agent rows are locked) |
| Live Call Center intake (same cursor lock as the app) | Waits for the batch, then reads the advanced cursor |
| Backlog changes between preview and run | Counts differ → abort; re-preview and re-approve |

**Not proven:** your Production server (version / compatibility level, volume, indexes), real Genesys traffic, tickets carrying a `RequestTypeId` (not seeded; compared NULL-safe), the C# application. The "active AI conversation" definition is an interpretation of the schema (§2.1) — the Preview's held-back list is where you confirm it.
