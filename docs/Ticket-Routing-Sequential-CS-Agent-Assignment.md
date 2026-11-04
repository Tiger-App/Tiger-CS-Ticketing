# Ticket routing — one shared, sequential CS Agent rotation

## Root cause of `Facilities Management Queue (Unassigned)`

The department and the owner were **coupled**: every route that changed a ticket's department also *cleared its owner*, and then asked the **destination department** to find a new one.

| Path | Code | What went wrong |
|---|---|---|
| Transfer (CS Agent → Facilities) | `Ticket.TransferToDepartment` set `CurrentOwnerEmployeeId = null`; `TicketAssignmentAppService.TransferAsync` superseded the assignment | The agent lost the ticket the moment it moved |
| Re-assignment in the destination | `ApplyAsync` (rule) then `ApplyRoundRobinFallbackAsync` rotated **inside the destination department** | Facilities had no active, unlocked, role-eligible staff, **or** the request-type rule said *Department Queue*, **or** workflow settings disabled assignment ⇒ `NoEligibleEmployee` / queue |
| Request-type routing, category routing | `TicketRequestTypeRoutingService`, `TicketClassificationAppService` | same pattern |
| Reopen | `Ticket.Reopen` cleared the owner; `ApplyAsync` only | same; no rotation at all, so most reopens were unassigned |
| Failure | `QueueAsync` only wrote an audit line | Nothing flagged the ticket for staff; no retry; UI printed `"<Dept> Queue (Unassigned)"`, which read as a normal state |

The rotation itself (`DepartmentAssignmentRotation`, row-locked cursor) was sound; it was just keyed **per department**.

## What changed

**One rotation.** The pool is the Call Center's eligible CS Agents (active member, CS Agent role, not locked out, not a service identity — the existing eligibility rules). The persisted cursor is the existing `DepartmentAssignmentRotations` row of the Call Center department, so **no schema change** and the existing Call Center intake rotation continues seamlessly. Order is ascending employee id (`DepartmentAssignmentRotation.PickNext`), stable and deterministic; the cursor row is locked `UPDLOCK, HOLDLOCK` for the caller's transaction, so concurrent creations cannot take the same position.

**`TicketAutoAssignmentService`**
- `EnsureCsAgentAsync` — the single guarantee. Closed/Resolved → nothing. Already owned → nothing written (no duplicate rows/events). Else the shared rotation. No agent → failure recorded.
- `RestorePreviousCsAgentAsync` — reopen keeps the ticket's previous agent while still eligible.
- `ApplyRoundRobinFallbackAsync` now delegates to `EnsureCsAgentAsync` (this is what Transfer, category routing, request-type routing, creation without a Call Center, and reopen reach), so *"rule says Department Queue"* / *"assignment disabled"* can no longer strand an active ticket.
- Failure = audit `AutoAssign … Status=Unassigned` **plus** one `ManualAssignmentRequired` workflow event (marker `CsAgentAssignmentFailed`), deduplicated per ticket.

**Lifecycle**
- Transfer / request-type routing / category routing: `TransferToDepartment(…, preserveOwner: true)` — the agent is preserved unless the transfer names an assignee (`AssignToEmployeeId`). Call-Center-first system routing keeps the old release behaviour (it must clear a stale automatic pre-assignment). The transfer event's `ToEmployeeId` is set only when someone actually took the ticket.
- Reopen: previous agent if eligible → department rule → shared rotation → recorded failure. The reopen itself never fails because no agent exists.
- Retry: `POST /api/tickets/{id}/assignment/retry` (`Tickets.Assign` + CS Manager / administrator). Idempotent; 409 `no-eligible-cs-agent` when still nobody is available. The Call Center reconciler is **not** a retry mechanism for these tickets: it is disabled by default (`CallCenterReconciliation:Enabled=false`, `DryRun=true`), considers only Open tickets, and deliberately skips tickets already transferred out of the Call Center. Existing Unassigned tickets are handled by the backfill below.
- UI: the label for a ticket with no agent is `Unassigned — awaiting CS Agent`; it is never presented as a department queue.

