namespace TigerCS.Application.Modules.GenesysIntegration.Dto;

/// <summary>
/// Everything Genesys may update on a ticket it created — and, by omission,
/// everything it may not.
///
/// <para>
/// <b>This is deliberately a narrow surface.</b> Genesys owns the
/// conversation: who handled it, when it ended, what was said, and whether it
/// still needs a human. It does <b>not</b> own the ticket's business state.
/// There is no field here for category, request type, priority, status,
/// owner, department, resolution or closure — those move only through their
/// own TigerCS operations, with their own rules, authorization and SLA
/// consequences. A field absent from this contract is a field Genesys cannot
/// reach.
/// </para>
///
/// <para>
/// Every part is optional and independently idempotent: send only what
/// changed, resend freely. In particular, ending a conversation that already
/// ended does not move the recorded end time or duplicate its transcript, and
/// <b>no update here ever closes the ticket</b>.
/// </para>
/// </summary>
/// <param name="ConversationId">
/// Required. The Genesys conversation this update belongs to. It must resolve
/// to an interaction on the ticket in the route — that cross-check is what
/// stops a transcript being applied to the wrong ticket.
/// </param>
/// <param name="AgentId">The Genesys agent now handling the conversation, when known. Applied only if the interaction does not already name one.</param>
/// <param name="AgentName">That agent's display name, same apply-if-absent rule.</param>
/// <param name="Ended">Set when the conversation has finished, for any reason — agent ended it, customer closed the browser, connection dropped, Genesys timed it out.</param>
/// <param name="Handoff">Set when the conversation needs a human agent, or when one has taken it. Omit entirely when neither applies.</param>
/// <param name="StartedAtUtc">When the interaction started on the Genesys side — recorded only if the creating call did not supply it; never moves once known.</param>
/// <param name="Routing">Set when Genesys moved the conversation to another queue, or connected / transferred it to an agent. Omit when the routing did not change.</param>
/// <param name="CustomerConfirmation">Set when the customer explicitly confirmed during the interaction that their issue is resolved. Record-only — never resolves or closes the ticket.</param>
/// <param name="AwaitingCustomerReply">true starts the chatbot inactivity timer (a repeat never restarts it); false cancels it (the customer replied); null leaves it as it is.</param>
/// <param name="CustomerActive">true = the customer just did something in the conversation (sent a message, answered the bot): restarts the Website/Chatbot inactivity clock at TigerCS's own time. Send it on EVERY inbound customer message. null/false changes nothing.</param>
/// <param name="ClosedByAi">true = the Genesys AI (voicebot / chatbot) finished the conversation: the ticket is closed (Resolved → Closed, reason "Closed by AI") — only if the AI still owns it. Refused, with a note and nothing changed, when a human is requested/pending/connected, a person owns the ticket, it is a Call Center ticket or it is already closed. null/false changes nothing.</param>
/// <param name="RequestType">Set when the bot has identified the request type: validated, then applied with its department routing, automatic assignment and SLA policy. Repeating the same one changes nothing; a different one replaces it only while no person has worked the ticket, otherwise it is refused.</param>
/// <param name="HumanFollowUpRequired">true = this ticket needs a person: stops both chatbot inactivity clocks and forbids the AI and document delivery from closing it. Sticky — only the authorized TigerCS workflow ends it. null/false changes nothing.</param>
/// <param name="Transcript">Messages of the live conversation, sent as they happen or in batches. Additive and idempotent (de-duplicated on <c>externalMessageId</c>, else sender+time+body); does not end the conversation. The final <c>ended.transcript</c> may repeat them safely.</param>
public sealed record GenesysTicketUpdateDto(
    string ConversationId,
    string? AgentId = null,
    string? AgentName = null,
    GenesysConversationEndUpdateDto? Ended = null,
    GenesysHandoffUpdateDto? Handoff = null,
    DateTime? StartedAtUtc = null,
    GenesysRoutingUpdateDto? Routing = null,
    GenesysCustomerConfirmationUpdateDto? CustomerConfirmation = null,
    bool? AwaitingCustomerReply = null,
    GenesysRequestTypeDto? RequestType = null,
    bool? CustomerActive = null,
    bool? ClosedByAi = null,
    IReadOnlyList<GenesysTranscriptMessageDto>? Transcript = null,
    bool? HumanFollowUpRequired = null);

