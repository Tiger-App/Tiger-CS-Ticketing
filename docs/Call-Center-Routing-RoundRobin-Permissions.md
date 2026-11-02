# Incident TG-LCS-20261102-0015 — new tickets still landing in Leasing/Accounting (root cause and fix)

**Symptom:** Open, `Leasing Customer Services Queue (Unassigned)` instead of Call Center + one agent.

**Root cause (code, now fixed).** Genesys creates the ticket first and reports `humanFollowUpRequired: true` *afterwards* on the ticket-update
call (the AI only learns it is a leasing request / complaint mid-conversation). `GenesysTicketUpdateAppService.MarkHumanFollowUpAsync` recorded the
flag and stopped chatbot closure, but never routed the ticket, and ticket creation only enters Call Center when the flag is already `true` in the
*create* payload. Result: a human-needed ticket stayed unassigned in its originating department forever. Fix: the update now calls
`TicketAutoAssignmentService.RouteExistingTicketToCallCenterAsync` (Call Center + one Round Robin agent; `OriginatingDepartmentId`, Request Type,
status, SLA untouched; human-owned, closed/resolved and already-CC tickets skipped; idempotent). AI-only conversations that never ask for a person
are not moved.

**Second cause (deployment, must be checked on the server).** The `publish/` build checked into this repo (31 Oct 09:43) predates Call Center first:
`TigerCS.Application.dll` contains no `AssignCallCenterIntakeAsync`/`ResolveCallCenterAsync`/`DepartmentAssignmentRotation`. A Production site
running that (or any pre-`bebcd3e`) build routes by category department only. Also confirm `DepartmentAssignmentRotations` exists in the Production DB
(`Deploy_TigerCS_Prod_Migrations.sql`) and that department `CC` is active with at least one eligible agent
(`Verify_TigerCS_CallCenter_Agents_ReadOnly.sql`).

**Read-only checks for the incident ticket:** its `AuditEntries` (a `Transfer` row = routing ran; none = it never did), its Genesys
`TicketInteractions.RequiresHumanFollowUp` / `Source` / `GenesysConversationId`, and the raw create/update payloads from the Genesys flow
(is `humanFollowUpRequired` sent at create, at update, or never?). A phone call answered by a Genesys agent without the flag is *not* treated as
human work automatically — decide whether Genesys must send the flag for every human-handled channel.

**Backlog (requirement 3).** `Preview_TigerCS_PROD_CrossDepartmentBacklogToCallCenter_ReadOnly.sql` sizes unassigned Open/InProgress/Pending tickets
outside CC (MOVE_TO_CC vs HOLD_AI_ACTIVE). The existing Execute script still deals only tickets already in CC; extending it to move-then-deal needs the
preview approved and a UAT dry run first. Nothing here has been run against any database.

---

# Reconciliation with the parallel workflow-fix line (release candidate)

This branch now also contains `claude/admiring-maxwell-6wvhed` (Round Robin eligibility hardening, role-scoped Transfer, single-owner
enforcement, interaction-based authority, My Tickets visibility, and `Deploy_TigerCS_Prod_Migrations.sql`). Where both lines changed the same rule:
* Transfer permission = the other line's rule (CS Manager any ticket; CS Agent/Supervisor their own; Supervisor their department); **plus** this line's
  request-type restriction for any non-administrative transfer out of Call Center (department code `CC`).
* Round Robin eligibility = the other line's (not locked out, not a service identity, operational role) over this line's Call Center intake.
* `AddDepartmentAssignmentRotation.sql` (root) is the UAT record only; Production uses `Deploy_TigerCS_Prod_Migrations.sql`.
* `Verify_TigerCS_CallCenter_Agents_ReadOnly.sql` lists who Round Robin can assign in `CC` (SELECT only).
* NOT included: `claude/cool-cannon-1r4eug` (per-user permissions + migration `AddUserPermissionOverrides`) - a fifth migration outside the approved four.

---

# Finalization (supersedes the earlier "queue option" and "handler" notes)

* `assignToDepartmentQueue` and creator self-assignment **cannot bypass** Call Center intake: the ticket enters Call Center and one agent is picked by Round Robin. With no eligible agent (or assignment disabled) it stays in the Call Center queue, audited `Status=Unassigned`. Only when no active `CC` department exists does the old routing apply.
* After a **manual** Transfer (any reason except system routing) the previous owner has no ownership, and a Genesys handler whose handling was recorded *before* the transfer is downgraded to read-only (cannot Resolve/Close/Transfer). Handling recorded after the transfer counts again. Supervisor/Manager/Administrator permissions are role-based and unchanged. (`ITicketInteractionHandlerRepository.HasActiveHandlerGrantAsync`)
* Call Center is identified by department **code `CC`** everywhere (`GET /api/departments` now returns `code`).
* A Call Center owner transferring a classified ticket is offered only the request type's department (pre-selected); the API returns `422 transfer-target-must-match-request-type` otherwise.
* My Tickets card, its link and the list all mean "every ticket currently assigned to me, any status" (Resolved included).
* Production DB: `DepartmentAssignmentRotations` script added as `AddDepartmentAssignmentRotation.sql` (not executed). No other schema change in this release.

