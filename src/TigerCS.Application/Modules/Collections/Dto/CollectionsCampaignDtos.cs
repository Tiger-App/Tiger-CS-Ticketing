namespace TigerCS.Application.Modules.Collections.Dto;

public sealed record CollectionsCampaignContactDto(
    string RecordId, string CustomerKey, int CompanyId, string TenantId,
    string CustomerName, string Phone, string Email, int? UnitId, string UnitCode,
    string ProjectCode, decimal? Amount, string Currency, DateOnly? DueDate,
    string Stage, string CycleKey, string Status, string Reason,
    string? TowerNumber = null, string? TowerName = null,
    decimal DueAmount = 0m, decimal OverdueAmount = 0m, int? CrmCustomerId = null)
{
    /// <summary>Due + Overdue of the unit (remaining amounts of unpaid or partly paid instalments due before the end of the current Dubai month).</summary>
    public decimal TotalAmount => DueAmount + OverdueAmount;
    public bool VoiceEligible => Status == "Ready" && Phone.Length > 0;
    public bool SmsEligible => Status == "Ready" && Phone.Length > 0;
    public bool EmailEligible => Status == "Ready" && Email.Length > 0;
}

public sealed record CollectionsCampaignPreviewDto(
    DateOnly BusinessDate, DateOnly LiveBusinessDate, DateTime ReadAtUtc,
    string Source, string Stage, string CycleKey, IReadOnlyList<DateOnly> ScheduledDates,
    bool IsScheduledDate, bool FinancialSourceValidated, bool LegacyExclusionsApplied,
    bool CanExportReview, int TotalCount, int ReadyCount, int ReviewCount, int Page, int PageSize,
    IReadOnlyList<CollectionsCampaignContactDto> Items,
    DateOnly? DateFrom = null, DateOnly? DateTo = null, IReadOnlyList<string>? RangeNotes = null,
    int? TowerId = null, SnapshotStatusDto? Snapshot = null, decimal MinAmount = 0m, ServerTimingsDto? Timings = null, CrmContactStatusDto? Crm = null, int NotEligibleCount = 0,
    string PaymentStatus = "Both", decimal? EligibleTotalAmount = null,
    decimal? EligibleOutstandingAmount = null, string? PaymentStatusNote = null,
    Reconciliation.SourceGateDto? SourceGate = null, IReadOnlyList<int>? TowerIds = null,
    string SendingMode = "Auto", int PreviewOnlyCount = 0, bool AutoSendingEnabled = false, IReadOnlyList<int>? Floors = null);

/// <summary>
/// The state of the CRM contact data behind a list. <c>State</c>: Disabled (CRM linking is switched off: PACT contacts only), Ready, NotLoaded, Stale or ReadFailed. A <c>Blocks</c> state is a CRM
/// SERVICE problem: the contacts are unconfirmed (every unit needs review and no Genesys list can be exported), which is different from a unit for which CRM simply holds no mobile.
/// </summary>
public sealed record CrmContactStatusDto(string State, bool Blocks, string? Message, DateTime? LastSuccessUtc, bool LastRefreshFailed = false)
{
    public static CrmContactStatusDto From(Services.CrmAvailability a) => new(a.State.ToString(), a.Blocks, a.Message, a.LastSuccessUtc, a.LastRefreshFailed);
}

/// <summary>
/// Internal API transport. <c>Csv</c> is the full audit/machine file (the Genesys import file, and the audit CSV for review mode).
/// For review mode <c>Xlsx</c> is the simplified employee workbook (8 columns) and <c>XlsxFileName</c> its name; the Web serves that
/// one when present, otherwise Csv as a UTF-8 downloadable file.
/// </summary>
public sealed record CollectionsCampaignExportDto(string FileName, string Csv, int RowCount, byte[]? Xlsx = null, string? XlsxFileName = null);
