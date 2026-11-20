using System.Globalization;
using TigerCS.Application.Modules.Collections.Dto;
using TigerCS.Web.Services.Api;

namespace TigerCS.Web.Pages.Collections;

/// <summary>Display helpers shared by the Receivables and Campaigns pages.</summary>
public static class CollectionsDisplay
{
    /// <summary>Dubai time through the one shared conversion (<see cref="TigerCS.Web.Models.TicketDisplay.ToUae"/>); Gulf Standard Time has no daylight saving.</summary>
    public static string DubaiTime(DateTime? utc) => utc is { } value
        ? TigerCS.Web.Models.TicketDisplay.ToUae(value).ToString("dd MMM yyyy HH:mm", CultureInfo.InvariantCulture) + " (Dubai)"
        : "never";

    /// <summary>The companies whose data could not be loaded: never loaded, stale, last refresh failed, or part of the requested dates missing. Null when everything is loaded.</summary>
    public static IReadOnlyList<SnapshotCompanyStatusDto>? FailedCompanies(SnapshotStatusDto? status)
    {
        if (status is null) return null;
        var gaps = status.Gaps.Select(g => g.CompanyId).ToHashSet();
        var failed = status.Companies.Where(c => !c.HasSnapshot || c.Freshness != "Fresh" || c.LastRefreshFailed || gaps.Contains(c.CompanyId)).ToList();
        return failed.Count == 0 ? null : failed;
    }

    /// <summary>
    /// The compact JSON the pages poll while a refresh runs (a few hundred bytes; no receivables). <c>active</c> is true only while a refresh of some company really executes within its
    /// expected duration - every other state is final until someone acts, so the browser stops checking.
    /// </summary>
    public static object StatusPayload(SnapshotStatusDto? status) => status is null
        ? new { available = false, active = false, companies = Array.Empty<object>() }
        : new
        {
            available = true,
            active = status.LoadInProgress,
            scheduledRefresh = status.ScheduledRefreshActive,
            schedule = status.RefreshSchedule,
            companies = status.Companies.Select(c => new
            {
                id = c.CompanyId, name = c.CompanyName, state = c.State, loading = c.IsLoading, hasData = c.HasData,
                startedUtc = c.IsLoading ? c.RefreshStartedUtc : null, lastSuccess = c.LastSuccessUtc is null ? null : DubaiTime(c.LastSuccessUtc),
                failure = c.FailureText, ageMinutes = c.AgeMinutes
            }).ToArray()
        };

    /// <summary>"Tiger Group Sharjah" / "Tiger Group Dubai and Tiger Group Sharjah".</summary>
    public static string CompanyList(IEnumerable<SnapshotCompanyStatusDto> companies) => string.Join(" and ", companies.Select(c => c.CompanyName));

    /// <summary>Tower option label: number and name; a tower whose name is only a placeholder (blank, or repeating its number) shows just the number - never a made-up name.</summary>
    public static string TowerLabel(CollectionsTowerDto tower) =>
        TigerCS.Domain.Modules.Collections.CollectionsTowerName.IsUsable(tower.TowerName, tower.TowerNumber) ? $"{tower.TowerNumber} - {tower.TowerName.Trim()}" : tower.TowerNumber;

    /// <summary>The tower of a table row: its real name, else its number, else the project code or a dash.</summary>
    public static string TowerCell(string? name, string? number, string? projectCode = null) =>
        TigerCS.Domain.Modules.Collections.CollectionsTowerName.IsUsable(name, number) ? name!.Trim()
        : !string.IsNullOrWhiteSpace(number) ? number.Trim() : !string.IsNullOrWhiteSpace(projectCode) ? projectCode.Trim() : "—";

    public static string Money(decimal amount) => amount.ToString("N2", CultureInfo.InvariantCulture);

    /// <summary>Today in Dubai (UTC+4, no DST): the preview date when a page has no report to take it from.</summary>
    public static DateOnly DubaiToday() => DateOnly.FromDateTime(TigerCS.Web.Models.TicketDisplay.ToUae(DateTime.UtcNow));

    public static string NoticeText(string? code) => code switch
    {
        "started" => "Loading the missing data in the background. Reload this page in a few minutes; the list stays marked incomplete until it finishes.",
        "running" => "A load is already running. Reload this page in a few minutes.",
        "covered" => "The selected dates are already loaded and fresh.",
        "failed" => "The load could not be started. Retry, or ask an administrator to check the receivables refresh.",
        _ => ""
    };

    /// <summary>The current local URL with the one-shot load notice replaced (open redirects are refused by the caller).</summary>
    public static string WithNotice(string localUrl, string code)
    {
        var cleaned = System.Text.RegularExpressions.Regex.Replace(localUrl, @"([?&])load=[^&]*&?", "$1").TrimEnd('?', '&');
        return cleaned + (cleaned.Contains('?') ? "&" : "?") + "load=" + code;
    }

    public static string NoticeCode(ApiResult<ReceivablesRangeLoadDto> result) => !result.IsSuccess ? "failed"
        : result.Value!.Accepted ? "started" : result.Value.AlreadyRunning ? "running" : result.Value.AlreadyCovered ? "covered" : "failed";
}