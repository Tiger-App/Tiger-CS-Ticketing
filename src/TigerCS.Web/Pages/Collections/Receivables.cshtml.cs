using System.Diagnostics;
using System.Globalization;
using System.Web;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using TigerCS.Application.Modules.Collections.Dto;
using TigerCS.Domain.Modules.Collections;
using TigerCS.Web.Services.Api;

namespace TigerCS.Web.Pages.Collections;

/// <summary>
/// Receivables: one row per unit (company + customer + unit) with everything the unit has not paid yet; View Details opens that unit's unpaid instalments.
/// The page itself (filters + an empty results area) renders immediately and never waits for data; the results are fetched from the local snapshot by
/// <see cref="OnGetResultsAsync"/> (HTML fragment, called by receivables.js) so only the results area shows loading.
/// </summary>
public sealed class ReceivablesModel(CollectionsApiClient api) : PageModel
{
    /// <summary>Default of Minimum Total (AED): units whose Due + Overdue is not greater than it are hidden. The user can change or clear it.</summary>
    public const decimal DefaultMinTotal = 100m;

    /// <summary>
    /// The Minimum Total of a request: absent = the default (100); present but empty = cleared (null: every unit with a Due or Overdue amount); otherwise the number.
    /// Read from the raw query because an empty value and a missing one both bind to null.
    /// </summary>
    public static decimal? ParseMinTotal(Microsoft.AspNetCore.Http.IQueryCollection query)
    {
        if (!query.TryGetValue("minTotal", out var raw)) return DefaultMinTotal;
        var text = raw.ToString().Trim();
        if (text.Length == 0) return null;
        return decimal.TryParse(text, NumberStyles.Number, CultureInfo.InvariantCulture, out var value) && value >= 0 ? value : DefaultMinTotal;
    }

    /// <summary>Every unpaid instalment due before the end of the current Dubai month, whatever its size: from the widest start the snapshot is loaded for through that month end. Nothing due from the next month on is listed.</summary>
    public static readonly DateOnly WindowFrom = new(2000, 1, 1);

    public static readonly IReadOnlyList<(string Value, string Label)> StatusOptions =
        [("all", "Due + Overdue"), ("due", "Due"), ("overdue", "Overdue")];

    /// <summary>The chosen towers (distinct, ascending; an old single <c>?towerId=7</c> URL is simply one of them). Empty = All towers.</summary>
    public IReadOnlyList<int> TowerIds { get; private set; } = [];
    /// <summary>What is sent to the API: null for All towers (none chosen, or every listed tower chosen - the unrestricted read); one tower as <c>towerId</c>; two or more as <c>towerIds</c>.</summary>
    public IReadOnlyList<int>? EffectiveTowerIds => TowerSelection.Effective(TowerIds, Towers);
    private int? SingleTowerId => EffectiveTowerIds is { Count: 1 } one ? one[0] : null;
    private IReadOnlyList<int>? ManyTowerIds => EffectiveTowerIds is { Count: > 1 } many ? many : null;
    /// <summary>The chosen apartment floors (repeated <c>floor</c> values; derived from the unit number). Empty = All Floors = no filtering.</summary>
    public IReadOnlyList<int> FloorIds { get; private set; } = [];
    public IReadOnlyList<CollectionsFloorDto> FloorOptions { get; private set; } = [];
    public bool FloorsFailed { get; private set; }
    private IReadOnlyList<int>? FloorFilter => FloorIds.Count == 0 ? null : FloorIds;
    /// <summary>Month and year of the instalments' DUE date; both empty = All months (historic overdue plus the whole current month). Narrows the results only: Due / Overdue stay classified against the current month.</summary>
    public int? Month { get; private set; }
    public int? Year { get; private set; }
    public decimal? MinTotal { get; private set; } = DefaultMinTotal;
    public string MinTotalText => MinTotal?.ToString("0.####", CultureInfo.InvariantCulture) ?? "";
    /// <summary>The chosen month starts after the current month: it is neither Due nor Overdue (no data call is made).</summary>
    public bool NothingDueYet { get; private set; }
    public IEnumerable<int> YearOptions()
    {
        var current = CollectionsDisplay.DubaiToday().Year;
        return Enumerable.Range(2020, Math.Max(current, Year ?? 0) - 2020 + 1);
    }
    /// <summary><c>all</c>, <c>overdue</c> or <c>due</c>: the units that have an instalment with that status.</summary>
    public string Status { get; private set; } = "all";
    public string? Search { get; private set; }
    public int PageNumber { get; private set; } = 1;
    public IReadOnlyList<CollectionsTowerDto> Towers { get; private set; } = [];
    public bool TowersFailed { get; private set; }
    public ApiOutcome Outcome { get; private set; }
    public string? Error { get; private set; }
    public string? LoadNotice { get; private set; }
    public PactInstalmentsPageDto? Report { get; private set; }
    public bool RenderFull { get; private set; }
    public int UnitCount => Report?.Totals.UnitCount ?? 0;
    public int TotalPages => Report is null ? 1 : Math.Max(1, (int)Math.Ceiling(UnitCount / (double)Report.PageSize));