/// <summary>
/// The customer's own statement, made during the interaction, that the issue
/// is resolved. It is <b>recorded</b> on the ticket — a typed workflow event
/// and an audit entry — and nothing else: the ticket is not resolved or
/// closed, because Resolve and Close keep their existing actors and
/// authorization. The owner reads it and acts through TigerCS.
/// </summary>
/// <param name="ConfirmedResolved">Must be <c>true</c> — this part exists only to report an explicit confirmation.</param>
/// <param name="ConfirmedAtUtc">When the customer said it. Defaults to now. Send it: a resend with the same value is recognized and stores nothing twice.</param>
/// <param name="Note">What the customer said, or the flow's own note. Display only.</param>
public sealed record GenesysCustomerConfirmationUpdateDto(
    bool? ConfirmedResolved = null,
    DateTime? ConfirmedAtUtc = null,
    string? Note = null);

/// <summary>
/// Where the conversation is now — the queue it sits in and the agent it is
/// connected to. Sent on a queue change, when an agent connects, and on
/// every transfer; always the same conversation, the same interaction and
/// the same ticket.
///
/// <para>
/// It records the conversation's current routing and nothing about the
/// ticket: no department, owner or status moves because Genesys transferred
/// a call. The first agent who handled the interaction stays recorded as its
/// handler. When an agent connects to a conversation whose human work is
/// still <c>WaitingForAgent</c>, that is the human taking it, and the work
/// is marked assigned to them exactly as <c>handoff.assignedAgentId</c>
/// would.
/// </para>
/// </summary>
/// <param name="QueueId">The Genesys queue the conversation is now in.</param>
/// <param name="QueueName">That queue's display name.</param>
/// <param name="AgentId">The Genesys User ID of the agent now <b>connected</b> (answered / accepted — never one that is merely ringing or assigned). Resolved to the mapped Ticketing user and recorded as a handler of the interaction, so every agent a transfer passes through is kept.</param>
/// <param name="AgentName">That agent's display name. Display only — never an identity key.</param>
/// <param name="ConnectedAtUtc">When that agent connected, as Genesys reports it (the participant's connected time). Optional; stored on the handler record when supplied.</param>
public sealed record GenesysRoutingUpdateDto(
    string? QueueId = null,
    string? QueueName = null,
    string? AgentId = null,
    string? AgentName = null,
    DateTime? ConnectedAtUtc = null);

/// <summary>
/// The conversation has finished. Recording this stores the transcript and
/// finalizes the interaction. An ending never reassigns a ticket that has an
/// owner, and never moves one the AI closed or is still waiting on the customer
/// for. The one thing it does: an AI conversation that ends without the AI
/// resolving or closing the ticket, and with nobody owning or handling it, is
/// routed for human handling (Call Center first, shared round robin as the
/// provisional owner until an agent actually accepts).
/// </summary>
/// <param name="EndedAtUtc">When it ended. Defaults to now when omitted.</param>
/// <param name="EndReason">Why, as reported (e.g. "AgentDisconnect", "CustomerDisconnect", "Timeout"). Free text — no vocabulary is confirmed.</param>
/// <param name="Transcript">The conversation, in order, as far as it is available. Resending an already-stored transcript stores nothing twice.</param>
public sealed record GenesysConversationEndUpdateDto(
    DateTime? EndedAtUtc = null,
    string? EndReason = null,
    IReadOnlyList<GenesysTranscriptMessageDto>? Transcript = null);

/// <summary>
/// Human-agent state for the conversation, on any channel. A callback is one
/// possible <paramref name="Mode"/>, never the concept.
/// </summary>
/// <param name="Required">
/// True records that the conversation needs a human agent — idempotent, as a
/// conversation whose human work is still outstanding is answered with that
/// same work item. <b>Explicit false</b> stands outstanding work down (the AI
/// resumed, or the human is no longer needed) and requires
/// <paramref name="Reason"/>. <b>Null (omitted) does neither</b>, which is
/// what lets an update carry only <paramref name="AssignedAgentId"/> without
/// the omitted flag reading as a cancellation — the reason this is nullable
/// rather than a plain bool.
/// </param>
/// <param name="AgentAvailable">True when Genesys is handing straight over to a named human, false when nobody is available and the work must wait.</param>
/// <param name="Mode">"Callback", "ContinueChat", "ReplyInChannel" or "HumanTakeover". Omit unless Genesys actually states it — TigerCS never derives it from the channel.</param>
/// <param name="Reason">Why a human is needed (a virtual agent's escalation reason, a routing note) — or, when standing work down, why the human is no longer needed. Required in that direction.</param>
/// <param name="Trigger">Why a human is needed, typed: "CustomerRequestedHuman", "AiConnectionLost", "AiEscalated", "RoutingDecision" or "AgentTransfer". Omit unless Genesys states it — never derived from the channel or the end reason.</param>
/// <param name="WorkItemId">Genesys' own routing-task id, if it has one. The stronger idempotency key; never invented in its absence.</param>
/// <param name="AssignedAgentId">Set to record that this Genesys agent has taken the outstanding work. Applies to the existing work item, never a second one.</param>
public sealed record GenesysHandoffUpdateDto(
    bool? Required = null,
    bool AgentAvailable = false,
    string? Mode = null,
    string? Reason = null,
    string? Trigger = null,
    string? WorkItemId = null,
    string? AssignedAgentId = null);

