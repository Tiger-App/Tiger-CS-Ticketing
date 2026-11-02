namespace TigerCS.Application.Modules.Ticketing.Dto;

/// <summary>
/// One piece of pending human work as the agent work list and Ticket Details
/// see it.
/// </summary>
/// <param name="TicketAgentHandoffId">The work item.</param>
/// <param name="TicketId">The ticket the work belongs to — never a new one.</param>
/// <param name="TicketNumber">The ticket's human-facing number. Empty on a mutation response, which reports the work item rather than re-reading the ticket.</param>
/// <param name="TicketInteractionId">The interaction the work arose from.</param>
/// <param name="GenesysConversationId">The Genesys conversation behind that interaction, when it had one.</param>
/// <param name="DepartmentId">The department the work belongs to.</param>
/// <param name="ChannelId">The channel the customer used.</param>
/// <param name="Status">One of NotRequired, WaitingForAgent, Assigned, InProgress, Completed, Cancelled — TigerCS' own vocabulary, not Genesys'.</param>
/// <param name="Mode">Callback, ContinueChat, ReplyInChannel or HumanTakeover — or null when the caller did not say. Never inferred from the channel.</param>
/// <param name="RequestReason">Why a human was needed, as reported (e.g. a virtual agent's escalation reason). Null when not supplied.</param>
/// <param name="RequestedAtUtc">When a human was first asked for — what "waiting since" is measured from.</param>
/// <param name="AssignedEmployeeId">The TigerCS employee handling it, when known.</param>
/// <param name="GenesysAgentId">Genesys' own agent identifier, when supplied — recorded even when no TigerCS employee matches.</param>
/// <param name="AssignedAtUtc">When an agent was named.</param>
/// <param name="StartedAtUtc">When an agent began handling it.</param>
/// <param name="CompletedAtUtc">When the work was completed.</param>
/// <param name="ResolvedAtUtc">When the work stopped being outstanding, by completion or cancellation. Null exactly while it is still pending.</param>
/// <param name="ResolutionNote">What the agent recorded on finishing, or the required reason for cancelling.</param>
/// <param name="CustomerName">The customer's name as the channel collected it.</param>
/// <param name="CustomerPhone">The customer's number for this interaction.</param>
/// <param name="RequestSummary">The ticket's one-line summary. Empty on a mutation response.</param>
/// <param name="TicketStatus">The ticket's own status — separate from this work item's, and never changed by it. Empty on a mutation response.</param>
/// <param name="TicketIsClassified">False while the ticket is still Unclassified — pending human work never waits for classification.</param>
/// <param name="InteractionEnded">Whether the live conversation already ended. The work stays actionable either way: a customer who closed the browser still needs an answer.</param>
/// <param name="ExternalWorkItemId">Genesys' own routing-task/work-item id, when it supplies one. External identifier only.</param>
public sealed record AgentHandoffDto(
    long TicketAgentHandoffId,
    long TicketId,
    string TicketNumber,
    long TicketInteractionId,
    string? GenesysConversationId,
    int DepartmentId,
    byte ChannelId,
    string Status,
    string? Mode,
    string? RequestReason,
    DateTime RequestedAtUtc,
    Guid? AssignedEmployeeId,
    string? GenesysAgentId,
    DateTime? AssignedAtUtc,
    DateTime? StartedAtUtc,
    DateTime? CompletedAtUtc,
    DateTime? ResolvedAtUtc,
    string? ResolutionNote,
    string? CustomerName,
    string? CustomerPhone,
    string RequestSummary,
    string TicketStatus,
    bool TicketIsClassified,
    bool InteractionEnded,
    string? ExternalWorkItemId);

public sealed record AgentHandoffListResultDto(
    IReadOnlyList<AgentHandoffDto> Items, int TotalCount, int Page, int PageSize);

/// <summary>Filters for the agent work list. All optional.</summary>
public sealed record AgentHandoffListRequestDto(
    int? DepartmentId = null,
    byte? ChannelId = null,
    Guid? AssignedEmployeeId = null,
    bool UnassignedOnly = false,
    bool IncludeResolved = false,
    int Page = 1,
    int PageSize = 50);

/// <summary>Finishing a piece of human work. The note is optional here and required when cancelling.</summary>
public sealed record CompleteAgentHandoffRequestDto(string? ResolutionNote = null);

/// <summary>Cancelling pending human work — a reason is required, because work is never dropped without a recorded why.</summary>
public sealed record CancelAgentHandoffRequestDto(string Reason);