    public static readonly string[] MonthNames = CultureInfo.InvariantCulture.DateTimeFormat.MonthNames.Take(12).ToArray();

    public async Task OnGetAsync(string? status, string? search, int? month = null, int? year = null, int page = 1, string? render = null, string? load = null, CancellationToken cancellationToken = default)
    {
        Bind(status, search, month, year, page, load);
        RenderFull = render == "full";
        // The shell never waits for data. The tower list is a small local read; results arrive through the Results handler (or render=full without JavaScript).
        var towers = await api.GetTowersAsync(cancellationToken);
        Towers = towers.IsSuccess && towers.Value is { } list ? list : [];
        TowersFailed = !towers.IsSuccess;
        var floors = await api.GetFloorsAsync(TowerIds, cancellationToken);
        FloorOptions = floors.IsSuccess && floors.Value is { } floorList ? floorList.Floors : [];
        FloorsFailed = !floors.IsSuccess;
        if (RenderFull) await LoadReportAsync(cancellationToken);
    }

    /// <summary>JSON for the Floor dropdown: the floors of the towers in the query (<c>towerId</c>, repeated); the browser reloads it whenever the towers change.</summary>
    public async Task<IActionResult> OnGetFloorsAsync(CancellationToken cancellationToken)
    {
        var floors = await api.GetFloorsAsync(TowerSelection.Parse(Request.Query["towerId"]), cancellationToken);
        Response.Headers.CacheControl = "no-store";
        return new JsonResult(FloorSelection.Payload(floors.IsSuccess ? floors.Value : null, !floors.IsSuccess));
    }

    /// <summary>The results area only (HTML fragment).</summary>
    public async Task<IActionResult> OnGetResultsAsync(string? status, string? search, int? month = null, int? year = null, int page = 1, string? load = null, CancellationToken cancellationToken = default)
    {
        var timer = Stopwatch.StartNew();
        Bind(status, search, month, year, page, load);
        await LoadReportAsync(cancellationToken);
        Response.Headers.CacheControl = "no-store";
        var t = Report?.Timings;
        Response.Headers["Server-Timing"] = string.Create(CultureInfo.InvariantCulture,
            $"web;dur={timer.Elapsed.TotalMilliseconds:F0}, sql;dur={t?.SourceMs ?? 0:F0}, map;dur={t?.MapMs ?? 0:F0}");
        return Partial("_InstalmentResults", this);
    }

    /// <summary>Per-company refresh state (JSON, tiny): what receivables.js polls while a refresh is running, instead of re-requesting the whole results fragment.</summary>
    public async Task<IActionResult> OnGetStatusAsync(CancellationToken cancellationToken)
    {
        var result = await api.GetSnapshotStatusAsync(cancellationToken);
        Response.Headers.CacheControl = "no-store";
        return new JsonResult(CollectionsDisplay.StatusPayload(result.IsSuccess ? result.Value : null));
    }

    private void Bind(string? status, string? search, int? month, int? year, int page, string? load)
    {
        TowerIds = TowerSelection.Parse(Request.Query["towerId"]);
        FloorIds = FloorSelection.Parse(Request.Query["floor"]);
        Status = StatusOptions.Any(o => o.Value == status?.Trim().ToLowerInvariant()) ? status!.Trim().ToLowerInvariant() : "all";
        Search = search?.Trim();
        MinTotal = ParseMinTotal(Request.Query);
        // A month needs its year (the current year when only the month was chosen); a year alone means All months.
        var today = CollectionsDisplay.DubaiToday();
        if (month is >= 1 and <= 12) { Month = month; Year = year is >= 2000 and <= 2100 ? year : today.Year; }
        PageNumber = Math.Max(1, page);
        LoadNotice = CollectionsDisplay.NoticeText(load) is { Length: > 0 } text ? text : null;
    }

    /// <summary>The results request does not render the picker: the tower list is read only when several towers are chosen, so "every tower chosen" still means All towers.</summary>
    private async Task EnsureTowersAsync(CancellationToken cancellationToken)
    {
        if (TowerIds.Count < 2 || Towers.Count > 0) return;
        var towers = await api.GetTowersAsync(cancellationToken);
        if (towers.IsSuccess && towers.Value is { } list) Towers = list;
    }