/// <summary>What a Genesys ticket update did.</summary>
public enum GenesysTicketUpdateOutcome
{
    /// <summary>Everything supplied was applied (or was already in that state).</summary>
    Applied,

    IntegrationDisabled,
    ConversationIdRequired,

    /// <summary>No interaction exists for this conversation — it never produced a ticket.</summary>
    ConversationNotFound,

    /// <summary>No such ticket.</summary>
    TicketNotFound,

    /// <summary>The conversation belongs to a different ticket than the one in the route. Refused rather than guessing which the caller meant.</summary>
    ConversationTicketMismatch,

    /// <summary>A transcript message had an unrecognized sender or an empty body. NOTHING was stored — resend the whole update.</summary>
    InvalidTranscript,

    /// <summary>The supplied handoff mode is not one of TigerCS' normalized values.</summary>
    InvalidHandoffMode,

    /// <summary>An assignment was supplied but there is no outstanding human work to apply it to.</summary>
    NoOpenHandoff,

    /// <summary>The supplied handoff trigger is not one of TigerCS' normalized values.</summary>
    InvalidHandoffTrigger,

    /// <summary>A handoff stand-down (<c>Required = false</c>) named no reason.</summary>
    HandoffReasonRequired,

    /// <summary>A customer confirmation was sent without <c>confirmedResolved: true</c>. Nothing was written.</summary>
    InvalidCustomerConfirmation,

    /// <summary>The supplied request type does not exist, is inactive, is ambiguous by name, or cannot route. Nothing was written.</summary>
    InvalidRequestType,

    /// <summary>The ticket already carries a different request type.</summary>
    RequestTypeConflict,

    /// <summary>The ticket is Closed, so a request type cannot be classified onto it.</summary>
    TicketClosed,

    /// <summary>The ticket changed underneath the classification; nothing was applied, retry.</summary>
    ConcurrencyConflict
}

/// <summary>
/// The result of one update. <see cref="TicketStatus"/> is echoed
/// deliberately: it is the proof that ending a conversation did not close the
/// ticket.
/// </summary>
/// <param name="Outcome">What happened.</param>
/// <param name="TicketId">The ticket.</param>
/// <param name="TicketNumber">Its number.</param>
/// <param name="TicketStatus">Its status — unchanged by anything in this contract.</param>
/// <param name="ConversationEnded">Whether the interaction is now recorded as ended.</param>
/// <param name="TranscriptMessageCount">How many transcript messages the interaction holds after this update.</param>
/// <param name="HandoffStatus">The pending human work's status, when there is any: WaitingForAgent, Assigned, InProgress, Completed or Cancelled. Null when this conversation never needed a human.</param>
/// <param name="TicketAgentHandoffId">That work item's id, when there is one.</param>
/// <param name="Detail">A human-readable explanation on a refusal.</param>
/// <param name="AwaitingCustomerReply">Whether the inactivity timer is running after this update.</param>
/// <param name="InactivityDeadlineUtc">When the ticket becomes eligible for inactivity closure, while the timer runs.</param>
/// <param name="AwaitingCustomerReplyNote">Why a requested timer was not started, when it was not.</param>
/// <param name="ClosedByAi">True when THIS call closed the ticket because <c>closedByAi: true</c> was sent.</param>
/// <param name="ClosedByAiNote">Why <c>closedByAi: true</c> did not close the ticket (human involved, Call Center, already closed, …), when it did not.</param>
/// <param name="Classification">What applying the update's request type did (routing, assignment, SLA), when it carried one.</param>
public sealed record GenesysTicketUpdateResult(
    GenesysTicketUpdateOutcome Outcome,
    long? TicketId = null,
    string? TicketNumber = null,
    string? TicketStatus = null,
    bool ConversationEnded = false,
    int TranscriptMessageCount = 0,
    string? HandoffStatus = null,
    long? TicketAgentHandoffId = null,
    string? Detail = null,
    bool AwaitingCustomerReply = false,
    DateTime? InactivityDeadlineUtc = null,
    string? AwaitingCustomerReplyNote = null,
    GenesysClassificationResult? Classification = null,
    bool ClosedByAi = false,
    string? ClosedByAiNote = null)
{
    public static GenesysTicketUpdateResult Failure(GenesysTicketUpdateOutcome outcome, string? detail = null) =>
        new(outcome, Detail: detail);
}
