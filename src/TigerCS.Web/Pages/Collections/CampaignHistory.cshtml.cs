using System.Globalization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using TigerCS.Application.Modules.Collections.History;
using TigerCS.Web.Services.Api;

namespace TigerCS.Web.Pages.Collections;

/// <summary>
/// Genesys Campaign History. READ ONLY: this page has no POST handler and calls only the history GET routes, so opening, filtering or paging it can never upload, resend, refresh or
/// otherwise change anything. Two separate tabs: Submissions (campaign uploads) and Export history (downloaded CSV files, which are not uploads).
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
    [BindProperty(SupportsGet = true)] public int PageNumber { get; set; } = 1;

    public GenesysHistoryPageDto? Submissions { get; private set; }
    public GenesysExportHistoryPageDto? Exports { get; private set; }
    public ApiOutcome Outcome { get; private set; }
    public string? Error { get; private set; }

    public bool ShowExports => string.Equals(Tab, ExportsTab, StringComparison.OrdinalIgnoreCase);
    public bool HasFilter => From is not null || To is not null || !string.IsNullOrWhiteSpace(Stage) || !string.IsNullOrWhiteSpace(Status) || !string.IsNullOrWhiteSpace(Mode)
        || Employee is not null || !string.IsNullOrWhiteSpace(List) || !string.IsNullOrWhiteSpace(Search);

    public int TotalPages(int total, int pageSize) => Math.Max(1, (int)Math.Ceiling(total / (double)Math.Max(1, pageSize)));

    public async Task OnGetAsync(CancellationToken ct)
    {
        PageNumber = Math.Max(1, PageNumber);
        if (ShowExports)
        {
            var result = await api.GetGenesysExportHistoryAsync(From, To, PageNumber, ct);
            Outcome = result.Outcome; Error = result.Detail; Exports = result.IsSuccess ? result.Value : null;
            return;
        }
        var filter = new GenesysHistoryFilter(From, To, Stage, Status, Mode, Employee, List, Search);
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
            Add("stage", Stage); Add("status", Status); Add("mode", Mode); Add("employee", Employee?.ToString("D")); Add("list", List); Add("search", Search);
        }
        if (page > 1) Add("pageNumber", page.ToString(CultureInfo.InvariantCulture));
        return "/Collections/CampaignHistory" + (parts.Count == 0 ? "" : "?" + string.Join('&', parts));
    }

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
        ApiOutcome.Forbidden => "Your account does not have permission to view the Genesys Campaign History.",
        ApiOutcome.Unauthorized => "Your session has expired. Sign in again.",
        ApiOutcome.ValidationError => detail ?? "The selected filters are invalid.",
        _ => (detail ?? "The Genesys Campaign History is unavailable.") + " It could not be loaded."
    };
}
