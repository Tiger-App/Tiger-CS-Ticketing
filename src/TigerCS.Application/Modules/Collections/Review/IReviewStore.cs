using TigerCS.Domain.Modules.Collections.Review;

namespace TigerCS.Application.Modules.Collections.Review;

/// <summary>A stored record together with its live "already dispatched" fact (looked up, never copied).</summary>
public sealed record ReviewRecordView(
    CollectionsReviewRecord Record, bool AlreadySent, string? PreviousDispatchStatus, DateTime? PreviousDispatchAtUtc)
{
    /// <summary>Stored status, adjusted for a stale run and for a previous dispatch.</summary>
    public ReviewValidationStatus EffectiveStatus(bool runStale) =>
        AlreadySent ? ReviewValidationStatus.AlreadySent
        : runStale && Record.ValidationStatus == ReviewValidationStatus.Ready ? ReviewValidationStatus.NeedsReview
        : Record.ValidationStatus;
}

/// <param name="Item">The uploaded dispatch item.</param>
/// <param name="ContactListId">The Genesys contact list it was uploaded to.</param>
/// <param name="FilterJson">The filter the dispatch was approved with (a Direct Send item is checked against its own filter).</param>
public sealed record UploadedContactView(CollectionsDispatchItem Item, string ContactListId, string? FilterJson = null);

public sealed record OverlapRow(string Phone, int CompanyId, string TenantId, string CustomerName, string UnitCode, string ReminderType,
    decimal? RemainingAmount, string Currency, string Reasons);

public sealed record OverlapGroup(string Phone, IReadOnlyList<OverlapRow> Rows);

public sealed record OverlapPage(int TotalGroups, IReadOnlyList<OverlapGroup> Groups);

public sealed record StoredPage(IReadOnlyList<ReviewRecordView> Items, int TotalCount);

/// <summary>Persistence port of the review and dispatch workflow. Implemented over EF Core; every filter, count and page runs in the database.</summary>
public interface IReviewStore
{
    // ---- runs
    Task<CollectionsReviewRun?> GetCurrentRunAsync(CancellationToken ct);
    Task<CollectionsReviewRun?> GetRunAsync(long runId, CancellationToken ct);
    Task<CollectionsReviewRun?> GetActiveRunAsync(CancellationToken ct);
    /// <summary>The newest run of any status (so a failure that is newer than the published run can be shown).</summary>
    Task<CollectionsReviewRun?> GetLatestRunAsync(CancellationToken ct);
    /// <summary>Adds a Queued run; false (and no row) when another run is already queued or running.</summary>
    Task<bool> TryAddRunAsync(CollectionsReviewRun run, CancellationToken ct);
    /// <summary>Atomic Queued to Running; false when another worker already claimed it.</summary>
    Task<bool> TryStartRunAsync(long runId, DateTime nowUtc, CancellationToken ct);
    Task UpdateRunProgressAsync(long runId, string phase, int percent, int? sourceRows, CancellationToken ct);
    /// <summary>Records on the run whether the financial source was reconciled (per the gate, for the companies the run covers) when its data was read.</summary>
    Task SetRunSourceReconciledAsync(long runId, bool reconciled, CancellationToken ct);
    Task FailRunAsync(long runId, string error, DateTime nowUtc, CancellationToken ct);
    Task AddRecordsAsync(long runId, IReadOnlyList<CollectionsReviewRecord> records, CancellationToken ct);
    /// <summary>Marks the run Completed and current, then prunes older runs' records, in one transaction.</summary>
    Task PublishRunAsync(long runId, int recordCount, int sourceRows, DateTime sourceReadAtUtc, DateTime nowUtc, int runsToKeep, CancellationToken ct);

    // ---- review queries (stale = the run's data is older than the freshness window)
    Task<StoredPage> QueryAsync(long runId, ReviewQuerySpec spec, bool stale, int skip, int take, CancellationToken ct);
    /// <summary>True when at least one record of the run carries a tower (data read before towers were stored has none).</summary>
    Task<bool> RunHasTowerDataAsync(long runId, CancellationToken ct);
    Task<ReviewCountsDto> CountAsync(long runId, ReviewQuerySpec specWithoutStatus, bool stale, CancellationToken ct);
    /// <summary>All Ready records matching the spec (Ready after the stale adjustment), ordered, capped at <paramref name="max"/> + 1.</summary>
    Task<IReadOnlyList<ReviewRecordView>> ResolveAsync(long runId, ReviewQuerySpec spec, IReadOnlyCollection<string>? keys, bool stale, int max, CancellationToken ct,
        IReadOnlyCollection<string>? unitKeys = null);

