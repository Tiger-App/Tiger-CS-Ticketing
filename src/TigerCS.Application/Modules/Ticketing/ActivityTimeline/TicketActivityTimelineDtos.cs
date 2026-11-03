namespace TigerCS.Application.Modules.Ticketing.ActivityTimeline;

/// <summary>The words the timeline uses when evidence is missing — never an invented value.</summary>
public static class TimelineWording
{
    public const string Unknown = "Unknown";
    public const string NotRecorded = "Not recorded";
}

/// <summary>Who performed a timeline event. A fixed vocabulary so the UI can style it and tests can assert it.</summary>
public static class TimelineActorTypes
{
    public const string Ai = "AI";
    public const string Agent = "Agent";
    public const string Supervisor = "Supervisor";
    public const string System = "System";
    public const string Customer = "Customer";
    public const string Unknown = "Unknown";
}

/// <summary>Who closed a ticket (requirement: Human Agent / AI / System Automation / Supervisor).</summary>
public static class ClosedByTypes
{
    public const string HumanAgent = "Human Agent";
    public const string Ai = "AI";
    public const string SystemAutomation = "System Automation";
    public const string Supervisor = "Supervisor";
    public const string Unknown = "Unknown";
}

/// <summary>Stable codes for what the closure analysis concluded about human activity around a closure.</summary>
public static class ClosureAssessments
{
    /// <summary>No human reply or connection after the closure.</summary>
    public const string NoHumanActivityAfterClosure = "NoHumanActivityAfterClosure";

    /// <summary>Human activity was recorded after the closure, only in a conversation that started after the closure — a new session, not the closed one.</summary>
    public const string SeparateInteractionSession = "SeparateInteractionSession";

    /// <summary>A human was already connected / had already replied before the closure, but TigerCS only received the event after it (out-of-order or delayed delivery).</summary>
    public const string DelayedEvent = "DelayedEvent";

    /// <summary>A human replied in the very conversation the system had closed for inactivity; TigerCS had no record of any human at closure time.</summary>
    public const string PrematureClosure = "PrematureClosure";

    /// <summary>Human activity after closure that the stored evidence cannot classify.</summary>
    public const string Inconclusive = "Inconclusive";

    public const string NotApplicable = "NotApplicable";
}

/// <summary>
/// The closure facts shown on a "Ticket closed" event (requirement 2). Every field is derived from stored evidence; where evidence is
/// missing the value is <see cref="TimelineWording.Unknown"/> / <see cref="TimelineWording.NotRecorded"/>, never a guess.
/// </summary>
/// <param name="ClosedAtUtc">When the Closed transition was recorded.</param>
/// <param name="ClosedByType">Human Agent / AI / System Automation / Supervisor / Unknown.</param>
/// <param name="ClosedByName">The actual actor when recorded (a person's name), otherwise "Not recorded" (or the automation's name).</param>
/// <param name="Reason">The recorded reason, verbatim from the lifecycle history / audit entry, or "Not recorded".</param>
/// <param name="ReasonCode">CustomerInactivity, AiResolved, DocumentDelivery, ResolvedByAgent, ManualClose, or NotRecorded.</param>
/// <param name="EvidenceSource">Which store supplied the attribution (Audit entry / Lifecycle history note / Lifecycle history actor).</param>
/// <param name="CustomerInteractionEnded">"Yes", "No" or "Unknown" — whether the customer's conversation had already ended at closure time.</param>
/// <param name="CustomerInteractionEndedDetail">One line explaining that answer.</param>
/// <param name="HumanAgentConnected">"Yes", "No" or "Unknown" — whether a human agent was connected or responding at closure time.</param>
/// <param name="HumanAgentDetail">One line explaining that answer (names the agent when known).</param>
/// <param name="LastHumanActivityBeforeClosureUtc">The latest recorded human reply / connection at or before closure, or null.</param>
/// <param name="HumanActivityAfterClosure">True when a human replied or connected after this closure (and before any reopen).</param>
/// <param name="FirstHumanActivityAfterClosureUtc">When the first such activity was sent, or null.</param>
/// <param name="Assessment">One of <see cref="ClosureAssessments"/>.</param>
/// <param name="AssessmentDetail">Plain-language reasoning behind <paramref name="Assessment"/>.</param>
public sealed record TicketClosureDetailsDto(
    DateTime ClosedAtUtc,
    string ClosedByType,
    string ClosedByName,
    string Reason,
    string ReasonCode,
    string EvidenceSource,
    string CustomerInteractionEnded,
    string CustomerInteractionEndedDetail,
    string HumanAgentConnected,
    string HumanAgentDetail,
    DateTime? LastHumanActivityBeforeClosureUtc,
    bool HumanActivityAfterClosure,
    DateTime? FirstHumanActivityAfterClosureUtc,
    string Assessment,
    string AssessmentDetail);

