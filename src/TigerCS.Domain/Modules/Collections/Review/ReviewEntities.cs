namespace TigerCS.Domain.Modules.Collections.Review;

public enum ReviewRunStatus { Queued = 1, Running = 2, Completed = 3, Failed = 4 }

/// <summary>
/// One explicit refresh of the review data: the financial source is read once, every record is validated and stored, and
/// the review screen then queries these stored rows. The page never reads PACT.
/// </summary>
public class CollectionsReviewRun
{
    public long CollectionsReviewRunId { get; set; }
    public ReviewRunStatus Status { get; set; }
    public DateOnly AsOfDate { get; set; }
    public int? CompanyId { get; set; }
    public DateOnly DueFrom { get; set; }
    public DateOnly DueTo { get; set; }
    public string Source { get; set; } = "";
    public string SourceProcedureSuffix { get; set; } = "";
    public bool SourceReconciled { get; set; }
    public Guid RequestedByEmployeeId { get; set; }
    public DateTime RequestedAtUtc { get; set; }
    public DateTime? StartedAtUtc { get; set; }
    public DateTime? CompletedAtUtc { get; set; }
    /// <summary>When the financial source was actually read (the "last refresh" users see).</summary>
    public DateTime? SourceReadAtUtc { get; set; }
    public string Phase { get; set; } = "Queued";
    public int ProgressPercent { get; set; }
    public int SourceRowCount { get; set; }
    public int RecordCount { get; set; }
    public string? Error { get; set; }
    public bool IsCurrent { get; set; }
    /// <summary>True while Queued or Running; a unique filtered index allows only one active run at a time.</summary>
    public bool IsActive { get; set; }
}

/// <summary>One unit and one reminder type: the grain at which amounts are quoted and contacts are created. Balances of different units are never merged.</summary>
public class CollectionsReviewRecord
{
    public long CollectionsReviewRecordId { get; set; }
    public long CollectionsReviewRunId { get; set; }
    /// <summary>SHA-256 of company, tenant, unit and monthly stage cycle: the stable identity used to detect repeat sends.</summary>
    public string RecordKey { get; set; } = "";
    public string CycleKey { get; set; } = "";
    public CampaignReminderType ReminderType { get; set; }
    public int CompanyId { get; set; }
    public string TenantId { get; set; } = "";
    public string CustomerName { get; set; } = "";
    public string Phone { get; set; } = "";
    public string Email { get; set; } = "";
    public int? UnitId { get; set; }
    public string UnitCode { get; set; } = "";
    public string ProjectCode { get; set; } = "";
    /// <summary>The unit's tower (local CollectionsTowers.TowerId) as read from the source: the structured Project / Tower filter. Null on data read before towers were stored.</summary>
    public int? TowerId { get; set; }
    public string? TowerNumber { get; set; }
    public string? TowerName { get; set; }
    public ReviewPaymentStatus PaymentStatus { get; set; }
    public string SourceStatus { get; set; } = "";
    public decimal? RemainingAmount { get; set; }
    public decimal? RawRemainingAmount { get; set; }
    public string Currency { get; set; } = "AED";
    public DateOnly? DueDate { get; set; }
    public int InstalmentCount { get; set; }
    public ReviewValidationStatus ValidationStatus { get; set; }
    /// <summary>Semicolon separated reason codes (needs-review, exclusions and warnings).</summary>
    public string Reasons { get; set; } = "";
    public DateTime SourceReadAtUtc { get; set; }
    public string Source { get; set; } = "";
    /// <summary>
    /// The customer's unpaid instalments (due date and remaining amount, <see cref="FilteredAmounts"/>) as read from the financial source, on every stage record of the unit. A Manual send
    /// calculates the amount it lists, approves and sends from these and the person's filters (Payment status, From / To, Minimum Total); <see cref="RemainingAmount"/> stays the stage amount
    /// that Auto uses. Null on records written before this column existed (a refresh fills it); a Manual send cannot calculate an amount for such a record.
    /// </summary>
    public string? InstalmentsJson { get; set; }
}

public enum DispatchStatus
{
    Queued = 1,
    Revalidating = 2,
    Sending = 3,
    Completed = 4,
    CompletedWithErrors = 5,
    ReviewRequired = 6,
    Failed = 7,
    Cancelled = 8
}