    // ---- dispatch (entities are returned detached; changes are written back with the Update methods)
    Task<CollectionsDispatch?> FindDispatchByKeyAsync(string idempotencyKey, CancellationToken ct);
    /// <summary>Persists the dispatch and items. False when a unique rule rejected it (same idempotency key, or a record already in a live dispatch).</summary>
    Task<bool> TryAddDispatchAsync(CollectionsDispatch dispatch, CancellationToken ct);
    Task<CollectionsDispatch?> GetDispatchAsync(Guid publicId, CancellationToken ct);
    Task<CollectionsDispatch?> GetDispatchByIdAsync(long dispatchId, CancellationToken ct);
    Task<IReadOnlyList<CollectionsDispatch>> ListDispatchesAsync(int take, CancellationToken ct);
    /// <summary>Atomically takes the execution lease (a Queued dispatch becomes Revalidating). False when another worker holds an unexpired lease or the dispatch is finished.</summary>
    Task<bool> TryAcquireLeaseAsync(long dispatchId, Guid owner, DateTime nowUtc, TimeSpan ttl, CancellationToken ct);
    Task<bool> RenewLeaseAsync(long dispatchId, Guid owner, DateTime nowUtc, TimeSpan ttl, CancellationToken ct);
    Task UpdateDispatchAsync(CollectionsDispatch dispatch, CancellationToken ct);
    /// <summary>Atomically Queued to Cancelled (only while no worker has taken the lease). False otherwise.</summary>
    Task<bool> TryCancelQueuedAsync(long dispatchId, string reason, DateTime nowUtc, CancellationToken ct);
    /// <summary>Sets every Approved item of the dispatch to Released so the records can be approved again.</summary>
    Task ReleaseItemsAsync(long dispatchId, string reason, CancellationToken ct);
    Task AddBatchesAsync(IReadOnlyList<CollectionsGenesysBatch> batches, CancellationToken ct);
    Task<CollectionsGenesysBatch?> GetBatchAsync(long batchId, CancellationToken ct);
    /// <summary>Atomic claim of a Pending batch for submission: increments the attempt and sets Submitting. False when it is no longer Pending.</summary>
    Task<bool> TryClaimBatchAsync(long batchId, DateTime nowUtc, CancellationToken ct);
    Task UpdateBatchAsync(CollectionsGenesysBatch batch, CancellationToken ct);
    Task<IReadOnlyList<CollectionsDispatchItem>> GetBatchItemsAsync(long batchId, CancellationToken ct);
    Task<IReadOnlyList<CollectionsDispatchItem>> GetItemsAsync(long dispatchId, CancellationToken ct);
    /// <summary>Uploaded contacts (with a Genesys id) uploaded since <paramref name="sinceUtc"/> that are not yet suppressed, oldest check first.</summary>
    Task<IReadOnlyList<UploadedContactView>> GetSuppressionCandidatesAsync(DateTime sinceUtc, int take, CancellationToken ct);
    /// <summary>Atomically takes the right to send the administrator report: only from None, Failed or NotConfigured (or a Sending claim older than <paramref name="staleBeforeUtc"/>) of a finished send. False when another worker has it or it was already Sent.</summary>
    Task<bool> TryClaimAdminEmailAsync(long dispatchId, DateTime nowUtc, DateTime staleBeforeUtc, CancellationToken ct);
    /// <summary>Records the report outcome. Touches only the AdminEmail* columns, never the Genesys status.</summary>
    Task RecordAdminEmailAsync(long dispatchId, AdminEmailStatus status, string? error, int recipientCount, DateTime nowUtc, CancellationToken ct);
    Task SetDispatchPhaseAsync(long dispatchId, string phase, CancellationToken ct);
    Task SetRevalidationMsAsync(long dispatchId, long ms, CancellationToken ct);
    /// <summary>Groups of records that share a phone across units or overlap reminder types today (blocked from dispatch).</summary>
    Task<OverlapPage> GetOverlapsAsync(long runId, int skip, int take, CancellationToken ct);
    Task UpdateItemsAsync(IReadOnlyCollection<CollectionsDispatchItem> items, CancellationToken ct);
}

/// <summary>Enqueues the explicit background jobs. The Hangfire implementation lives in Infrastructure.</summary>
public interface IReviewJobScheduler
{
    /// <summary>False when nothing will ever execute an enqueued job (background jobs switched off): a run must then fail visibly instead of waiting in Queued.</summary>
    bool IsAvailable => true;
    /// <summary>
    /// Whether a refresh of the review data can run. It can whenever background jobs run, and also when they are switched off but the refresh runs inside this process
    /// (<see cref="RefreshRunsInProcess"/>): a refresh is an explicit, authorized request, unlike a dispatch or the suppression sweep, which still need real background jobs.
    /// </summary>
    bool CanRunRefresh => IsAvailable;
    /// <summary>True when a refresh is started by a person while background jobs are off: it runs on a single bounded worker in this process (one at a time, with a cooldown) and never recurs.</summary>
    bool RefreshRunsInProcess => false;
    void EnqueueRefresh(long runId);
    void EnqueueDispatch(long dispatchId);
    void EnqueueSuppressionSweep();
}

