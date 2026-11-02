# Call Center first — Production diagnosis and one-time redistribution of ALL Open tickets (owned or not) to the Call Center

**Database:** `TigerCS_Ticketing_Prod` · **Status: NOT EXECUTED on Production, and nothing has been deployed.** The Preview / Execute / Rollback scripts **were validated on a real SQL Server 2022 container** (§11) — not on your Production server.
Every script defaults to a mode that changes nothing; the execution script cannot commit without the approval gates in §5.

**Explicitly approved scope (one time) — this supersedes the narrower scope of PR #143/#144 (unowned Open / InProgress / Pending only):**
1. **Every ticket with Status = Open**, from **every** department (Accounting, Leasing, Collections, Customer Service, …, inactive departments included), whether it is **unowned or currently owned by another employee**, is transferred to Call Center (`CC`) and dealt to **exactly one** eligible CC employee by the **existing Round Robin** (no workload balancing).
2. The previous owner is **replaced, never erased**: the previous current `TicketAssignments` row stays as superseded history (`IsCurrent = 0`, exactly what a normal reassignment does), and the previous owner / assignment id / department are written to the audit trail and the workflow event.
3. **Never touched:** InProgress, PendingCustomer, PendingThirdParty, Resolved, Closed (the schema has no "Cancelled"); tickets with a **pending approval** (`TicketApprovals.Status = 1`) and tickets with an **active AI conversation** — **held back**, owned or not, listed separately by the Preview, their counts are commit gates.
4. Preserved: `OriginatingDepartmentId`, request type, category, priority, status, SLA, escalation, audit trail, workflow history. No application code, configuration or workflow changes — future tickets behave as today.

| File | Purpose | Writes? |
|---|---|---|
| `Diagnose_TigerCS_PROD_CallCenterFirst_ReadOnly.sql` | **Step 0.** Is Call Center-first live? How was `TG-ACC-20261102-0021` routed? Which new tickets skipped the Call Center and why? | **No** (SELECT only) |
| `Preview_TigerCS_PROD_CallCenterBacklogDistribution_ReadOnly.sql` | Dry run: total Open, **owned vs unowned**, eligible per original department, **held-back tickets**, **who loses tickets**, agents (with the reason each is in/out, incl. permission denials), expected distribution per agent, exact ticket → previous owner → agent plan, the **six** approval numbers (§3a) | **No** (SELECT + session temp tables) |
| `Execute_TigerCS_PROD_CallCenterBacklogDistribution.sql` | Moves the Open backlog to the Call Center, replaces owners, Round Robin deals it. `@DryRun = 1` by default | Only with `@DryRun = 0` **and** all gates |
| `Rollback_TigerCS_PROD_CallCenterBacklogDistribution.sql` | Undoes only this batch: restores the **previous owner, previous department and previous current assignment row**. `@DryRun = 1` by default | Only with `@DryRun = 0` |
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

## 2. What the procedure does (requirement → enforcement)

