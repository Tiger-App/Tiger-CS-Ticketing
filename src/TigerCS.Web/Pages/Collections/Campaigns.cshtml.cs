using System.Globalization;
using System.Text;
using System.Web;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using TigerCS.Application.Modules.Collections.Dto;
using TigerCS.Application.Modules.Collections.Review;
using TigerCS.Application.Modules.Collections.Services;
using TigerCS.Domain.Modules.Collections;
using TigerCS.Web.Services.Api;

namespace TigerCS.Web.Pages.Collections;

[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public sealed class CampaignsModel(CollectionsApiClient api, Microsoft.Extensions.Configuration.IConfiguration configuration) : PageModel
{
    public string Stage { get; private set; } = "OverdueReminder";
    public DateOnly? BusinessDate { get; private set; }

    /// <summary>"Auto" (default: the approved policy schedule; scheduled campaigns are dispatched by the Auto dispatcher while AutoSendingEnabled is on) or "Manual" (an authorized person sends any campaign type at any time).</summary>
    public string SendingMode { get; private set; } = CollectionsSendingModes.Auto;
    public bool IsManual => SendingMode == CollectionsSendingModes.Manual;
    public DateOnly? DateFrom { get; private set; }
    public DateOnly? DateTo { get; private set; }
    /// <summary>Due, Overdue or Both (default). Sent to the API with every preview, page and export so they always agree.</summary>
    public string PaymentStatus { get; private set; } = "Both";
    /// <summary>The chosen towers (local ids, repeated <c>towerId</c> query values). Empty = All projects, which is also the default.</summary>
    public IReadOnlyList<int> TowerIds { get; private set; } = [];
    /// <summary>Minimum Total (AED): a unit is listed only when its Due + Overdue is greater than this. Default 100; null (cleared) = every unit that has a Due or Overdue amount.</summary>
    public decimal? MinTotal { get; private set; } = ReceivablesModel.DefaultMinTotal;
    public string MinTotalText => MinTotal?.ToString("0.####", CultureInfo.InvariantCulture) ?? "";
    /// <summary>The chosen dates start in a month after the current one: neither Due nor Overdue, so no data call is made.</summary>
    public bool NothingDueYet { get; private set; }
    public IReadOnlyList<CollectionsTowerDto> Towers { get; private set; } = [];
    public bool TowersFailed { get; private set; }
    public string? Search { get; private set; }
    public int PageNumber { get; private set; } = 1;
    public ApiOutcome Outcome { get; private set; }
    public string? Error { get; private set; }
    public bool RenderFull { get; private set; }
    public CollectionsCampaignPreviewDto? Report { get; private set; }
    /// <summary>Manual: the stored review records of the chosen campaign type that match the filters, with the amount calculated from the filters and their exact validation reasons.</summary>
    public ReviewPageDto? ManualPage { get; private set; }
    public int ManualTotalPages => ManualPage is null ? 1 : Math.Max(1, (int)Math.Ceiling(ManualPage.TotalCount / (double)ManualPage.PageSize));
    /// <summary>Manual: the existing Genesys contact list the person chose (a campaign type name); empty = the campaign type\'s own list.</summary>
    public string ContactList { get; private set; } = "";

    /// <summary>The campaign type the chosen stage stands for.</summary>
    public TigerCS.Domain.Modules.Collections.Review.CampaignReminderType ManualType =>
        CollectionsEnums.TryParse<CollectionsCampaignStage>(Stage, out var stage) ? TigerCS.Domain.Modules.Collections.Review.CampaignReminderTypes.FromStage(stage) : TigerCS.Domain.Modules.Collections.Review.CampaignReminderType.Overdue;

    /// <summary>The existing contact lists that suit the chosen campaign type; the first is its own list and the default.</summary>
    public IReadOnlyList<string> ContactListChoices => TigerCS.Domain.Modules.Collections.Review.CampaignReminderTypes.ManualDestinations(ManualType).Select(t => t.ToString()).ToList();

    /// <summary>The list that is selected: the person\'s choice when it suits the campaign type, otherwise the type\'s own list.</summary>
    public string SelectedContactList => ContactListChoices.Contains(ContactList, StringComparer.Ordinal) ? ContactList : ContactListChoices[0];

    private static string ParseContactList(string? raw) =>
        Enum.GetValues<TigerCS.Domain.Modules.Collections.Review.CampaignReminderType>().Select(t => t.ToString()).FirstOrDefault(n => string.Equals(n, raw?.Replace(" ", "").Trim(), StringComparison.OrdinalIgnoreCase)) ?? "";
    public int TotalPages => Report is null ? 1 : Math.Max(1, (int)Math.Ceiling(Report.TotalCount / (double)Report.PageSize));
    /// <summary>Distinct, valid tower ids from repeated <c>towerId</c> values; blanks and junk are ignored (an empty result means All projects).</summary>
    public static IReadOnlyList<int> ParseTowerIds(Microsoft.Extensions.Primitives.StringValues values) => TowerSelection.Parse(values);

    /// <summary>What is sent to the API: null for All projects (no tower chosen, or every listed tower chosen - the unrestricted, fastest read).</summary>
    public IReadOnlyList<int>? EffectiveTowerIds => TowerSelection.Effective(TowerIds, Towers);

    /// <summary>One chosen tower travels as the established <c>towerId</c>; two or more as <c>towerIds</c>.</summary>
    private int? SingleTowerId => EffectiveTowerIds is { Count: 1 } one ? one[0] : null;
    private IReadOnlyList<int>? ManyTowerIds => EffectiveTowerIds is { Count: > 1 } many ? many : null;

    public static readonly string[] PaymentStatusChoices = ["Both", "Due", "Overdue"];

    /// <summary>The page (filters + an empty results area) renders at once and never waits for data; results come from <see cref="OnGetResultsAsync"/>.</summary>
    public async Task OnGetAsync(string stage = "OverdueReminder", DateOnly? businessDate = null,
        string? search = null, [FromQuery(Name = "page")] int pageNumber = 1, DateOnly? dateFrom = null, DateOnly? dateTo = null,
        int? month = null, int? year = null, string? render = null, string? load = null, string? paymentStatus = null, string? sendingMode = null, CancellationToken cancellationToken = default)
    {
        SetFilters(stage, businessDate, search, pageNumber, dateFrom, dateTo, month, year, load, paymentStatus, sendingMode);
        RenderFull = render == "full";
        var towers = await api.GetTowersAsync(cancellationToken);
        Towers = towers.IsSuccess && towers.Value is { } list ? list : [];
        TowersFailed = !towers.IsSuccess;
        if (RenderFull) await LoadReportAsync(cancellationToken);
        await LoadApprovalStateAsync(cancellationToken);
        if (TempData["ApprovalError"] is string error) ApprovalError = error;
        if (TempData["ApprovalNotice"] is string notice) ApprovalNotice = notice;
    }

    /// <summary>The results area only (HTML fragment): summary, snapshot status, table, pagination and export links.</summary>
    public async Task<IActionResult> OnGetResultsAsync(string stage = "OverdueReminder", DateOnly? businessDate = null,
        string? search = null, [FromQuery(Name = "page")] int pageNumber = 1, DateOnly? dateFrom = null, DateOnly? dateTo = null,
        int? month = null, int? year = null, string? load = null, string? paymentStatus = null, string? sendingMode = null, CancellationToken cancellationToken = default)
    {
        var timer = System.Diagnostics.Stopwatch.StartNew();
        SetFilters(stage, businessDate, search, pageNumber, dateFrom, dateTo, month, year, load, paymentStatus, sendingMode);
        await LoadReportAsync(cancellationToken);
        Response.Headers.CacheControl = "no-store";
        var t = Report?.Timings;
        Response.Headers["Server-Timing"] = string.Create(CultureInfo.InvariantCulture,
            $"web;dur={timer.Elapsed.TotalMilliseconds:F0}, sql;dur={t?.SourceMs ?? 0:F0}, map;dur={t?.MapMs ?? 0:F0}");
        return Partial("_CampaignResults", this);
    }

    /// <summary>The results / export / approval requests do not render the picker: load the tower list only when several towers are chosen, so "every tower chosen" still means All projects.</summary>
    private async Task EnsureTowersAsync(CancellationToken cancellationToken)
    {
        if (TowerIds.Count < 2 || Towers.Count > 0) return;
        var towers = await api.GetTowersAsync(cancellationToken);
        if (towers.IsSuccess && towers.Value is { } list) Towers = list;
    }

    private async Task LoadReportAsync(CancellationToken cancellationToken)
    {
        if (!ModelState.IsValid) { Outcome = ApiOutcome.ValidationError; Error = "Choose valid filters and retry."; return; }
        if (IsManual) { await EnsureTowersAsync(cancellationToken); await LoadManualAsync(cancellationToken); return; }
        // Instalments due from the next month on are neither Due nor Overdue and never listed: a period that starts there has nothing to ask the API for.
        if (DateFrom is { } start && !CollectionsDueMonth.IsInScope(start, CollectionsDisplay.DubaiToday())) { NothingDueYet = true; return; }
        await EnsureTowersAsync(cancellationToken);
        var result = await api.GetCampaignPreviewAsync(Stage, BusinessDate, null, Search, PageNumber, cancellationToken, DateFrom, DateTo, SingleTowerId, MinTotal, PaymentStatus, ManyTowerIds, SendingMode);
        Outcome = result.Outcome;
        Report = result.IsSuccess ? result.Value : null;
        Error = result.Detail;
        if (Report is not null)
        {
            // Environment readiness is advisory for the button only (the API refuses again at confirmation); a failed read must never block or hide the list.
            var readiness = await api.GetDispatchReadinessAsync(cancellationToken);
            DispatchBlockers = readiness.IsSuccess && readiness.Value is { Ready: false, Blockers: { } blockers } ? blockers : [];
        }
        BusinessDate ??= Report?.BusinessDate;
        // To shows the effective end. From stays EMPTY unless the user chose it: empty = All months = the whole history up to the end of the current month (the read scope); the stage
        // eligibility scope is unchanged. Writing the effective 2000-01-01 back into the input would turn the next page / export request into an explicit From
        // and widen the stage eligibility, so it is never done. The Period control (collections-campaign-filters.js) only fills the visible From / To inputs.
        DateTo ??= Report?.DateTo;
    }

    /// <summary>
    /// Manual: the list comes from the stored review records of the chosen campaign type, which already carry every mandatory check (reconciliation, contact and identity validation, shared phone,
    /// legal release, duplicate sends, frequency limit); the stage schedule is the one rule waived. The amount is calculated from the filters. The same filter is re-resolved at approval, so what is
    /// listed as eligible is exactly what can be sent.
    /// </summary>
    private async Task LoadManualAsync(CancellationToken cancellationToken)
    {
        var result = await api.GetReviewPageAsync(ApprovalFilter(), PageNumber, cancellationToken);
        Outcome = result.Outcome;
        ManualPage = result.IsSuccess ? result.Value : null;
        Error = result.Detail;
        if (ManualPage is null) return;
        var readiness = await api.GetDispatchReadinessAsync(cancellationToken);
        DispatchBlockers = readiness.IsSuccess && readiness.Value is { Ready: false, Blockers: { } blockers } ? blockers : [];
        CanSend = !(readiness.IsSuccess && readiness.Value is { CanSend: false });
    }

    public async Task<IActionResult> OnGetExportAsync(string stage = "OverdueReminder", string mode = "review",
        DateOnly? businessDate = null, string? search = null, DateOnly? dateFrom = null, DateOnly? dateTo = null,
        int? month = null, int? year = null, string? paymentStatus = null, string? sendingMode = null, CancellationToken cancellationToken = default)
    {
        SetFilters(stage, businessDate, search, 1, dateFrom, dateTo, month, year, null, paymentStatus, sendingMode);
        if (!ModelState.IsValid) { Outcome = ApiOutcome.ValidationError; Error = "Choose valid filters and retry."; return Page(); }
        // Same stage, preview date, tower, dates, minimum amount and search as the preview: the file contains exactly the previewed units.
        await EnsureTowersAsync(cancellationToken);
        var result = await api.GetCampaignExportAsync(Stage, mode, BusinessDate, null, Search, cancellationToken, DateFrom, DateTo, SingleTowerId, MinTotal, PaymentStatus, ManyTowerIds, SendingMode);
        if (result.IsSuccess && result.Value is { } file)
        {
            // Review export: the simplified employee workbook. Genesys export: the unchanged machine CSV.
            if (file.Xlsx is { Length: > 0 } workbook)
                return File(workbook, TigerCS.Application.Modules.Collections.Services.CollectionsCampaignReviewWorkbook.ContentType, file.XlsxFileName ?? "collections-review.xlsx");
            var bytes = Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(file.Csv)).ToArray();
            return File(bytes, "text/csv; charset=utf-8", file.FileName);
        }
        Outcome = result.Outcome;
        Error = result.Detail;
        RenderFull = true;
        return Page();
    }

    private void SetFilters(string stage, DateOnly? date, string? search, int page, DateOnly? from, DateOnly? to, int? month, int? year, string? load, string? paymentStatus, string? sendingMode = null)
    {
        SendingMode = CollectionsSendingModes.TryParse(sendingMode, out var parsedMode) ? parsedMode : CollectionsSendingModes.Auto;
        ContactList = ParseContactList(Request.Query["contactList"]);
        PaymentStatus = CampaignPaymentFilters.TryParse(paymentStatus, out var payment) ? CampaignPaymentFilters.Name(payment) : paymentStatus!.Trim();
        RequestedBusinessDate = date; RequestedDateTo = to;   // what the person asked for; BusinessDate / DateTo are later filled with the report's own defaults
        Stage = stage; BusinessDate = date; TowerIds = ParseTowerIds(Request.Query["towerId"]); Search = search?.Trim(); PageNumber = Math.Max(1, page);
        MinTotal = ReceivablesModel.ParseMinTotal(Request.Query);
        LoadNotice = CollectionsDisplay.NoticeText(load) is { Length: > 0 } text ? text : null;
        // Month / Year are no longer controls; an old bookmark that still carries them keeps working as its whole-month range.
        if (month is >= 1 and <= 12 && year is >= 2000 and <= 2100) { (DateFrom, DateTo) = CollectionsDateRanges.Month(year.Value, month.Value); }
        else { DateFrom = from; DateTo = to; }
    }

    public string? LoadNotice { get; private set; }

    /// <summary>The preview date the Period shortcuts are measured from: the chosen Preview Date, else today (Dubai).</summary>
    public DateOnly PeriodAnchor => BusinessDate ?? Report?.BusinessDate ?? CollectionsDisplay.DubaiToday();

    /// <summary>
    /// Before any preview the Period control starts on "Up to today" (From empty = the whole history, To = the preview date). The shown dates are exactly what is
    /// sent: nothing is substituted for another date field. After a preview the dates the user previewed with are shown unchanged.
    /// </summary>
    public DateOnly? FormDateTo => IsManual ? DateTo : DateTo ?? (DateFrom is null && Report is null && !RenderFull ? PeriodAnchor : null);

    /// <summary>Which Period shortcut the From / To dates correspond to; anything else is a custom range.</summary>
    public string Period
    {
        get
        {
            var to = FormDateTo; var anchor = PeriodAnchor;
            if (DateFrom is null && to == anchor) return "UpToToday";
            if (DateFrom is { } from && to is { } end)
            {
                var (monthFrom, monthTo) = CollectionsDateRanges.Month(anchor.Year, anchor.Month);
                if (from == monthFrom && end == monthTo) return "ThisMonth";
                if ((from, end) == CollectionsDateRanges.LastSixMonths(anchor)) return "LastSixMonths";
            }
            return "Custom";
        }
    }

    /// <summary>The less-used controls stay collapsed unless one of them holds a non-default value, so an applied filter is never hidden.</summary>
    private DateOnly? RequestedBusinessDate { get; set; }
    private DateOnly? RequestedDateTo { get; set; }

    /// <summary>"More filters" opens only for a filter the person actually changed (never for the defaults a preview writes back), so an applied filter is never hidden.</summary>
    public bool AdvancedOpen =>
        IsManual
        || DateFrom is not null
        || (RequestedDateTo is { } requestedTo && requestedTo != (RequestedBusinessDate ?? CollectionsDisplay.DubaiToday()))
        || !PaymentStatus.Equals("Both", StringComparison.OrdinalIgnoreCase)
        || (RequestedBusinessDate is { } requestedDate && requestedDate != CollectionsDisplay.DubaiToday())
        || MinTotal != ReceivablesModel.DefaultMinTotal;

    /// <summary>Environment settings that stop any send to Genesys (switched off, missing credentials or contact lists); empty when configured or when it could not be read.</summary>
    public IReadOnlyList<string> DispatchBlockers { get; private set; } = [];
    /// <summary>Whether the caller holds the send permission (read from the readiness answer); a reader is told why Send is unavailable.</summary>
    public bool CanSend { get; private set; } = true;

    /// <summary>
    /// The single, specific reason Approve &amp; Send is unavailable for the current preview (null = available). The browser adds "nothing ticked" on top of this. Checked in the order a person
    /// can act on: permission, financial reconciliation, Genesys/background-job configuration, financial data freshness, CRM contacts, the stage's send schedule, then whether any customer is Ready. Every gate is fail-closed and the
    /// API enforces the same ones again; this only names which one applies instead of a generic message.
    /// </summary>
    public string? ApproveBlockedReason
    {
        get
        {
            if (IsManual) return ManualBlockedReason();
            if (!IsManual) return "Auto mode is dispatched by the schedule. Use Manual to send a campaign now.";
            if (Report is not { } report) return "Preview customers first.";
            if (!report.CanExportReview) return "Your account does not have permission to approve and send campaigns (Collections send permission required).";
            if (!report.FinancialSourceValidated)
                return "The financial source is not reconciled, so nothing can be approved or sent." + (report.SourceGate?.Message is { Length: > 0 } why ? $" {why}" : "");
            if (DispatchBlockers.Count > 0) return "Sending to Genesys is not configured in this environment. " + string.Join(" ", DispatchBlockers);
            if (report.Snapshot is { IsReady: false, ReadyProblem: { } problem }) return $"Financial data: {problem}";
            if (report.Crm is { Blocks: true } crm) return crm.Message ?? "CRM contacts are unavailable, so contacts are unconfirmed.";
            if (TimingRestriction(report) is { } timing) return timing;
            if (report.ReadyCount > 0) return null;
            return report.TotalCount - report.NotEligibleCount > 0
                ? "No customers are Ready to send. The eligible customers need review or are blocked; see the reasons above."
                : "No customers are eligible in this preview.";
        }
    }

    /// <summary>
    /// Why Send to Genesys is unavailable in Manual (null = available). Checked in the order a person can act on: data, reconciliation, configuration,
    /// freshness, then whether any customer is eligible. The API enforces every gate again at confirmation; this only names which one applies. None of these gates applies to the file export.
    /// </summary>
    public string? ManualBlockedReason()
    {
        if (ManualPage is not { } page) return "Preview customers first.";
        if (!CanSend) return "Your account does not have permission to approve and send campaigns (Collections send permission required).";
        if (page.Run is not { } run) return "There is no approval data yet. Refresh the approval data, then preview again.";
        if (!run.SourceReconciled) return "The financial source is not reconciled, so nothing can be approved or sent.";
        if (DispatchBlockers.Count > 0) return "Sending to Genesys is not configured in this environment. " + string.Join(" ", DispatchBlockers);
        if (run.IsStale || run.Status != "Completed") return "The approval data is stale or still being prepared. Refresh the approval data, then preview again.";
        if (run.AsOfDate != CollectionsDisplay.DubaiToday()) return $"The approval data was built for {run.AsOfDate:dd MMM yyyy}, not today. Refresh the approval data, then preview again.";
        if (page.Counts.Ready == 0)
            return page.Counts.Total == 0 ? "No customers match these filters." : "No customers are eligible to send. See the reasons below; blocked customers are never sent.";
        return null;
    }

    /// <summary>Why the Manual Export review CSV is unavailable (null = available): the file only needs the permission and stored approval data.</summary>
    public string? ExportManualReviewBlockedReason =>
        ManualPage is not { } page ? "Preview customers first."
        : !CanSend ? "Your account does not have permission to export campaign lists (Collections send permission required)."
        : page.Run is null ? "There is no approval data yet. Refresh the approval data, then preview again."
        : page.Counts.Total == 0 ? "No customers match these filters."
        : null;

    /// <summary>
    /// Why Export for Genesys is unavailable (null = available). A file is not a send: it needs the permission, approval data and matching customers, and nothing else. Reconciliation, freshness,
    /// the build date, the schedule, Genesys configuration, background jobs and the Ready count never block it; what is unverified is labelled on the file instead.
    /// </summary>
    public string? ExportGenesysBlockedReason => IsManual ? ManualExportBlockedReason() : AutoExportBlockedReason();

    private string? ManualExportBlockedReason() =>
        ManualPage is not { } page ? "Preview customers first."
        : !CanSend ? "Your account does not have permission to export campaign lists (Collections send permission required)."
        : page.Run is null ? "There is no approval data yet. Refresh the approval data, then export."
        : page.Counts.Total == 0 ? "No customers match these filters, so there is nothing to export."
        : null;

    private string? AutoExportBlockedReason() =>
        Report is not { } report ? "Preview customers first."
        : !report.CanExportReview ? "Your account does not have permission to export campaign lists (Collections send permission required)."
        : Run is null ? "There is no approval data yet. Refresh the approval data, then export."
        : report.TotalCount == 0 ? "No customers match these filters, so there is nothing to export."
        : null;

    /// <summary>On the Auto page the refresh button is offered beside the export when the approval data is missing, stale, or built on another day: a refresh is advised, never required.</summary>
    public bool AutoExportNeedsRefresh => !IsManual && Report is { CanExportReview: true } && (Run is null || Run.IsStale || Run.Status != "Completed" || Run.AsOfDate != CollectionsDisplay.DubaiToday());

    /// <summary>The destination list of the export, as the page shows it: the chosen list in Manual, the campaign's own list in Auto.</summary>
    public string ExportDestinationLabel => ContactListLabel(IsManual ? SelectedContactList : ManualType.ToString());

    /// <summary>
    /// What the person must know about a file they just downloaded, shown on the next page view: the count, the destination, what was left out and why, what was combined, what is unverified, and
    /// whether any customer was exported before. The API's notices are shown as they are.
    /// </summary>
    public static string ExportNotice(GenesysExportDto file)
    {
        var text = new StringBuilder();
        text.Append(CultureInfo.InvariantCulture, $"Exported {file.RowCount:N0} row(s)");
        if (file.UnitCount > file.RowCount) text.Append(CultureInfo.InvariantCulture, $" ({file.UnitCount:N0} customer units)");
        text.Append(CultureInfo.InvariantCulture, $" for the {file.ContactList} list to {file.FileName} ({file.Currency} {file.TotalAmount:N2}).");
        if (file.ExcludedUnits > 0)
            text.Append(CultureInfo.InvariantCulture, $" Left out: {file.ExcludedUnits:N0} unit(s)")
                .Append(file.ExcludedRecords is { Count: > 0 } named ? $" ({string.Join(", ", named.Take(20).Select(r => $"{r.UnitCode}: {r.Code}"))}{(named.Count > 20 ? ", ..." : "")})." : ".");
        if (!file.DataVerified) text.Append(CultureInfo.InvariantCulture, $" {GenesysExportService.UnverifiedDataLabel}.");
        foreach (var note in file.Notices.Where(n => n.Length > 0)) text.Append(' ').Append(note);
        return text.ToString();
    }

    /// <summary>What a person can do about a Manual hold: the review workflow behind each exact reason.</summary>
    public static string ManualAdvice(string code) => code switch
    {
        "SharedPhoneMultipleUnits" or "ManualSharedPhone" => "This hold comes from approval data built before the per-unit calling decision; refresh the approval data and preview again.",
        "ReminderTypeOverlap" or "ManualReminderTypeOverlap" => "This customer is also in another campaign that is scheduled today. Send that campaign (Auto does it on its date), or send this one on another day.",
        "ContactedToday" => "Contact-frequency limit: one contact per customer per day. Try again tomorrow.",
        "LegalNoticeReleaseRequired" => "Legal notices have not been released for customers. Nothing is sent until Legal releases them.",
        "LegalCaseNotApproved" => "An internal Legal referral is never sent to a customer.",
        "StaleSource" or "SourceReconciliationRequired" => "Refresh the approval data once the financial source is current and reconciled.",
        "SourceMismatchUnresolved" => "This unit's snapshot differs from PACT even after a refresh (a synchronization or mapping defect). Investigate it on the Financial reconciliation page; other customers are not held back by it.",
        "PaymentStatusUnknown" => "The remaining amount of this balance cannot be determined reliably: it is missing or negative, or the source contradicts it (Status Paid with a remainder owed, or original and paid amounts that do not add up). Confirm the balance in PACT, refresh the approval data and preview again; this is a data rule, not the schedule.",
        "NoValidContact" or "CrmCustomerAmbiguous" or "CrmCompanyAmbiguous" or "ContactSourceConflict" or "CrmUnavailable" or "ConflictingContactDetails" =>
            "Review: fix the customer's contact in CRM or PACT, refresh the approval data and preview again.",
        _ => "Review: resolve the data problem at its source, refresh the approval data and preview again."
    };

    /// <summary>Why the review export is unavailable for the current preview (null = available). Same two conditions the page always applied; the API enforces them again.</summary>
    public string? ExportReviewProblem =>
        Report is not { } report ? "Preview customers first."
        : !report.CanExportReview ? "Your account does not have permission to export campaign lists."
        : report.Snapshot is { IsReady: false } snapshot ? ExportDataProblem(snapshot)
        : null;

    // Stale data needs a reload; a coverage gap's own message already says to load the missing data.
    private static string ExportDataProblem(TigerCS.Application.Modules.Collections.Dto.SnapshotStatusDto snapshot) =>
        snapshot.ReadyProblem is not { } problem ? "The receivables data is not ready. Reload the data, then export."
        : snapshot.IsFresh ? problem : $"{problem} Reload the data, then export.";

    /// <summary>Plain-language reasons the eligible customers are not Ready: the report-wide gates first, then the per-customer reasons found on the page shown.</summary>
    public IReadOnlyList<string> NotReadyReasons() => [.. NotReadyGateReasons(), .. NotReadyCustomerReasons()];

    /// <summary>The report-wide gates (financial data, reconciliation, CRM, schedule). They are mandatory safety information and are always shown in full.</summary>
    public IReadOnlyList<string> NotReadyGateReasons()
    {
        var reasons = new List<string>();
        if (Report is not { } report) return reasons;
        if (report.Snapshot is { IsReady: false } snapshot && snapshot.ReadyProblem is { } problem) reasons.Add($"Financial data: {problem}");
        if (!report.FinancialSourceValidated) reasons.Add("The financial source has not been reconciled against PACT.");
        if (report.Crm is { Blocks: true } crm) reasons.Add(crm.Message ?? "CRM contacts are unavailable, so contacts are unconfirmed.");
        if (TimingRestriction(report) is { } timing) reasons.Add(timing);
        return reasons;
    }

    /// <summary>The per-customer reasons found on the page shown, most frequent first. The page may fold the long tail into an expandable list.</summary>
    public IReadOnlyList<string> NotReadyCustomerReasons()
    {
        var reasons = new List<string>();
        if (Report is not { } report) return reasons;
        var counts = report.Items.Where(i => i.Status is not ("Ready" or "NotEligible"))
            .SelectMany(i => i.Reason.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)).Where(r => r != "Qualifies")
            .GroupBy(r => r).OrderByDescending(g => g.Count()).ThenBy(g => g.Key, StringComparer.Ordinal);
        foreach (var g in counts) reasons.Add($"{ReasonText(g.Key)} ({g.Count():N0} on this page)");
        return reasons;
    }

    /// <summary>Why the Auto list is not dispatched today because of WHEN: the stage\'s policy schedule (null = today is a scheduled day).</summary>
    public static string? TimingRestriction(CollectionsCampaignPreviewDto report) =>
        ScheduleRestriction(StageName(report.Stage), report.ScheduledDates, report.BusinessDate, report.LiveBusinessDate, report.IsScheduledDate);

    /// <summary>Auto mode, as a small label: the stage's calculated policy date(s) for the month of the preview date.</summary>
    public string PolicyScheduleLabel()
    {
        if (!CollectionsEnums.TryParse<CollectionsCampaignStage>(Stage, out var stage)) return "";
        var anchor = BusinessDate ?? Report?.BusinessDate ?? CollectionsDisplay.DubaiToday();
        return "Schedule: " + string.Join(" and ", CollectionsCampaignPolicy.ScheduledDates(stage, anchor).Select(d => d.ToString("dd MMM yyyy", CultureInfo.InvariantCulture)));
    }

    /// <summary>
    /// Why a list can be previewed but not sent today, in terms of the stage's real schedule (null = no schedule restriction). Preview is always allowed; SENDING is allowed only when the preview
    /// date is today (Dubai) and today is one of the stage's scheduled send days. The restriction is never overridden here.
    /// </summary>
    public static string? ScheduleRestriction(string stageName, IReadOnlyList<DateOnly> scheduledDates, DateOnly previewDate, DateOnly today, bool isScheduledDate)
    {
        var days = scheduledDates.Count == 0 ? "its scheduled days" : string.Join(" and ", scheduledDates.Select(d => d.ToString("dd MMM yyyy", CultureInfo.InvariantCulture)));
        if (previewDate != today)
            return $"Preview only: the preview date ({previewDate:dd MMM yyyy}) is not today ({today:dd MMM yyyy}, Dubai). A list can only be sent for today, and \"{stageName}\" is sent on {days}.";
        if (!isScheduledDate)
            return $"Preview only: \"{stageName}\" is scheduled for {days}; today ({today:dd MMM yyyy}) is not one of those days. The customer list can be previewed and exported for review, but it cannot be sent outside the schedule.";
        return null;
    }

    public static string ReasonText(string code) => code switch
    {
        "StaleSource" => "Financial data is older than the allowed age",
        "CoverageIncomplete" => "The selected dates are not fully loaded",
        "SourceReconciliationRequired" => "Financial source not reconciled",
        "SourceMismatchUnresolved" => "Snapshot differs from PACT after a refresh (investigate)",
        "NoValidContact" => "No valid mobile or email in CRM or PACT",
        "CrmCustomerAmbiguous" => "Several CRM customers match",
        "CrmCompanyAmbiguous" => "CRM sale cannot be tied to one company",
        "ContactSourceConflict" => "CRM and PACT contacts differ",
        "CrmUnavailable" => "CRM unavailable: contacts unconfirmed",
        "OutsideSchedule" => "Not a scheduled send day for this stage: preview only (Auto dispatches only on its scheduled days; Manual can send it at any time)",
        "InternalLegalReferralOnly" => "Internal legal referral: never sent to customers",
        "LegalNoticeReleaseRequired" => "Legal notice sending has not been released",
        "AmbiguousInstalments" => "Ambiguous instalments",
        "AmountPrecisionNeedsReview" => "Amount precision needs review",
        "MissingUnitIdentity" => "Unit identity missing",
        "ConflictingContactDetails" => "Conflicting contact details",
        "UnitAllocationNeedsReview" => "Unit allocation needs review",
        "ContradictoryPaymentStatus" => "Contradictory payment status",
        "CurrencyNeedsReview" => "Currency needs review",
        "SharedPhoneMultipleUnits" => "Phone number shared (old approval data; refresh)",
        "ManualReminderTypeOverlap" => "Also in another campaign scheduled today",
        "ManualSharedPhone" => "Phone number shared (old approval data; refresh)",
        "ReminderTypeOverlap" => "Also in another campaign scheduled today",
        "LegalCaseNotApproved" => "Internal legal referral: never sent to a customer",
        "ContactedToday" => "Already contacted today (frequency limit)",
        "PaymentStatusUnknown" => "Remaining amount cannot be determined reliably",
        "MissingCustomerIdentity" => "Customer identity missing",
        "DuplicateSourceRecord" => "Duplicate source record",
        "SourceAmountsInconsistent" => "Source amounts inconsistent",
        _ => code
    };

    /// <summary>The configured receivables start date (CollectionsSource:PactReceivables:StartDate, as in the API; 2026-01-01 when absent). Overdue and legal stages
    /// look back months, so an unedited From is this date and not 1 January of the preview year - the same default the API applies to a blank From.</summary>
    public DateOnly ConfiguredStart => DateTime.TryParse(configuration["CollectionsSource:PactReceivables:StartDate"], CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)
        ? DateOnly.FromDateTime(d) : DateOnly.FromDateTime(new TigerCS.Application.Modules.Collections.PactReceivablesOptions().StartDate);

    /// <summary>Defaults the browser re-derives for UNEDITED dates: From is empty (All months = the whole history); To is the month end for current-month/follow-up, else the preview date.
    /// The configured start date (<see cref="ConfiguredStart"/>) only bounds which units are ELIGIBLE for a stage; it is not shown as the From date.</summary>
    public DateOnly? DefaultFrom => null;
    public DateOnly DefaultTo => CollectionsEnums.TryParse<CollectionsCampaignStage>(Stage, out var stage)
        ? CollectionsCampaignPolicy.DefaultDateTo(stage, BusinessDate ?? Report?.BusinessDate ?? CollectionsDisplay.DubaiToday())
        : BusinessDate ?? Report?.BusinessDate ?? CollectionsDisplay.DubaiToday();

    // ------------------------------------------------------------------ approval and dispatch (one page: select, approve, send)

    [BindProperty(SupportsGet = true, Name = "dispatch")] public Guid? DispatchId { get; set; }
    public ReviewRunDto? Run { get; private set; }
    public bool RunActive => Run is { Status: "Queued" or "Running" };
    public SelectionSummaryDto? Summary { get; private set; }
    public DispatchDto? DispatchResult { get; private set; }
    public OverlapPageDto? Overlaps { get; private set; }
    public IReadOnlyList<DispatchDto> History { get; private set; } = [];
    public string? ApprovalError { get; private set; }
    public string? ApprovalNotice { get; private set; }
    public string ConfirmKey { get; private set; } = Guid.NewGuid().ToString("N");
    public string SelectionMode { get; private set; } = ReviewQueryService.ModeSelectedUnits;
    public string UnitKeys { get; private set; } = "";
    public int ContactPage { get; private set; } = 1;
    public static IReadOnlyList<string> FilterFieldNames => ["stage", "sendingMode", "businessDate", "dateFrom", "dateTo", "towerId", "minTotal", "paymentStatus", "search"];
    /// <summary>The Manual destination travels with the approval (it is part of the audited filter), but is not a customer filter.</summary>
    public static string ContactListLabel(string name) => name switch { "CurrentMonth" => "Current Month", "FollowUp" => "Follow Up", _ => name };

    /// <summary>Progress / failure of the approval-data refresh and the latest sends, shown above the list. Never throws: a missing piece just hides its panel.</summary>
    private async Task LoadApprovalStateAsync(CancellationToken ct)
    {
        try
        {
            var run = await api.GetReviewRunAsync(ct);
            if (run.IsSuccess) Run = run.Value;
            var history = await api.ListDispatchesAsync(ct);
            if (history.IsSuccess && history.Value is { } sends) History = sends.Take(10).ToList();
            if (DispatchId is { } id)
            {
                var dispatch = await api.GetDispatchAsync(id, ct);
                if (dispatch.IsSuccess) DispatchResult = dispatch.Value; else ApprovalError ??= Describe(dispatch.Outcome, dispatch.Detail);
            }
        }
        catch (Exception ex) when (ex is System.Text.Json.JsonException or HttpRequestException)
        {
            // The list itself must stay usable when the approval status cannot be read; the panels are simply absent.
            ApprovalError ??= "The approval and send status could not be read. The customer list below is unaffected; reload to retry.";
        }
    }

    /// <summary>Per-company refresh state (JSON, tiny): polled while a snapshot refresh runs, instead of re-requesting the whole results fragment.</summary>
    public async Task<IActionResult> OnGetStatusAsync(CancellationToken ct)
    {
        var result = await api.GetSnapshotStatusAsync(ct);
        Response.Headers.CacheControl = "no-store";
        return new JsonResult(CollectionsDisplay.StatusPayload(result.IsSuccess ? result.Value : null));
    }

    /// <summary>Progress poll for the approval-data refresh (JSON, no financial data).</summary>
    public async Task<IActionResult> OnGetRunAsync(CancellationToken ct)
    {
        var run = await api.GetReviewRunAsync(ct);
        return new JsonResult(run.IsSuccess && run.Value is { } r
            ? new { r.Status, r.Phase, r.ProgressPercent, r.Error, active = r.Status is "Queued" or "Running" }
            : new { Status = "Unknown", Phase = "", ProgressPercent = 0, Error = (string?)null, active = false });
    }

    /// <summary>Polled while a send is running: the dispatch status without the page.</summary>
    public async Task<IActionResult> OnGetSendAsync(Guid dispatch, CancellationToken ct)
    {
        var result = await api.GetDispatchAsync(dispatch, ct);
        return new JsonResult(result.IsSuccess && result.Value is { } d
            ? new { d.Status, d.Phase, active = d.Status is "Queued" or "Revalidating" or "Sending" }
            : new { Status = "Unknown", Phase = (string?)null, active = false });
    }

    public async Task<IActionResult> OnPostRefreshApprovalAsync(string? returnUrl, CancellationToken ct)
    {
        var started = await api.StartReviewRefreshAsync(null, ct);
        var target = !string.IsNullOrEmpty(returnUrl) && Url.IsLocalUrl(returnUrl) ? returnUrl : "/Collections/Campaigns";
        if (started.IsSuccess)
        {
            // A refresh that is already running shows its progress; one that finished moments ago is not repeated (in-process refreshes are rate limited), and the person is told so.
            // A newly started refresh is always Queued, so a Completed run in the answer is the reused one.
            if (started.Value is { Status: "Completed", IsStale: false } done)
                TempData["ApprovalNotice"] = $"The approval data was refreshed at {done.CompletedAtUtc:HH:mm} UTC and is current. A refresh is not repeated within a minute; try again later if the source data has changed.";
            else if (started.Value is { Status: "Completed" } stale)
                TempData["ApprovalNotice"] = $"The approval data from {stale.CompletedAtUtc:HH:mm} UTC was reused (a refresh is not repeated within a minute), but its source read is already stale. Try again in a minute.";
            return LocalRedirect(target);
        }
        SetFiltersFromForm();
        await LoadApprovalStateAsync(ct);
        ApprovalError = Describe(started.Outcome, started.Detail);
        return Page();
    }

    /// <summary>
    /// "Approve &amp; Send to Genesys", step 1: make sure the approved financial snapshot is ready, then re-resolve the exact selection (revalidation against eligibility,
    /// suppression and duplicate rules happens there) and show the confirmation. Nothing is sent here.
    /// </summary>
    public async Task<IActionResult> OnPostApproveAsync(string? selectionMode, string? unitKeys, int contactPage = 1, CancellationToken ct = default)
    {
        SetFiltersFromForm();
        await EnsureTowersAsync(ct);
        if (!IsManual) return await AutoModeRefusedAsync(ct);
        SelectionMode = string.Equals(selectionMode, ReviewQueryService.ModeAllMatching, StringComparison.OrdinalIgnoreCase) ? ReviewQueryService.ModeAllMatching : ReviewQueryService.ModeSelectedUnits;
        UnitKeys = unitKeys ?? "";
        ContactPage = Math.Max(1, contactPage);
        await LoadApprovalStateAsync(ct);

        // The approval data is a stored, validated copy of the financial snapshot. Missing, failed or stale data starts a visible refresh instead of approving from a guess.
        if (Run is null || Run.Status == "Failed" || (Run.Status == "Completed" && Run.IsStale))
        {
            var started = await api.StartReviewRefreshAsync(null, ct);
            if (!started.IsSuccess) { ApprovalError = Describe(started.Outcome, started.Detail); return Page(); }
            Run = started.Value;
        }
        if (RunActive)
        {
            ApprovalNotice = "The approved financial snapshot is being prepared. Approve & Send is available when it has finished.";
            return Page();
        }
        await ShowSummaryAsync(ct);
        return Page();
    }

    /// <summary>
    /// "Export for Genesys": the download-only file of every customer matching the filters (Manual: the ticked customers when any are ticked), in the format of the destination contact list, for a
    /// manual import. It needs no selection and no approval; it uploads nothing and marks no one as sent; the API records it in the audit trail. The file is served as UTF-8 with a byte-order mark,
    /// like the other CSV downloads of this page, so that names in Arabic open correctly in a spreadsheet. A refused export returns to the same mode, filters and list with the exact reason.
    /// </summary>
    public async Task<IActionResult> OnPostExportGenesysAsync(string? selectionMode, string? unitKeys, CancellationToken ct = default)
    {
        SetFiltersFromForm();
        await EnsureTowersAsync(ct);
        // Both modes export the same population and neither sends. Auto has no ticks. In Manual, ticked customers narrow the file; nothing ticked means every matching customer.
        var ticked = IsManual && string.Equals(selectionMode, ReviewQueryService.ModeSelectedUnits, StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(unitKeys);
        SelectionMode = ticked ? ReviewQueryService.ModeSelectedUnits : ReviewQueryService.ModeAllMatching;
        UnitKeys = ticked ? unitKeys! : "";
        var result = await api.ExportForGenesysAsync(BuildSelection(), ct);
        if (result.IsSuccess && result.Value is { } file)
        {
            // The file is a download, so what the person must know about it (count, exclusions, unverified data, repeats, an unchecked template) is shown on the next page view.
            TempData["ApprovalNotice"] = ExportNotice(file);
            return File(Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(file.Csv)).ToArray(), "text/csv; charset=utf-8", file.FileName);
        }
        // Back to the very same list: the mode, every filter and the chosen contact list travel in the URL, so a refused export never resets the page.
        TempData["ApprovalError"] = Describe(result.Outcome, result.Detail);
        return LocalRedirect(PageUrl(1));
    }

    /// <summary>The Manual "Export review CSV": the filtered list with status and reasons. Not a Genesys file; it needs the same data and permission rules as the Genesys export.</summary>
    public async Task<IActionResult> OnPostExportReviewAsync(CancellationToken ct = default)
    {
        SetFiltersFromForm();
        await EnsureTowersAsync(ct);
        if (!IsManual) return await AutoModeRefusedAsync(ct);
        var result = await api.ExportManualReviewAsync(ApprovalFilter(), ct);
        if (result.IsSuccess && result.Value is { } file)
            return File(Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(file.Csv)).ToArray(), "text/csv; charset=utf-8", file.FileName);
        await LoadApprovalStateAsync(ct);
        ApprovalError = Describe(result.Outcome, result.Detail);
        return Page();
    }

    /// <summary>A person sends only in Manual. Auto campaigns are dispatched by the scheduled Auto dispatcher (while AutoSendingEnabled is on), never from this page.</summary>
    private async Task<IActionResult> AutoModeRefusedAsync(CancellationToken ct)
    {
        await LoadApprovalStateAsync(ct);
        ApprovalError = "Auto campaigns are dispatched on their scheduled dates by the automatic dispatcher. To send a campaign now, switch to Manual.";
        return Page();
    }

    private async Task ShowSummaryAsync(CancellationToken ct)
    {
        var result = await api.SummarizeSelectionAsync(BuildSelection(), ct);
        if (!result.IsSuccess) { ApprovalError = Describe(result.Outcome, result.Detail); return; }
        var summary = result.Value!;
        // The reconciliation gate is decided once, for the source: no confirmation dialog is shown while it is not passed, whatever the customers' own status.
        if (!summary.SourceReconciled) { ApprovalError = "The financial source has not been reconciled against PACT for the configured procedure, so nothing can be sent."; return; }
        if (summary.Count == 0) { ApprovalError = "None of the selected customers is Ready to send now (needs review, paid, or already sent)."; return; }
        Summary = summary;
        var overlaps = await api.GetOverlapsAsync(1, ct);
        if (overlaps.IsSuccess) Overlaps = overlaps.Value;
    }

    /// <summary>Step 2: the confirmation. Creates the approval record (who, when, exactly which list) and queues the revalidate-and-upload job.</summary>
    public async Task<IActionResult> OnPostConfirmAsync(string? selectionMode, string? unitKeys, int expectedCount, string? expectedFingerprint, string? idempotencyKey, bool ackCampaign, CancellationToken ct = default)
    {
        SetFiltersFromForm();
        await EnsureTowersAsync(ct);
        if (!IsManual) return await AutoModeRefusedAsync(ct);
        SelectionMode = string.Equals(selectionMode, ReviewQueryService.ModeAllMatching, StringComparison.OrdinalIgnoreCase) ? ReviewQueryService.ModeAllMatching : ReviewQueryService.ModeSelectedUnits;
        UnitKeys = unitKeys ?? "";
        var request = new ConfirmDispatchRequest(BuildSelection(), expectedCount, expectedFingerprint ?? "", idempotencyKey ?? "", ackCampaign);
        var result = await api.ConfirmDispatchAsync(request, ct);
        if (result.IsSuccess) return Redirect($"/Collections/Campaigns?dispatch={result.Value!.DispatchId:D}");
        await LoadApprovalStateAsync(ct);
        ApprovalError = result.Outcome == ApiOutcome.Conflict ? (result.Detail ?? "The list changed. Review the current list and confirm again.") : Describe(result.Outcome, result.Detail);
        await ShowSummaryAsync(ct);   // re-show what is really there now
        return Page();
    }

    /// <summary>Retries ONLY the administrator email of a finished send; the Genesys upload is never repeated.</summary>
    public async Task<IActionResult> OnPostRetryEmailAsync(Guid dispatch, CancellationToken ct)
    {
        var result = await api.RetryAdminEmailAsync(dispatch, ct);
        if (!result.IsSuccess) TempData["ApprovalError"] = Describe(result.Outcome, result.Detail);
        return Redirect($"/Collections/Campaigns?dispatch={dispatch:D}");
    }

    public async Task<IActionResult> OnPostReconcileAsync(Guid dispatch, long batchId, string? resolution, string? note, CancellationToken ct)
    {
        var result = await api.ReconcileBatchAsync(dispatch, batchId, new ReconcileBatchRequest(resolution ?? "", note ?? ""), ct);
        if (!result.IsSuccess) TempData["ApprovalError"] = Describe(result.Outcome, result.Detail);
        return Redirect($"/Collections/Campaigns?dispatch={dispatch:D}");
    }

    /// <summary>Download of the contacts of one send in the Genesys contact-list columns.</summary>
    public async Task<IActionResult> OnGetContactsAsync(Guid dispatch, CancellationToken ct)
    {
        var bytes = await api.GetDispatchContactsCsvAsync(dispatch, ct);
        return bytes is null ? NotFound() : File(Encoding.UTF8.GetPreamble().Concat(bytes).ToArray(), "text/csv; charset=utf-8", $"genesys-contacts-{dispatch:N}.csv");
    }

    private SelectionRequest BuildSelection() => new(ApprovalFilter(), SelectionMode, null, null, ContactPage, 50,
        string.IsNullOrWhiteSpace(UnitKeys) ? [] : UnitKeys.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

    /// <summary>The same filters the preview used, read from the posted form (so the approval re-resolves exactly the list that was previewed).</summary>
    private void SetFiltersFromForm()
    {
        var form = Request.HasFormContentType ? Request.Form : null;
        string? Get(string name) => form is not null && form.TryGetValue(name, out var v) ? v.ToString() : null;
        DateOnly? Date(string name) => DateOnly.TryParse(Get(name), CultureInfo.InvariantCulture, out var d) ? d : null;
        PaymentStatus = CampaignPaymentFilters.TryParse(Get("paymentStatus"), out var payment) ? CampaignPaymentFilters.Name(payment) : "Both";
        Stage = Get("stage") is { Length: > 0 } st ? st : "OverdueReminder";
        SendingMode = CollectionsSendingModes.TryParse(Get("sendingMode"), out var postedMode) ? postedMode : CollectionsSendingModes.Auto;
        ContactList = ParseContactList(Get("contactList"));
        BusinessDate = Date("businessDate"); DateFrom = Date("dateFrom"); DateTo = Date("dateTo");
        TowerIds = ParseTowerIds(form is not null && form.TryGetValue("towerId", out var towerValues) ? towerValues : default);
        Search = Get("search")?.Trim();
        MinTotal = ParseMinTotalText(form?.ContainsKey("minTotal") == true, Get("minTotal"));
    }

    public static decimal? ParseMinTotalText(bool present, string? raw)
    {
        if (!present) return ReceivablesModel.DefaultMinTotal;
        var text = raw?.Trim() ?? "";
        if (text.Length == 0) return null;
        return decimal.TryParse(text, NumberStyles.Number, CultureInfo.InvariantCulture, out var value) && value >= 0 ? value : ReceivablesModel.DefaultMinTotal;
    }

    /// <summary>The Campaigns link that reopens the list with the filters of the posted form (used by Refresh / Retry so the person does not lose their place).</summary>
    public string ReturnUrl() => PageUrl(1).Replace("&render=full", "");

    public static string Money(decimal? amount) => amount?.ToString("N2", CultureInfo.InvariantCulture) ?? "—";

    public static string Label(string value) => value switch
    {
        "CurrentMonth" => "Current Month", "FollowUp" => "Follow Up", "LegalNotice" => "Legal Notice", "LegalCase" => "Legal Case",
        "UploadedToGenesys" => "Uploaded to Genesys", "UnknownOutcome" => "Unconfirmed", "CompletedWithErrors" => "Partial failure",
        "ReviewRequired" => "Review required", "NotConfigured" => "Not configured", _ => value
    };

    /// <summary>The send's headline for people: Success, Partial failure, Failure, or still running.</summary>
    public static (string Css, string Text) Verdict(DispatchDto d) => d.Status switch
    {
        "Queued" or "Revalidating" or "Sending" => ("running", "In progress: nothing is reported as sent until Genesys confirms it"),
        "Completed" when d.UploadedCount > 0 && d.FailedCount == 0 && d.UnknownCount == 0 => ("success", "Success: Genesys accepted every approved contact"),
        "Completed" => ("neutral", "Finished: nothing was uploaded to Genesys"),
        "CompletedWithErrors" when d.UploadedCount > 0 => ("partial", "Partial failure: some contacts were uploaded, others were not or are unconfirmed"),
        "CompletedWithErrors" => ("failure", "Failure: Genesys did not accept the contacts"),
        "ReviewRequired" => ("failure", "Stopped for review: nothing was sent"),
        "Cancelled" => ("neutral", "Cancelled before sending"),
        _ => ("failure", "Failure: nothing was sent")
    };

    public static string EmailStatusText(DispatchDto d) => d.AdminEmailStatus switch
    {
        "Sent" => $"Administrator report emailed to {d.AdminEmailRecipientCount} recipient(s)",
        "Failed" => "Administrator report email failed (the Genesys result is not affected)",
        "NotConfigured" => "Administrator report NOT sent: notification is not configured",
        "Disabled" => "Administrator report is switched off",
        "Sending" => "Administrator report is being sent",
        _ => d.Status is "Completed" or "CompletedWithErrors" ? "Administrator report pending" : "Administrator report is sent when the send finishes"
    };

    public static string CampaignActionLabel(string? action) => action switch { "Created" => "Created", "ReusedExisting" => "Reused existing", _ => "—" };

    public static string StageName(string reminderType) =>
        Enum.TryParse<TigerCS.Domain.Modules.Collections.Review.CampaignReminderType>(reminderType, out var type) && Enum.IsDefined(type)
            ? TigerCS.Domain.Modules.Collections.Review.CampaignReminderTypes.StageName(type) : reminderType;

    private static string Describe(ApiOutcome outcome, string? detail) => outcome switch
    {
        ApiOutcome.Forbidden => "Your account does not have permission for this Collections operation.",
        ApiOutcome.Unauthorized => "Your session has expired. Sign in again.",
        ApiOutcome.Unreachable => "The Tiger CS API could not be reached. Nothing was changed.",
        _ => detail ?? "The request could not be completed."
    };

    public async Task<IActionResult> OnPostLoadCoverageAsync(DateOnly dateFrom, DateOnly dateTo, string? returnUrl, int? companyId = null, CancellationToken cancellationToken = default)
    {
        var result = await api.RequestCoverageLoadAsync(dateFrom, dateTo, cancellationToken, companyId);
        var target = !string.IsNullOrEmpty(returnUrl) && Url.IsLocalUrl(returnUrl) ? returnUrl : "/Collections/Campaigns";
        return LocalRedirect(CollectionsDisplay.WithNotice(target, CollectionsDisplay.NoticeCode(result)));
    }

    /// <summary>"Last 6 months": six calendar months before the preview date through the preview date (explicit, so it overrides the stage default).</summary>
    public string LastSixMonthsUrl()
    {
        var preview = BusinessDate ?? Report?.BusinessDate ?? CollectionsDisplay.DubaiToday();
        var (from, to) = CollectionsDateRanges.LastSixMonths(preview);
        var query = HttpUtility.ParseQueryString(string.Empty);
        query["stage"] = Stage;
        if (IsManual) query["sendingMode"] = SendingMode;
        query["businessDate"] = preview.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        query["dateFrom"] = from.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        query["dateTo"] = to.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        foreach (var tower in TowerIds) query.Add("towerId", tower.ToString(CultureInfo.InvariantCulture));
        query["minTotal"] = MinTotalText;
        query["paymentStatus"] = PaymentStatus;
        if (!string.IsNullOrWhiteSpace(Search)) query["search"] = Search;
        return $"/Collections/Campaigns?{query}";
    }

    public string PageUrl(int page = 1, string? export = null)
    {
        var query = HttpUtility.ParseQueryString(string.Empty);
        query["stage"] = Stage;
        if (IsManual) query["sendingMode"] = SendingMode;
        if (IsManual && ContactList.Length > 0) query["contactList"] = SelectedContactList;
        if (BusinessDate is { } date) query["businessDate"] = date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        if (DateFrom is { } from) query["dateFrom"] = from.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        if (DateTo is { } to) query["dateTo"] = to.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        foreach (var tower in TowerIds) query.Add("towerId", tower.ToString(CultureInfo.InvariantCulture));
        query["minTotal"] = MinTotalText;
        query["paymentStatus"] = PaymentStatus;
        if (!string.IsNullOrWhiteSpace(Search)) query["search"] = Search;
        if (export is null) { query["page"] = page.ToString(CultureInfo.InvariantCulture); query["render"] = "full"; }
        else { query["handler"] = "Export"; query["mode"] = export; }
        return $"/Collections/Campaigns?{query}";
    }

    /// <summary>
    /// The approval filter for exactly this list: the stage, the due-date range, Due / Overdue, tower, minimum total and search of the preview, expressed for the stored approval data.
    /// Approval and dispatch re-resolve this same filter, so what is approved is what the preview listed (and only its Ready customers).
    /// </summary>
    public ReviewFilter ApprovalFilter()
    {
        string? reminderType = CollectionsEnums.TryParse<CollectionsCampaignStage>(Stage, out var stage)
            ? TigerCS.Domain.Modules.Collections.Review.CampaignReminderTypes.FromStage(stage).ToString() : null;
        // Manual: the chosen campaign type, no send date, the contact list the person chose (blank = the type's own list). The date range, Payment status and Minimum Total are applied to the
        // customer's instalments, so they decide the amount sent.
        if (IsManual)
            return new ReviewFilter(Customer: string.IsNullOrWhiteSpace(Search) ? null : Search, DueFrom: DateFrom, DueTo: DateTo, MinRemaining: MinTotal ?? 0m, ReminderType: reminderType,
                DueStatus: PaymentStatus is "Both" ? null : PaymentStatus, TowerId: SingleTowerId, TowerIds: ManyTowerIds, SendingMode: CollectionsSendingModes.Manual,
                ContactList: ContactList.Length > 0 ? SelectedContactList : null);
        return new ReviewFilter(Customer: string.IsNullOrWhiteSpace(Search) ? null : Search, DueFrom: DateFrom, DueTo: DateTo,
            MinRemaining: MinTotal ?? 0m, ReminderType: reminderType, DueStatus: PaymentStatus is "Both" ? null : PaymentStatus, TowerId: SingleTowerId, TowerIds: ManyTowerIds,
            SendingMode: SendingMode);
    }

    /// <summary>The stage's display name = the Genesys campaign name used when this list is sent.</summary>
    public string? CampaignName => CollectionsEnums.TryParse<CollectionsCampaignStage>(Stage, out var stage)
        ? TigerCS.Domain.Modules.Collections.Review.CampaignReminderTypes.StageName(stage) : null;

    public string ResultsUrl(int page = 1) => PageUrl(page).Replace("render=full", "handler=Results");

    /// <summary>Short review notes of a unit's CRM / PACT linking (the reasons the unit cannot be trusted as one customer).</summary>
    public static IEnumerable<string> ReviewNotes(CollectionsCampaignContactDto row, string? crmState = null)
    {
        if (row.Reason.Contains("CrmCustomerAmbiguous", StringComparison.Ordinal)) yield return "Several CRM customers: needs review";
        if (row.Reason.Contains("CrmCompanyAmbiguous", StringComparison.Ordinal)) yield return "CRM sale cannot be tied to one company: needs review";
        if (row.Reason.Contains("ContactSourceConflict", StringComparison.Ordinal)) yield return "CRM and PACT contacts differ: needs review";
        if (row.Reason.Contains("CrmUnavailable", StringComparison.Ordinal)) yield return "CRM unavailable: contact unconfirmed, needs review";
        // When CRM could not be used the reason above already says the contact is unconfirmed; it must not also claim that CRM holds nothing.
        if (row.Reason.Contains("NoValidContact", StringComparison.Ordinal) && !row.Reason.Contains("CrmUnavailable", StringComparison.Ordinal)) yield return NoContactNote(row, crmState);
    }

    /// <summary>Why a unit cannot be sent: "CRM or PACT" is only claimed when CRM was consulted and linked this unit; a switched-off feed or an unlinked owner is said as such.</summary>
    public static string NoContactNote(CollectionsCampaignContactDto row, string? crmState)
    {
        if (crmState == "Disabled") return "No mobile or email in PACT; CRM linking is switched off, so CRM was not checked: cannot be sent";
        var ambiguous = row.Reason.Contains("CrmCustomerAmbiguous", StringComparison.Ordinal) || row.Reason.Contains("CrmCompanyAmbiguous", StringComparison.Ordinal);
        if (crmState is not null && row.CrmCustomerId is null && !ambiguous) return "No mobile or email in PACT; no CRM owner is linked to this unit: cannot be sent";
        return "No mobile or email in CRM or PACT: cannot be sent";
    }

    /// <summary>The note under an empty mobile: when CRM could not be used it says so instead of claiming the customer has no mobile.</summary>
    public static string NoMobileNote(CollectionsCampaignContactDto row) =>
        row.Reason.Contains("CrmUnavailable", StringComparison.Ordinal) ? "Mobile unconfirmed: CRM unavailable" : "No mobile: voice / SMS not possible";

    /// <summary>Readiness to send. It never changes the displayed balances: a unit the stage rule does not make eligible keeps its Due / Overdue / Total and only says why it is not ready.</summary>
    public static string Readiness(CollectionsCampaignContactDto row) => row.Status switch
    {
        "Ready" => "Ready",
        "NeedsReview" => "Needs review",
        "PreviewOnly" => "Preview only",
        "InternalReview" => "Internal review",
        "NotEligible" => row.Reason.Contains("BelowThreshold", StringComparison.Ordinal) ? "Not eligible: below this stage's threshold" : "Not eligible: no balance qualifies for this stage",
        _ => row.Status,
    };

    /// <summary>The one-word state shown on every row: Ready, Blocked (needs review / internal) or Preview only (+ why), so the three groups are never mixed up.</summary>
    public static string RowState(CollectionsCampaignContactDto row) => row.Status switch
    {
        "Ready" => "Ready",
        "PreviewOnly" => $"Preview only: {ReasonText(row.Reason.Split(';')[0])}",
        "NeedsReview" or "InternalReview" => "Blocked",
        "NotEligible" => "Not eligible",
        _ => row.Status
    };

    public static string MoneyOrDash(decimal amount) => amount > 0 ? amount.ToString("N2", CultureInfo.InvariantCulture) : "—";
}