/// <summary>Runs the body of one review refresh. Implemented by <see cref="ReviewRefreshService"/>; the in-process scheduler resolves it in a fresh scope.</summary>
public interface IReviewRefreshRunner
{
    Task RunAsync(long runId, CancellationToken ct);
}

/// <summary>One earlier file export as the audit trail holds it: when, and the audit text (counts, filter, hash and customer fingerprints; never a customer's data).</summary>
public sealed record GenesysExportAuditRecord(DateTime OccurredAtUtc, Guid? ActorEmployeeId, string? Details);

/// <summary>Read access to the audit entries of earlier file exports, so a repeat export of the same customers can be noticed before another file is made. Read only.</summary>
public interface IGenesysExportHistory
{
    /// <summary>The audit entries written by <paramref name="action"/> since <paramref name="sinceUtc"/>, newest first, at most <paramref name="take"/>.</summary>
    Task<IReadOnlyList<GenesysExportAuditRecord>> ListExportsAsync(string action, DateTime sinceUtc, int take, CancellationToken ct);
}

/// <summary>
/// One file export at a time across the application: the repeat check and its audit entry run inside the lock, so two simultaneous exports cannot both miss each other's entry. The lock is
/// held only for that step, not while the file is built.
/// </summary>
public interface IGenesysExportLock
{
    /// <summary>Waits up to <paramref name="wait"/> for the lock; disposing the handle releases it. Null when another export is still recording its audit entry.</summary>
    Task<IAsyncDisposable?> TryAcquireAsync(TimeSpan wait, CancellationToken ct);
}

public enum GenesysUploadOutcome
{
    /// <summary>Genesys accepted the batch; contact ids were returned.</summary>
    Accepted,
    /// <summary>Genesys definitely did not create the contacts (4xx, rate limit, auth failure, request never sent).</summary>
    Rejected,
    /// <summary>Timeout, connection loss or 5xx after the request may have reached Genesys. Contacts may exist.</summary>
    Unknown
}

public sealed record GenesysContactPayload(string ContactListId, string Phone, string CustomerName, string EmailAddress,
    string ReminderType, string AmountDue, string DueDate, bool Callable);

public sealed record GenesysUploadResult(GenesysUploadOutcome Outcome, int? HttpStatus, IReadOnlyList<string> ContactIds, string? Error);

/// <param name="Outcome">Accepted, Rejected (certainly unchanged) or Unknown.</param>
/// <param name="HttpStatus">The HTTP status, when a response arrived.</param>
/// <param name="Error">A safe description; never a token or response body.</param>
/// <param name="CallableAfter">What Genesys reports for the contact after the update; the update only counts when this is false.</param>
/// <param name="ContactMissing">Genesys answered 404: the contact no longer exists, so it cannot be dialled.</param>
public sealed record GenesysSuppressResult(GenesysUploadOutcome Outcome, int? HttpStatus, bool? CallableAfter, bool ContactMissing, string? Error);

/// <summary>A Genesys outbound campaign as far as this integration cares: its id, exact name, the contact list it dials and its status.</summary>
public sealed record GenesysCampaignRef(string Id, string Name, string? ContactListId, string? Status);

/// <summary>Every campaign Genesys returned for the name filter in <c>Matches</c>; callers still compare names exactly.</summary>
public sealed record GenesysCampaignLookupResult(GenesysUploadOutcome Outcome, int? HttpStatus, IReadOnlyList<GenesysCampaignRef> Matches, string? Error);

public sealed record GenesysCampaignCreateResult(GenesysUploadOutcome Outcome, int? HttpStatus, GenesysCampaignRef? Campaign, string? Error);

/// <summary>A Genesys contact list as far as this integration cares: the names of its columns (data columns and phone columns).</summary>
public sealed record GenesysContactListInfo(GenesysUploadOutcome Outcome, int? HttpStatus, IReadOnlyList<string> Columns, string? Error);

/// <summary>The organization the OAuth client belongs to (GET /api/v2/organizations/me): proves the token, the API region and which organization the configured contact list ids are looked up in.</summary>
public sealed record GenesysOrganizationInfo(GenesysUploadOutcome Outcome, int? HttpStatus, string? Id, string? Name, string? Error);

