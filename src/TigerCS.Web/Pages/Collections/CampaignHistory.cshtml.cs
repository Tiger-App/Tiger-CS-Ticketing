using System.Globalization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using TigerCS.Application.Modules.Collections.History;
using TigerCS.Web.Services.Api;

namespace TigerCS.Web.Pages.Collections;

/// <summary>
/// Campaign List (formerly Genesys Campaign History). Opening, filtering or paging it only reads stored data; it never uploads, resends or calls Genesys. The one POST handler, Sync from Genesys,
/// is a manual button that READS delivery results from Genesys and stores them locally (see <c>GenesysCampaignSyncService</c>): it sends nothing to Genesys and no job runs it. Two separate tabs:
/// Campaigns (uploads) and Export history (downloaded CSV files, which are not uploads).
/// </summary>
public sealed class CampaignHistoryModel(CollectionsApiClient api) : PageModel
{
    public const string SubmissionsTab = "submissions", ExportsTab = "exports";

    [BindProperty(SupportsGet = true)] public string? Tab { get; set; }
    [BindProperty(SupportsGet = true)] public DateOnly? From { get; set; }
    [BindProperty(SupportsGet = true)] public DateOnly? To { get; set; }
    [BindProperty(SupportsGet = true)] public string? Stage { get; set; }
    [BindProperty(SupportsGet = true)] public string? Status { get; set; }
    [BindProperty(SupportsGet = true)] public string? Mode { get; set; }
    [BindProperty(SupportsGet = true)] public Guid? Employee { get; set; }
    [BindProperty(SupportsGet = true)] public string? List { get; set; }
    [BindProperty(SupportsGet = true)] public string? Search { get; set; }
    [BindProperty(SupportsGet = true)] public int? Company { get; set; }
    [BindProperty(SupportsGet = true)] public int PageNumber { get; set; } = 1;

    public GenesysHistoryPageDto? Submissions { get; private set; }
    public GenesysExportHistoryPageDto? Exports { get; private set; }
    public ApiOutcome Outcome { get; private set; }
    public string? Error { get; private set; }
    public string? SyncNotice { get; private set; }
    public string? SyncProblem { get; private set; }

    public bool ShowExports => string.Equals(Tab, ExportsTab, StringComparison.OrdinalIgnoreCase);
    public bool HasFilter => From is not null || To is not null || !string.IsNullOrWhiteSpace(Stage) || !string.IsNullOrWhiteSpace(Status) || !string.IsNullOrWhiteSpace(Mode)
        || Company is not null || Employee is not null || !string.IsNullOrWhiteSpace(List) || !string.IsNullOrWhiteSpace(Search);

    public int TotalPages(int total, int pageSize) => Math.Max(1, (int)Math.Ceiling(total / (double)Math.Max(1, pageSize)));

    public async Task OnGetAsync(CancellationToken ct)
    {
        PageNumber = Math.Max(1, PageNumber);
        SyncNotice = TempData["SyncNotice"] as string; SyncProblem = TempData["SyncProblem"] as string;
        if (ShowExports)
        {
            var result = await api.GetGenesysExportHistoryAsync(From, To, PageNumber, ct);
            Outcome = result.Outcome; Error = result.Detail; Exports = result.IsSuccess ? result.Value : null;
            return;
        }
        var filter = new GenesysHistoryFilter(From, To, Stage, Status, Mode, Employee, List, Search, Company);
        var submissions = await api.GetGenesysHistoryAsync(filter, PageNumber, ct);
        Outcome = submissions.Outcome; Error = submissions.Detail; Submissions = submissions.IsSuccess ? submissions.Value : null;
    }

    /// <summary>The page's own URL with the current filters and the given page / tab (so paging and tab links keep the filters).</summary>
    public string PageUrl(int page, string? tab = null)
    {
        var parts = new List<string>();
        void Add(string name, string? value) { if (!string.IsNullOrWhiteSpace(value)) parts.Add($"{name}={Uri.EscapeDataString(value)}"); }
        var active = tab ?? (ShowExports ? ExportsTab : SubmissionsTab);
        if (active == ExportsTab) Add("tab", ExportsTab);
        Add("from", From?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)); Add("to", To?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        if (active != ExportsTab)
        {
            Add("stage", Stage); Add("status", Status); Add("mode", Mode); Add("employee", Employee?.ToString("D")); Add("list", List); Add("search", Search); Add("company", Company?.ToString(CultureInfo.InvariantCulture));
        }
        if (page > 1) Add("pageNumber", page.ToString(CultureInfo.InvariantCulture));
        return "/Collections/CampaignList" + (parts.Count == 0 ? "" : "?" + string.Join('&', parts));
    }

    /// <summary>
    /// Manual Sync from Genesys for the campaigns on the page (or one campaign). Reads delivery results from Genesys and stores them; sends nothing to Genesys. Redirects back to the same filtered page.
    /// </summary>
    public async Task<IActionResult> OnPostSyncAsync(string? ids, bool force, string? returnUrl, CancellationToken ct)
    {
        var list = (ids ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(x => Guid.TryParse(x, out var g) ? g : (Guid?)null).OfType<Guid>().ToList();
        var result = await api.SyncGenesysAsync(list, force, ct);
        if (result.IsSuccess && result.Value is { } r)
        {
            TempData["SyncNotice"] = $"Synced {r.Synced} campaign(s) from Genesys" + (r.Skipped > 0 ? $", {r.Skipped} already confirmed" : "") + (r.Failed > 0 ? $", {r.Failed} failed" : "") + ".";
            if (r.Problems.Count > 0) TempData["SyncProblem"] = string.Join(" ", r.Problems);
        }
        else TempData["SyncProblem"] = result.Outcome == ApiOutcome.Forbidden ? "Your account does not have permission to sync from Genesys." : result.Detail ?? "The sync from Genesys could not be completed.";
        return LocalRedirect(!string.IsNullOrEmpty(returnUrl) && Url.IsLocalUrl(returnUrl) ? returnUrl : "/Collections/CampaignList");
    }

    /// <summary>The company words of a filter value.</summary>
    public static string CompanyLabel(int company) => company == 4 ? "Dubai" : company == 32 ? "Sharjah" : $"Company {company}";

    /// <summary>"—" while a figure is not confirmed yet (never a made-up zero).</summary>
    public static string Figure(int? value) => value is { } v ? v.ToString("N0", CultureInfo.InvariantCulture) : "—";

    /// <summary>The CSS modifier of a status badge. The text is always the status word, never colour alone.</summary>
    public static string BadgeClass(string state) => state switch
    {
        GenesysUploadStates.Uploaded => "history-badge--ok",
        GenesysUploadStates.Failed => "history-badge--bad",
        GenesysUploadStates.Partial or GenesysUploadStates.Unknown => "history-badge--warn",
        GenesysUploadStates.Pending or GenesysUploadStates.Uploading => "history-badge--run",
        _ => "history-badge--idle"
    };

    public static string Money(decimal amount) => CollectionsDisplay.Money(amount);

    public static string ProblemText(ApiOutcome outcome, string? detail) => outcome switch
    {
        ApiOutcome.Forbidden => "Your account does not have permission to view the Campaign List.",
        ApiOutcome.Unauthorized => "Your session has expired. Sign in again.",
        ApiOutcome.ValidationError => detail ?? "The selected filters are invalid.",
        _ => (detail ?? "The Campaign List is unavailable.") + " It could not be loaded."
    };
}
