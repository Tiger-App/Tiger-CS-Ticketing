using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using TigerCS.Application.Modules.Collections.Exceptions;
using TigerCS.Application.Modules.Collections.Services;
using TigerCS.Domain.Modules.Collections;
using TigerCS.Domain.Modules.Collections.Review;
using TigerCS.Web.Services.Api;

namespace TigerCS.Web.Pages.Collections;

/// <summary>
/// Collections -> Manage Exceptions (/Collections/Exceptions): the GULF_CUSTOMERS units of the current approval data, searchable by unit code, tower and customer, with a manual inclusion in the current
/// Manual campaign (today, the chosen campaign type). Server-rendered; works without script. The page decides nothing: the Api checks the send right, re-validates every unit against the stored data
/// and refuses a permanent special case; Preview, Export, Approval and the upload read the inclusion again.
/// </summary>
public sealed class ExceptionsModel(CollectionsApiClient api) : PageModel
{
    public static readonly (string Stage, string Label)[] Stages =
    [
        ("OverdueReminder", "Overdue"), ("CurrentMonthReminder", "Current Month"), ("FollowUpReminder", "Follow Up"), ("LegalNotice", "Legal Notice"), ("LegalReferral", "Legal Case")
    ];

    public string Stage { get; private set; } = "CurrentMonthReminder";
    public string? Search { get; private set; }
    public int PageNumber { get; private set; } = 1;
    public ExceptionListDto? Data { get; private set; }
    public string? LoadError { get; private set; }

    [TempData] public string? Notice { get; set; }
    [TempData] public string? Error { get; set; }

    public async Task OnGetAsync(string? stage, string? search, int page = 1, CancellationToken ct = default)
    {
        Stage = Normalize(stage);
        Search = string.IsNullOrWhiteSpace(search) ? null : search.Trim();
        PageNumber = Math.Max(1, page);
        var result = await api.GetExceptionUnitsAsync(ReminderType(Stage), Search, null, PageNumber, ct);
        Data = result.IsSuccess ? result.Value : null;
        LoadError = result.IsSuccess ? null : Describe(result.Outcome, result.Detail);
    }

    public Task<IActionResult> OnPostIncludeAsync(string? stage, string? search, int page, string[]? unitKey, CancellationToken ct) => ChangeAsync(stage, search, page, unitKey, include: true, ct);

    public Task<IActionResult> OnPostRemoveAsync(string? stage, string? search, int page, string[]? unitKey, CancellationToken ct) => ChangeAsync(stage, search, page, unitKey, include: false, ct);

    private async Task<IActionResult> ChangeAsync(string? stage, string? search, int page, string[]? unitKey, bool include, CancellationToken ct)
    {
        stage = Normalize(stage);
        if (unitKey is not { Length: > 0 }) { Error = "Select at least one unit."; return Back(stage, search, page); }
        var request = new ExceptionChangeRequest(ReminderType(stage), unitKey);
        var result = include ? await api.IncludeExceptionUnitsAsync(request, ct) : await api.RemoveExceptionUnitsAsync(request, ct);
        if (result.IsSuccess && result.Value is { } change)
        {
            Notice = (include ? $"{change.Changed} unit(s) included in today's Manual campaign." : $"{change.Changed} manual inclusion(s) removed.")
                + (change.Unchanged > 0 ? $" {change.Unchanged} unchanged." : "")
                + (change.Rejected.Count > 0 ? $" {change.Rejected.Count} not changed: {string.Join(" ", change.Rejected.Select(r => r.Reason).Distinct())}" : "");
        }
        else Error = Describe(result.Outcome, result.Detail);
        return Back(stage, search, page);
    }

    /// <summary>Back to the same list. Built as a URL: an anonymous route value named <c>page</c> would replace the Razor Page route key.</summary>
    private IActionResult Back(string stage, string? search, int page) =>
        Redirect($"/Collections/Exceptions?stage={Uri.EscapeDataString(stage)}&search={Uri.EscapeDataString(search ?? "")}&page={Math.Max(1, page)}");

    public static string Normalize(string? stage) => Stages.Any(s => s.Stage.Equals(stage, StringComparison.OrdinalIgnoreCase)) ? Stages.First(s => s.Stage.Equals(stage, StringComparison.OrdinalIgnoreCase)).Stage : "CurrentMonthReminder";

    public static string ReminderType(string stage) =>
        CollectionsEnums.TryParse<CollectionsCampaignStage>(stage, out var parsed) ? CampaignReminderTypes.FromStage(parsed).ToString() : nameof(CampaignReminderType.CurrentMonth);

    public static string Describe(ApiOutcome outcome, string? detail) => outcome switch
    {
        ApiOutcome.Forbidden or ApiOutcome.Unauthorized => "Your account is not allowed to manage exceptions. It needs the right to send Collections campaigns.",
        ApiOutcome.Conflict or ApiOutcome.ValidationError => detail ?? "The request could not be completed.",
        _ => detail ?? "The exception units could not be loaded right now. Try again shortly."
    };

    public int TotalPages => Data is null ? 1 : Math.Max(1, (int)Math.Ceiling(Data.TotalCount / (double)Data.PageSize));
}