---

# FINAL RULE — Call Center is the first human-handling department for ALL channels

Supersedes the Genesys-only wording in the Flow table below.

| Step | Behavior | Where |
|---|---|---|
| New ticket needs a person (manual UI/API, Genesys with `requiresHumanFollowUp`) | Created, then moved to **Call Center** (`OriginatingDepartmentId` untouched; `Transfer` audit + `TicketTransferred`/SystemRouting event, not counted as a transfer) and given to ONE eligible Call Center agent by Round Robin (trigger `CallCenterIntake`). Request-type rules and creator self-assignment do not apply. | `TicketCreationAppService`, `TicketAutoAssignmentService.AssignCallCenterIntakeAsync` |
| AI-handled Genesys ticket (no human follow-up) | Unchanged: stays where it was created; AI closure (`closedByAi`) is untouched. Reaches Call Center only if a person is later requested. | `TicketCreationAppService` (`needsHuman`) |
| Human requested later (Genesys handoff) | Routed to Call Center **and** Round Robin assigned (previously left in the Call Center queue). | `GenesysAgentHandoffAppService` |
| Call Center agent | Resolve/Close directly (owner right), or **Transfer**. The assigned Call Center agent may Transfer (previously CS Manager only); for a classified ticket the target must be the request type's department (`422 transfer-target-must-match-request-type`). | `TicketAssignmentAppService.TransferAsync` |
| Transfer | Previous assignment superseded, previous owner loses ownership; destination rule decides first, otherwise destination Round Robin picks ONE eligible employee; explicit assignee, explicit department-queue rule, disabled assignment still win. No eligible employee -> visible unassigned queue. | `TicketAutoAssignmentService.ApplyRoundRobinFallbackAsync` |
| System request-type classification | A ticket sitting in Call Center is classified but NOT moved by the system; the Call Center agent transfers it. | `TicketRequestTypeRoutingService` |

Fallbacks: no active `CC` department -> previous behaviour (ticket stays in its category department). `assignToDepartmentQueue=true` -> enters Call Center, unassigned.

---

# Call Center routing, Round Robin assignment, Resolve/Close/Reopen permissions

## Flow
| Step | Behavior |
|---|---|
| AI resolved (`closedByAi`) | Unchanged: existing closure validations apply (Resolved → Closed, "Closed by AI"). |
| AI asks for a human (Genesys handoff) | `GenesysAgentHandoffAppService.RequestAsync` routes a ticket that nobody owns to the **Call Center** department (code `CC`) before the work item is recorded. Skipped when already in Call Center, already owned by a person, closed, or the trigger is an explicit `AgentTransfer`. Audited (`Transfer`) + `TicketTransferred` workflow event (system routing). |
| Call Center selects a category | Category → Department. `TicketClassificationAppService` lets a ticket sitting in Call Center be routed to the category's (active) department; manual ticket creation by a Call Center user for another department does the same. Category Type/Request Type still drives workflow, SLA and the assignment rule. |
| Assignment | `TicketAutoAssignmentService`: a configured request-type rule decides first. If none decided, the department's **Round Robin** picks the next eligible employee. |

## Round Robin
* `AssignmentMode.RoundRobin` can also be configured per request type (Admin → Request type → Assignment).
* Eligible = active members of the ticket's **current** department (stable id order).
* Cursor persisted in `DepartmentAssignmentRotations` (one row per department; migration `AddDepartmentAssignmentRotation`), so it survives restarts and is shared by all instances.
* Concurrency: on SQL Server the cursor row is created if missing and read `WITH (UPDLOCK, ROWLOCK)` inside the assignment transaction, so concurrent assignments serialize and never pick the same slot. `rowversion` is a second line of defence (surfaces as the existing concurrency conflict).
* No eligible employee (or assignment disabled for the department) → ticket stays in the department queue, shown as **"<Department> Queue (Unassigned)"**; the reason is audited.
* Every assignment/reassignment writes a `TicketAssignments` row and an `AutoAssign`/`Assign` audit entry (system actions have a null actor).

## Permissions
* **Resolve / Close:** the ticket's current assignee may always do both, regardless of role or department. Existing role rules (Department Head, CS layer, ADR-0024 override) still apply to everyone else. Read-only roles never qualify through ownership. All lifecycle validations and audit are unchanged.
* **Reopen:** unchanged rule — Call Center users (CS Agent/Supervisor/Manager) with ticket access; a non-blank reason is mandatory (service + controller); audit stores user, timestamp, previous status (`Closed`) and new status (`InProgress`) plus the reason. Inactivity-closure and reopen-window rules are untouched.
