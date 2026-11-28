# Genesys messaging channels — Genesys names who handles the conversation

**Rule.** For tickets that originate from a Genesys **messaging** conversation, TigerCS never deals a provisional human owner. Genesys decides who handles the
conversation; TigerCS records it.

Scope (`GenesysMessagingAssignmentPolicy`, `src/TigerCS.Application/Modules/Ticketing/Services/`): WhatsApp, web live chat (`LIVE_CHAT`), Instagram, Facebook,
generic social DM and the legacy combined WhatsApp / Live Chat id (channel ids 3, 4, 6, 7, 10, 11). **Not** in scope: Phone (voice keeps its own rules and PR #307),
the Queueing System, manually created tickets, e-mail / walk-in and every non-Genesys integration. The policy is always evaluated against a Genesys conversation
(a Genesys context at creation, a Genesys interaction afterwards), never against the channel id alone.

## Flow

Incoming message → ticket created (department routing, Call Center first, AI Agent as recorded owner) → **waits, no human owner** → Genesys confirms the person who
is actually handling the conversation → assigned **directly** to that person.

| Event | Result |
|---|---|
| Ticket created, AI-handled or human requested | No Round Robin, no rule assignment, no provisional owner. Queue + AI ownership as before. |
| AI requests a handoff (`handoff.required`) | Routed to the Call Center queue; AI Agent stays the owner; nobody dealt. A request is not an acceptance. |
| `humanFollowUpRequired` update | Same: routed, flagged, nobody dealt. |
| Update with no employee identity / offered or `handoff.assignedAgentId` only | Nothing assigned. |
| `routing.agentId` = mapped, eligible CS Agent (**connected**) | Assigned directly to them (`AssignOnAnswer`, one assignment row, no `AutoAssign`). |
| A mapped person wrote a `HumanAgent` transcript line | Assigned to that person (existing evidence rule). |
| Unknown / unmapped / inactive / ineligible employee | Flagged (`ManualAssignmentRequired` + `AnswerAssignmentSkipped`), stays unassigned. **No fallback Round Robin.** |
| Duplicate / older / other-conversation event | Existing idempotency and ordering protections. |
| Conversation ends without identification | Existing end handling; no Round Robin. |
| AI resolves (`closedByAi`) | Unchanged. |
| Reconciler, backfill, intake, retry-by-system | Held back **in code** (`TicketAutoAssignmentService.IsAwaitingGenesysMessagingAsync`), whatever the job configuration says. |
| Authorised manual assignment / `ManualRetry` / a person's department transfer | Unchanged — explicit human decisions still assign. |

Enforcement points: `TicketCreationAppService` (creation), `GenesysAgentHandoffAppService` (handoff), `GenesysTicketUpdateAppService` (follow-up, classification),
`TicketAutoAssignmentService` (`AssignCallCenterIntakeAsync`, `EnsureCsAgentAsync`, `AssignDepartmentRoundRobinAsync`, `RouteExistingTicketToCallCenterAsync`,
`ApplyAnsweringAgentAsync` fallback), `TicketRequestTypeRoutingService` (AI classification assigns nobody), `CallCenterUnassignedWorkReconciler`, `CsAgentBackfill`.

## Attribution (assignment history, first response, SLA, performance)
* The assignment history of a confirmed ticket has one row: the person Genesys confirmed. No superseded provisional row is created.
* First Response / SLA reports credit the person who held the ticket **at the moment of the response** (`TicketAssignments`). A confirmed agent from
  `routing.agentId` is recorded at Genesys' `connectedAtUtc` (before their first reply). **Limitation:** when the only evidence is a late human transcript line (no
  `routing.agentId`), the assignment is recorded at receipt time and is deliberately not back-dated, so a first reply that was *sent* earlier is credited to nobody
  until then. Fixing that needs Genesys to send `routing.agentId` + `connectedAtUtc`; nothing is fabricated.
* Employee response-time reporting is offer-based and voice-only today (`docs/Employee-Response-Time-Reporting.md`); digital offers are not sent by Genesys.

## Legacy tickets
Tickets that already carry a provisional Round Robin owner (created before this change) are not modified. The existing "a confirmed agent replaces an untouched
automatic pre-assignment" logic still applies to them.

## Rollout
No migration, no schema change, no configuration change. Deploy `TigerCS.Api`.