**Deliberate interpretations (please confirm)**
1. *Live AI conversations are not given an agent* until a person is requested (handoff) — `UnassignedTicketClassifier` already protects them, and rule 1's exception ("resolved and closed by AI") would otherwise be meaningless. They are not counted as failures.
2. After a transfer the department's own staff can no longer *claim* the ticket from a queue, because it has an owner. The receiving department gets it via an explicit reassignment (transfer + `AssignToEmployeeId`, or Assign).
3. With **no active Call Center department configured** there is no pool, so assignment fails *loudly* (recorded) rather than falling back to department staff.

## Files changed

Backfill: `CsAgentBackfill.cs` (options, classifier, service), `CsAgentBackfillRunner.cs`, `AdminCsAgentBackfillController.cs`, `ITicketUnassignedWorkRepository.ListActiveUnownedAsync`, `IAssignmentRotationRepository.PeekLastAssignedAsync`, `Verify_TigerCS_CsAgentBackfill_Batch_ReadOnly.sql`. Domain: `Ticket.cs`. Application: `TicketAutoAssignmentService.cs`, `TicketAssignmentAppService.cs`, `TicketLifecycleAppService.cs`, `TicketCreationAppService.cs`, `TicketRequestTypeRoutingService.cs`, `TicketClassificationAppService.cs`, `TicketOperationsDto.cs`. Api: `TicketsController.cs`. Web: `TicketDisplay.cs`. Scripts/docs: `Diagnose_TigerCS_ActiveTicketsMissingCsAgent_ReadOnly.sql`, this file. Tests: new `SharedCsAgentRotationTests`, additions to `TicketLifecycleAppServiceTests`, `CallCenterAllChannelsRegressionTests`; existing tests that encoded "transfer clears the owner" / department-scoped rotation / the old label were updated to the new rules. Collections, Campaigns and unrelated modules are untouched.

## Database migration

**None.** No schema, index or seed change. (`ManualAssignmentRequired` and the cursor table already exist.)

## Backfill of existing tickets — implemented, NOT executed

**Not automatic.** The fix above does not touch tickets that are already Unassigned, and nothing schedules the backfill. It runs only when a System Administrator calls the admin endpoint, and a live run additionally needs `CsAgentBackfill:Enabled=true` (default `false`, also pinned `false` in `appsettings.Production.json`). The reconciler stays off. The existing `Execute_TigerCS_PROD_CallCenterBacklogDistribution.sql` is **not** used: it moves tickets into the Call Center department and covers Open tickets only.

**What it does.** For every *active* (Open / In Progress / Pending) ticket with no CS Agent it runs the same engine as new tickets (`EnsureCsAgentAsync`): the shared Call Center CS Agent pool, the same eligibility rules (including lock-out and service identities, which SQL cannot evaluate) and the same persisted, row-locked cursor — so it *continues* the rotation. Oldest ticket id first. It writes only the owner, one assignment row (`Trigger=Backfill`, system actor) and a `BackfillAssign` audit entry carrying the `BatchId`. Department, request type, priority, status, SLA and history are unchanged (there is a test asserting it).

| Excluded (never assigned) | Why |
|---|---|
| Closed, Resolved | not active |
| Already has an owner | never redistributed (a manual / Supervisor assignment and an existing eligible CS Agent are preserved); re-checked under the transaction |
| Pending approval | actively handled by the approval process |
| Live AI conversation (no human requested, no human reply) with recent activity — or with the stale rule off | stays AI-owned until a handoff, or until it is stale (see *Stale AI conversations*) |
| Younger than `MinTicketAgeMinutes` (3) | its own creation flow may still be assigning it |

**Included (final rules).** A ticket whose Genesys-confirmed handling agent is an eligible Call Center CS Agent is given to THAT agent in place (`Source=ConfirmedHandler`; the rotation cursor does not move). If that person is not an eligible CS Agent (unmapped, deactivated, locked out, service account, not a CS Agent) it falls to the shared rotation (`Source=RoundRobin`). An AI conversation that ended without resolving or closing the ticket is assigned (`IncludeAiEndedWithoutHuman` now defaults to `true`). Department, Request Type, Status, Priority, SLA and history are never changed and no ticket is moved into the Call Center. Active tickets owned by a non-CS-Agent are not "unassigned": they are reported by section 4b of the diagnostic for a business decision, not touched.