/// <summary>
/// One row of the ticket Activity timeline. Timestamps are UTC; the UI converts them to Asia/Dubai. No description contains a clock
/// time, so no text can carry a different zone from the timestamp column.
/// </summary>
/// <param name="OccurredAtUtc">When the event happened (the source system's clock where it supplied one, otherwise TigerCS's recording time).</param>
/// <param name="RecordedAtUtc">When TigerCS recorded it, when that is known and differs materially from <paramref name="OccurredAtUtc"/> — a delayed / out-of-order event. Null otherwise.</param>
/// <param name="EventType">Stable machine code (e.g. "TicketClosed").</param>
/// <param name="EventName">Readable name (e.g. "Ticket closed").</param>
/// <param name="Category">Lifecycle, Ownership, Conversation, Handoff, Routing, Sla, Closure or Anomaly.</param>
/// <param name="ActorType">One of <see cref="TimelineActorTypes"/>.</param>
/// <param name="ActorName">A name, or "Unknown" / "Not recorded".</param>
/// <param name="Description">What happened.</param>
/// <param name="Reason">Why, when recorded.</param>
/// <param name="PreviousValue">The value before, where relevant.</param>
/// <param name="NewValue">The value after, where relevant.</param>
/// <param name="Source">The store the event came from (e.g. "TicketStatusHistory", "TicketInteractions", "AuditEntries").</param>
/// <param name="TimeNote">A caveat about the timestamp (e.g. "Connect time not supplied by Genesys; showing when TigerCS recorded it").</param>
/// <param name="TicketInteractionId">The interaction the event belongs to, when it belongs to one — the UI links to that conversation.</param>
/// <param name="Severity">"Info" or "Warning" (an anomaly worth a reader's attention).</param>
/// <param name="Closure">Closure facts, on "Ticket closed" events only.</param>
public sealed record TicketActivityEventDto(
    DateTime OccurredAtUtc,
    DateTime? RecordedAtUtc,
    string EventType,
    string EventName,
    string Category,
    string ActorType,
    string ActorName,
    string Description,
    string? Reason,
    string? PreviousValue,
    string? NewValue,
    string Source,
    string? TimeNote = null,
    long? TicketInteractionId = null,
    string Severity = "Info",
    TicketClosureDetailsDto? Closure = null);

/// <summary>
/// The full chronological audit trail of one ticket.
/// </summary>
/// <param name="TicketId">The ticket.</param>
/// <param name="TicketNumber">Its number.</param>
/// <param name="Events">Oldest first.</param>
/// <param name="Closures">Every closure analysed (also attached to their events) — newest last.</param>
/// <param name="EvidenceGaps">Plain-language notes on evidence that was missing, so a reader knows what the timeline could not say.</param>
public sealed record TicketActivityTimelineDto(
    long TicketId,
    string TicketNumber,
    IReadOnlyList<TicketActivityEventDto> Events,
    IReadOnlyList<TicketClosureDetailsDto> Closures,
    IReadOnlyList<string> EvidenceGaps);