/// <summary>One outbound conversation (interaction) Genesys ran for a contact of a campaign. <c>ConversationId</c> is the Genesys interaction id.</summary>
public sealed record GenesysCampaignConversation(string ConversationId, string? ContactId, string? ContactListId, DateTime? StartedAtUtc);

/// <summary>Interactions of one campaign. <c>Complete</c> is false when the answer was cut short (too many pages): such a sync is never presented as complete.</summary>
public sealed record GenesysCampaignConversationsResult(GenesysUploadOutcome Outcome, int? HttpStatus, IReadOnlyList<GenesysCampaignConversation> Conversations, bool Complete, string? Error);

/// <summary>One message of a conversation with the status Genesys last reported. <c>Status</c> is the raw Genesys value (queued, sent, delivered, read, failed, ...), lower-cased.</summary>
public sealed record GenesysMessageInfo(string MessageId, string Status, DateTime? MessageTimeUtc, string? FailureReason);

public sealed record GenesysConversationMessagesResult(GenesysUploadOutcome Outcome, int? HttpStatus, IReadOnlyList<GenesysMessageInfo> Messages, string? Error);

public interface IGenesysOutboundClient
{
    /// <summary>
    /// READ ONLY. POST /api/v2/analytics/conversations/details/query filtered to one outbound campaign id: the interactions Genesys really ran for it in the interval. Needs the
    /// <c>analytics:conversationDetail:view</c> permission. Never writes to Genesys.
    /// </summary>
    Task<GenesysCampaignConversationsResult> GetCampaignConversationsAsync(string campaignId, DateTime fromUtc, DateTime toUtc, CancellationToken ct) =>
        Task.FromResult(new GenesysCampaignConversationsResult(GenesysUploadOutcome.Unknown, null, [], false, "This client cannot read campaign interactions."));

    /// <summary>READ ONLY. GET /api/v2/conversations/messages/{conversationId}: each message of the interaction with its last delivery status (needs <c>conversation:message:view</c>).</summary>
    Task<GenesysConversationMessagesResult> GetConversationMessagesAsync(string conversationId, CancellationToken ct) =>
        Task.FromResult(new GenesysConversationMessagesResult(GenesysUploadOutcome.Unknown, null, [], "This client cannot read conversation messages."));

    /// <summary>GET /api/v2/organizations/me (read only): which organization and region the configured credentials reach.</summary>
    Task<GenesysOrganizationInfo> GetOrganizationAsync(CancellationToken ct) =>
        Task.FromResult(new GenesysOrganizationInfo(GenesysUploadOutcome.Unknown, null, null, null, "This client cannot read the organization."));

    /// <summary>GET /api/v2/outbound/contactlists/{contactListId} (read only): the list's column names, to check that an upload will fit it.</summary>
    Task<GenesysContactListInfo> GetContactListAsync(string contactListId, CancellationToken ct) =>
        Task.FromResult(new GenesysContactListInfo(GenesysUploadOutcome.Unknown, null, [], "This client cannot read contact lists."));

    /// <summary>GET /api/v2/outbound/campaigns filtered to those that dial <paramref name="contactListId"/> (read only), with their status.</summary>
    Task<GenesysCampaignLookupResult> FindCampaignsByContactListAsync(string contactListId, CancellationToken ct) =>
        Task.FromResult(new GenesysCampaignLookupResult(GenesysUploadOutcome.Unknown, null, [], "This client cannot look up campaigns by contact list."));

    /// <summary>GET /api/v2/outbound/campaigns?name=... (read only: an Unknown outcome changed nothing).</summary>
    Task<GenesysCampaignLookupResult> FindCampaignsByNameAsync(string name, CancellationToken ct);

    /// <summary>POST /api/v2/outbound/campaigns with the exact <paramref name="name"/>, status off, and the template settings.</summary>
    Task<GenesysCampaignCreateResult> CreateCampaignAsync(string name, string contactListId, GenesysCampaignOptions template, CancellationToken ct);

    /// <summary>
    /// PUT /api/v2/outbound/contactlists/{contactListId}/contacts/{contactId} with callable=false and the contact's original data
    /// (the documented "Update a contact" operation; DELETE only removes contacts not in use by a campaign, so it is not relied on).
    /// </summary>
    Task<GenesysSuppressResult> SetNotCallableAsync(string contactListId, string contactId, GenesysContactPayload contact, CancellationToken ct);

    /// <summary>POST /api/v2/outbound/contactlists/{contactListId}/contacts for exactly one list and at most 1,000 contacts.</summary>
    Task<GenesysUploadResult> UploadContactsAsync(string contactListId, IReadOnlyList<GenesysContactPayload> contacts, CancellationToken ct);
}
