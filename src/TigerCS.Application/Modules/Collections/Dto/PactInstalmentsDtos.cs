namespace TigerCS.Application.Modules.Collections.Dto;

/// <summary>
/// One instalment. Original and paid amounts are what the source returned; they are null (never estimated) when it did not.
/// PaymentStatus (Unpaid / PartiallyPaid / FullyPaid / Unknown) is separate from Classification (Overdue / Due / NotYetDue / NotApplicable),
/// so an instalment can be partially paid and overdue at once.
/// </summary>
public sealed record PactInstalmentRowDto(
    int CompanyId, string CompanyName, string? TowerNumber, string? TowerName, int? UnitId, string UnitCode, string TenantId,
    string CustomerName, string Mobile, string Email, string VoucherNumber, string ChequeNumber, DateOnly DueDate,
    decimal? OriginalAmount, decimal? PaidAmount, decimal RemainingAmount, string PaymentStatus, string Classification, string SourceStatus);

public sealed record PactInstalmentTotalsDto(
    int Count, decimal RemainingTotal, int OverdueCount, decimal OverdueRemaining, int DueCount, decimal DueRemaining,
    int NotYetDueCount, decimal NotYetDueRemaining, int FullyPaidCount, int UnitCount = 0);

/// <summary>
/// One unit of the "By unit" view: all matching instalments of one (company, customer, unit), grouped in SQL before paging. Counts and amounts cover exactly the
/// listed <see cref="Instalments"/> (the same filters as the instalment view).
/// </summary>
public sealed record PactInstalmentUnitDto(
    int CompanyId, string CompanyName, string? TowerNumber, string? TowerName, int UnitId, string UnitCode, string TenantId, string CustomerName,
    int InstalmentCount, decimal RemainingTotal, DateOnly LatestDueDate, IReadOnlyList<PactInstalmentRowDto> Instalments,
    int? CrmCustomers = null, int? CrmCustomerId = null, string? CrmName = null, string? CrmPhone = null, string? CrmEmail = null, bool CrmCompanyAmbiguous = false);

/// <summary>
/// One due-date month of the month overview, over the whole filtered set (before paging, ignoring the month selection). Overdue uses the business-date rule
/// (remaining &gt; 0 and due before the first day of the current Dubai month); the counts and amounts equal the list's totals when filtered to that month.
/// </summary>
public sealed record PactInstalmentMonthDto(int Year, int Month, int InstalmentCount, decimal RemainingTotal, int OverdueCount, decimal OverdueRemaining);

/// <summary>Which payment views the loaded data can answer reliably (the UI disables the others and says why).</summary>
public sealed record PaymentViewAvailabilityDto(bool Outstanding, bool Unpaid, bool PartiallyPaid, bool FullyPaid, bool All, int UnclassifiedRows);

public sealed record PactInstalmentsPageDto(
    DateOnly BusinessDate, DateOnly AsOfMonthStart, DateOnly AsOfMonthEnd, DateOnly DateFrom, DateOnly DateTo, int? TowerId,
    string PaymentFilter, decimal MinAmount, bool MinAmountApplied, string Currency,
    PactInstalmentTotalsDto Totals, int Page, int PageSize, IReadOnlyList<PactInstalmentRowDto> Items,
    SnapshotStatusDto Snapshot, PaymentViewAvailabilityDto Views, IReadOnlyList<string> Notes, DateTime ReadAtUtc, ServerTimingsDto? Timings = null,
    string View = "instalments", IReadOnlyList<PactInstalmentUnitDto>? Units = null,
    IReadOnlyList<PactInstalmentMonthDto>? Months = null, string? DueMonth = null, CrmContactStatusDto? Crm = null,
    IReadOnlyList<int>? TowerIds = null, IReadOnlyList<int>? Floors = null);
