using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using TigerCS.Application.Authorization;
using TigerCS.Application.Modules.EmployeeReviews.Abstractions;
using TigerCS.Domain.Modules.EmployeeReviews;
using TigerCS.Domain.Modules.IdentityAndAccess;
using TigerCS.Infrastructure.Persistence;

namespace TigerCS.Infrastructure.Modules.EmployeeReviews;

public sealed class EmployeeReviewRepository(TigerCsDbContext dbContext, IServiceIdentityRegistry serviceIdentities) : IEmployeeReviewRepository
{
    /// <summary>The Customer Service department's code, as seeded by the production reference data.</summary>
    public const string CustomerServiceDepartmentCode = "CS";

    public async Task AddDeviceAsync(EmployeeReviewDevice device, CancellationToken cancellationToken = default) =>
        await dbContext.EmployeeReviewDevices.AddAsync(device, cancellationToken);

    public Task<EmployeeReviewDevice?> GetDeviceAsync(Guid id, CancellationToken cancellationToken = default) =>
        dbContext.EmployeeReviewDevices.FirstOrDefaultAsync(d => d.Id == id, cancellationToken);

    public Task<EmployeeReviewDevice?> GetDeviceByTokenHashAsync(byte[] tokenHash, CancellationToken cancellationToken = default) =>
        dbContext.EmployeeReviewDevices.FirstOrDefaultAsync(d => d.TokenHash == tokenHash, cancellationToken);

    public async Task<IReadOnlyList<EmployeeReviewDevice>> ListRedeemableActivationDevicesAsync(DateTime nowUtc, CancellationToken cancellationToken = default) =>
        await dbContext.EmployeeReviewDevices.AsNoTracking()
            .Where(d => d.ActivationCodeHash != null && d.ActivationCodeSalt != null && d.ActivationCodeExpiresAtUtc > nowUtc)
            .ToListAsync(cancellationToken);