**Stale AI conversations (`CsAgentBackfill:StaleAiAfterHours`).** A "live" AI conversation whose end event never arrived stays protected for ever under the plain rule. With `StaleAiAfterHours` set, a ticket becomes eligible for a CS Agent when ALL of these hold: its Genesys conversation is still live (no `EndedAtUtc` / `InactivityClosedAtUtc`); there is **no confirmed human handling** (no HumanAgent transcript line, handler row, answering agent, first human response, or agent-connected/transferred event) and **no human request** (no follow-up flag, no open handoff); no approval is pending; and there has been **no genuine activity for MORE than the threshold** (exactly the threshold is not stale). "Activity" is the latest of ticket creation, interaction stamps (created / started / last customer interaction / awaiting-reply), **every transcript message** (customer, AI or agent) and Genesys workflow events — so a late customer message or Genesys event resets the clock; system touches (SLA ticks, notifications) do not.
- **Disabled by default.** `StaleAiAfterHours` is `null` in `appsettings.json` and pinned `null` in `appsettings.Production.json`; `null`, `0` or a negative value all mean *off* (every live AI conversation stays protected). **24 hours is the proposed value, for review only — it is not applied anywhere.** Setting it is a separate, approved configuration change.
- **Open Handoff is unchanged.** A ticket that asked for a person (open handoff or human follow-up) is human work and always gets a CS Agent, however recent the conversation and whether or not the stale rule is on.
- **Who gets it.** A confirmed, eligible Genesys handling agent first (`Source=ConfirmedHandler`, rotation unmoved); otherwise the existing shared sequential Round-Robin (`Source=RoundRobin`). Stale AI tickets have no handler by definition, so they are dealt by the rotation. Department, Status, SLA, Priority and ticket history are not touched; the audit entry gains `Reason=StaleAi;LastActivityUtc=…`.
- **Preview = Run.** Preview, dry run and live run call the same `CsAgentBackfillClassifier`; preview/dry-run rows carry `staleAi` and `lastActivityUtc`. A live run **re-checks eligibility inside the per-ticket transaction** with fresh evidence: a customer message, Genesys event, human pick-up or approval that arrived after the preview protects the ticket again and it is reported as `Skipped` (nothing written).
- **Choosing the threshold.** Run `Diagnose_TigerCS_PROD_StaleAiBackfill_12_24_48_ReadOnly.sql` (SELECT only, `READ UNCOMMITTED`, database-name guard): it shows eligible-now, tickets added by the rule at 12, 24 and 48 hours, per-department counts, the silence distribution and the ticket list. Review it with the business before any change.

**Endpoints** (System Administrator; `Administration.Configuration.View` / `Edit`)
- `GET /api/admin/cs-agent-backfill/preview?afterTicketId=0&limit=50` — read-only. Returns the pool size, the current cursor, eligible count, exclusion counts and reasons, eligible-per-department, and per ticket: number, status, current department, class and the **proposed CS Agent in the real rotation order**. Locks nothing, writes nothing, does not advance the cursor.
- `POST /api/admin/cs-agent-backfill/run` `{ "dryRun": true, "batchSize": 25, "afterTicketId": 0, "batchId": "bf-2026-11-xx" }` — **`dryRun` defaults to true.** One throttled batch (`batchSize` is clamped to `MaxBatchSize`=200; `DelayBetweenTicketsMs`=250 after each live assignment). The result gives `resumeAfterTicketId`, `backlogExhausted`, counts and per-ticket outcomes.

