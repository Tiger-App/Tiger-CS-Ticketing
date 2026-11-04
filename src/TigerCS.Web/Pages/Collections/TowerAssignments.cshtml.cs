using System.Globalization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using TigerCS.Application.Modules.Collections.TowerAssignments;
using TigerCS.Web.Services.Api;

namespace TigerCS.Web.Pages.Collections;

/// <summary>
/// Administration -> Collections Tower Assignments (served at /Admin/CollectionsTowerAssignments): one compact table of the Collections team and the towers each holds for a month. Editing one collector opens the same page with that row expanded
/// (a searchable multi-select; works without script). The page decides nothing: the Api checks who may read or change, validates the collector and the towers, and enforces one collector per tower.
/// </summary>
public sealed class TowerAssignmentsModel(TowerAssignmentsApiClient api) : PageModel
{
    private const string DubaiZone = "Asia/Dubai";

    public static readonly string[] MonthNames = [.. CultureInfo.InvariantCulture.DateTimeFormat.MonthNames.Take(12)];

    public int Year { get; private set; }
    public int Month { get; private set; }
    public Guid? EditEmployeeId { get; private set; }
    public TowerAssignmentMonthDto? Data { get; private set; }
    public IReadOnlyList<TowerAssignmentHistoryDto> History { get; private set; } = [];
    public ApiOutcome Outcome { get; private set; }
    public string? LoadError { get; private set; }

    [TempData] public string? Notice { get; set; }
    [TempData] public string? Error { get; set; }

    public async Task OnGetAsync(int? year, int? month, Guid? edit, CancellationToken ct)
    {
        (Year, Month) = Resolve(year, month);
        EditEmployeeId = edit;
        var result = await api.GetMonthAsync(Year, Month, ct);
        Outcome = result.Outcome;
        LoadError = result.IsSuccess ? null : Describe(result.Outcome, result.Detail);
        Data = result.IsSuccess ? result.Value : null;
        if (Data is not null)
        {
            var history = await api.GetHistoryAsync(Year, Month, ct);
            History = history.IsSuccess && history.Value is not null ? [.. history.Value.Take(50)] : [];
        }
    }

    /// <param name="towerId">The ticked towers (the shared picker names its checkboxes <c>towerId</c>); the complete set the employee should hold.</param>
    /// <param name="activate">True: Save and activate; false: Save as draft.</param>
    /// <param name="moveFromOthers">True: take towers another collector holds this month.</param>
    public async Task<IActionResult> OnPostSaveAsync(int year, int month, Guid employeeId, int[]? towerId, bool activate, bool moveFromOthers, CancellationToken ct)
    {
        var result = await api.SaveEmployeeAsync(employeeId, new SaveEmployeeTowersRequest(year, month, towerId ?? [], activate, moveFromOthers), ct);
        if (result.IsSuccess) Notice = activate ? "Saved and activated." : "Saved as draft. It takes effect when activated.";
        else Error = Describe(result.Outcome, result.Detail);
        return RedirectToPage(new { year, month, edit = result.IsSuccess ? (Guid?)null : employeeId });
    }

    public async Task<IActionResult> OnPostActivateAsync(int year, int month, CancellationToken ct)
    {
        var result = await api.ActivateAsync(new ActivateTowerAssignmentsRequest(year, month), ct);
        if (result.IsSuccess) Notice = "All drafts of this month are active.";
        else Error = Describe(result.Outcome, result.Detail);
        return RedirectToPage(new { year, month });
    }

    public async Task<IActionResult> OnPostCopyAsync(int year, int month, CancellationToken ct)
    {
        var result = await api.CopyPreviousAsync(new CopyPreviousMonthRequest(year, month), ct);
        if (result.IsSuccess && result.Value is { } copied)
            Notice = $"Copied {copied.Copied} tower(s) from the previous month as drafts"
                + (copied.SkippedAlreadyAssigned + copied.SkippedUnavailable > 0
                    ? $" ({copied.SkippedAlreadyAssigned} already assigned, {copied.SkippedUnavailable} skipped because the tower or collector is no longer available)." : ".");
        else Error = Describe(result.Outcome, result.Detail);
        return RedirectToPage(new { year, month });
    }

    public static (int Year, int Month) Resolve(int? year, int? month)
    {
        var (currentYear, currentMonth) = CollectionsMonths.Of(DateTime.UtcNow, DubaiZone);
        return year is >= 2000 and <= 2200 && month is >= 1 and <= 12 ? (year.Value, month.Value) : (currentYear, currentMonth);
    }

    public static string Describe(ApiOutcome outcome, string? detail) => outcome switch
    {
        ApiOutcome.Forbidden or ApiOutcome.Unauthorized => "Your account is not allowed to do this. Tower assignments are managed by the Collections manager.",
        ApiOutcome.Conflict or ApiOutcome.ValidationError => detail ?? "The change could not be saved.",
        ApiOutcome.ServiceUnavailable => detail ?? "The tower list is not available right now. Try again shortly.",
        _ => detail ?? "The tower assignments could not be loaded."
    };

    public static string TowerText(TowerAssignmentRowDto t) => CollectionsDisplay.TowerLabel(new(t.TowerId, t.TowerCode, t.TowerName, t.CompanyId, true));
}
