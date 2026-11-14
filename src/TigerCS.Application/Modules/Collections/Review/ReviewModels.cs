using TigerCS.Domain.Modules.Collections.Review;

namespace TigerCS.Application.Modules.Collections.Review;

/// <summary>The review filters as sent by the API. Everything optional; blank means "no restriction". <c>Floors</c>: the chosen apartment floors (derived from the unit code, <see cref="TigerCS.Domain.Modules.Collections.CollectionsUnitFloor"/>); null / empty = All floors. It is part of the filter a dispatch stores, so an approval, its revalidation and the paid-after-upload check resolve the same customers.</summary>
public sealed record ReviewFilter(
    int? CompanyId = null, string? Project = null, string? Unit = null, string? Customer = null,
    int? Year = null, int? Month = null, DateOnly? DueFrom = null, DateOnly? DueTo = null,
    string? PaymentStatus = null, decimal? MinRemaining = null, decimal? MaxRemaining = null,
    string? ReminderType = null, string? ValidationStatus = null, string? Reason = null,
    string? DueStatus = null, int? TowerId = null, IReadOnlyList<int>? TowerIds = null,
    string? SendingMode = null, string? ContactList = null,
    IReadOnlyList<int>? Floors = null);

/// <summary>
/// The normalized, validated filter the store understands. <see cref="PaymentStatuses"/> is null for "All".
/// </summary>
public sealed record ReviewQuerySpec(
    int? CompanyId, string? Project, string? Unit, string? CustomerText, string? PhoneDigits,
    DateOnly? DueFrom, DateOnly? DueTo, IReadOnlyList<ReviewPaymentStatus>? PaymentStatuses,
    decimal? MinRemaining, decimal? MaxRemaining, CampaignReminderType? ReminderType,
    ReviewValidationStatus? ValidationStatus, string? Reason, int? TowerId = null, IReadOnlyList<int>? TowerIds = null,
    DateTime? ContactedSinceUtc = null, IReadOnlyList<int>? Floors = null,
    IReadOnlyCollection<string>? UnitCodes = null, IReadOnlyCollection<string>? ExcludedUnitCodes = null,
    bool HeldBackOnly = false);

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

/// <summary>Every row of one Manual list in the page's order, from one calculation (see <c>ReviewQueryService.ManualListAsync</c>): for a file that holds the whole list.</summary>
public sealed record ManualListDto(ReviewRunDto Run, IReadOnlyList<ReviewRecordDto> Items);

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

/// <summary>Units left out of an Export for Genesys file, by reason: the code, how many units, and the plain-language explanation.</summary>
public sealed record GenesysExportExclusionDto(string Code, int Units, string Explanation);

/// <summary>One unit left out of an Export for Genesys file, named: which record (company, tenant, unit, customer, campaign type) and the exact reason code.</summary>
public sealed record GenesysExportExcludedRecordDto(int CompanyId, string TenantId, string UnitCode, string CustomerName, string ReminderType, string Code);

/// <summary>
/// The "Export for Genesys" file: every customer matching the filters, in the format of the Genesys contact list the file is for. <c>Columns</c> is the file's header exactly. <c>RowCount</c> rows hold
/// <c>UnitCount</c> customer units (one per row: a phone number never merges or drops a record); <c>Exclusions</c> says which matching units are NOT in the file and why, and <c>ExcludedRecords</c> names each of them.
/// <c>IncludedByStatus</c> counts the units by the status the Manual page shows (Ready, NeedsReview, Excluded, AlreadySent): a file is not a send, so held-back customers are in it.
/// <c>DataVerified</c> is false when the financial data behind the amounts is not verified (unreconciled source, stale or not built today, or units whose data needs review, PaymentStatusUnknown
/// included); <c>UnverifiedReasons</c> names why, and the file is labelled "Financial data not verified". <c>SchemaVerified</c> is true when the list's real columns were read from Genesys and fit;
/// false when they could not be read (integration not configured, login refused, Genesys unreachable), so the documented six-column template was used and the file is labelled "Template not
/// verified against Genesys". <c>RepeatedCustomers</c> is how many of the customers were exported before within the look-back (informational; a repeat is audited, never a block).
/// </summary>
public sealed record GenesysExportDto(
    string FileName, string Csv, int RowCount, string Campaign, string ContactList, string ContactListId, decimal TotalAmount, string Currency,
    IReadOnlyList<string> Columns, bool SchemaVerified, IReadOnlyList<string> Notices, string Sha256, int RepeatedCustomers = 0, DateTime? LastExportedAtUtc = null,
    int UnitCount = 0, IReadOnlyList<GenesysExportExclusionDto>? Exclusions = null, IReadOnlyDictionary<string, int>? IncludedByStatus = null,
    bool DataVerified = true, IReadOnlyList<string>? UnverifiedReasons = null, int Skipped = 0, IReadOnlyList<GenesysExportExcludedRecordDto>? ExcludedRecords = null)
{
    public int ExcludedUnits => Exclusions?.Sum(e => e.Units) ?? 0;
}

/// <summary>
/// The Manual "Export review CSV": every customer of the filtered Manual list with the status and the exact reason a held-back customer cannot be sent, for people to review (not for Genesys).
/// </summary>
public sealed record ReviewExportDto(string FileName, string Csv, int RowCount, int ReadyCount, string Sha256);
