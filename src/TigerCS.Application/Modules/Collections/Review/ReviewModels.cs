using TigerCS.Domain.Modules.Collections.Review;

namespace TigerCS.Application.Modules.Collections.Review;

/// <summary>The review filters as sent by the API. Everything optional; blank means "no restriction".</summary>
public sealed record ReviewFilter(
    int? CompanyId = null, string? Project = null, string? Unit = null, string? Customer = null,
    int? Year = null, int? Month = null, DateOnly? DueFrom = null, DateOnly? DueTo = null,
    string? PaymentStatus = null, decimal? MinRemaining = null, decimal? MaxRemaining = null,
    string? ReminderType = null, string? ValidationStatus = null, string? Reason = null,
    string? DueStatus = null, int? TowerId = null, IReadOnlyList<int>? TowerIds = null,
    string? SendingMode = null, string? ContactList = null);

/// <summary>
/// The normalized, validated filter the store understands. <see cref="PaymentStatuses"/> is null for "All".
/// </summary>
public sealed record ReviewQuerySpec(
    int? CompanyId, string? Project, string? Unit, string? CustomerText, string? PhoneDigits,
    DateOnly? DueFrom, DateOnly? DueTo, IReadOnlyList<ReviewPaymentStatus>? PaymentStatuses,
    decimal? MinRemaining, decimal? MaxRemaining, CampaignReminderType? ReminderType,
    ReviewValidationStatus? ValidationStatus, string? Reason, int? TowerId = null, IReadOnlyList<int>? TowerIds = null,
    DateTime? ContactedSinceUtc = null);

public sealed record ReviewRunDto(
    long RunId, string Status, string Phase, int ProgressPercent, DateOnly AsOfDate, int? CompanyId,
    DateOnly DueFrom, DateOnly DueTo, string Source, string? SourceProcedureSuffix, bool SourceReconciled,
    DateTime RequestedAtUtc, DateTime? StartedAtUtc, DateTime? CompletedAtUtc, DateTime? SourceReadAtUtc,
    int SourceRowCount, int RecordCount, string? Error, bool IsCurrent, bool IsStale);

public sealed record ReviewCountsDto(int Total, int Ready, int NeedsReview, int Excluded, int AlreadySent, int UnknownPaymentStatus = 0);

public sealed record ReviewRecordDto(
    string RecordKey, string ReminderType, int CompanyId, string CustomerName, string Phone, string Email,
    string ProjectCode, string UnitCode, string PaymentStatus, decimal? RemainingAmount, string Currency,
    DateOnly? DueDate, string ValidationStatus, IReadOnlyList<ReviewReasonDto> Reasons,
    string Source, DateTime SourceReadAtUtc, string? PreviousDispatchStatus, DateTime? PreviousDispatchAtUtc,
    string? TowerNumber = null, string? TowerName = null, string? TenantId = null);

public sealed record ReviewReasonDto(string Code, string Kind, string Explanation);

public sealed record ReviewPageDto(
    ReviewRunDto? Run, ReviewCountsDto Counts, int Page, int PageSize, int TotalCount,
    IReadOnlyList<ReviewRecordDto> Items, decimal MinRemainingApplied, bool TowerDataMissing = false,
    decimal? ReadyTotalAmount = null);

public sealed record SelectionRequest(
    ReviewFilter? Filter, string Mode, IReadOnlyList<string>? RecordKeys = null,
    IReadOnlyList<string>? ExcludedKeys = null, int ContactPage = 1, int ContactPageSize = 50,
    IReadOnlyList<string>? UnitKeys = null);

public sealed record SelectionContactDto(
    string RecordKey, string ReminderType, string CustomerName, string Phone, string Email,
    string UnitCode, decimal Amount, string Currency, DateOnly DueDate, bool SharedPhone);

public sealed record SelectionSummaryDto(
    long RunId, int Count, string Fingerprint, IReadOnlyDictionary<string, decimal> TotalsByCurrency,
    IReadOnlyDictionary<string, int> CountByReminderType, int SharedPhoneRecordCount, int SharedPhoneNumberCount,
    int Page, int PageSize, IReadOnlyList<SelectionContactDto> Contacts, IReadOnlyList<string> Notices,
    IReadOnlyDictionary<string, string>? ContactListByReminderType = null, bool SourceReconciled = true, string? ContactListName = null);