| # | Requirement | Enforcement |
|---|---|---|
| 1 | **All Open tickets, all departments, owned or not** | `TicketStatus = 1` only; any owner situation (none / active employee / deactivated / locked / service account / other department / owner without assignment row / no owner but a stale current row); any `CurrentDepartmentId`, inactive departments included |
| 2 | Exclude Closed, Resolved and every other non-Open status | `TicketStatus <> 1` is never selected; the Preview lists the non-Open counts as "never touched"; the UPDATE re-checks `TicketStatus = 1` per row |
| 3 | **Exclude active AI conversations** | held back by default (§2.1), owned or not, listed by the Preview; interaction / handoff tables never written; count is a commit gate |
| 4 | **Exclude pending approvals** | `NOT EXISTS (TicketApprovals … Status = 1)` in Preview and Execute; each held-back ticket is listed (type, since-date, current owner); count is a commit gate; approved / rejected / cancelled approvals do **not** block |
| 5 | Round Robin over active, unlocked, eligible CS Agents in CC | Same pool rules, persisted cursor, row lock and order as `AssignRoundRobinAsync` (§2.1 for the two deliberate additions); dealt oldest-first so per-agent counts differ by ≤ 1 (validated; aborts otherwise) |
| 6 | Exclude deactivated employees, service accounts, read-only users | not deactivated, not locked out, **not in `@ServiceIdentity`**, holds **CS Agent**, member of CC (read-only / supervisory / admin roles never qualify). Re-evaluated **fresh after the writes**; the agents' `Employees` rows are locked for the run |
| 7 | **Exactly ONE owner per ticket** | `Tickets.CurrentOwnerEmployeeId` + filtered unique index `TicketAssignments(TicketId) WHERE IsCurrent = 1`; the previous current row is retired first, then one new current row; post-write validation re-proves one owner = one current assignment per ticket |
| 8 | **Update `CurrentDepartmentId` to CC and replace the assignment atomically** | One `UPDATE` sets department and owner together (guarded by status, previous owner, previous department and `RowVersion`), then retire + insert of the assignment rows — all in **one transaction**, all-or-nothing |
| 9 | Preserve originating department, request type, category, priority, status, SLA, audit, workflow history | in-transaction before/after comparison of every preserved field (aborts on any difference); every pre-existing assignment row must still exist unchanged except its `IsCurrent` flag; audit / events are append-only; status history, SLA instances/pauses, interactions, notes, approvals, notifications are never written |
| 10 | **Record every department transfer and reassignment** | per ticket: `AutoAssign` audit (`BeforeValue = PreviousOwnerId / PreviousAssignmentId / PreviousDepartmentId`); `Transfer` audit when the department changes; `TicketTransferred / SystemRouting` workflow event when the department changes **or** the ticket had an owner (note names the previous owner); one `BacklogDistributionBatch` audit row per run |
| 11 | Every assigned ticket appears in the employee's **My Tickets** | My Tickets is driven by `CurrentOwnerEmployeeId` (any department, any status); verified per ticket in the suite (§8) |
| 12 | The assignee can **Resolve and Close** (subject to closure validations and explicit denials) | The assignee may always Resolve/Close **unless Tickets.Resolve / Tickets.Close is denied** — so agents with an explicit DENY of `Tickets.View` / `Resolve` / `Close` are kept **out of the pool** (§2.1). Status is not changed, so the existing lifecycle rules still apply (§8) |
| 13 | **No duplicate owners, no concurrent reassignment conflicts** | `sp_getapplock`, CC cursor `UPDLOCK/ROWLOCK` (live intake queues behind it), candidate rows and agent rows `UPDLOCK/ROWLOCK`, per-row `RowVersion` guard, "superseded rows are exactly the rows read at selection" check (Msg 50317), post-write re-checks (Msg 50340 / 50341 / 50342), 20 s lock timeout. Full race table in §11 |
| 14 | Dry run / approvals | Preview (read-only) + `@DryRun = 1`; commit needs the **six** approved counts (tickets, transfers, **owners replaced**, agents, held-back approvals, held-back AI) + `@ServiceIdsConfirmed = 1` (or `@NoServiceAccountsExist = 1`), every listed service id a real employee |
| 15 | Rollback | Rollback script keyed by the batch `CorrelationId`; restores previous owner, department **and previous current assignment row**; skips anything touched since (§6) |

