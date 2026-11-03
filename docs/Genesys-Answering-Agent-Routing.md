# Genesys human handling — the agent who answers owns the ticket

**Rule.** When a Genesys call is answered by a mapped human Call Center agent, the ticket is assigned **directly to that agent**, in the Call Center — not to the next agent in
Round Robin. Round Robin is used **only** when a human handoff was requested and nobody has accepted the interaction yet.

Kill-switch: `Genesys:AssignOnAnswer` (default `true`). Set `false` (environment variable `Genesys__AssignOnAnswer=false`, then recycle the API pool) to return to the previous
behaviour — Round Robin for every ticket that needs a person, the answering agent only recorded as a handler. No redeploy, no data change.

> Relationship to `docs/Genesys/Human-Accepted-Call-Routing-Runbook.md` (human-accepted calls → Call Center): that document decides **whether** a conversation is
> human-handled and routes it; this rule decides **who owns it** when a specific agent answered. They compose: the evidence a person has the call is the same
> (`GenesysHumanHandlingEvidence`), and a ticket that already passed through the Call Center and left it is never pulled back by either.

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
| `agentId` on an AI-handled chat with no human flag and no mapping (a bot, a not-yet-connected agent) | **No** — the ticket is not dragged into the Call Center. (A *mapped* user is a real person, so a mapped-but-ineligible one is flagged, not ignored.) |

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

---

# Final ownership rule (PR #154) — the human who actually receives/accepts the interaction owns the ticket

All channels (Phone, WhatsApp, Live Chat, Instagram, Facebook, other social):

| Evidence of who received it | Result |
|---|---|
| Phone inquiry created at pickup naming a mapped agent; `routing.agentId` (connected) on an update; a `HumanAgent` transcript line whose `senderId` maps to a user | That employee owns the ticket (Call Center first). |
| Human required, nobody has accepted | Call Center Round Robin = a **provisional** owner. |
| A provisional owner exists and the real agent connects / writes | The real agent **replaces** it (previous assignment row kept, superseded; both audited). |
| The agent cannot take it (unmapped-but-known, inactive, locked, not a Call Center member, **not a CS Agent**) | The ticket waits unowned in the Call Center, flagged `ManualAssignmentRequired`. It is **never** handed to someone else instead. |

Never replaced: ownership after a person worked the ticket (first reply, status moved on, manual assignment, **an internal note**), and any intentional transfer. The agent's *own* reply, recorded in the same update, does not count as the provisional owner's work. AI-only conversations (no human evidence; `VirtualAgent`/`Customer` lines never count) are not moved.

**CS Agent restriction.** In the Call Center (department `CC`) automatic assignment — Round Robin *and* the confirmed answering agent — requires the CS Agent role. Other departments keep CS Agent / Department Employee / Department Head.

**One-time backlog redistribution scripts** (`Execute_/Preview_/Rollback_TigerCS_PROD_CallCenterBacklogDistribution*`) deal owned *and* unowned Open tickets to Round Robin owners without regard to a recorded actual handler (`TicketInteractionHandlers`). They have **not** been aligned with this rule (their Docker-based test harness could not be run in this change) and must not be run as-is; see section 7 of `Verify_TigerCS_DigitalChannelVisibility_ReadOnly.sql` for the affected tickets.