    public async Task<bool> TryRedeemActivationAsync(
        Guid deviceId, byte[] expectedCodeHash, byte[] newTokenHash, DateTime nowUtc, CancellationToken cancellationToken = default)
    {
        var device = await dbContext.EmployeeReviewDevices.FirstOrDefaultAsync(d => d.Id == deviceId, cancellationToken);
        if (device is null || !device.TryRedeemActivationCode(expectedCodeHash, newTokenHash, nowUtc))
        {
            return false;
        }

        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
            return true;
        }
        catch (DbUpdateConcurrencyException)
        {
            // Someone redeemed or replaced the code between our read and our write: this caller lost the race.
            dbContext.Entry(device).State = EntityState.Detached;
            return false;
        }
    }

    /// <summary>More than enough retries for the handful of requests that can ever collide on one counter; if it is still contended, fail closed.</summary>
    private const int MaxCounterRetries = 25;

    public async Task<int> RegisterActivationAttemptAsync(string key, TimeSpan window, DateTime nowUtc, CancellationToken cancellationToken = default)
    {
        var count = 0;
        var saved = await ChangeThrottleAsync(key, nowUtc, row => count = row.Register(nowUtc, window), createIfMissing: true, cancellationToken);

        // Out of retries means the counter is being hammered and the count was never stored: report "over every limit" so the caller refuses.
        // (Never return the unsaved count - it would be a number some other request has already moved past.)
        return saved ? count : int.MaxValue;
    }

    public Task ClearActivationAttemptsAsync(string key, CancellationToken cancellationToken = default) =>
        ChangeThrottleAsync(key, DateTime.UtcNow, row => row.Clear(), createIfMissing: false, cancellationToken);

    public Task RefundActivationAttemptAsync(string key, CancellationToken cancellationToken = default) =>
        ChangeThrottleAsync(key, DateTime.UtcNow, row => row.Refund(), createIfMissing: false, cancellationToken);

    /// <summary>Applies <paramref name="change"/> to a counter row and saves it, reloading and re-applying it whenever another request changed the row first.</summary>
    private async Task<bool> ChangeThrottleAsync(
        string key, DateTime nowUtc, Action<EmployeeReviewActivationThrottle> change, bool createIfMissing, CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < MaxCounterRetries; attempt++)
        {
            var row = await dbContext.EmployeeReviewActivationThrottles.FirstOrDefaultAsync(t => t.Key == key, cancellationToken);
            if (row is null)
            {
                if (!createIfMissing)
                {
                    return true;
                }

                row = new EmployeeReviewActivationThrottle(key, nowUtc);
                dbContext.EmployeeReviewActivationThrottles.Add(row);
            }

            change(row);
            try
            {
                await dbContext.SaveChangesAsync(cancellationToken);
                return true;
            }
            catch (Exception ex) when (ex is DbUpdateConcurrencyException || ex is DbUpdateException dbEx && IsDuplicate(dbEx))
            {
                // Another request created or changed the row first. Forget our stale copy and apply the change to the current one.
                dbContext.Entry(row).State = EntityState.Detached;
            }
        }

        return false;
    }

    public async Task<IReadOnlyList<DeviceListRow>> ListDevicesAsync(CancellationToken cancellationToken = default)
    {
        var devices = await dbContext.EmployeeReviewDevices.AsNoTracking().ToListAsync(cancellationToken);
        var infos = (await GetEmployeeInfosAsync([.. devices.Select(d => d.EmployeeId).Distinct()], cancellationToken)).ToDictionary(e => e.EmployeeId);
        return [.. devices.Select(d => new DeviceListRow(
            d, infos.GetValueOrDefault(d.EmployeeId) ?? new ReviewEmployeeInfo(d.EmployeeId, "Unknown employee", false, null, null)))];
    }

    public async Task<IReadOnlyList<ReviewEmployeeInfo>> ListEligibleEmployeesAsync(CancellationToken cancellationToken = default)
    {
        string[] csRoles = [Roles.CsAgent, Roles.CsSupervisor];
        var serviceIds = serviceIdentities.ServiceIdentityIds.ToList();

        var ids = await dbContext.Employees.AsNoTracking()
            .Where(e => e.DeactivatedAtUtc == null && !serviceIds.Contains(e.EmployeeId))
            .Where(e =>
                dbContext.UserDepartmentAssignments.Any(a => a.EmployeeId == e.EmployeeId && a.Department.Code == CustomerServiceDepartmentCode)
                || (from ur in dbContext.UserRoles
                    join r in dbContext.Roles on ur.RoleId equals r.Id
                    where ur.UserId == e.EmployeeId && csRoles.Contains(r.Name!)
                    select ur.UserId).Any())
            .Select(e => e.EmployeeId)
            .ToListAsync(cancellationToken);

        return await GetEmployeeInfosAsync(ids, cancellationToken);
    }

    public async Task<ReviewEmployeeInfo?> GetEmployeeInfoAsync(Guid employeeId, CancellationToken cancellationToken = default) =>
        (await GetEmployeeInfosAsync([employeeId], cancellationToken)).FirstOrDefault();

    public async Task<IReadOnlyList<ReviewEmployeeInfo>> GetEmployeeInfosAsync(IReadOnlyCollection<Guid> employeeIds, CancellationToken cancellationToken = default)
    {
        if (employeeIds.Count == 0)
        {
            return [];
        }

        var ids = employeeIds.Distinct().ToList();
        var employees = await dbContext.Employees.AsNoTracking()
            .Where(e => ids.Contains(e.EmployeeId))
            .Select(e => new { e.EmployeeId, e.DisplayName, e.DeactivatedAtUtc })
            .ToListAsync(cancellationToken);

        var assignments = await dbContext.UserDepartmentAssignments.AsNoTracking()
            .Where(a => ids.Contains(a.EmployeeId))
            .Select(a => new { a.EmployeeId, a.DepartmentId, a.IsPrimary, DepartmentName = a.Department.Name })
            .ToListAsync(cancellationToken);

        return [.. employees.Select(e =>
        {
            // The primary department when there is one; otherwise the alphabetically first membership, so the column is never blank for a member.
            var mine = assignments.Where(a => a.EmployeeId == e.EmployeeId).ToList();
            var shown = mine.FirstOrDefault(a => a.IsPrimary) ?? mine.OrderBy(a => a.DepartmentName, StringComparer.Ordinal).FirstOrDefault();
            return new ReviewEmployeeInfo(e.EmployeeId, e.DisplayName, e.DeactivatedAtUtc is null, shown?.DepartmentId, shown?.DepartmentName);
        })];
    }

    public Task SaveChangesAsync(CancellationToken cancellationToken = default) => dbContext.SaveChangesAsync(cancellationToken);

    public Task<bool> SubmissionExistsAsync(Guid submissionId, CancellationToken cancellationToken = default) =>
        dbContext.EmployeeReviews.AsNoTracking().AnyAsync(r => r.SubmissionId == submissionId, cancellationToken);

    public async Task<bool> TryAddReviewAsync(EmployeeReview review, CancellationToken cancellationToken = default)
    {
        dbContext.EmployeeReviews.Add(review);
        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
            return true;
        }
        catch (DbUpdateException ex) when (IsDuplicate(ex))
        {
            dbContext.Entry(review).State = EntityState.Detached;
            return false;
        }
    }

    public async Task<IReadOnlyList<ReviewRatingCount>> CountByEmployeeAndRatingAsync(EmployeeReviewQuery query, CancellationToken cancellationToken = default) =>
        await Filter(query)
            .GroupBy(r => new { r.EmployeeId, r.Rating })
            .Select(g => new ReviewRatingCount(g.Key.EmployeeId, g.Key.Rating, g.Count()))
            .ToListAsync(cancellationToken);

    public async Task<IReadOnlyList<StoredReviewRow>> ListReviewsAsync(
        EmployeeReviewQuery query, bool withCommentOnly, int take, CancellationToken cancellationToken = default)
    {
        var reviews = Filter(query);
        if (withCommentOnly)
        {
            reviews = reviews.Where(r => r.Comment != null);
        }

        // Ordered by time in memory-agnostic terms: the id breaks ties so paging by "take" is stable.
        var rows = await reviews
            .OrderByDescending(r => r.CreatedAtUtc).ThenByDescending(r => r.Id)
            .Take(take)
            .Select(r => new { r.Id, r.EmployeeId, r.Rating, r.Comment, r.CreatedAtUtc })
            .ToListAsync(cancellationToken);
        return [.. rows.Select(r => new StoredReviewRow(r.Id, r.EmployeeId, r.Rating, r.Comment, r.CreatedAtUtc))];
    }

    public async Task<bool> IsInScopeAsync(ReviewScope scope, Guid employeeId, CancellationToken cancellationToken = default)
    {
        if (scope.All || scope.SelfEmployeeId == employeeId)
        {
            return await dbContext.Employees.AsNoTracking().AnyAsync(e => e.EmployeeId == employeeId, cancellationToken);
        }

        var departments = scope.DepartmentIds.ToList();
        return departments.Count > 0
            && await dbContext.UserDepartmentAssignments.AsNoTracking()
                .AnyAsync(a => a.EmployeeId == employeeId && departments.Contains(a.DepartmentId), cancellationToken);
    }

    public async Task<IReadOnlyList<Guid>> ListReviewedEmployeeIdsAsync(ReviewScope scope, CancellationToken cancellationToken = default) =>
        await InScope(dbContext.EmployeeReviews.AsNoTracking(), scope).Select(r => r.EmployeeId).Distinct().ToListAsync(cancellationToken);

    public async Task<IReadOnlyList<ReviewDepartmentInfo>> ListDepartmentsAsync(ReviewScope scope, CancellationToken cancellationToken = default)
    {
        var reviewed = InScope(dbContext.EmployeeReviews.AsNoTracking(), scope).Select(r => r.EmployeeId).Distinct();
        var departments = await dbContext.UserDepartmentAssignments.AsNoTracking()
            .Where(a => reviewed.Contains(a.EmployeeId))
            .Select(a => new { a.DepartmentId, a.Department.Name })
            .Distinct()
            .ToListAsync(cancellationToken);

        // A supervisor sees only the departments they belong to, even when a colleague of theirs also sits in another one.
        return [.. departments
            .Where(d => scope.All || scope.DepartmentIds.Contains(d.DepartmentId))
            .Select(d => new ReviewDepartmentInfo(d.DepartmentId, d.Name))];
    }

    private IQueryable<EmployeeReview> Filter(EmployeeReviewQuery query)
    {
        var reviews = InScope(dbContext.EmployeeReviews.AsNoTracking(), query.Scope);

        if (query.FromUtc is { } from)
        {
            reviews = reviews.Where(r => r.CreatedAtUtc >= from);
        }

        if (query.ToUtc is { } to)
        {
            reviews = reviews.Where(r => r.CreatedAtUtc < to);
        }

        if (query.EmployeeId is { } employeeId)
        {
            reviews = reviews.Where(r => r.EmployeeId == employeeId);
        }

        if (query.DepartmentId is { } departmentId)
        {
            reviews = reviews.Where(r => dbContext.UserDepartmentAssignments.Any(a => a.EmployeeId == r.EmployeeId && a.DepartmentId == departmentId));
        }

        if (query.Rating is { } rating)
        {
            var star = (byte)rating;
            reviews = reviews.Where(r => r.Rating == star);
        }

        return reviews;
    }

    /// <summary>Restricts reviews to the employees the scope allows: everyone, or the caller themselves plus members of the scope's departments.</summary>
    private IQueryable<EmployeeReview> InScope(IQueryable<EmployeeReview> reviews, ReviewScope scope)
    {
        if (scope.All)
        {
            return reviews;
        }

        var self = scope.SelfEmployeeId;
        var departments = scope.DepartmentIds.ToList();
        return reviews.Where(r =>
            (self != null && r.EmployeeId == self)
            || (departments.Count > 0
                && dbContext.UserDepartmentAssignments.Any(a => a.EmployeeId == r.EmployeeId && departments.Contains(a.DepartmentId))));
    }

    private static bool IsDuplicate(DbUpdateException ex)
    {
        for (var inner = ex.InnerException; inner is not null; inner = inner.InnerException)
        {
            if (inner is SqlException { Number: 2601 or 2627 })
            {
                return true;
            }

            // SQLite (the test database): SQLITE_CONSTRAINT with a UNIQUE message.
            if (inner.GetType().Name == "SqliteException" && inner.Message.Contains("UNIQUE", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}