### 2.1 Decisions baked into the defaults (change only with approval)
* **`@ReassignTicketsAlreadyOwnedByEligibleCcAgent = 1` (literal rule) — DECISION FOR YOU.** The approved text says *all* Open tickets, *including tickets currently assigned to another employee*, so an Open ticket **already in CC and owned by an eligible CC agent** is dealt again too. This includes tickets that Genesys just gave to the agent who answered the call (PR #148 "the answering agent owns the ticket"): they can move to a different agent. The Preview counts them (`KeptInPlace_OwnedByEligibleCcAgent` shows what `= 0` would leave alone, and the "Who loses tickets" table shows how many each CC agent loses). Setting it to `0` in **both** scripts leaves those tickets with their agent; the count gates then differ, so Preview and Execute cannot disagree silently.
* **`@ExcludeAgentsWithTicketDenials = 1` — requirement 12.** The live Round Robin ignores per-employee permission overrides. A CC agent with an explicit **DENY** of `Tickets.View`, `Tickets.Resolve` or `Tickets.Close` would be dealt tickets they cannot see, resolve or close (and a denial is never overridden). They are therefore excluded from the pool and listed by the Preview (`DeniedPermissions`). If `dbo.UserPermissionOverrides` does not exist yet (migration `Deploy_TigerCS_PROD_AddUserPermissionOverrides.sql` not applied) nobody has a denial. Set `0` in both scripts to follow the engine exactly. Overrides are read once at the start of the run (not locked).
* **`@CsAgentRoleOnly = 1`.** The live pool also admits *Department Employee* / *Department Head* members of CC, but only **CS Agent / Supervisor / Manager** hold the `Transfer` role (`TicketRoleSets.Transfer`); a Department Employee could Resolve and Close an owned ticket but **not Transfer** it, which breaks the Call Center workflow. The Preview lists any such member as excluded.
* **Pending approvals — always held back** (no switch). A ticket waiting on an approval cycle keeps its approval and its current owner/department. Once decided it needs a second, separately approved batch.
* **`@IncludeLiveAiConversations = 0`.** An Open ticket with an **active AI conversation** is held back. *Active AI conversation* means: a Genesys-sourced interaction that is **not ended, not closed for inactivity, needs no human follow-up and was not taken by a person** (`Source = 2 AND EndedAtUtc IS NULL AND InactivityClosedAtUtc IS NULL AND RequiresHumanFollowUp = 0 AND HandledByUserId IS NULL`), **and** no interaction on the ticket is flagged `RequiresHumanFollowUp`, **and** no open human handoff (`TicketAgentHandoffs.Status` Waiting / Assigned / InProgress). This is an interpretation of the schema — **confirm the Preview's held-back list matches your understanding before approving.**
* **System actor.** The new assignment row has `AssigningActorEmployeeId = NULL`, like every automatic assignment. The app (`TicketOwnershipRules.IsAutomaticPreAssignmentAsync`) treats an Open, unanswered ticket whose current row has no actor as an *automatic pre-assignment*: if Genesys later reports that the customer wants a person, such a ticket may be released to the **Call Center queue** (it stays in CC). A human ownership claim by the agent (status change, reply) ends that.
* **Status is preserved** (an Open ticket stays Open). **No notifications** are emitted. Round Robin deals evenly from the cursor; it does not level existing workloads (the Preview shows `ActiveOwnedBefore/After`).
* **Department workflow settings** (`AllowTransferToOtherDepartments`, `AllowInternalReassignment`) are not consulted — this is an approved administrative bulk action, like system routing; the Preview lists departments whose settings would normally forbid it. CC itself must allow assignment (the script aborts otherwise).

## 3. Approved procedure (gated — do not skip or reorder)

| Step | Who | Action | Gate |
|---|---|---|---|
| 0 | DBA, read-only login | Run **Diagnose**. Record the Verdict and result sets 4–10 for `TG-ACC-20261102-0021` | Cause identified (A/B/C/D) |
| 1 | DBA + release owner | If cause A/B: fix it first — apply both migration scripts (§1.2 items 1–2), set service-account env vars (item 4), publish & deploy **Api and Web** per `docs/Production-Deployment.md`, fix the `CC` department. Verify with **Diagnose** again + `Verify_TigerCS_CallCenter_Agents_ReadOnly.sql` (`READY`) | Verdict `LIVE`; one new test ticket routes CC-first and lands in an agent's My Tickets |
| 2 | You | Run the **Preview** exactly as in §3a. Return all result sets | Verdict `READY` |
| 3 | You + release owner | Review: agents (incl. denials), per-agent spread ≤ 1, eligible tickets per original department **owned vs unowned**, **who loses tickets** (incl. service accounts, deactivated people, other departments' employees), the held-back list, `KeptInPlace` decision (§2.1), anomalies (data drift, legacy owners, duplicate current rows must be 0) | **Written approval of the SIX counts** and of the §2.1 decisions |
| 4 | DBA | Full backup / snapshot of `TigerCS_Ticketing_Prod`; record its name | Backup verified |
| 5 | DBA | On UAT or a **restore of the backup under the name `TigerCS_Ticketing_Prod` on a non-production server**: run **Execute** with defaults (`@DryRun = 1`), then a real run (`@DryRun = 0`, gates filled), verify (§7, §8), then **Rollback** (dry run, then real) and re-verify that every owned ticket has its previous owner back. All validations must pass | Clean on the copy |
| 6 | DBA, approved low-traffic window | Run **Execute** on Prod, still `@DryRun = 1`; compare counts with step 2 | Matches the approved numbers |
| 7 | DBA, on explicit go | Fill `@ServiceIdentity`, `@ServiceIdsConfirmed = 1`, `@ApprovedTicketCount`, `@ApprovedTransferCount`, `@ApprovedReassignedCount`, `@ApprovedAgentCount`, `@ApprovedHeldBackPendingApprovalCount`, `@ApprovedHeldBackActiveAiCount` from the approved preview, `@DryRun = 0`; leave `@CsAgentRoleOnly`, `@IncludeLiveAiConversations`, `@ReassignTicketsAlreadyOwnedByEligibleCcAgent`, `@ExcludeAgentsWithTicketDenials` equal to the Preview's. Run as **one batch in SSMS** (or `sqlcmd -I` — filtered indexes need `QUOTED_IDENTIFIER ON`). **Save the printed `BatchId`** | Counts must equal the live computation or it aborts |
| 8 | You | Post-checks (§7) and the receiving-employee checks (§8) | Sign-off |

Locking note: the candidate tickets, the Call Center agents' `Employees` rows and the CC cursor are locked until commit — ≈ 0.7 ms per ticket measured on the test container. Live writers to those rows (and new Call Center intake) wait; the 20 s lock timeout makes the script fail with nothing changed instead of waiting. Run in a quiet window. **Tell the owners of the affected tickets first:** tickets disappear from their lists (§10).

## 3a. Preview — exact instructions (read-only, safe to run any time)

1. Open `Preview_TigerCS_PROD_CallCenterBacklogDistribution_ReadOnly.sql` in SSMS, connected to `TigerCS_Ticketing_Prod` with a **read-only** login (`db_datareader` is enough — tested). It aborts on any other database name.
2. **Service accounts** (SQL cannot know them): take every id from `appsettings` — `Authorization:ServiceIdentity:EmployeeIds` **and** `Collections:Authorization:IntegrationEmployeeIds` (Production: API-pool environment variables) — and add one line each below the marker:
   ```sql
   INSERT INTO @ServiceIdentity VALUES ('xxxxxxxx-xxxx-xxxx-xxxx-xxxxxxxxxxxx');
   ```
   then set `DECLARE @ServiceIdsConfirmed bit = 1;`. Only if you have verified that **no** service / integration account exists, leave the list empty and set `@NoServiceAccountsExist = 1` instead. Until then the verdict is `BLOCKER`; a mistyped id is also a `BLOCKER` (`ServiceIdsNotFound` lists it).
3. Leave the four switches at their approved defaults (`@CsAgentRoleOnly = 1`, `@IncludeLiveAiConversations = 0`, `@ReassignTicketsAlreadyOwnedByEligibleCcAgent = 1`, `@ExcludeAgentsWithTicketDenials = 1`). If anyone changes one, the **same** value must be used in Execute.
4. Execute (F5) **as one batch**. Nothing is written.
5. `Verdict` (first result set) must be **`READY`**. Then read — and send **every result set** (ticket numbers and employee names only, no customer data):

   | Result set | What to check |
   |---|---|
   | 1 Verdict | `TotalOpenTickets`, `OpenCurrentlyOwned`, `OpenCurrentlyUnowned`, `EligibleTickets_Total`, `TicketsMovedIntoCC`, `EligibleCurrentlyOwned_OwnerReplaced`, `HeldBack_*`, `KeptInPlace_OwnedByEligibleCcAgent`, `EligibleAgents` |
   | 2 Eligible by **original department**, unowned vs owned | The move being approved; inactive departments included |
   | 2b **HELD BACK** | Counts per department (and how many are owned), then **every held-back ticket** with its reason (`PENDING APPROVAL` / `ACTIVE AI CONVERSATION`) and its current owner, who keeps it |
   | 3 Exclusions | Non-Open statuses by count (never touched); data-drift / legacy-owner rows (included, reported); **`TicketsWithMoreThanOneCurrentAssignment_MustBe0` must be 0**; departments whose settings would normally forbid the move |
   | 3b **Who loses tickets** | Every current owner of an eligible ticket, with kind (CC agent in the pool / other department / deactivated / locked / **SERVICE ACCOUNT** / …) and the number of Open tickets taken away |
   | 4 Agents | Every CC member: `ELIGIBLE` or the reason excluded (deactivated / locked / service account / **explicit permission denial** / `@CsAgentRoleOnly` / no operational role), `DeniedPermissions`; plus the "possible service account" list |
   | 5 **Expected distribution** | Tickets per agent (spread ≤ 1); from other departments vs already in CC; **taken from another owner vs from the queue**; load before → after; department × agent matrix |
   | 6 **Approval values** | The **six numbers** Execute demands: `ApprovedTicketCount`, `ApprovedTransferCount`, `ApprovedReassignedCount`, `ApprovedAgentCount`, `ApprovedHeldBackPendingApprovalCount`, `ApprovedHeldBackActiveAiCount` |
   | 7 Exact plan | Every ticket → original department → previous owner → receiving agent, oldest first |
6. Re-run as often as you like; it never changes anything. The numbers move only as the live backlog moves.

## 4. Service-account exclusion list
Service accounts are **not in SQL**; the application builds the list from two settings (`ServiceIdentityRegistry`): `Authorization:ServiceIdentity:EmployeeIds` and `Collections:Authorization:IntegrationEmployeeIds`
(Production: API-pool environment variables `Authorization__ServiceIdentity__EmployeeIds__0..n`, `Collections__Authorization__IntegrationEmployeeIds__0..n`; the committed value is an empty list).
Both scripts take the same ids in `@ServiceIdentity`; Preview/Execute **refuse to commit** while `@ServiceIdsConfirmed = 0`, and Execute re-validates that no ticket went to a listed id.
The preview also prints a name-based heuristic (genesys / integration / service / bot / api) of CC members for manual review. **The ids are not available to this session — see Blockers.** A service account that currently *owns* Open tickets is shown in "Who loses tickets" as `SERVICE ACCOUNT`; those tickets are replaced like any other.

## 5. Execution gates (all enforced by `Execute`)
`DB_NAME() = TigerCS_Ticketing_Prod` · schema columns / indexes present (incl. the unique filtered index and the hold-back source tables) · exactly one active `CC` · assignment enabled for CC · no earlier `BacklogDistributionBatch` (checked after the app lock) ·
`@ServiceIdsConfirmed = 1`, list non-empty **or** `@NoServiceAccountsExist = 1`, every listed id a real employee · **six approved counts** (tickets, transfers, owners replaced, agents, held-back approvals, held-back AI) equal the live computation · app lock held ·
every candidate row still Open / same owner / same department / same `RowVersion` · superseded assignment rows = exactly the rows read (Msg 50317) · **post-write re-checks** (no planned ticket gained a pending approval — Msg 50340 — or an active AI conversation — Msg 50341; every new owner still an eligible agent — Msg 50342) ·
post-write validations (one owner = one current assignment; all in CC and still Open; every preserved field unchanged; all pre-existing assignment rows kept; previous owner recorded in every audit row; no deactivated / service owner; spread ≤ 1; audit / assignment / event counts exact).

## 6. Rollback strategy
*Before commit:* one transaction — any error or mismatch rolls everything back; `@DryRun = 1` always does.
*After commit:* `Rollback_…sql` with the saved `BatchId` (also the `CorrelationId` of the batch's audit rows).
* Working set = exactly the batch's `AutoAssign` audit rows (count cross-checked with the batch row: tickets, moved, owners replaced); each carries the **previous owner, previous current assignment id and previous department**. No other ticket can be touched.
* A ticket is rolled back only if it is **still as the batch left it**: same owner, still in Call Center, **still Open**, batch row still the single current assignment, no later assignment, the previous owner's assignment row still retired, nobody worked it (no status change, note, first response, Genesys handler record, new interaction, new approval request or other workflow event after the batch). Otherwise it is **skipped and reported, left byte-for-byte as found** — the new owner's work is never taken away (`@IncludeWorked = 1` relaxes only the "worked" test).
* Per rolled-back ticket: owner → **previous owner** (NULL if it had none), department → **previous department**, batch assignment row → `IsCurrent = 0`, **previous current row → `IsCurrent = 1` again**, + `AutoAssignRollback` / `Transfer` audit rows and a `TicketTransferred` event going back (receiving employee = previous owner). Nothing is deleted. Cursor restored only if no live ticket was dealt since.
* Caveat: a ticket the new owner has already started (status change) or touched is skipped — it stays with the new owner; the previous owner is **not** restored over live work.
* Last resort for a catastrophic problem is the step-4 backup.

## 7. Post-execution verification (read-only)
```sql
-- every batch ticket: now in CC, exactly one owner, exactly one current assignment, same person, still Open
SELECT COUNT(*) AS BatchTickets,
       SUM(CASE WHEN t.CurrentOwnerEmployeeId = a.AssignedEmployeeId AND t.CurrentDepartmentId = a.AssignedDepartmentId AND t.TicketStatus = 1 THEN 1 ELSE 0 END) AS OwnerDeptStatusMatch
FROM dbo.AuditEntries ae
JOIN dbo.Tickets t ON t.TicketId = TRY_CONVERT(bigint, ae.EntityId)
JOIN dbo.TicketAssignments a ON a.TicketId = t.TicketId AND a.IsCurrent = 1
WHERE ae.CorrelationId = '<BatchId>' AND ae.Action = N'AutoAssign';

-- must return 0 rows
SELECT TicketId FROM dbo.TicketAssignments WHERE IsCurrent = 1 GROUP BY TicketId HAVING COUNT(*) > 1;
-- the only Open tickets NOT in Call Center / not owned by a CC agent must be the held-back ones (pending approval / active AI) and tickets created after the run
SELECT t.TicketNumber, d.Code, t.CurrentOwnerEmployeeId FROM dbo.Tickets t JOIN dbo.Departments d ON d.DepartmentId = t.CurrentDepartmentId
WHERE t.TicketStatus = 1 AND (d.Code <> N'CC' OR t.CurrentOwnerEmployeeId IS NULL);
-- previous owners are kept in the audit trail
SELECT TOP 20 EntityId, BeforeValue, AfterValue FROM dbo.AuditEntries WHERE CorrelationId = '<BatchId>' AND Action = N'AutoAssign' AND AfterValue LIKE '%Reassigned=1';
```

## 8. Receiving-employee verification (requirements 11 and 12)
| Capability | Basis in code | Coverage |
|---|---|---|
| Appears in **My Tickets** | driven by `CurrentOwnerEmployeeId` (any department, any status) — `TicketRepository` / `DashboardQueryRepository` | the suite proves per agent that the own-tickets list equals the tickets dealt to them; app tests `MyTicketsVisibilityTests`, `DashboardOverviewTests.MyTickets_…` (C# not re-run here) |
| **Resolve** and **Close** | the current assignee may always do both (`IsCurrentOwner`), unless read-only **or an explicit denial of `Tickets.Resolve` / `Tickets.Close`** (`TicketLifecycleAppService.ResolveAsync`) | the pool excludes agents with a denial of View / Resolve / Close (suite T16). `TicketLifecycleAppServiceTests.Assignee_InAnyDepartmentAndRole_CanResolveAndClose` |
| **Existing closure validations still apply** | `Ticket.Resolve` accepts only InProgress or PendingCustomer; Close needs Resolved | **An Open ticket must first be started (Open → InProgress) by its new owner before it can be resolved.** The batch deliberately does not change status |
| **Transfer** | requires a `Transfer` role (CS Agent / Supervisor / Manager) **and** to be the current owner; target must match the request type once classified | `CallCenterFirstRoutingTests…` |
| **Previous owner loses rights** | `TicketOwnershipRules.WasPreviousOwnerAsync` — superseded assignment rows remove Resolve / Close / Transfer | why the old rows are kept and the new owner's row is current |

Live check after execution (UI, as one real receiving CS Agent): My Tickets lists the batch tickets; open one → start it → Resolve; open another → Close; open a third → Transfer. Use test/low-risk tickets agreed in advance. Also check as one **previous owner**: the tickets are gone from their My Tickets.

## 9. Blockers before execution
1. **Service-account ids** (both settings) are not available → the scripts refuse to commit; also decides whether the Genesys integration account is a CC member.
2. **Production state is unverified** — nothing here could connect to Production. Step 0 (Diagnose) must be run and, if the Call Center-first build/schema/`CC` department is missing, step 1 completed first. `dbo.UserPermissionOverrides` should exist (denial check).
3. **No real counts** — the Preview must be run by someone with Production read access; the approval document is its output.
4. **Validated on a SQL Server 2022 test container, not on Production** (§11). Production's version / compatibility level (needs `STRING_AGG`, level ≥ 130), volume, indexes and real ticket shapes are untested; the first Production-like run must be the restored-copy run (step 5). Treat any error there as a defect to fix before Production.
5. **Decisions:** (a) `@ReassignTicketsAlreadyOwnedByEligibleCcAgent` (§2.1), (b) `@ExcludeAgentsWithTicketDenials`, (c) `@CsAgentRoleOnly`, (d) the held-back definitions — confirm against the Preview's list, (e) Genesys unflagged-ticket policy (§1.3), (f) backup/window owner, (g) communication to the employees who lose tickets.
6. **The SQL has an automated suite (§11); the C# test suite was not run** (no .NET SDK in the authoring environment) and C# is unchanged.

## 10. Residual risks
* Backlog drift between preview and execution — execution aborts unless counts equal the approved numbers; re-preview and re-approve.
* **People lose work they own.** FIN / ACC / LEG / … employees will see Open tickets they own disappear, **including tickets they have already answered** (the Preview counts `AlreadyAnsweredByAPerson_StillOpen`). The new owner gets no notification and no context beyond the ticket itself. Communicate before the window.
* Tickets that Genesys just assigned to the answering agent (PR #148) can move to another agent unless the keep-in-place switch is used (§2.1).
* The new assignment is an *automatic* one (§2.1, "System actor").
* Workflow settings of source departments are not consulted (§2.1).
* **Held-back tickets stay where they are**, with their current owner, and carry on in their own approval / AI flow; they are *not* picked up automatically afterwards (a second, separately approved batch is needed).
* Rarely a competing writer can make SQL Server choose the batch as a **deadlock victim** (Msg 1205): it rolls back completely and nothing changes — re-run.
* Service-account detection depends on the ids supplied. Permission denials are read once at the start.

## 11. Validation on SQL Server (what is proven, and what is not)
The three scripts were run against a **real SQL Server 2022 (Developer, Linux container)**, schema built from this repository's own migration scripts (incl. `UserPermissionOverrides`). **220 checks, 220 passed** (`tests/backlog-distribution/setup_test_db.sh`, then `run_tests.py`). The expected deal, the owner/assignment history and the hold-back rules are recomputed by an **independent Python oracle** from raw rows, not by the SQL under test. The suite first failed on 9 checks; all were test-harness errors (a header-parsing quirk, a NULL lockout comparison, an over-strict digest, a miscounted fixture total), fixed in the tests, not in the scripts.

| Proven | |
|---|---|
| Preview | read-only (table digests unchanged); runs under a plain `db_datareader` login; total Open / owned / unowned, owner-replaced count, per-owner "who loses tickets" and per-agent distribution = oracle; plan = oracle for all 99 tickets incl. previous owner; held-back (owned and unowned) listed separately; denied agents shown excluded; blocks on an unconfirmed / empty / mistyped service-account list |
| Dry run / gates | `@DryRun = 1` leaves every table byte-identical; each missing or wrong approval (**six** counts) and a wrong database name refuse and change nothing |
| Scope | only Open tickets move: InProgress / Pending / legacy / Resolved / Closed (owned and unowned, even with a pending approval) stay byte-identical; owned tickets of every kind are replaced: active employee, other department, deactivated, locked, service account, supervisor, owner without assignment row, no owner but stale current row, answered-but-Open |
| History | every pre-existing assignment row still exists unchanged except `IsCurrent`; the previous current row is retired; exactly one new current row; `AutoAssign` audit carries previous owner / assignment / department; `Transfer` audit for department changes; `TicketTransferred` event for moves **and** reassignments naming the previous owner; originating dept / request type / category / priority / status / SLA / first-response preserved |
| Held back | pending approvals and live AI conversations (owned or not) byte-identical with their approvals / interactions / handoffs / assignment rows; decided approvals and ended / flagged / handed-off / taken / inactive AI do not block; changes between preview and run change the counts and refuse the run |
| Pool | CS Agents only by default; deactivated / locked / service / read-only / supervisory accounts and an agent with an explicit **DENY** of View / Resolve / Close never receive tickets; a Grant is not a denial; works when `UserPermissionOverrides` does not exist; each switch must match between Preview and Execute |
| My Tickets / Resolve / Close | every dealt ticket is owned by (listed under) a CC member who is active, unlocked, CS Agent, undenied; status stays Open; previous owners hold only retired rows |
| Rollback | untouched batch → the exact pre-batch `Tickets` table **and** assignment table (previous owners and previous current rows back); tickets reassigned / worked / started / transferred / newly approved / new interaction are skipped byte-for-byte; history only grows; second rollback refused |
| Scale | 16,099 eligible (16,087 moved, 5,357 owner-replaced) + 100,000 closed: preview 0.6 s, dry run 10 s, commit ≈ 10 s, rollback ≈ 3 s; signatures equal the pre-batch ones after rollback |

**Concurrent-assignment safeguards** (each has a test):

| Race | Outcome |
|---|---|
| Second run while one is running | Fails immediately on the application lock; nothing written |
| A person reassigns a candidate **owned** ticket mid-run (app-style `RowVersion` update) | Blocks until commit, then updates 0 rows |
| A racing unguarded writer on an unowned ticket | Blocks, then updates 0 rows |
| A competing assignment row appears between selection and the write | Superseded-rows check aborts the whole run (Msg 50317); the person's row is not silently retired |
| A duplicate *current* assignment | Rejected by the unique filtered index |
| Pending approval / AI conversation created on a planned (owned) ticket **before** the update | Post-write re-check aborts the whole run (Msg 50340 / 50341); nothing else changed |
| Same, **after** the update | The competing insert waits / loses the deadlock; no batch ticket ends up with a pre-commit pending approval |
| A new owner is locked out mid-run | Re-check aborts the whole run (Msg 50342) |
| An agent is deactivated mid-run | Blocked until the batch commits |
| Live Call Center intake (same cursor lock as the app) | Waits for the batch, then reads the advanced cursor |
| Backlog changes between preview and run (a ticket taken, started, approved, AI ended) | Counts differ → abort; re-preview and re-approve |

**Mutation checks** (a safeguard removed, a test must fail — all caught): Open-only filter widened; denial filter removed; rollback not re-activating the previous row; rollback restoring no owner; superseded-rows check removed.

**Not proven:** your Production server (version / compatibility level, volume, indexes), real Genesys traffic, tickets carrying a `RequestTypeId` (compared NULL-safe, none seeded), the C# application (no .NET SDK here; C# unchanged), and whether the "active AI conversation" interpretation (§2.1) matches your data — the Preview's held-back list is where you confirm it.