/// <summary>What an agent-facing handoff operation did.</summary>
public enum AgentHandoffOutcome
{
    Success,

    NotFound,

    /// <summary>The caller may not act on work in this department.</summary>
    Forbidden,

    /// <summary>The work was already Completed or Cancelled — a redelivered or double-clicked action, treated as already done rather than as a failure.</summary>
    AlreadyResolved,

    /// <summary>Cancellation was attempted without a reason.</summary>
    ReasonRequired,

    /// <summary>
    /// Another agent already holds this work. Deliberately distinct from
    /// <see cref="Success"/>: the claim is exclusive, and answering a second
    /// agent with success — carrying the first agent's assignee — is how two
    /// people end up calling the same customer.
    /// </summary>
    AlreadyClaimed,

    /// <summary>
    /// The ticket moved between this request's read and its commit (its own
    /// RowVersion, or the handoff's, no longer matched). Nothing was written;
    /// the caller re-reads and retries.
    /// </summary>
    ConcurrencyConflict,

    /// <summary>
    /// The ticket is already owned by another person (not an automatic pre-assignment). Accepting would silently take it from them, so it is
    /// refused: a ticket has one owner, and a supervisor reassigns it deliberately (Assign/Transfer, which is audited).
    /// </summary>
    TicketOwnedByAnotherEmployee,

    /// <summary>
    /// The accepting employee is not an active member of the ticket's current
    /// department, so accepting would have to assign the ticket to a
    /// non-member — which MVP-API-Contracts.md §3.5 forbids and
    /// <c>TicketAssignmentAppService.AssignAsync</c> already refuses as
    /// <c>EmployeeNotInDepartment</c>.
    ///
    /// <para>
    /// <b>Checked before anything is claimed.</b> Accepting is one operation:
    /// claim the work, own the ticket, start it. A caller who cannot complete
    /// all three must change nothing, or the system ends up in the state this
    /// outcome exists to prevent — handoff InProgress while the ticket sits
    /// Open and unowned, which reads as "someone is on it" to the queue and
    /// "nobody owns this" to the ticket.
    /// </para>
    ///
    /// <para>
    /// <b>Not an authorization failure</b>, and deliberately distinct from
    /// <see cref="Forbidden"/>: the caller may well be entitled to see and act
    /// on this department's work (the CS layer is cross-department for
    /// visibility). What they cannot do is become the ticket's owner. The
    /// route to handling it is the existing Department Transfer, after which a
    /// member of the new current department accepts.
    /// </para>
    ///
    /// <para>
    /// <b>The ADR-0024 override does not reach this.</b> The override answers
    /// "may this caller act at all"; department membership of the assignee is
    /// a domain invariant about the ticket's data, not a permission. A System
    /// Administrator may therefore see and administer work anywhere and still
    /// not assign a ticket to themselves in a department they do not belong
    /// to.
    /// </para>
    /// </summary>
    AgentNotInTicketDepartment,

    /// <summary>
    /// The ticket behind this work is Closed, so it can take neither an owner
    /// nor a status change (closed-ticket immutability). Refused rather than
    /// claiming the work and leaving it attached to a ticket nothing can move
    /// — stand the work down instead.
    /// </summary>
    TicketClosed
}

/// <param name="Outcome">What happened.</param>
/// <param name="Handoff">The work item, on success.</param>
/// <param name="HolderEmployeeId">On <see cref="AgentHandoffOutcome.AlreadyClaimed"/>, the agent who holds it — so the refusal can name them rather than being a mystery.</param>
/// <param name="ClaimedAtUtc">On <see cref="AgentHandoffOutcome.AlreadyClaimed"/>, when they took it.</param>
public sealed record AgentHandoffResult(
    AgentHandoffOutcome Outcome,
    AgentHandoffDto? Handoff = null,
    Guid? HolderEmployeeId = null,
    DateTime? ClaimedAtUtc = null)
{
    public static AgentHandoffResult Success(AgentHandoffDto handoff) => new(AgentHandoffOutcome.Success, handoff);
    public static AgentHandoffResult Failure(AgentHandoffOutcome outcome) => new(outcome);

    /// <summary>Refused because another agent holds the work, naming the holder.</summary>
    public static AgentHandoffResult AlreadyClaimed(Guid holderEmployeeId, DateTime claimedAtUtc) =>
        new(AgentHandoffOutcome.AlreadyClaimed, Handoff: null, holderEmployeeId, claimedAtUtc);
}