    private async Task LoadReportAsync(CancellationToken cancellationToken)
    {
        await EnsureTowersAsync(cancellationToken);
        // The dates are the instalments' DUE dates: the chosen month, or everything up to the end of the current month. Never from the next month on (neither Due nor
        // Overdue), and the Due / Overdue classification stays against the current Dubai month whatever month is chosen.
        var today = CollectionsDisplay.DubaiToday();
        var (from, to) = (WindowFrom, CollectionsDueMonth.MonthEnd(today));
        if (Month is { } m && Year is { } y)
        {
            var (first, last) = TigerCS.Domain.Modules.Collections.CollectionsDateRanges.Month(y, m);
            if (!CollectionsDueMonth.IsInScope(first, today)) { NothingDueYet = true; return; }
            (from, to) = (first, CollectionsDueMonth.ClampTo(last, today));
        }
        var result = await api.GetInstalmentsAsync(SingleTowerId, from, to, "outstanding", null, Search, PageNumber, cancellationToken, "units", null, Status == "all" ? null : Status, MinTotal, ManyTowerIds, FloorFilter);
        Outcome = result.Outcome;
        Error = result.Detail;
        Report = result.IsSuccess ? result.Value : null;
    }

    /// <summary>Query string of the current filters (page 1 unless given); the same names the form submits.</summary>
    public string Query(int? page = null)
    {
        var query = HttpUtility.ParseQueryString(string.Empty);
        foreach (var floor in FloorIds) query.Add("floor", floor.ToString(CultureInfo.InvariantCulture));   // the Floor picker's own name: the selection survives paging and the other filters
        foreach (var tower in TowerIds) query.Add("towerId", tower.ToString(CultureInfo.InvariantCulture));   // the picker's own name: the selection survives status / month / year / minimum / search / page changes
        if (Status != "all") query["status"] = Status;
        if (!string.IsNullOrWhiteSpace(Search)) query["search"] = Search;
        if (Month is { } m && Year is { } y) { query["month"] = m.ToString(CultureInfo.InvariantCulture); query["year"] = y.ToString(CultureInfo.InvariantCulture); }
        query["minTotal"] = MinTotalText;   // always sent: empty means "cleared" and must survive paging
        if (page is > 1) query["page"] = page.Value.ToString(CultureInfo.InvariantCulture);
        return query.ToString()!;
    }

    public string PageUrl(int page) => $"/Collections/Receivables?{Query(page)}&render=full";

    /// <summary>"1 unit" / "25 units".</summary>
    public static string Plural(int count, string one, string many) => count == 1 ? $"1 {one}" : $"{count:N0} {many}";

    /// <summary>Starts the background load (no-JavaScript fallback; receivables.js posts the same form with fetch). With a company: only that company is retried.</summary>
    public async Task<IActionResult> OnPostLoadCoverageAsync(DateOnly dateFrom, DateOnly dateTo, string? returnUrl, int? companyId = null, CancellationToken cancellationToken = default)
    {
        var result = await api.RequestCoverageLoadAsync(dateFrom, dateTo, cancellationToken, companyId);
        var target = !string.IsNullOrEmpty(returnUrl) && Url.IsLocalUrl(returnUrl) ? returnUrl : "/Collections/Receivables";
        return LocalRedirect(CollectionsDisplay.WithNotice(target, CollectionsDisplay.NoticeCode(result)));
    }

    // ---- One unit row and its details. Every number on the row is derived from the very instalments the details list, so the two can never disagree. ----

    public const string Overdue = "Overdue", Due = "Due";

    /// <summary>Overdue: due before the first day of the current month; Due: due anywhere inside it (today = the Dubai date of the report). Instalments due from the next month on are not part of this page.</summary>
    public static string Classify(DateOnly dueDate, DateOnly today) => CollectionsDueMonth.IsOverdue(dueDate, today) ? Overdue : Due;

    /// <summary>Whole days between the due date and today for an instalment that is already past due (before today); null otherwise. A Due instalment of a coming day has none.</summary>
    public static int? DaysLate(DateOnly dueDate, DateOnly today) => dueDate < today ? today.DayNumber - dueDate.DayNumber : null;

    public sealed record InstalmentLine(int Number, string Description, DateOnly DueDate, decimal? Original, decimal? Paid, decimal Remaining, string Status, int? DaysLate);

    /// <summary><see cref="Total"/> = Due + Overdue = the sum of the listed instalments.
public sealed record UnitLine(string Customer, string Phone, string Email, string Tower, string Apartment, decimal Total, decimal Due, decimal Overdue,
        IReadOnlyList<InstalmentLine> Instalments, string DetailsId, IReadOnlyList<string> Notes);

