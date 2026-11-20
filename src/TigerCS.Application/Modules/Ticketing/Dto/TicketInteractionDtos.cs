namespace TigerCS.Application.Modules.Ticketing.Dto;

/// <summary>One message of an interaction's transcript, as Ticket Details renders it.</summary>
/// <param name="Sequence">Position within the transcript — the authoritative ordering.</param>
/// <param name="Sender">"Customer", "Agent" or "System".</param>
/// <param name="SenderName">The sender's display name, where the channel reported one.</param>
/// <param name="SentAtUtc">When the message was sent, in UTC.</param>
/// <param name="Body">The message text, verbatim.</param>
public sealed record TicketInteractionMessageDto(
    int Sequence,
    string Sender,
    string? SenderName,
    DateTime SentAtUtc,
    string Body);

/// <summary>
/// One interaction of a ticket's conversation history — the call or chat
/// itself plus, for text channels, its full transcript. Ordered
/// chronologically alongside the ticket's other interactions.
/// </summary>
/// <param name="TicketInteractionId">The interaction.</param>
/// <param name="IsOriginatingInteraction">True on the one interaction the ticket was created from.</param>
/// <param name="Source">"Genesys" or "Ticketing" — who produced the interaction context.</param>
/// <param name="ChannelId">The channel the interaction happened on.</param>
/// <param name="ChannelName">That channel's display name, resolved from channel configuration — still shown for a channel that has since been deactivated.</param>
/// <param name="CustomerPhone">The customer's phone number for this interaction, as captured.</param>
/// <param name="CustomerName">The customer's name as the channel reported it, where available.</param>
/// <param name="CustomerEmail">The customer's email as the channel reported it, where available.</param>
/// <param name="Direction">"Inbound"/"Outbound" as reported, where available.</param>
/// <param name="GenesysConversationId">The Genesys conversation this interaction records, or null for a locally-created (walk-in) interaction.</param>
/// <param name="GenesysQueueId">The Genesys queue the inquiry was routed to, where reported.</param>
/// <param name="GenesysQueueName">That queue's display name, where reported.</param>
/// <param name="AgentName">The handling agent's display name, where reported.</param>
/// <param name="AgentId">The handling agent's Genesys identifier, where reported.</param>
/// <param name="StartedAtUtc">When the interaction started on the Genesys side, where reported; otherwise when Ticketing recorded it.</param>
/// <param name="EndedAtUtc">When the conversation ended/disconnected, or null while it is still live (or was never reported ended).</param>
/// <param name="EndReason">Why it ended, as reported.</param>
/// <param name="Status">"Ended" once an end was recorded, otherwise "Active" — the INTERACTION's own lifecycle, which is independent of the ticket's status.</param>
/// <param name="Messages">The transcript, in order. Empty for a voice call, and for a chat with nothing recorded.</param>
/// <param name="Handoff">The pending human work raised from this interaction, when there is any — so an agent opening the ticket sees <b>why</b> a human was asked for and where that work stands, beside the transcript that led to it. Null when this interaction never needed a human.</param>
/// <param name="HandlingState">Who is handling a Genesys interaction right now: "Ai" (the virtual agent is the live, only participant), "AiEnded" (the AI conversation ended and no human need was recorded), "HumanFollowUp" (human follow-up is required and no person has accepted or replied - whether or not an employee has been provisionally assigned), "WaitingForHuman" (a human was explicitly requested through a handoff and nobody has taken it) or "Human" (a person took it, spoke, or — on the phone — answered). Null for an interaction Ticketing created itself. Independent of the ticket's status.</param>
/// <param name="HumanAccepted">A person actually accepted the interaction: a handoff taken, a recorded handler, or (phone) an answering agent. Assigning an employee for follow-up is NOT acceptance.</param>
/// <param name="HumanResponded">A person actually wrote to the customer: a <c>HumanAgent</c> line is in this ticket's own transcript. Assignment and acceptance are not a response.</param>
public sealed record TicketInteractionDto(
    long TicketInteractionId,
    bool IsOriginatingInteraction,
    string Source,
    byte ChannelId,
    string? ChannelName,
    string CustomerPhone,
    string? CustomerName,
    string? CustomerEmail,
    string? Direction,
    string? GenesysConversationId,
    string? GenesysQueueId,
    string? GenesysQueueName,
    string? AgentName,
    string? AgentId,
    DateTime StartedAtUtc,
    DateTime? EndedAtUtc,
    string? EndReason,
    string Status,
    IReadOnlyList<TicketInteractionMessageDto> Messages,
    AgentHandoffSummaryDto? Handoff = null,
    string? HandlingState = null,
    bool HumanAccepted = false,
    bool HumanResponded = false);

/// <summary>
/// The pending-human-work summary shown inside Ticket Details' Conversation
/// History. A compact view of <c>TicketAgentHandoff</c>: the work item's own
/// status is deliberately separate from the ticket's.
/// </summary>
/// <param name="TicketAgentHandoffId">The work item.</param>
/// <param name="Status">WaitingForAgent, Assigned, InProgress, Completed or Cancelled — never the ticket's status.</param>
/// <param name="Mode">Callback, ContinueChat, ReplyInChannel or HumanTakeover, or null when Genesys did not state it.</param>
/// <param name="RequestReason">Why a human was needed, as reported — the line an agent taking over a bot conversation most needs.</param>
/// <param name="RequestedAtUtc">When a human was first asked for.</param>
/// <param name="AssignedEmployeeId">The TigerCS employee handling it, when known.</param>
/// <param name="GenesysAgentId">Genesys' own agent identifier, when supplied.</param>
/// <param name="ResolvedAtUtc">When the work stopped being outstanding. Null while still pending.</param>
/// <param name="ResolutionNote">What was recorded on finishing, or the required reason for cancelling.</param>
/// <param name="Trigger">Who/what initiated the request, as Genesys stated it: CustomerRequestedHuman, AiEscalated, RoutingDecision (business rule), AiConnectionLost or AgentTransfer. Null when Genesys did not state it — shown as "not stated", never guessed.</param>
public sealed record AgentHandoffSummaryDto(
    long TicketAgentHandoffId,
    string Status,
    string? Mode,
    string? RequestReason,
    DateTime RequestedAtUtc,
    Guid? AssignedEmployeeId,
    string? GenesysAgentId,
    DateTime? ResolvedAtUtc,
    string? ResolutionNote,
    string? Trigger = null);

/// <summary>
/// A ticket's whole conversation history: every interaction it accumulated,
/// oldest first, each with its transcript. Read-only — nothing here changes
/// the ticket.
/// </summary>
/// <param name="TicketId">The ticket.</param>
/// <param name="Interactions">Every interaction, chronologically.</param>
public sealed record TicketInteractionHistoryDto(long TicketId, IReadOnlyList<TicketInteractionDto> Interactions);
