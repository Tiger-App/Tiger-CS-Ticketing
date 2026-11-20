namespace TigerCS.Application.Modules.GenesysIntegration.Dto;

/// <summary>
/// Genesys reporting that an interaction needs a human agent — on any
/// channel.
///
/// <para>
/// This is <b>not</b> a callback request. The same event covers a caller who
/// reached an IVR, a website chat a bot could not finish, a WhatsApp thread
/// that needs a person, and a social-media message a human must answer. What
/// differs between them is <see cref="Mode"/>, and even that is only recorded
/// because the caller stated it — TigerCS never derives it from the channel.
/// </para>
/// </summary>
/// <param name="ConversationId">The conversation needing a human. Its ticket and interaction already exist; this never creates either.</param>
/// <param name="AgentAvailable">
/// True when Genesys is handing straight over to a human (a live transfer),
/// false when nobody is available and the work must wait. When true and an
/// agent is named, the work item is recorded as Assigned rather than waiting.
/// </param>
/// <param name="Mode">"Callback", "ContinueChat", "ReplyInChannel" or "HumanTakeover" — omit when Genesys has not stated how the channel continues.</param>
/// <param name="Reason">Why a human is needed (a virtual agent's escalation reason, a routing note). Free text: no vocabulary has been confirmed.</param>
/// <param name="Trigger">Why a human is needed, typed: "CustomerRequestedHuman", "AiConnectionLost", "AiEscalated", "RoutingDecision" or "AgentTransfer". Omit when Genesys does not state it — never derived from the end reason or the channel.</param>
/// <param name="AgentId">Genesys' own agent identifier, when one is already handling it.</param>
/// <param name="AgentName">The agent's display name, when supplied — recorded on the interaction.</param>
/// <param name="WorkItemId">Genesys' own routing-task/work-item identifier, if it supplies one. Used as the stronger idempotency key when present; never invented.</param>
/// <param name="RequestedAtUtc">When Genesys decided a human was needed. Defaults to now when not supplied.</param>
/// <param name="TicketId">The ticket the human work belongs to, when the conversation carries several tickets. Absent resolves the conversation's default (first) ticket.</param>
public sealed record GenesysHandoffRequestDto(
    string ConversationId,
    bool AgentAvailable = false,
    string? Mode = null,
    string? Reason = null,
    string? Trigger = null,
    string? AgentId = null,
    string? AgentName = null,
    string? WorkItemId = null,
    DateTime? RequestedAtUtc = null,
    long? TicketId = null);

/// <summary>Genesys standing the pending human work down — the human is no longer needed.</summary>
/// <param name="ConversationId">The conversation whose outstanding work this stands down.</param>
/// <param name="Reason">Why the human is no longer needed. Required — pending customer work is never dropped without a recorded why.</param>
/// <param name="TicketId">The ticket whose work this stands down, when the conversation carries several tickets.</param>
public sealed record GenesysHandoffCancellationDto(string ConversationId, string? Reason = null, long? TicketId = null);

/// <summary>Genesys reporting that an agent has (or has not) taken the pending work.</summary>
/// <param name="ConversationId">The conversation whose pending work this concerns.</param>
/// <param name="AgentId">Genesys' agent identifier. Required — an assignment must name someone.</param>
/// <param name="AgentName">The agent's display name, when supplied.</param>
/// <param name="AssignedAtUtc">When the assignment happened. Defaults to now.</param>
/// <param name="TicketId">The ticket whose work this concerns, when the conversation carries several tickets.</param>
public sealed record GenesysHandoffAssignmentDto(
    string ConversationId,
    string? AgentId = null,
    string? AgentName = null,
    DateTime? AssignedAtUtc = null,
    long? TicketId = null);

public enum GenesysHandoffOutcome
{
    /// <summary>A new pending work item was recorded for this conversation.</summary>
    HandoffRecorded,

    /// <summary>This conversation already had outstanding human work — the existing item is returned and nothing was created. The idempotent answer.</summary>
    AlreadyRequested,

    /// <summary>An existing work item's assignment was updated.</summary>
    AssignmentRecorded,

    IntegrationDisabled,
    ConversationIdRequired,

    /// <summary>No interaction exists for this conversation — it never produced a ticket (a call that rang and was never answered).</summary>
    ConversationNotFound,

    /// <summary>No outstanding human work exists for this conversation to assign.</summary>
    NoOpenHandoff,

    /// <summary>The supplied mode is not one of TigerCS' normalized values.</summary>
    InvalidMode,

    /// <summary>The supplied trigger is not one of TigerCS' normalized values.</summary>
    InvalidTrigger,

    /// <summary>An assignment named nobody.</summary>
    AgentRequired,

    /// <summary>Outstanding human work was stood down — Genesys reported the human is no longer needed (the AI resumed, the customer left, the case went away).</summary>
    HandoffCancelled,

    /// <summary>
    /// The work item was already Completed or Cancelled, so there was nothing
    /// to stand down. The idempotent answer to a redelivered cancellation —
    /// the same convention <c>AlreadyRequested</c> uses for a redelivered
    /// request, and the reason a repeated stand-down is safe rather than an
    /// error.
    /// </summary>
    AlreadyResolved,

    /// <summary>A stand-down named no reason. Pending customer work is never dropped without a recorded why.</summary>
    ReasonRequired,

    /// <summary>The ticket is Closed: closed is terminal, so human follow-up is never raised on it (nothing was written).</summary>
    TicketClosed
}

public sealed record GenesysHandoffResult(
    GenesysHandoffOutcome Outcome,
    long? TicketAgentHandoffId = null,
    long? TicketId = null,
    string? TicketNumber = null,
    string? Status = null,
    string? Detail = null)
{
    public static GenesysHandoffResult Failure(GenesysHandoffOutcome outcome, string? detail = null) =>
        new(outcome, Detail: detail);
}
