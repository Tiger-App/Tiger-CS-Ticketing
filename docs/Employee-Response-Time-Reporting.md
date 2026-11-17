# Employee response time — Genesys AI → human voice transfers

Fair, per-employee reporting of how fast a person responds to a Genesys call **offer**, shown in the employee's SLA details
(`/Reports/TeamPerformanceSla`, `GET /api/reports/team-performance/sla-details` → `employeeResponseSummary`, `employeeResponses`).
It is **separate from** the ticket's First Response / Resolution SLA, which is unchanged: nothing computed here feeds the SLA engine, the
breach history, the SLA counts or compliance %.

## What the report shows

One row per offer **received by that employee** (never an offer to someone else on the same ticket): offer time, answer time, response
duration, outcome. The ticket SLA is the existing table on the same page.

| Outcome | When | Counts toward performance |
|---|---|---|
| **Answered** | Offer in the employee's verified shift, Genesys confirms *this* employee answered *this* offer, answer time ≥ offer time. Duration = answer − offer (AI handling and queue wait before the offer are excluded). | Yes |
| **Not Responding** | Offer in shift and Genesys **confirms** the employee did not respond to it. Tracked apart from the ticket SLA breach. | Yes |
| **Declined / Redirected / Caller Abandoned** | In-shift offer where Genesys confirms that specific result. Shown separately, **never** as Not Responding, and excluded from performance scoring until a policy is agreed. | No |
| **Outside Shift** | Offer outside the employee's shift or during approved leave (whatever Genesys says happened to it). | No |
| **Availability Unknown** | No offer event, **or** no historical shift/leave data for that instant, **or** the offer's outcome is not verified (incl. "answered" with no usable answer time). | No |

No employee breach threshold exists: the measured time and confirmed outcome are shown as they are. There is no employee breach count or
compliance %. Another agent answering never makes the first agent "Not Responding" — only Genesys' own statement about that agent's offer does.

## What TigerCS actually receives and stores today (verified in code)

| Needed | Received / stored today? | Evidence |
|---|---|---|
| Receiving agent ↔ user mapping | **Yes** — `AspNetUsers.GenesysUserId` (unique filtered index `UX_AspNetUsers_GenesysUserId`) | `ApplicationUser`, `GenesysAgentResolutionAppService` |
| Human **answer / connect** time | **Partly** — `routing.connectedAtUtc` (and the phone inquiry's agent) → `TicketInteractionHandlers.ConnectedAtUtc`, optional, only for events naming the *connected* agent | `GenesysRoutingUpdateDto`, `TicketInteractionHandler` |
| Who answered (per agent, transfers kept) | **Yes** — one `TicketInteractionHandlers` row per connected agent | `GenesysInteractionHandlerRecorder` |
| **Call offer timestamp for the receiving agent** | **No.** Ringing / alerting / offered events never reach TigerCS; the contract says so and forbids sending them. | `GenesysContracts.cs` ("A ringing call never reaches TigerCS"), `docs/Genesys-Answering-Agent-Routing.md` |
| **Offer outcome** (answered / no-answer / declined / redirected) | **No.** | — |
| Employee **shift** / roster history | **No.** No shift table. The business calendar is company-wide working hours, not per employee. | `BusinessCalendars` |
| Employee **leave** history | **No.** | — |

Ticket assignment time, queue entry and current presence are deliberately **not** used as substitutes for the offer.

## Behaviour until the data exists

`IGenesysOfferHistory` and `IEmployeeShiftHistory` are registered with "no data" implementations (`NoGenesysOfferHistory`,
`NoEmployeeShiftHistory`). Consequently today:

* no offers are listed;
* a phone call the employee is recorded as having **connected** to (handler row with a Genesys connect time, source Inquiry/Routing) is
  listed as **Availability Unknown**, with its real answer time and **no duration** (nothing to measure it from);
* no employee is ever shown Answered, Not Responding or Outside Shift, and no number is guessed.

## Integration needed for full rollout

1. **Genesys → TigerCS offer events.** For every offer of a voice conversation to an agent: `conversationId`, the agent's Genesys User ID,
   `offeredAtUtc` (the alert/offer time for that agent), and the offer's terminal result with its own timestamp: *answered* (+ `answeredAtUtc`),
   *not answered / timed out*, and any other result (declined, redirected, cancelled by Genesys, caller abandoned). Store one row per offer
   (new table keyed by conversation + agent + offer time), resolve the agent through `AspNetUsers.GenesysUserId`, and implement
   `IGenesysOfferHistory` over it. This needs a Genesys-side flow/notification subscription and a new endpoint; the current "never send ringing"
   rule in the contract must be relaxed for this separate event type only (offers must still never be treated as answers by the ownership rules).
2. **Business ruling on scoring.** Only a Genesys-confirmed *no answer* is Not Responding. Confirmed declined / redirected / caller-abandoned are
   displayed separately and not scored until a policy is agreed; any other or unconfirmed result (e.g. cancelled by Genesys) stays Availability Unknown.
3. **Shift and approved-leave history** (HR / rostering source or a new roster + leave table in TigerCS), queryable as of a past instant, with
   history retained so a later roster edit cannot rewrite what applied at offer time. Implement `IEmployeeShiftHistory` over it.
4. Operational: every Call Center agent mapped to `GenesysUserId` (see `Verify_TigerCS_GenesysAnswerRouting_ReadOnly.sql`), otherwise their offers cannot be attributed.

Until 1 and 3 exist, the feature is safe to deploy but reports nothing measurable.

## Verification status of the external sources (as of this change)

Nothing below could be verified from the development environment; none is enabled.

| Item | Status | Needed from |
|---|---|---|
| Real offer event (`user.start`-style alert per agent) with offer time, agent, terminal result | **Not verified.** TigerCS never calls Genesys' API and holds no Genesys credential (`GenesysOptions`); the integration is inbound-only through TigerGroupWeb. `docs/Genesys/Genesys-UAT-Final-Handoff.md` records "No real Genesys event seen". Contract change required (new offer event type). | Genesys Cloud / Architect team (event + Notification-API/trigger permission); TigerGroupWeb operator (forwarding) |
| Anonymized AI → Human trace incl. A-no-answer / B-answer | **Not obtained.** | Genesys team: export of one conversation's participant/segment history |
| Tiger HR historical shifts and approved leave | **No HR interface exists in this repo** (no reference, table or client). Employee key, timezone and history retention unknown. | Tiger HR owner: API/export, employee id ↔ `AspNetUsers`, timezone, history guarantee |
| UAT end-to-end run | **Not run.** | Needs the above plus a UAT test call |