**Safety.** One transaction and one DbContext per ticket; a failure or lost race affects only that ticket. Concurrent writers: the cursor row lock + ticket row version + unique current-assignment index (a lost race is reported as `LostRace`, never double-assigned). A single-flight SQL application lock (shared with the reconciler) prevents overlapping runs (`409 busy`). A live run with an empty agent pool is refused before touching anything (`409 no-eligible-cs-agent`); if the pool empties mid-run it stops at that ticket (failure recorded once), and the resume point stays *before* it.

**Recovery.** Idempotent and resumable: pass the returned `resumeAfterTicketId` to continue after an interruption, or simply re-run from 0 — assigned tickets are no longer candidates. There is no automatic rollback; every change is traceable by `BatchId` (`Verify_TigerCS_CsAgentBackfill_Batch_ReadOnly.sql`) and a wrong assignment is corrected with the normal Assign action.

**Execution instructions (for review — do not run before approval)**
1. Run `Diagnose_TigerCS_ActiveTicketsMissingCsAgent_ReadOnly.sql` (read-only) and note the counts.
2. `GET …/preview` repeatedly (`afterTicketId` = last ticket id of the previous page) and review the proposals (`proposedSource` = ConfirmedHandler / RoundRobin) and exclusions with the business.
3. `POST …/run` with `dryRun:true` for a few batches to confirm the numbers match the preview. To review the stale rule, run `Diagnose_TigerCS_PROD_StaleAiBackfill_12_24_48_ReadOnly.sql` first; the threshold stays `null` unless the business approves a value (24 proposed) as a separate configuration change.
4. After written approval: set `CsAgentBackfill:Enabled=true`, then `POST …/run` with `{"dryRun":false,"batchSize":25,"batchId":"<id>"}` for ONE batch; run `Verify_TigerCS_CsAgentBackfill_Batch_ReadOnly.sql` with that `BatchId`; continue batch by batch using `resumeAfterTicketId` until `backlogExhausted`.
5. Set `CsAgentBackfill:Enabled=false` again and re-run the diagnostic: `BACKFILL_CANDIDATE` must be 0 and section 5 must return no rows. Only `AI_IN_PROGRESS` and `PENDING_APPROVAL` tickets may remain: they are the intentional exclusions.

## Final fix for remaining Unassigned tickets (second pass)

New-ticket gaps closed (each has a regression test):
- **Handoff / human follow-up on a ticket already in the Call Center.** No routing happens (already there), and the assignment lived inside the routing branch, so the ticket stayed unowned. `GenesysAgentHandoffAppService` and `GenesysTicketUpdateAppService` now ensure a CS Agent (confirmed answering agent first, otherwise the shared rotation) even when routing is declined.
- **Unusable answering agent** (unmapped, deactivated, locked out, service account, not a CS Agent / Call Center member). `ApplyAnsweringAgentAsync` used to flag and leave the ticket unassigned ("never a substitute person"). It now flags AND deals the next shared-rotation CS Agent; an existing eligible CS Agent is preserved and the problem is still flagged. New outcome `AssignedByRoundRobinFallback`.
- Backfill: confirmed handler honoured, AI-ended tickets included, `HumanHandledNeedsManualReview` retired.

Unchanged and verified by existing tests: creation (all channels), manual creation, category / request-type routing, department transfer (owner preserved), reopen (previous agent, else rotation).

## Deployment and verification

1. Deploy the build (no migration step). 2. Confirm the Call Center has ≥1 active, unlocked CS Agent member. 3. Create tickets on several channels and from different departments; confirm owners go A, B, C, … in a fixed order. 4. Transfer one to Facilities Management: the department changes, the agent does not. 5. Reopen a closed ticket: it has an agent. 6. Temporarily lock out all agents, create a ticket: it shows `Unassigned — awaiting CS Agent`, a *Manual Assignment Required* event exists once; unlock, call `…/assignment/retry`: assigned. 7. Run the diagnostic.

## Verification status

See the PR description for the exact test run. SQL Server–gated tests (`*SqlServerTests`, including the parallel-cursor tests that exercise the real `UPDLOCK` row lock) are **skipped** unless `TIGERCS_TEST_SQL_SERVER` is set; the diagnostic SQL file was not executed against a database from this environment.
