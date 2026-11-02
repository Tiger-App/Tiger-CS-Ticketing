# Call Center first — Production diagnosis and the one-time distribution of ALL existing Open tickets (assigned or not, every department)

**Database:** `TigerCS_Ticketing_Prod` · **Status: NOT EXECUTED on Production, and nothing has been deployed.** The Preview / Execute / Rollback scripts **were validated on a real SQL Server 2022 container** (§11), and the Execute/Rollback result was then driven through the **real API** (§8) — not on your Production server.
Every script defaults to a mode that changes nothing; the execution script cannot commit without the approval gates in §5. **Do not execute against Production until the Preview counts are approved and the backup and rollback plan are confirmed (§3).**

**Final business rules (this supersedes the earlier "unowned Open/InProgress/Pending" scope):**
1. **Every Open ticket from every department — whether it has an owner or not —** is moved to the Call Center (`CC`) and given to exactly **one** eligible Call Center **CS Agent**. *Not Open* tickets (InProgress, Pending*, Resolved, Closed) are never touched.
2. **Round Robin** (the existing engine's pool, order and persisted cursor, oldest ticket first, spread ≤ 1) — **except** a ticket whose Genesys interaction was **answered** by an eligible agent: it goes directly to that answering agent and does not consume a Round Robin turn (the same rule the live "the agent who answers owns the ticket" logic applies to new calls).
3. **Held back and never touched:** tickets with a **pending approval** (`TicketApprovals.Status = 1`) and tickets with an **active AI conversation** — listed separately by the Preview; both counts are commit gates.
4. **Preserved:** originating department, status (still Open), SLA, category, request type, priority, and **all history** — audit, workflow events, status history, notes, and **every assignment row** (the previous current row is retired, never deleted).
5. **Previous owners lose ordinary ticket-action access:** the ticket's owner changes and the previous owner is left holding a *superseded* assignment, which the application treats as "previous owner" — they can no longer Resolve, Close, Transfer or change the status of it (proved through the API, §8).
6. The receiving agent sees every assigned ticket in **My Tickets** and works it through the normal workflow (status → Resolve → Close, or Transfer).
7. **Future tickets** need no manual claiming: a ticket needing a person is routed to the Call Center and given to one CS Agent by Round Robin at creation (§12); a Genesys call answered by a mapped, eligible agent goes directly to that agent.

| File | Purpose | Writes? |
|---|---|---|
| `Diagnose_TigerCS_PROD_CallCenterFirst_ReadOnly.sql` | **Step 0.** Is Call Center-first live? How was `TG-ACC-20261102-0021` routed? Which new tickets skipped the Call Center and why? | **No** (SELECT only) |
| `Preview_TigerCS_PROD_CallCenterBacklogDistribution_ReadOnly.sql` | Dry run: Open tickets per current department (owned / not), **held-back tickets listed separately**, **who loses tickets**, eligible agents in rotation order, **expected per-agent distribution**, exact ticket → agent plan, the **seven** approval values (§3a) | **No** (SELECT + session temp tables) |
| `Execute_TigerCS_PROD_CallCenterBacklogDistribution.sql` | Moves every Open ticket to the Call Center and deals it (answering agent, else Round Robin); retires the previous assignment. `@DryRun = 1` by default | Only with `@DryRun = 0` **and** all gates |
| `Rollback_TigerCS_PROD_CallCenterBacklogDistribution.sql` | Undoes only this batch: previous owner, previous department and previous assignment come back. `@DryRun = 1` by default | Only with `@DryRun = 0` |
| `tests/backlog-distribution/` | Validation suite on a real SQL Server (see its README and §11) | Disposable container only |
| `Verify_TigerCS_TakeOwnership_ReadOnly.sql` | Read-only: the manual Take Ownership pool and claims (§9a) | **No** |

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

## 2. What the procedure does (requirement → enforcement)

| # | Requirement | Enforcement |
|---|---|---|
| 1 | **All Open tickets, all departments, assigned or not** | `TicketStatus = 1` only; any `CurrentDepartmentId` (inactive departments included); any `CurrentOwnerEmployeeId` (or none); legacy data (owner without an assignment row, or an assignment row without an owner) is included and repaired |
| 1b | **Pending approvals are held back** | `NOT EXISTS (TicketApprovals … Status = 1)` in Preview and Execute; each held-back ticket is listed by the Preview (type, since-date); the held-back count is a commit gate. Approved / rejected / cancelled approvals do **not** block |
| 1c | **Active AI conversations are preserved** | held back by default (§2.1) and listed by the Preview; interaction, handoff and approval tables are never written; count is a commit gate |
| 1d | **Non-Open tickets are excluded** | InProgress, PendingCustomer, PendingThirdParty, Resolved, Closed are never read into the plan; counted in the Preview's exclusions |
| 2 | Move to Call Center | `CurrentDepartmentId → CC` only. Written exactly as the live intake writes it: `Transfer` audit (system actor; BeforeValue names the previous department **and previous owner**) + `TicketTransferred` workflow event (`SystemRouting`, not an escalation) |
| 3 | Distribute fairly via the existing engine | Same pool rules, same persisted cursor, same row lock, same order as `AssignRoundRobinAsync`; dealt oldest-first so the Round Robin counts differ by ≤ 1 (validated; aborts otherwise). A ticket answered by an eligible agent is **pinned** to that agent and does not move the cursor |
| 4 | Exactly one CS Agent per ticket | pool = not deactivated, not locked out, **not a service account**, holds **CS Agent** (`@CsAgentRoleOnly = 1`, §2.1), member of CC. Re-evaluated **fresh after the writes**; the agents' `Employees` rows are locked for the run |
| 5 | **Ownership protection** | Candidate rows are locked `UPDLOCK/ROWLOCK`; the `UPDATE` only succeeds if the ticket is still Open, in the planned department, **owned by whom the plan saw**, with the same `RowVersion` — one mismatch aborts everything. A person saving the ticket in the application meanwhile either waits for the commit and then fails the application's own row-version check, or is seen and aborts the run (§11, T07/T13) |
| 5b | **Preview/Execute cannot drift apart silently** | `@ApprovedReassignedCount` (tickets whose owner is replaced) and `@ApprovedPlanFingerprint` (a checksum of the exact ticket → agent plan) are commit gates, so a person taking a ticket after the Preview, or any change in who-gets-what, refuses the run |
| 6 | Preserve status, SLA, category, request type, history | `OriginatingDepartmentId`, `TicketNumber`, **status (Open)**, priority, request type, category, SLA state, escalation, verification, created time are compared before/after **inside the transaction** (abort on any difference). Status history, SLA instances/pauses, interactions, notes, notifications/outbox are **never written**. Audit and assignment history are append-only |
| 7 | One owner, one current assignment, **history kept** | `Tickets.CurrentOwnerEmployeeId` + filtered unique index `TicketAssignments(TicketId) WHERE IsCurrent = 1`. The previous current row is set `IsCurrent = 0` (never deleted) and one new current row is added; validated: assignment rows only grow by exactly one per changed ticket, and every retired row is a previous *current* row |
| 8 | Concurrency / atomicity | One transaction, `XACT_ABORT ON`, `sp_getapplock`, CC cursor `UPDLOCK/ROWLOCK` (live intake queues behind it), candidate rows and CC agent rows `UPDLOCK/ROWLOCK`, post-write re-checks, 20 s lock timeout, all-or-nothing. Race table in §11 |
| 9 | Dry run / approvals | Preview (read-only) + `@DryRun = 1`; commit needs the **seven** approved values + `@ServiceIdsConfirmed = 1` (or `@NoServiceAccountsExist = 1`), every listed service id a real employee |
| 10 | Rollback | Rollback script, keyed by the batch `CorrelationId`; restores previous owner **and** department **and** assignment; skips anything touched since (§6) |

### 2.1 Decisions baked into the defaults (change only with approval)
* **Assigned Open tickets are taken from their owners** (new in the final rule). The Preview's *who loses tickets* table lists every previous owner and how many Open tickets they lose, so this is approved knowingly. Tickets already in CC and already owned by the dealt agent are **KEPT** (no write, no history noise).
* **Answering agent first.** `TicketInteractions.HandledByUserId` of the ticket's **originating** interaction (the agent Genesys reported as having connected) — if that person is in the pool — gets the ticket directly. If they are not in the pool (deactivated, locked, service, not CS Agent, not in CC), the ticket falls back to Round Robin. These tickets do not advance the rotation cursor, so the Round Robin remains fair among the rest.
* **`@CsAgentRoleOnly = 1`.** Only **CS Agent** receives tickets: only CS Agent / Supervisor / Manager hold `Transfer`, so a Department Employee who received a Call Center ticket could Resolve and Close it but not pass it on. The live Call Center intake now follows the same rule (§12), so the backlog and future tickets are handled by the same people. The Preview lists any other CC member as “engine-eligible but excluded”.
* **Pending approvals — always held back** (no switch). A ticket waiting on an approval cycle keeps its approval and stays where it is; moving it would pull it out from under its approvers. Once the approval is decided it is an ordinary Open ticket and needs a follow-up decision (a second, separately approved batch).
* **`@IncludeLiveAiConversations = 0`.** An Open ticket with an **active AI conversation** is held back and listed, because moving it would disable AI closure / chatbot-inactivity handling. *Active AI conversation* means: a Genesys-sourced interaction that is **not ended, not closed for inactivity,
  needs no human follow-up and was not taken by a person** (`Source = 2 AND EndedAtUtc IS NULL AND InactivityClosedAtUtc IS NULL AND RequiresHumanFollowUp = 0 AND HandledByUserId IS NULL`), **and** no interaction on the ticket is flagged `RequiresHumanFollowUp`, **and** there is no open human handoff
  (`TicketAgentHandoffs.Status` Waiting / Assigned / InProgress). **Confirm the Preview's held-back list matches your understanding before approving.** `@IncludeLiveAiConversations = 1` (in *both* Preview and Execute) includes them only with explicit approval; pending approvals stay held back either way.
* **Only Open.** A ticket a person has already started (InProgress) or parked (Pending) is *not* taken from them, even if assigned. If the business later wants those included, that is a separate, explicitly approved change.
* **No notifications** are emitted (assignments never emitted any in this app; avoids a bulk burst to agents). **Status is preserved**, not forced to InProgress as new Call Center tickets are.
* Round Robin deals evenly from the cursor; it does not level existing workloads (the Preview shows `OpenOwnedBefore` / `OpenOwnedAfter_Estimated`).

## 3. Approved procedure (gated — do not skip or reorder)

| Step | Who | Action | Gate |
|---|---|---|---|
| 0 | DBA, read-only login | Run **Diagnose**. Record the Verdict and result sets 4–10 for `TG-ACC-20261102-0021` | Cause identified (A/B/C/D) |
| 1 | DBA + release owner | If cause A/B: fix the cause first — apply both migration scripts (§1.2 items 1–2), set service-account env vars (item 4), publish & deploy **Api and Web** per `docs/Production-Deployment.md`, fix the `CC` department. Verify with **Diagnose** again + `Verify_TigerCS_CallCenter_Agents_ReadOnly.sql` (`READY`) | Verdict `LIVE`; one new test ticket routes CC-first and lands in an agent's My Tickets |
| 2 | You | Run the **Preview** exactly as in §3a. Return all result sets | Verdict `READY` |
| 3 | You + release owner | Review: agent list, **expected distribution per agent**, Open tickets per department, **who loses tickets**, **the held-back list (pending approvals and active AI conversations)**, anomalies (duplicate current rows must be 0) | **Written approval of the seven values** |
| 4 | DBA | Full backup / snapshot of `TigerCS_Ticketing_Prod`; record its name; **confirm the rollback plan (§6) and that the restore of the backup has been rehearsed**; set `TicketClaim__Enabled=false` for the window (§9a) | Backup verified, rollback plan confirmed |
| 5 | DBA | On UAT or a **restore of the backup under the name `TigerCS_Ticketing_Prod` on a non-production server**: run **Execute** with defaults (`@DryRun = 1`), then a real run (`@DryRun = 0`, gates filled), then **Rollback** (dry run, then real). All validations must pass | Clean on the copy |
| 6 | DBA, approved low-traffic window | Run **Execute** on Prod, still `@DryRun = 1`; compare counts with step 2 | Matches the approved numbers |
| 7 | DBA, on explicit go | Fill `@ServiceIdentity`, `@ServiceIdsConfirmed = 1`, `@ApprovedTicketCount`, `@ApprovedTransferCount`, `@ApprovedAgentCount`, `@ApprovedHeldBackPendingApprovalCount`, `@ApprovedHeldBackActiveAiCount`, `@ApprovedReassignedCount`, `@ApprovedPlanFingerprint` from the approved preview, `@DryRun = 0`; leave `@CsAgentRoleOnly` / `@IncludeLiveAiConversations` equal to the Preview's. Run as **one batch in SSMS** (or `sqlcmd -I` — filtered indexes need `QUOTED_IDENTIFIER ON`). **Save the printed `BatchId`** | Counts must equal the live computation or it aborts |
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
   | 1 Verdict | `OpenTickets_InPlan`, `TicketsChanged`, `TicketsMovedIntoCC`, `TicketsAlreadyCorrect_Kept`, `NoOwnerBefore_Assigned`, `Reassigned_FromAnotherOwner`, `ToTheAnsweringAgent`, `HeldBack_PendingApproval`, `HeldBack_ActiveAiConversation`, `EligibleAgents` |
   | 2 Open tickets by **current department × owned/not** | The move being approved: how many Open tickets leave each department (rows marked `MOVE to CC + assign`), inactive departments included |
   | 2b **HELD BACK** | Counts per department, then **every held-back ticket** with its reason: `PENDING APPROVAL` (approval type and since-date) or `ACTIVE AI CONVERSATION`; a ticket with both is listed once under the approval. These are left exactly as they are |
   | 3 Exclusions | Resolved/Closed and InProgress/Pending tickets (never touched), legacy/drift rows (in the plan, repaired); **`TicketsWithMoreThanOneCurrentAssignment_MustBe0` must be 0** |
   | 3b **Who loses tickets** | Every current owner who will lose Open tickets (name, roles, how many, how many leave their department). Approve this knowingly |
   | 4 Agents | Every Call Center member: `ELIGIBLE` or the reason excluded (deactivated / locked / service account / excluded by `@CsAgentRoleOnly` / no operational role). Anyone unexpected? Also read the "possible service account" list |
   | 5 **Expected distribution per agent** | Open tickets each agent will own, by Round Robin vs as answering agent; from other departments; already theirs (kept); taken from another owner; Open load before → after (information only — no balancing); Round Robin spread (must be ≤ 1); department × agent matrix |
   | 6 **Approval values** | The **seven values** Execute will demand: `ApprovedTicketCount`, `ApprovedTransferCount`, `ApprovedAgentCount`, `ApprovedHeldBackPendingApprovalCount`, `ApprovedHeldBackActiveAiCount`, `ApprovedReassignedCount`, `ApprovedPlanFingerprint` |
   | 7 Exact plan | Every ticket → current department → receiving agent, with `PlanAction` (KEEP / ASSIGN / REASSIGN), basis (answering agent / round robin) and the current owner who loses it, oldest first |
6. Re-run as often as you like; it never changes anything. The numbers move only as the live backlog moves.

## 4. Service-account exclusion list
Service accounts are **not in SQL**; the application builds the list from two settings (`ServiceIdentityRegistry`): `Authorization:ServiceIdentity:EmployeeIds` and `Collections:Authorization:IntegrationEmployeeIds`
(Production: API-pool environment variables `Authorization__ServiceIdentity__EmployeeIds__0..n`, `Collections__Authorization__IntegrationEmployeeIds__0..n`; the committed value is an empty list).
Both scripts take the same ids in `@ServiceIdentity`; the Preview/Execute **refuse to commit** while `@ServiceIdsConfirmed = 0`, and the Execute re-validates that no ticket went to a listed id.
The preview also prints a name-based heuristic (genesys / integration / service / bot / api) of CC members for manual review. **The ids are not available to this session — see Blockers.**

## 5. Execution gates (all enforced by `Execute`)
`DB_NAME() = TigerCS_Ticketing_Prod` · schema columns / indexes present (incl. the unique filtered index and the hold-back source tables) · exactly one active `CC` · assignment enabled for CC · no earlier `BacklogDistributionBatch` (checked after the app lock) ·
`@ServiceIdsConfirmed = 1`, list non-empty **or** `@NoServiceAccountsExist = 1`, every listed id a real employee · **seven approved values** (tickets, transfers, agents, held-back pending approvals, held-back active AI, reassigned, plan fingerprint) equal the live computation · app lock held ·
every changed row still Open / same department / **same owner** / same `RowVersion` (Msg 50316) · the previous current assignment rows retired are exactly the planned ones (Msg 50318) · **post-write re-checks** (no planned ticket gained a pending approval — Msg 50340 — or an active AI conversation — Msg 50341; every new owner still an eligible agent — Msg 50342) ·
post-write validations (one owner = one current assignment; all Open and in CC; every preserved field unchanged; no deactivated / service owner; Round Robin spread ≤ 1; audit / assignment / event counts exact; assignment history grows by exactly one row per changed ticket and every retired row is a previous current row).

## 6. Rollback strategy
*Before commit:* one transaction — any error or mismatch rolls everything back; `@DryRun = 1` always does.
*After commit:* `Rollback_…sql` with the saved `BatchId` (also the `CorrelationId` of the batch's audit rows). **Confirm this plan before approving execution.**
* Working set = exactly the batch's per-ticket `AutoAssign` audit rows (count cross-checked with the batch row, as are the batch's transfer events); their BeforeValue records `PreviousOwner`, `PreviousDepartmentId` and `PreviousAssignmentId` for every ticket. No other ticket can be touched. Tickets the batch KEPT have no audit row and are not touched.
* A ticket is rolled back only if it is **still as the batch left it**: same owner, still in Call Center, still active, batch row still the single current assignment, no later assignment, nobody worked it
  (no status change, note, first response, Genesys handler record, **new interaction, new approval request** or other workflow event after the batch). Otherwise it is **skipped and reported, left byte-for-byte as found** — human work in progress is never taken away (`@IncludeWorked = 1` relaxes only the “worked” test). Held-back and non-Open tickets were never part of the batch.
* Per rolled-back ticket: **owner → the previous owner (or none), department → the one it came from, the batch assignment row → `IsCurrent = 0` (history kept), and the previous assignment is restored as a NEW current row** (employee and department copied from the retired previous row, which stays as history — rows are never re-opened or deleted),
  + `AutoAssignRollback` and `Transfer` audit rows and a `TicketTransferred` event going back. Cursor restored only if no live ticket was dealt since. Verified: after a clean rollback the `Tickets` table is byte-identical to before the batch and the set of current assignments is identical.
* Last resort for a catastrophic problem is the step-4 backup.

## 7. Post-execution verification (read-only)
```sql
-- every batch ticket: now in CC, exactly one owner, exactly one current assignment, same person
SELECT COUNT(*) AS BatchTickets,
       SUM(CASE WHEN t.CurrentOwnerEmployeeId = a.AssignedEmployeeId AND t.CurrentDepartmentId = a.AssignedDepartmentId THEN 1 ELSE 0 END) AS OwnerAndDeptMatchAssignment
FROM dbo.AuditEntries ae
JOIN dbo.Tickets t ON t.TicketId = TRY_CONVERT(bigint, ae.EntityId)
JOIN dbo.TicketAssignments a ON a.TicketId = t.TicketId AND a.IsCurrent = 1
WHERE ae.CorrelationId = '<BatchId>' AND ae.Action = N'AutoAssign';   -- BatchTickets must equal TicketsChanged; OwnerAndDeptMatchAssignment must equal BatchTickets

-- must return 0 rows
SELECT TicketId FROM dbo.TicketAssignments WHERE IsCurrent = 1 GROUP BY TicketId HAVING COUNT(*) > 1;
-- must return 0 (other than tickets held back, or created after the run): Open tickets that are still unowned or outside the Call Center
SELECT COUNT(*) FROM dbo.Tickets t WHERE t.TicketStatus = 1 AND (t.CurrentOwnerEmployeeId IS NULL OR t.CurrentDepartmentId <> (SELECT DepartmentId FROM dbo.Departments WHERE Code = N'CC'))
  AND NOT EXISTS (SELECT 1 FROM dbo.TicketApprovals ap WHERE ap.TicketId = t.TicketId AND ap.Status = 1);
-- history check: every retired assignment of a batch ticket is still there (rows are never deleted)
SELECT COUNT(*) AS RetiredAssignmentRows FROM dbo.TicketAssignments WHERE IsCurrent = 0 AND TicketId IN
  (SELECT TRY_CONVERT(bigint, EntityId) FROM dbo.AuditEntries WHERE CorrelationId = '<BatchId>' AND Action = N'AutoAssign');
```

## 8. Receiving-employee verification — proven end to end, and what must still be seen live
The real Preview / Execute / Rollback scripts are run on a database built from the **application's own EF schema**, and the **real API** is then driven as the people involved
(`BulkDistributionEndToEndSqlServerTests`, real HTTP pipeline, real permissions):

| Capability | What the test does | Result |
|---|---|---|
| Appears in **My Tickets** | the receiving agent lists `GET /api/tickets?ownerEmployeeId=<agent>` | the reassigned Open ticket is listed |
| **Status → Resolve → Close** | the receiving agent moves an unassigned-before ticket InProgress, resolves and closes it | 200 / 200 / 200 |
| **Transfer** | the receiving agent transfers a ticket that was taken from a department employee | 200 |
| **Previous owner (department employee)** loses access | the former owner opens the moved ticket | 403 |
| **Previous owner (another CS Agent)** loses Resolve / Close / Transfer / status | the former owner tries all four on the ticket that went to the agent who answered it | 403 ×4 |
| **Answering agent** | the ticket answered by agent X goes to X, not to Round Robin | owner = X |
| **Not Open** left alone | an InProgress ticket owned by the department employee | unchanged; the employee still works it (200) |
| **Rollback** | the batch is rolled back | the former owner owns the ticket again and the application agrees (opens it as owner, can move it InProgress); a ticket already worked to Closed since the batch is left as it is; X, who held it meanwhile, can no longer close it |

Existing coverage of the same rules in isolation: `TicketLifecycleAppServiceTests.Assignee_InAnyDepartmentAndRole_CanResolveAndClose`, `CallCenterFirstRoutingTests.CallCenterOwner_CanTransfer_…`, `OnlyTheAssignedCallCenterAgent_MayTransfer_…`, `TransferredOwnerAccessTests`.
Live check after execution (UI, as one real receiving CS Agent, on test/low-risk tickets agreed in advance): My Tickets lists the batch tickets; open one → Resolve; another → Close; a third → Transfer; and, as one **previous owner**, confirm the ticket is no longer actionable.

## 9. Blockers before execution
1. **Service-account ids** (both settings) are not available → the scripts refuse to commit; also decides whether the Genesys integration account is a CC member.
2. **Production state is unverified** — nothing here could connect to Production. Step 0 (Diagnose) must be run and, if the Call Center-first build/schema/`CC` department is missing, step 1 completed first.
3. **No real counts** — the Preview must be run by someone with Production read access; the approval document is its output.
4. **Validated on a SQL Server 2022 test container, not on Production** (§11). Production's version / compatibility level (needs `STRING_AGG`, level ≥ 130), volume, indexes and real ticket shapes are untested; the first Production-like run must be the restored-copy run (step 5). Treat any error there as a defect to fix before Production.
5. **Decisions:** (a) **taking Open tickets away from their current owners** (the *who loses tickets* table), (b) Open only — InProgress/Pending assigned tickets stay with their owners, (c) the answering-agent rule, (d) `@CsAgentRoleOnly` default, (e) the held-back definitions (pending approval; *active AI conversation* as defined in §2.1 — confirm against the Preview's list), (f) Genesys unflagged-ticket policy (§1.3), (g) backup/window owner.
6. **Backup verified, restore rehearsed and rollback plan confirmed** before any `@DryRun = 0` (step 4).

## 9a. Interaction with "Take Ownership" (manual claim of unassigned tickets)
Call Center agents can take ownership of unassigned tickets themselves ([Call-Center-Take-Ownership.md](Call-Center-Take-Ownership.md)). The manual pool (unassigned, active, no pending approval, no active AI) is a subset of this batch's reach, so the two never double-assign: a claimed ticket is owned, and the batch simply treats it as an Open ticket with an owner (it would be re-dealt if still Open; kept if it already belongs to the dealt agent). **Rollback never touches a claim** (keyed on this batch's `AutoAssign` audit rows).
One thing to plan: Execute aborts the whole run if any planned ticket changed after the Preview, and a claim changes the *reassigned* count and the plan fingerprint — so a claim made between Preview and Execute refuses the run (safe, rolled back — re-preview and re-approve). **Set `TicketClaim__Enabled=false` (config + API recycle, no redeploy) for the Preview → Execute window** and re-enable afterwards.

## 10. Residual risks
* Backlog drift between preview and execution — execution aborts unless counts equal the approved numbers; re-preview and re-approve.
* Transferred tickets leave their department's queue: department heads will see them disappear; communicate before the window. A source department whose settings forbid transfers out is not consulted (system routing, as in live intake).
* **Held-back tickets stay where they are, with whoever owns them**, and carry on in their own approval / AI flow; they are *not* picked up automatically afterwards (a second, separately approved batch is needed once an approval is decided or an AI conversation ends).
* Rarely a competing writer can make SQL Server choose the batch as a **deadlock victim** (Msg 1205): it rolls back completely and nothing changes — re-run.
* **People mid-work:** an agent who has the ticket open (still Open, not yet started) loses it at commit; their next save fails the application's row-version check (409) and the ticket is no longer theirs. Communicate before the window.
* **Rolling back after people have worked the new tickets** is deliberately partial: worked tickets are skipped and stay with the new owner (§6).
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
