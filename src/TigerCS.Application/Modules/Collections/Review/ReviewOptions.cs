using TigerCS.Domain.Modules.Collections.Review;

namespace TigerCS.Application.Modules.Collections.Review;

/// <summary>Settings of the stored review data and its approval flow (<c>Collections:Review</c>).</summary>
public sealed class CollectionsReviewOptions
{
    public const string SectionName = "Collections:Review";

    /// <summary>Default for the editable "minimum remaining amount" filter (AED).</summary>
    public decimal DefaultMinimumRemaining { get; set; } = 100m;

    /// <summary>The largest list one confirmation may approve. A bigger selection must be split.</summary>
    public int MaxSelection { get; set; } = 20000;

    /// <summary>The refresh job's overall read budget (the PACT read has its own SQL timeout).</summary>
    public int RefreshTimeoutMinutes { get; set; } = 15;

    /// <summary>A run still "Running" after this long is treated as dead and may be replaced.</summary>
    public int StuckRunMinutes { get; set; } = 30;

    /// <summary>A run still "Queued" after this long was never picked up by a worker (jobs off, server down): it is marked Failed so the page offers a retry.</summary>
    public int QueuedTimeoutMinutes { get; set; } = 5;

    /// <summary>
    /// Review records written per database round trip group (one transaction each). Small enough that a slow or flaky link never has to carry thousands of wide rows in one
    /// statement batch, large enough to keep the number of transactions low. 1-2000.
    /// </summary>
    public int PersistBatchSize { get; set; } = 500;

    /// <summary>Attempts for a database write that failed with a transient connection error (1 = no retry). Only idempotent writes are retried. 1-5.</summary>
    public int PersistMaxAttempts { get; set; } = 3;

    /// <summary>Floating-point noise tolerance in AED when normalizing a floating source value to fils.</summary>
    public decimal FloatNoiseTolerance { get; set; } = MoneyNormalizer.DefaultFloatTolerance;

    /// <summary>
    /// With background jobs off, a refresh a person starts runs inside the API process. To keep a double click, or several users, from reading the financial source over and over, a refresh is
    /// not started again within this many seconds of the last published one for today (the page says when it can be repeated). 0 turns the cooldown off. Applies only to in-process refreshes.
    /// </summary>
    public int InProcessRefreshCooldownSeconds { get; set; } = 60;

    /// <summary>
    /// A file export (Export for Genesys) does not mark anyone as sent. To keep the same customers from being exported over and over unnoticed, an export that contains customers exported within this many
    /// days is held until the person confirms it (the page says who was exported when). The default is 30 days. A customer last exported further back than this is not warned about. 0 turns the check off. 1-365.
    /// </summary>
    public int ExportRepeatLookbackDays { get; set; } = 30;

    /// <summary>Review records of runs older than the newest N completed runs are deleted.</summary>
    public int RunsToKeep { get; set; } = 2;

    /// <summary>
    /// The most customers one Manual list can hold. A list that needs more than one pass to read is refused, never cut short: a silently partial list would under-report and under-count.
    /// </summary>
    public int ManualListCapacity { get; set; } = 100_000;
}

/// <summary>
/// Genesys Cloud outbound upload settings (<c>Collections:GenesysOutbound</c>). The client id and secret are secrets:
/// supply them through user-secrets or environment variables, never a committed file, and they are never logged.
/// </summary>
public sealed class GenesysOutboundOptions
{
    public const string SectionName = "Collections:GenesysOutbound";
    public const int MaxBatchSize = 1000;

    /// <summary>False (default): nothing can be uploaded. Confirmation, revalidation and batching still run in tests.</summary>
    public bool Enabled { get; set; }
    public string LoginBaseUrl { get; set; } = "https://login.mypurecloud.de";
    public string ApiBaseUrl { get; set; } = "https://api.mypurecloud.de";
    public string? ClientId { get; set; }
    public string? ClientSecret { get; set; }
    public int BatchSize { get; set; } = MaxBatchSize;
    public int RequestTimeoutSeconds { get; set; } = 60;
    /// <summary>Refresh the cached token this long before its stated expiry.</summary>
    public int TokenExpirySkewSeconds { get; set; } = 60;

    /// <summary>
    /// Master switch for uploading approved customers to the contact lists. False (default): confirmation is refused. Turn on only for a
    /// UAT environment whose <c>ContactListIds</c> point at TEST lists with no active campaign, and for production only after the paid-after-upload
    /// suppression has been proven end to end on a test list (see docs).
    /// </summary>
    public bool LiveCustomerDispatchEnabled { get; set; }

    /// <summary>Runs the sweep that marks uploaded contacts not callable (PUT callable=false) when their balance was paid or changed. Required for dispatch.</summary>
    public bool SuppressionEnabled { get; set; }
    public string SuppressionSweepCron { get; set; } = "*/10 * * * *";
    /// <summary>Uploaded contacts older than this are no longer swept (the month's cycle has ended).</summary>
    public int SuppressionWindowDays { get; set; } = 3;
    /// <summary>Genesys rate limits apply per OAuth client; one PUT per contact.</summary>
    public int MaxSuppressionsPerSweep { get; set; } = 300;

