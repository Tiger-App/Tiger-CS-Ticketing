using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using TigerCS.Application.Modules.Collections.History;
using TigerCS.Web.Services.Api;

namespace TigerCS.Web.Pages.Collections;

/// <summary>One Genesys submission and its unit records. READ ONLY: no POST handler, no resend; opening it only reads stored history.</summary>
public sealed class CampaignHistoryDetailsModel(CollectionsApiClient api) : PageModel
{
    [BindProperty(SupportsGet = true)] public Guid Id { get; set; }
    [BindProperty(SupportsGet = true)] public string? Result { get; set; }
    [BindProperty(SupportsGet = true)] public string? Search { get; set; }
    [BindProperty(SupportsGet = true)] public int PageNumber { get; set; } = 1;

    public GenesysSubmissionDetailDto? Detail { get; private set; }
    public GenesysRecordsPageDto? Records { get; private set; }
    public ApiOutcome Outcome { get; private set; }
    public string? Error { get; private set; }

    public int TotalPages => Records is null ? 1 : Math.Max(1, (int)Math.Ceiling(Records.TotalCount / (double)Math.Max(1, Records.PageSize)));

    public async Task<IActionResult> OnGetAsync(CancellationToken ct)
    {
        PageNumber = Math.Max(1, PageNumber);
        SyncNotice = TempData["SyncNotice"] as string; SyncProblem = TempData["SyncProblem"] as string;
        var detail = await api.GetGenesysSubmissionAsync(Id, ct);
        Outcome = detail.Outcome; Error = detail.Detail;
        if (!detail.IsSuccess) return detail.Outcome == ApiOutcome.NotFound ? NotFound() : Page();
        Detail = detail.Value;
        var records = await api.GetGenesysSubmissionRecordsAsync(Id, Result, Search, PageNumber, ct);
        Records = records.IsSuccess ? records.Value : null;
        if (!records.IsSuccess) { Outcome = records.Outcome; Error = records.Detail; }
        return Page();
    }

    public string? SyncNotice { get; private set; }
    public string? SyncProblem { get; private set; }

    public async Task<IActionResult> OnPostSyncAsync(bool force, CancellationToken ct)
    {
        var result = await api.SyncGenesysAsync([Id], force, ct);
        if (result.IsSuccess && result.Value is { } r) { TempData["SyncNotice"] = $"Synced {r.Synced} from Genesys."; if (r.Problems.Count > 0) TempData["SyncProblem"] = string.Join(" ", r.Problems); }
        else TempData["SyncProblem"] = result.Outcome == ApiOutcome.Forbidden ? "Your account does not have permission to sync from Genesys." : result.Detail ?? "The sync from Genesys could not be completed.";
        return LocalRedirect($"/Collections/CampaignList/{Id:D}");
    }

    public string PageUrl(int page)
    {
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(Result)) parts.Add("result=" + Uri.EscapeDataString(Result));
        if (!string.IsNullOrWhiteSpace(Search)) parts.Add("search=" + Uri.EscapeDataString(Search));
        if (page > 1) parts.Add("pageNumber=" + page);
        return $"/Collections/CampaignList/{Id:D}" + (parts.Count == 0 ? "" : "?" + string.Join('&', parts));
    }
}