/// <summary>An approved, frozen list of records to upload to Genesys, with who approved it and what was approved.</summary>
public class CollectionsDispatch
{
    public long CollectionsDispatchId { get; set; }
    public Guid PublicId { get; set; } = Guid.NewGuid();
    public string IdempotencyKey { get; set; } = "";
    /// <summary>Hash of the approved list (record keys, amounts, contacts). Confirming a different list is refused.</summary>
    public string Fingerprint { get; set; } = "";
    public DispatchStatus Status { get; set; }
    public Guid InitiatedByEmployeeId { get; set; }
    public DateTime InitiatedAtUtc { get; set; }
    public long ReviewRunId { get; set; }
    public string SelectionMode { get; set; } = "";
    public string FilterJson { get; set; } = "";
    public int ApprovedCount { get; set; }
    /// <summary>JSON object of totals by currency at approval, e.g. {"AED":"1200.50"}.</summary>
    public string ApprovedTotalsJson { get; set; } = "{}";
    public bool AcknowledgedActiveCampaignRisk { get; set; }
    public DateTime? StartedAtUtc { get; set; }
    public DateTime? CompletedAtUtc { get; set; }
    public string? StatusReason { get; set; }
    public int ExcludedAtDispatchCount { get; set; }
    /// <summary>Human-readable progress of the running job ("Reading company 4", "Uploading batch 2 of 3").</summary>
    public string? Phase { get; set; }
    /// <summary>How long the pre-send revalidation took (source reads + validation), for capacity planning.</summary>
    public long? RevalidationMs { get; set; }
    /// <summary>Held by the worker that is revalidating/sending; an expired lease means that worker died and another may continue.</summary>
    public Guid? LeaseOwner { get; set; }
    public DateTime? LeaseExpiresAtUtc { get; set; }
    public AdminEmailStatus AdminEmailStatus { get; set; }
    public DateTime? AdminEmailAttemptedAtUtc { get; set; }
    public int AdminEmailAttempts { get; set; }
    public int AdminEmailRecipientCount { get; set; }
    /// <summary>Safe diagnostic only (never an address or message body).</summary>
    public string? AdminEmailError { get; set; }
    public List<CollectionsDispatchItem> Items { get; set; } = [];
    public List<CollectionsGenesysBatch> Batches { get; set; } = [];
}

/// <summary>
/// Outcome of the administrator report of a finished send. Tracked apart from <see cref="DispatchStatus"/>: a mail failure never changes what Genesys received
/// and is never repaired by sending the Genesys batch again.
/// </summary>
public enum AdminEmailStatus
{
    /// <summary>Not attempted yet (send still running, or the send ended without anything uploaded).</summary>
    None = 0,
    /// <summary>A worker has claimed the report and is sending it.</summary>
    Sending = 1,
    /// <summary>The mail provider accepted the report for every recipient.</summary>
    Sent = 2,
    /// <summary>The provider refused or failed for at least one recipient; retry sends the report only.</summary>
    Failed = 3,
    /// <summary>Notifications are on but no valid recipient (or a supported attachment format) is configured. Never reported as sent.</summary>
    NotConfigured = 4,
    /// <summary>Notifications are switched off by configuration.</summary>
    Disabled = 5
}

public enum DispatchItemStatus
{
    /// <summary>Approved and waiting for revalidation/upload. Counts as "already sent" for duplicate prevention.</summary>
    Approved = 1,
    /// <summary>Accepted by the Genesys contact list. Says nothing about a call or message having happened.</summary>
    UploadedToGenesys = 2,
    Failed = 3,
    /// <summary>The upload outcome is unknown (timeout/5xx). Blocks re-sending until reconciled.</summary>
    UnknownOutcome = 4,
    /// <summary>Removed at dispatch time (paid, balance changed...). Never uploaded.</summary>
    Excluded = 5,
    /// <summary>The dispatch stopped before upload; the record is free to be approved again.</summary>
    Released = 6
}

public class CollectionsDispatchItem
{
    public long CollectionsDispatchItemId { get; set; }
    public long CollectionsDispatchId { get; set; }
    public string RecordKey { get; set; } = "";
    public CampaignReminderType ReminderType { get; set; }
    public int CompanyId { get; set; }
    public string TenantId { get; set; } = "";
    public int? UnitId { get; set; }
    public string UnitCode { get; set; } = "";
    /// <summary>The unit's project as the review record held it at approval (Manual contact list <c>ProjectName</c>). Empty on items approved before this column existed.</summary>
    public string ProjectName { get; set; } = "";
    /// <summary>The unit's real tower name at approval (Manual contact list <c>TowerName</c>); empty when the tower has none. Empty on items approved before this column existed.</summary>
    public string TowerName { get; set; } = "";
    public string CustomerName { get; set; } = "";
    public string Phone { get; set; } = "";
    public string Email { get; set; } = "";
    public decimal Amount { get; set; }
    public string Currency { get; set; } = "AED";
    public DateOnly DueDate { get; set; }
    public DispatchItemStatus Status { get; set; }
    public string? StatusReason { get; set; }
    public long? CollectionsGenesysBatchId { get; set; }
    /// <summary>Position inside the batch request, used to match the contact ids Genesys returns.</summary>
    public int? BatchPosition { get; set; }
    public string? GenesysContactId { get; set; }
    public DateTime? UploadedAtUtc { get; set; }
    /// <summary>Frozen at approval: a valid international phone and Ready status. Only voice-eligible records are ever uploaded as callable.</summary>
    public bool VoiceEligible { get; set; }
    public ContactSuppressionStatus SuppressionStatus { get; set; }
    public DateTime? SuppressedAtUtc { get; set; }
    public string? SuppressionError { get; set; }
    /// <summary>Last time the suppression sweep compared this uploaded contact with the current balance.</summary>
    public DateTime? BalanceCheckedAtUtc { get; set; }
}

