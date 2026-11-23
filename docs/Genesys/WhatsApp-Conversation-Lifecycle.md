# WhatsApp (Digital) — conversation lifecycle, status and assignment (PR #292)

Scope: WhatsApp and the other digital Genesys channels (web chat, Instagram, Facebook, social media). **Phone is a separate rule set** (`Voice-Ownership-Rules.md`) and is unchanged by this document.

## 1. Events → state (verified in code)

| Genesys event (inquiry / update field) | Effect on the ticket | Conversation status (derived) |
|---|---|---|
| Inquiry, no `humanFollowUpRequired` | Ticket created, **AI Agent owns it**, no owner, **no Round Robin** (creation takes the "AI-handled" branch) | Conversation In Progress |
| Inquiry with `humanFollowUpRequired: true` | Call Center first + ONE Round Robin agent at creation (provisional owner) | Awaiting Genesys Update |
| Update: `transcript` (VirtualAgent lines), queue change (`routing` without agent), top-level `agentId` (assigned / ringing), repeated or delayed updates | Stored only. **No assignment, no Round Robin**, AI ownership kept. A top-level `agentId` records a handler row (`Update` source) — never a confirmation | Conversation In Progress |
| Update: `humanFollowUpRequired: true` or `handoff.required` | Human work: Call Center first + ONE Round Robin agent (approved Digital workflow); handoff item `WaitingForAgent` | Awaiting Genesys Update |
| Update: `routing.agentId` (+ `connectedAtUtc`) — **connected** | The answering agent owns the ticket (Call Center, no rotation); `ReassignOnTransfer` moves it to a later connected agent; idempotent per agent | Assigned |
| Update: `HumanAgent` transcript line (`Genesys:AssignOnTranscriptEvidence`, Phone gate only) | Human work is flagged; the mapped writer is recorded as handler | Assigned |
| Update: `ended` | Interaction end is recorded (write-once). Ending **never closes, reopens or reassigns** by itself — but see §3 (end without evidence on a digital channel) | Conversation Ended |
| Update: `closedByAi` | Closes only an AI-only, unowned ticket (no human work, not a Call Center ticket) | — (ticket Closed) |
| Any event on a **Closed** ticket | Audited (`GenesysEventIgnoredOnClosedTicket`); **no** owner, status, department or Round Robin change | unchanged |

The conversation status is **derived** (`GenesysConversationStatus`, no column, no migration), exposed as `TicketInteractionDto.ConversationStatus` and shown on Ticket Details as **Conversation status**. It is separate from `TicketStatus`, from `Assigned To` and from Assignment History. *Assigned* requires a **confirmed** human only: a handler from a routing / transcript / creating event, an accepted handoff, or a human reply — never an agent that was merely assigned, offered or ringing.

Contract limit: Genesys sends no explicit "conversation active" or "awaiting update" event; *In Progress* and *Awaiting* are inferred from the facts above.

## 2. Assignment decision points (digital)

1. **Creation** (`TicketCreationAppService`): human needed → Call Center first + Round Robin; otherwise AI-owned, no rotation.
2. **Handoff request** (`GenesysAgentHandoffAppService.RequestAsync`): connected agent → that agent; otherwise Call Center first + Round Robin.
3. **Human-follow-up signal on an update** (`MarkHumanFollowUpAsync`): Call Center first + Round Robin (no-op when a person owns it or it is Closed/Resolved).
4. **Connected agent** (`GenesysAnsweringAgentAssignmentService`): direct assignment, transfer on a later connected agent.
5. **End of an unfinished AI conversation** (`AiEndedUnresolved`): see §3.
6. **Background**: `CallCenterReconciliation` (disabled in Production, DryRun) deals unowned Open tickets that are human work; a live AI conversation is protected (no stale threshold configured, stale-AI action off). `VoiceCallReconciliation` never classifies a non-Phone ticket. All jobs share one run lock and skip non-Open tickets.

## 3. Differences from `main` and open business questions

* `main` currently carries the earlier "unified channels" snapshot (`GenesysOwnershipRules`): AI ownership rules on every channel. This PR removes it, so **WhatsApp returns to Call Center first + Round Robin** at the decision points above. This is the approved split ("digital keeps its previous behaviour"), but it changes behaviour relative to today's `main` and needs release sign-off.
* **End without evidence (needs approval):** a digital conversation whose `ended` arrives with no human evidence, no owner and no `closedByAi` in the same update is promoted to human work and dealt by Round Robin immediately. If Genesys sends `closedByAi` in a *later* update, it is refused ("needs human follow-up" / Call Center tickets are never AI-closed) and the resolved conversation stays open as human work. Whether `ended` and `closedByAi` always arrive in one update is not established by the contract. Options: keep, require `closedByAi`/evidence first, or defer the promotion behind a grace window (needs a job that is enabled in Production).
* **Terminal Closed:** a customer reply on the **same** Genesys conversation after closure is ignored (the transcript may still be appended; events carrying human evidence are audited as `GenesysEventIgnoredOnClosedTicket`; nothing reopens, nothing reassigns). A new WhatsApp conversation id creates a **new ticket**. A new message inside the same conversation id after closure therefore creates no new ticket and no agent work — this is the main business implication of terminal Closed for WhatsApp.