    public static IReadOnlyList<UnitLine> Lines(PactInstalmentsPageDto report)
    {
        var today = report.BusinessDate;
        var lines = new List<UnitLine>();
        foreach (var unit in report.Units ?? [])
        {
            // Newest due date first; a remaining balance is what makes an instalment unpaid.
            var rows = unit.Instalments.Where(i => i.RemainingAmount > 0 && CollectionsDueMonth.IsInScope(i.DueDate, today)).OrderByDescending(i => i.DueDate).ThenBy(i => i.VoucherNumber, StringComparer.Ordinal).ToList();
            var instalments = rows.Select((i, index) => new InstalmentLine(index + 1,
                i.VoucherNumber.Length > 0 ? $"Voucher {i.VoucherNumber}" : $"Instalment {index + 1}", i.DueDate, i.OriginalAmount, i.PaidAmount, i.RemainingAmount,
                Classify(i.DueDate, today), DaysLate(i.DueDate, today))).ToList();
            // PACT's contact for the apartment is taken from its EARLIEST payment (then smallest voucher): the same representative row Campaigns use, though the details list newest first.
            var contactRows = rows.OrderBy(r => r.DueDate).ThenBy(r => r.VoucherNumber, StringComparer.Ordinal).ToList();
            var tower = CollectionsDisplay.TowerCell(unit.TowerName, unit.TowerNumber);
            if (instalments.Count == 0) continue;   // nothing Due or Overdue: the unit is not listed
            // CRM first, PACT completing, nobody merged on conflict (CollectionsContactLinker): the same rule SQL applies to Campaigns and the Payment Summary uses.
            var crmStatus = unit.CrmCustomers is null ? CrmLinkStatus.None : unit.CrmCustomers > 1 ? CrmLinkStatus.Ambiguous : unit.CrmCompanyAmbiguous ? CrmLinkStatus.CompanyAmbiguous : CrmLinkStatus.Single;
            var linked = CollectionsContactLinker.Link(
                new SourceContact(unit.CustomerName.Trim(), CollectionsContactNormalizer.NormalizePhone(First(contactRows.Select(r => r.Mobile), "")), CollectionsContactNormalizer.NormalizeEmail(First(contactRows.Select(r => r.Email), ""))),
                new SourceContact(unit.CrmName?.Trim() ?? "", unit.CrmPhone ?? "", unit.CrmEmail ?? ""), crmStatus);
            var notes = new List<string>();
            if (linked.CrmCustomerAmbiguous) notes.Add("Several CRM customers: needs review");
            if (linked.CrmCompanyAmbiguous) notes.Add("CRM sale cannot be tied to one company: needs review");
            // A CRM SERVICE problem is not "CRM has no value": when CRM linking is on but unusable, the contact below is PACT-only and unconfirmed.
            if (report.Crm is { Blocks: true }) notes.Add("CRM unavailable: contact unconfirmed, needs review");
            else if (!linked.HasContact) notes.Add(NoContactNote(report.Crm?.State, crmStatus));
            else if (linked.Phone.Length == 0) notes.Add("No mobile");
            else if (linked.Email.Length == 0) notes.Add("No email");
            lines.Add(new UnitLine(linked.Name.Length > 0 ? linked.Name : unit.CustomerName,
                linked.Phone.Length > 0 ? linked.Phone : "—", linked.Email.Length > 0 ? linked.Email : "—", tower, unit.UnitCode,
                instalments.Sum(i => i.Remaining), instalments.Where(i => i.Status == Due).Sum(i => i.Remaining), instalments.Where(i => i.Status == Overdue).Sum(i => i.Remaining),
                instalments, $"unit-{unit.CompanyId}-{unit.UnitId}-{Math.Abs(string.GetHashCode(unit.TenantId + "|" + unit.UnitCode, StringComparison.Ordinal))}", notes));
        }
        return lines;
    }

    /// <summary>Why nobody can be reached: only claims "CRM or PACT" when CRM was really consulted for this unit's owner. Switched off and not linked are different from CRM holding nothing.</summary>
    public static string NoContactNote(string? crmState, CrmLinkStatus unitLink) => crmState switch
    {
        "Disabled" => "No mobile or email in PACT; CRM linking is switched off, so CRM was not checked: cannot be contacted",
        not null when unitLink == CrmLinkStatus.None => "No mobile or email in PACT; no CRM owner is linked to this unit: cannot be contacted",
        _ => "No mobile or email in CRM or PACT: cannot be contacted",
    };

    private static string First(IEnumerable<string> values, string fallback = "—") => values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v))?.Trim() ?? fallback;

    public static string Money(decimal? amount) => amount is { } value ? value.ToString("N2", CultureInfo.InvariantCulture) : "—";

    /// <summary>A zero amount in the Due / Overdue columns reads as a dash.</summary>
    public static string MoneyOrDash(decimal amount) => amount > 0 ? Money(amount) : "—";
}