public sealed record ConfirmDispatchRequest(
    SelectionRequest Selection, int ExpectedCount, string ExpectedFingerprint, string IdempotencyKey,
    bool AcknowledgeActiveCampaignRisk);

public sealed record DispatchBatchDto(
    long BatchId, string ReminderType, string ContactListId, int Sequence, int ContactCount, string Status,
    int AttemptCount, int? HttpStatus, int? ReturnedContactCount, string? Error,
    DateTime? StartedAtUtc, DateTime? CompletedAtUtc, DateTime? ReconciledAtUtc, string? ReconciliationNote,
    string? StageName = null, string? GenesysCampaignName = null, string? GenesysCampaignId = null, string? CampaignAction = null);

public sealed record DispatchDto(
    Guid DispatchId, string Status, string? StatusReason, Guid InitiatedByEmployeeId, DateTime InitiatedAtUtc,
    DateTime? StartedAtUtc, DateTime? CompletedAtUtc, int ApprovedCount, int UploadedCount, int ExcludedCount,
    int FailedCount, int UnknownCount, IReadOnlyDictionary<string, decimal> ApprovedTotalsByCurrency,
    IReadOnlyList<DispatchBatchDto> Batches, string Disclaimer,
    string? Phase = null, long? RevalidationMs = null, int SuppressedCount = 0, int SuppressionFailedCount = 0,
    int UploadedWithoutContactId = 0,
    string? ApprovedByName = null, string? AdminEmailStatus = null, string? AdminEmailError = null, DateTime? AdminEmailAttemptedAtUtc = null,
    int AdminEmailRecipientCount = 0);

public sealed record OverlapRowDto(string CustomerName, int CompanyId, string TenantId, string UnitCode, string ReminderType, decimal? RemainingAmount, string Currency);

/// <summary>One phone number that would receive several calls: the customers and units behind it, and why it is held back.</summary>
public sealed record OverlapGroupDto(
    string Phone, int CustomerCount, int UnitCount, int RecordCount, bool SharedAcrossUnits, bool ReminderTypeOverlap,
    IReadOnlyList<string> ReminderTypes, IReadOnlyList<OverlapRowDto> Rows);

public sealed record OverlapPageDto(int TotalPhones, int Page, int PageSize, IReadOnlyList<OverlapGroupDto> Groups);

public sealed record ReconcileBatchRequest(string Resolution, string Note);

/// <summary>
/// Whether this environment is configured to send an approved list to Genesys, and if not exactly which settings are missing. Names settings only; never a secret, id or customer data.
/// <c>CanSend</c> is whether the caller holds the send permission, so a reader is told why Send is unavailable before trying it.
/// A configuration problem here is a hard stop: it is shown before the person approves anything, and confirmation refuses again.
/// </summary>
public sealed record DispatchReadinessDto(bool Ready, IReadOnlyList<string> Blockers, bool CanSend = true);

/// <summary>
/// The "Export for Genesys" file: customers of a Manual selection in the format of the Genesys contact list they would be imported into. <c>Columns</c> is the file's header exactly. <c>SchemaVerified</c> is
/// true when that list's real columns were read from Genesys and fit; false when the Genesys integration is not configured, so the documented six-column template was used: the file is then labelled
/// "Template not verified against Genesys" (in <c>Notices</c> and in the file name). <c>RepeatedCustomers</c> is how many of the customers had already been exported (0 = none), confirmed by the person;
/// <c>LastExportedAtUtc</c> is the latest earlier export of any of them.
/// </summary>
public sealed record GenesysExportDto(
    string FileName, string Csv, int RowCount, string Campaign, string ContactList, string ContactListId, decimal TotalAmount, string Currency,
    IReadOnlyList<string> Columns, bool SchemaVerified, IReadOnlyList<string> Notices, string Sha256, int RepeatedCustomers = 0, DateTime? LastExportedAtUtc = null);

/// <summary>
/// The Manual "Export review CSV": every customer of the filtered Manual list with the status and the exact reason a held-back customer cannot be sent, for people to review (not for Genesys).
/// </summary>
public sealed record ReviewExportDto(string FileName, string Csv, int RowCount, int ReadyCount, string Sha256);