    /// <summary>How often a running dispatch renews its execution lease while it waits on the slow financial source. Must stay well below the 5-minute lease.</summary>
    public double LeaseHeartbeatSeconds { get; set; } = 60;

    /// <summary>Reminder type to Genesys contact list id. Defaults are the lists supplied for this integration.</summary>
    public Dictionary<string, string> ContactListIds { get; set; } = new(StringComparer.OrdinalIgnoreCase)
    {
        [nameof(CampaignReminderType.CurrentMonth)] = "79e5ae74-ea6e-4941-b76d-45ddf487d8d1",
        [nameof(CampaignReminderType.FollowUp)] = "3a91c06e-47ab-4a5e-a720-004bd5cf5bba",
        [nameof(CampaignReminderType.LegalCase)] = "41178d2a-af65-45ae-9a33-b50260dc1b9b",
        [nameof(CampaignReminderType.LegalNotice)] = "372d81d7-6d2d-4b9e-8f09-cf2262aecfdf",
        [nameof(CampaignReminderType.Overdue)] = "d5f4d808-2e12-410e-8fe6-b810cca1ef4c"
    };

    /// <summary>Optional override of the ReminderType text written to Genesys; unset types use <see cref="CampaignReminderTypes.DefaultLabel"/>.</summary>
    public Dictionary<string, string> ReminderTypeLabels { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public string ContactListIdFor(CampaignReminderType type) =>
        ContactListIds.TryGetValue(type.ToString(), out var id) && Guid.TryParse(id, out var parsed)
            ? parsed.ToString("D")
            : throw new InvalidOperationException($"No valid Genesys contact list id is configured for {type}.");

    /// <summary>
    /// The Genesys contact list id a Manual send of <paramref name="type"/> uploads to, from the existing <see cref="ContactListIds"/>: the campaign type's own list, or the one the person chose
    /// among the lists that suit the type (<see cref="CampaignReminderTypes.ManualDestinations"/>). Throws <see cref="InvalidOperationException"/> with the reason otherwise.
    /// </summary>
    public string ManualContactListIdFor(CampaignReminderType type, string? requested)
    {
        if (!CampaignReminderTypes.TryResolveManualDestination(type, requested, out var destination))
            throw new InvalidOperationException($"A {CampaignReminderTypes.DefaultLabel(type)} campaign cannot be sent to that contact list. Choose {string.Join(", ", CampaignReminderTypes.ManualDestinations(type).Select(CampaignReminderTypes.DefaultLabel))}.");
        return ContactListIdFor(destination);
    }

    public string LabelFor(CampaignReminderType type) =>
        ReminderTypeLabels.TryGetValue(type.ToString(), out var label) && !string.IsNullOrWhiteSpace(label)
            ? label.Trim() : CampaignReminderTypes.DefaultLabel(type);

    public int EffectiveBatchSize => Math.Clamp(BatchSize, 1, MaxBatchSize);

    /// <summary>Campaign lookup/creation (<c>Collections:GenesysOutbound:Campaign</c>). Off by default: contacts are then uploaded to the lists exactly as before.</summary>
    public GenesysCampaignOptions Campaign { get; set; } = new();
}

/// <summary>
/// Genesys campaign handling. When <see cref="Enabled"/>, every upload batch first resolves a campaign named exactly like its stage
/// (<see cref="CampaignReminderTypes.StageName(CampaignReminderType)"/>): an existing campaign with that name is reused, never duplicated;
/// otherwise one is created (status off) from this template when <see cref="AllowCreate"/> is set.
/// </summary>
public sealed class GenesysCampaignOptions
{
    public bool Enabled { get; set; }
    /// <summary>False: only an existing campaign may be used; a missing one fails the batch before any upload.</summary>
    public bool AllowCreate { get; set; }
    /// <summary>Required to create: the queue agents/calls are routed to (Genesys queue id).</summary>
    public string? QueueId { get; set; }
    /// <summary>Required to create: e.g. preview, power, predictive, progressive or agentless.</summary>
    public string? DialingMode { get; set; }
    /// <summary>Required to create: the caller name shown to the customer.</summary>
    public string? CallerName { get; set; }
    /// <summary>Required to create: the caller number (E.164).</summary>
    public string? CallerAddress { get; set; }
    public string? ScriptId { get; set; }
    public string? CallAnalysisResponseSetId { get; set; }
    public double? AbandonRate { get; set; }

    /// <summary>The settings still missing for creation, by name; empty when a campaign can be created.</summary>
    public IReadOnlyList<string> MissingForCreate() => new[]
    {
        (nameof(QueueId), QueueId), (nameof(DialingMode), DialingMode), (nameof(CallerName), CallerName), (nameof(CallerAddress), CallerAddress)
    }.Where(p => string.IsNullOrWhiteSpace(p.Item2)).Select(p => p.Item1).ToList();
}