/// <summary>What was done to stop an uploaded contact being dialled after its balance changed.</summary>
public enum ContactSuppressionStatus
{
    None = 0,
    /// <summary>Genesys confirmed the contact is no longer callable (callable=false) or no longer exists.</summary>
    Suppressed = 1,
    /// <summary>Genesys rejected the update; the contact may still be dialled. Needs attention.</summary>
    Failed = 2,
    /// <summary>The update's outcome is unknown (timeout/5xx). Retried by the next sweep.</summary>
    Unconfirmed = 3
}

public enum GenesysBatchStatus
{
    Pending = 1,
    Submitting = 2,
    Uploaded = 3,
    Failed = 4,
    UnknownOutcome = 5,
    /// <summary>An operator confirmed in Genesys that nothing was created, so the batch may be re-submitted.</summary>
    ConfirmedNotUploaded = 6
}

/// <summary>One POST to /contactlists/{id}/contacts: a single reminder type, at most 1,000 contacts.</summary>
public class CollectionsGenesysBatch
{
    public long CollectionsGenesysBatchId { get; set; }
    public long CollectionsDispatchId { get; set; }
    public CampaignReminderType ReminderType { get; set; }
    public string ContactListId { get; set; } = "";
    /// <summary>The stage's display name at planning time (the exact Genesys campaign name). <see cref="ReminderType"/> remains the internal stage id.</summary>
    public string? StageName { get; set; }
    /// <summary>The campaign name actually matched or created in Genesys; equals <see cref="StageName"/>.</summary>
    public string? GenesysCampaignName { get; set; }
    public string? GenesysCampaignId { get; set; }
    /// <summary>"Created" (this send made the campaign) or "ReusedExisting" (a campaign with that exact name already existed).</summary>
    public string? CampaignAction { get; set; }
    public int Sequence { get; set; }
    public int ContactCount { get; set; }
    public GenesysBatchStatus Status { get; set; }
    public int AttemptCount { get; set; }
    public string? RequestHash { get; set; }
    public int? HttpStatus { get; set; }
    public int? ReturnedContactCount { get; set; }
    /// <summary>A safe, non-sensitive failure description (never a token, secret or response body).</summary>
    public string? Error { get; set; }
    public DateTime? StartedAtUtc { get; set; }
    public DateTime? CompletedAtUtc { get; set; }
    public Guid? ReconciledByEmployeeId { get; set; }
    public DateTime? ReconciledAtUtc { get; set; }
    public string? ReconciliationNote { get; set; }
}

/// <summary>
/// One message attempt Genesys reported for an uploaded contact, kept as last confirmed by a manual "Sync from Genesys". The natural key is the Genesys conversation + message id, so syncing
/// the same data twice (or a retry that Genesys reports as a second delivery event for the same message) never creates a second row. A contact can have several rows (retries / re-sends):
/// the contact-level outcome is derived from all of them, see <c>GenesysDeliveryRules</c>.
/// </summary>
public class CollectionsGenesysMessageResult
{
    public long CollectionsGenesysMessageResultId { get; set; }
    public long CollectionsDispatchId { get; set; }
    public long CollectionsDispatchItemId { get; set; }
    public string GenesysConversationId { get; set; } = "";
    public string GenesysMessageId { get; set; } = "";
    /// <summary>The Genesys interaction id shown to the user (the conversation id).</summary>
    public string? GenesysContactId { get; set; }
    /// <summary>Queued, Sent, Delivered, Failed or Unknown: the message's last status as Genesys reported it.</summary>
    public string Status { get; set; } = "";
    public DateTime? SentAtUtc { get; set; }
    public DateTime? DeliveredAtUtc { get; set; }
    public string? FailureReason { get; set; }
    public DateTime SyncedAtUtc { get; set; }
}

/// <summary>
/// The persisted result of the last Sync from Genesys for one dispatch (campaign): when it ran, whether it completed, and the confirmed figures, so the Campaign List never calls Genesys
/// to open. Contact counts and message-attempt counts are kept apart: <c>Sent</c> includes <c>Delivered</c>; <c>Sent + Failed + Pending + NotSent = Contacts</c>.
/// </summary>
public class CollectionsGenesysSyncState
{
    public long CollectionsDispatchId { get; set; }
    public DateTime LastSyncedAtUtc { get; set; }
    /// <summary>Completed, or Failed (the figures below are then those of the last completed sync, if any).</summary>
    public string Outcome { get; set; } = "";
    public string? Error { get; set; }
    public int Contacts { get; set; }
    public int Sent { get; set; }
    public int Delivered { get; set; }
    public int Failed { get; set; }
    public int Pending { get; set; }
    public int NotSent { get; set; }
    public int MessageAttempts { get; set; }
    public int ConversationsFound { get; set; }
    public DateTime? LastCompletedAtUtc { get; set; }
}
