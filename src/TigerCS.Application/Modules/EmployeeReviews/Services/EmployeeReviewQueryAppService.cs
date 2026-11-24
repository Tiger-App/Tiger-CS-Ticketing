using TigerCS.Application.Modules.EmployeeReviews.Abstractions;
using TigerCS.Application.Modules.EmployeeReviews.Dto;
using TigerCS.Application.Modules.IdentityAndAccess.Abstractions;
using TigerCS.Domain.Common;
using TigerCS.Domain.Modules.IdentityAndAccess;

namespace TigerCS.Application.Modules.EmployeeReviews.Services;

public enum EmployeeReviewQueryOutcome
{
    Success,

    /// <summary>The employee is not someone the caller may see reviews of.</summary>
    Forbidden
}

public sealed record EmployeeReviewQueryResult<T>(EmployeeReviewQueryOutcome Outcome, T? Value = default)
{
    public static EmployeeReviewQueryResult<T> Success(T value) => new(EmployeeReviewQueryOutcome.Success, value);

    public static EmployeeReviewQueryResult<T> Forbidden() => new(EmployeeReviewQueryOutcome.Forbidden);
}

/// <summary>
/// The Employee Reviews dashboard. The scope is always resolved from the caller's own roles and department memberships, never from
/// the request: management sees everyone, a CS Supervisor the employees of their departments, anyone else only themselves. Filters can
/// only narrow it. Dates are UAE calendar days (the same convention as the other reports) converted to a UTC range; times are shown in UAE time.
/// </summary>
public sealed class EmployeeReviewQueryAppService(
    IEmployeeReviewRepository repository,
    IUserDepartmentAssignmentRepository assignmentRepository,
    TimeProvider timeProvider)
{
    public const int DefaultPeriodDays = 30;
    public const int CommentLimit = 100;
    public const int DetailLimit = 500;

    private static readonly string[] CompanyWideRoles =
        [Roles.SystemAdministrator, Roles.CsManager, Roles.GeneralManager, Roles.ChairmanCeo];

    private static readonly string[] DepartmentScopedRoles = [Roles.CsSupervisor, Roles.DepartmentHead];

    public async Task<ReviewScope> ResolveScopeAsync(Guid callerEmployeeId, IReadOnlyCollection<string> callerRoles, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(callerRoles);

        if (callerRoles.Any(r => CompanyWideRoles.Contains(r)))
        {
            return ReviewScope.Everyone;
        }

        IReadOnlyCollection<int> departments = [];
        if (callerRoles.Any(r => DepartmentScopedRoles.Contains(r)))
        {
            var memberships = await assignmentRepository.GetByEmployeeIdAsync(callerEmployeeId, cancellationToken);
            departments = [.. memberships.Select(m => m.DepartmentId).Distinct()];
        }

        return new ReviewScope(false, departments, callerEmployeeId);
    }

    public async Task<EmployeeReviewsOverviewDto> GetOverviewAsync(
        Guid callerEmployeeId, IReadOnlyCollection<string> callerRoles, EmployeeReviewFilterRequestDto request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var scope = await ResolveScopeAsync(callerEmployeeId, callerRoles, cancellationToken);
        var (from, to) = ResolvePeriod(request);
        var query = BuildQuery(scope, from, to, request);

        var counts = await repository.CountByEmployeeAndRatingAsync(query, cancellationToken);
        var comments = await repository.ListReviewsAsync(query, withCommentOnly: true, CommentLimit, cancellationToken);

        var employeeIds = counts.Select(c => c.EmployeeId).Union(comments.Select(c => c.EmployeeId)).ToList();
        var infos = (await repository.GetEmployeeInfosAsync(employeeIds, cancellationToken)).ToDictionary(e => e.EmployeeId);

        var rows = counts.GroupBy(c => c.EmployeeId)
            .Select(g =>
            {
                infos.TryGetValue(g.Key, out var info);
                var summary = Summarize(g);
                return new EmployeeReviewEmployeeRowDto(
                    g.Key, info?.DisplayName ?? "Unknown employee", info?.PrimaryDepartment,
                    summary.TotalReviews, summary.AverageRating, summary.Distribution);
            })
            .OrderByDescending(r => r.ReviewCount).ThenBy(r => r.EmployeeName, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

        return new EmployeeReviewsOverviewDto(
            await BuildFiltersAsync(scope, from, to, request, cancellationToken),
            Summarize(counts),
            rows,
            [.. comments.Select(r => ToEntry(r, infos))]);
    }

    public async Task<EmployeeReviewQueryResult<EmployeeReviewDetailDto>> GetEmployeeAsync(
        Guid callerEmployeeId, IReadOnlyCollection<string> callerRoles, Guid employeeId, EmployeeReviewFilterRequestDto request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var scope = await ResolveScopeAsync(callerEmployeeId, callerRoles, cancellationToken);
        if (!await repository.IsInScopeAsync(scope, employeeId, cancellationToken))
        {
            return EmployeeReviewQueryResult<EmployeeReviewDetailDto>.Forbidden();
        }

        var (from, to) = ResolvePeriod(request);
        // The drill-down is about one employee: any employee filter the page carried is replaced by this employee.
        var query = BuildQuery(scope, from, to, request with { EmployeeId = employeeId });

        var counts = await repository.CountByEmployeeAndRatingAsync(query, cancellationToken);
        var reviews = await repository.ListReviewsAsync(query, withCommentOnly: false, DetailLimit, cancellationToken);
        var info = await repository.GetEmployeeInfoAsync(employeeId, cancellationToken);
        var infos = info is null ? [] : new Dictionary<Guid, ReviewEmployeeInfo> { [employeeId] = info };

        return EmployeeReviewQueryResult<EmployeeReviewDetailDto>.Success(new EmployeeReviewDetailDto(
            await BuildFiltersAsync(scope, from, to, request with { EmployeeId = employeeId }, cancellationToken),
            employeeId, info?.DisplayName ?? "Unknown employee", info?.PrimaryDepartment,
            Summarize(counts),
            [.. reviews.Select(r => ToEntry(r, infos))]));
    }

    /// <summary>The UAE calendar days of the filter, defaulting to the last 30 days ending today; from/to swapped when given backwards.</summary>
    internal (DateOnly From, DateOnly To) ResolvePeriod(EmployeeReviewFilterRequestDto request)
    {
        var today = UaeCalendar.Today(timeProvider);
        var to = request.DateTo ?? today;
        var from = request.DateFrom ?? to.AddDays(-(DefaultPeriodDays - 1));
        return from <= to ? (from, to) : (to, from);
    }

    private static EmployeeReviewQuery BuildQuery(ReviewScope scope, DateOnly from, DateOnly to, EmployeeReviewFilterRequestDto request)
    {
        var (fromUtc, toUtc) = UaeCalendar.DayRangeUtc(from, to);
        return new EmployeeReviewQuery(
            scope, fromUtc, toUtc, request.EmployeeId, request.DepartmentId,
            request.Rating is >= 1 and <= 5 ? request.Rating : null);
    }

    private async Task<EmployeeReviewFiltersDto> BuildFiltersAsync(
        ReviewScope scope, DateOnly from, DateOnly to, EmployeeReviewFilterRequestDto request, CancellationToken cancellationToken)
    {
        var reviewed = await repository.ListReviewedEmployeeIdsAsync(scope, cancellationToken);
        var infos = await repository.GetEmployeeInfosAsync(reviewed, cancellationToken);
        var departments = await repository.ListDepartmentsAsync(scope, cancellationToken);
        return new EmployeeReviewFiltersDto(
            from, to, request.EmployeeId, request.DepartmentId, request.Rating is >= 1 and <= 5 ? request.Rating : null,
            [.. infos.OrderBy(i => i.DisplayName, StringComparer.CurrentCultureIgnoreCase).Select(i => new ReviewEmployeeOptionDto(i.EmployeeId, i.DisplayName))],
            [.. departments.OrderBy(d => d.Name, StringComparer.CurrentCultureIgnoreCase).Select(d => new ReviewOptionDto(d.DepartmentId, d.Name))]);
    }

    private static EmployeeReviewSummaryDto Summarize(IEnumerable<ReviewRatingCount> counts)
    {
        var byStar = new int[6];
        foreach (var c in counts)
        {
            if (c.Rating is >= 1 and <= 5)
            {
                byStar[c.Rating] += c.Count;
            }
        }

        var total = byStar.Sum();
        var average = total == 0 ? (double?)null : Math.Round(Enumerable.Range(1, 5).Sum(s => (double)s * byStar[s]) / total, 2);
        return new EmployeeReviewSummaryDto(total, average, [.. Enumerable.Range(1, 5).Reverse().Select(s => new StarCountDto(s, byStar[s]))]);
    }

    private static EmployeeReviewEntryDto ToEntry(StoredReviewRow row, IReadOnlyDictionary<Guid, ReviewEmployeeInfo> infos)
    {
        infos.TryGetValue(row.EmployeeId, out var info);
        return new EmployeeReviewEntryDto(
            row.ReviewId, row.EmployeeId, info?.DisplayName ?? "Unknown employee", info?.PrimaryDepartment,
            row.Rating, row.Comment, row.CreatedAtUtc, UaeCalendar.ToUae(row.CreatedAtUtc));
    }
}
