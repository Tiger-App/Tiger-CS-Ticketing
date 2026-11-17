namespace TigerCS.Application.Modules.QueueingIntegration;

/// <summary>What happens when a queue ticket type has no usable request-type mapping.</summary>
public enum QueueingMissingRequestTypePolicy
{
    /// <summary>The event is kept unresolved and retried; no ticket is created until the mapping exists. Nothing is guessed. (Default.)</summary>
    Hold = 0,

    /// <summary>The ticket is created in the company's department with the department's existing default General classification, and the gap is audited.</summary>
    DefaultClassification = 1
}

/// <summary>What happens when a person changes a queue-managed ticket's assignment, status or closure in TigerCS.</summary>
public enum QueueingManualChangePolicy
{
    /// <summary>Refused with a clear message: the Queueing System is the source of truth for assignment, status and closure. Comments, notes and attachments stay open. (Default.)</summary>
    Block = 0,

    /// <summary>Allowed, but it is only a local edit: the next Queueing System event overwrites it. Use only while the Queueing System cannot yet be driven from the ticket.</summary>
    Allow = 1
}

/// <summary>How an external employee id is turned into a TigerCS employee.</summary>
public enum QueueingUserLookupMode
{
    /// <summary>Through the <c>QueueingUserMappings</c> table (queue user id -> TigerCS employee). Reliable and administered. (Default.)</summary>
    Mapping = 0,

    /// <summary>The Queueing System's user id IS the TigerCS employee id (a GUID). For an environment that provisions both from one directory.</summary>
    EmployeeId = 1
}

/// <summary>
/// Settings of the Queueing System receiving API (<c>QueueingIntegration</c> section). A plain value in the Application layer, bound by the
/// composition root. No credential lives here: the API keys are verified by the Api layer from hashed configuration.
/// </summary>
public sealed class QueueingIntegrationOptions
{
    public const string SectionName = "QueueingIntegration";

    /// <summary>The feature flag. False (default): the endpoint answers 503 and nothing is processed.</summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// The TigerCS employee the integration acts as (ticket creator, integration audit actor). Never the assignee of a ticket: a ticket is only ever
    /// assigned to the real employee the Queueing System names. Must be a configured service identity (<c>Authorization:ServiceIdentity:EmployeeIds</c>).
    /// </summary>
    public Guid? ServiceAccountEmployeeId { get; set; }

    /// <summary>The code of the channel queue tickets are recorded on - the existing Walk in / Kiosk channel.</summary>
    public string ChannelCode { get; set; } = "WALK_IN_KIOSK";

    /// <summary>The starting priority of a queue ticket, by name (resolved from the Priorities table, never an id). Same convention as <c>Genesys:DefaultTicketPriority</c>.</summary>
    public string DefaultTicketPriority { get; set; } = "Normal";

    public QueueingUserLookupMode UserLookupMode { get; set; } = QueueingUserLookupMode.Mapping;

    public QueueingMissingRequestTypePolicy MissingRequestTypePolicy { get; set; } = QueueingMissingRequestTypePolicy.Hold;

    public QueueingManualChangePolicy ManualChangePolicy { get; set; } = QueueingManualChangePolicy.Block;

    /// <summary>How many times an unresolved event is retried before it is marked Failed (needs a person; a redelivery restarts it).</summary>
    public int MaxRetryAttempts { get; set; } = 12;

    /// <summary>The first retry delay; each further attempt doubles it, capped at <see cref="MaxRetryDelaySeconds"/>.</summary>
    public int RetryBaseDelaySeconds { get; set; } = 30;

    public int MaxRetryDelaySeconds { get; set; } = 3600;

    /// <summary>An event stamped further in the future than this (clock skew allowance) is refused as invalid.</summary>
    public int MaxClockSkewSeconds { get; set; } = 300;

    /// <summary>The longest <c>Comment</c> accepted.</summary>
    public int MaxCommentLength { get; set; } = 4000;
}
