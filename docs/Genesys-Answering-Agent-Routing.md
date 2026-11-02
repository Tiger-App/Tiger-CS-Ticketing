# Genesys human handling — the agent who answers owns the ticket

**Rule.** When a Genesys call is answered by a mapped human Call Center agent, the ticket is assigned **directly to that agent**, in the Call Center — not to the next agent in
Round Robin. Round Robin is used **only** when a human handoff was requested and nobody has accepted the interaction yet.

Kill-switch: `Genesys:AssignOnAnswer` (default `true`). Set `false` (environment variable `Genesys__AssignOnAnswer=false`, then recycle the API pool) to return to the previous
behaviour — Round Robin for every ticket that needs a person, the answering agent only recorded as a handler. No redeploy, no data change.

## What counts as "answered"

Only an agent id Genesys sends as **connected** (answered / accepted):

| Signal | Answered? |
|---|---|
| `agentId` on the ticket-creating inquiry of a **phone call**, or of an inquiry already marked `humanFollowUpRequired` | **Yes** |
| `routing.agentId` on an update (with optional `connectedAtUtc`) | **Yes** |
| `handoff.agentAvailable` + `agentId` on a handoff request (an agent *assigned*, possibly ringing) | **No** — Round Robin |
| `handoff.assignedAgentId` (Genesys names who is to take the work) | **No** |
| Top-level `agentId` on an update / conversation end (conversation context) | **No** (recorded as a handler only, as before) |
| Ringing, offered, alerting, queue-only events | **No** — they must not be sent as `routing.agentId` |
| `agentId` on an AI-handled chat with no human flag (a bot, a not-yet-connected agent) | **No** — the ticket is not dragged into the Call Center |

The Genesys flow therefore **must send `routing.agentId` / the inquiry `agentId` only once the agent has actually connected.** An id sent while the agent is only ringing would be treated as an answer.

## What happens

1. The Genesys User ID is matched to `AspNetUsers.GenesysUserId` (the only key; names and emails never match).
2. **Eligible** = mapped, employee active, account not locked out, not a service identity, member of the Call Center, holds an operational role (CS Agent / Department Employee / Department Head; read-only roles never).
3. **Eligible agent:** the ticket moves to department `CC` if it was elsewhere (system routing, `OriginatingDepartmentId` unchanged, `Transfer` audit + `TicketTransferred/SystemRouting` event) and is assigned to the agent. The assignment row and the `AssignOnAnswer` audit entry name the **agent as the actor** — a human act, like a self-assignment — so a later Genesys event can never mistake it for an automatic pre-assignment. The Round Robin cursor is **not** advanced.
4. **Unmapped / deactivated / locked / service account / not in CC / no operational role:** the ticket is left **unowned in the Call Center queue**, never given to anyone else, and flagged: a `ManualAssignmentRequired` workflow event (shown in the ticket activity as "needs manual assignment", note names the Genesys agent and the reason) plus an `AnswerAssignmentSkipped` audit entry, written once per (ticket, agent, reason). An *automatic* pre-assignment (Round Robin / rule) already on the ticket is released (`Unassign` audit, assignment row superseded) so it is not left with someone who did not answer.
5. **Ownership is never overwritten.** A person who owns the ticket (manual assignment, self-assignment, an earlier answer) keeps it; only an automatic pre-assignment yields to the agent who actually answered. Another agent connecting later (a Genesys call transfer) becomes a *handler* (can see the ticket) but not the owner.
6. **A person's transfer stands.** Once a person has transferred the ticket (any transfer that is not system routing, escalations included), later Genesys updates change nothing: not the department, not the new owner, no flag.
7. **Idempotent and race-safe.** The same event twice writes nothing. Two competing writers: the loser is rejected by the ticket's row version (reported as `ConcurrencyConflict`, safe to redeliver), and the unique index on `TicketAssignments(TicketId) WHERE IsCurrent = 1` makes a second current assignment impossible.
8. **Preserved:** ticket status, SLA, priority, request type, all earlier audit and workflow history (append-only), and every permission. The answering agent is an ordinary owner: may Resolve/Close (owner right) and Transfer (CS Agent role + owner).

## Deployment requirements

**Database — none.** No migration, no schema change: the new `ManualAssignmentRequired` event type is stored in the existing unconstrained `TicketWorkflowEvents.EventType` tinyint (verified: no check constraint; `EfModelAndMigrationConsistencyTests` pass against SQL Server).

**Application — deploy `TigerCS.Api` and `TigerCS.Web`** from the merged commit (Api: the rule; Web: the "needs manual assignment" activity label). The Call Center-first release items it builds on must already be live (migrations `…AddDepartmentAssignmentRotation`, `…AddTicketTransferTracking`, `…AddTicketInteractionHandlers`, `…AddTicketCreatedBy`; department `CC` with eligible members; service-account ids configured) — see `docs/Call-Center-Backlog-Distribution-Runbook.md` §1.2.

**Data — every Call Center agent must be mapped before go-live** (`AspNetUsers.GenesysUserId` = the agent's Genesys User ID). An unmapped agent who answers a call produces a flagged, unowned ticket rather than an assignment. Run `Verify_TigerCS_GenesysAnswerRouting_ReadOnly.sql` (read-only) first: section 2 must show `ActiveButNotMapped_FixBeforeGoLive = 0`.

**Genesys flow** — send the connected agent's id (inquiry `agentId` for an answered call; `routing.agentId` with `connectedAtUtc`) only at connection time, never at ringing/offer.

**Configuration** — nothing required (`Genesys:AssignOnAnswer` defaults to `true`). The kill-switch is the rollback.

**After deployment** — run the same script: section 3 lists tickets waiting for manual assignment (give them to a person from the Call Center queue), section 4 shows direct assignments vs flags per day, section 5 must be all zeros.

**Rollback** — set `Genesys__AssignOnAnswer=false` and recycle the API pool (immediate), or redeploy the previous build. Assignments already made stay valid; flagged tickets stay in the Call Center queue for manual assignment.

## Verification performed

See the PR description: the .NET test suite (including real-SQL-Server concurrency tests and the EF/migration consistency check) and mutation checks of each rule.
