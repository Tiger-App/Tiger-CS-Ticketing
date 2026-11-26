using TigerCS.Domain.Modules.EmployeeReviews;

namespace TigerCS.Application.Modules.EmployeeReviews.Abstractions;

/// <summary>Who may be reviewed, as the caller is allowed to see them. <c>All</c> for management; otherwise the departments plus (optionally) the caller themselves.</summary>
public sealed record ReviewScope(bool All, IReadOnlyCollection<int> DepartmentIds, Guid? SelfEmployeeId)
{
    public static ReviewScope Everyone { get; } = new(true, [], null);
}

/// <summary>A query over stored reviews. The period is UTC, half-open: <c>[FromUtc, ToUtc)</c>.</summary>
public sealed record EmployeeReviewQuery(
    ReviewScope Scope, DateTime? FromUtc, DateTime? ToUtc, Guid? EmployeeId, int? DepartmentId, int? Rating);

public sealed record ReviewRatingCount(Guid EmployeeId, int Rating, int Count);

public sealed record StoredReviewRow(Guid ReviewId, Guid EmployeeId, int Rating, string? Comment, DateTime CreatedAtUtc);

public sealed record ReviewEmployeeInfo(Guid EmployeeId, string DisplayName, bool IsActive, int? PrimaryDepartmentId, string? PrimaryDepartment);

public sealed record ReviewDepartmentInfo(int DepartmentId, string Name);

public sealed record DeviceListRow(EmployeeReviewDevice Device, ReviewEmployeeInfo Employee);

/// <summary>Storage for review tablets and the reviews they collect. Reads over the existing Employees / Departments tables; no second employee store.</summary>
public interface IEmployeeReviewRepository
{
    Task AddDeviceAsync(EmployeeReviewDevice device, CancellationToken cancellationToken = default);

    /// <summary>The tracked device, for an administrative change.</summary>
    Task<EmployeeReviewDevice?> GetDeviceAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>The tracked device whose credential hashes to <paramref name="tokenHash"/>, or <c>null</c>.</summary>
    Task<EmployeeReviewDevice?> GetDeviceByTokenHashAsync(byte[] tokenHash, CancellationToken cancellationToken = default);

    /// <summary>Tablets holding an activation code that has not expired at <paramref name="nowUtc"/> (untracked). The redeemer checks the submitted code against each.</summary>
    Task<IReadOnlyList<EmployeeReviewDevice>> ListRedeemableActivationDevicesAsync(DateTime nowUtc, CancellationToken cancellationToken = default);

    /// <summary>
    /// Redeems an activation code: stores <paramref name="newTokenHash"/> and erases the code, but only while the row still holds
    /// <paramref name="expectedCodeHash"/>, the code has not expired, and the tablet is switched on. The code's hash is a concurrency token, so the
    /// write itself is conditional in the database. Returns <c>false</c> when nothing was changed — which is how two simultaneous redemptions of one
    /// code are resolved: exactly one of them wins.
    /// </summary>
    Task<bool> TryRedeemActivationAsync(Guid deviceId, byte[] expectedCodeHash, byte[] newTokenHash, DateTime nowUtc, CancellationToken cancellationToken = default);

    /// <summary>
    /// Counts one activation attempt against <paramref name="key"/> (<b>before</b> the code is checked) and returns how many attempts the key has made
    /// inside the current window; a window that ended is restarted first. The count is guarded by a concurrency token and re-applied on conflict, so
    /// concurrent attempts each add exactly one. If the count cannot be stored the result is <see cref="int.MaxValue"/> — the caller then refuses.
    /// </summary>
    Task<int> RegisterActivationAttemptAsync(string key, TimeSpan window, DateTime nowUtc, CancellationToken cancellationToken = default);

    /// <summary>Forgets a key's attempts (a successful activation by that client).</summary>
    Task ClearActivationAttemptsAsync(string key, CancellationToken cancellationToken = default);

    /// <summary>Gives back one attempt of a shared counter (a successful activation must not eat the shared budget).</summary>
    Task RefundActivationAttemptAsync(string key, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<DeviceListRow>> ListDevicesAsync(CancellationToken cancellationToken = default);

    /// <summary>Active Customer Service employees: members of the Customer Service department or holders of the CS Agent / CS Supervisor roles.</summary>
    Task<IReadOnlyList<ReviewEmployeeInfo>> ListEligibleEmployeesAsync(CancellationToken cancellationToken = default);

    Task<ReviewEmployeeInfo?> GetEmployeeInfoAsync(Guid employeeId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ReviewEmployeeInfo>> GetEmployeeInfosAsync(IReadOnlyCollection<Guid> employeeIds, CancellationToken cancellationToken = default);

    /// <summary>Commits staged changes (device edits and their audit rows) in one transaction.</summary>
    Task SaveChangesAsync(CancellationToken cancellationToken = default);

    /// <summary>True when a review with this <c>SubmissionId</c> is already stored — the cheap check before inserting; the unique index remains the real guard.</summary>
    Task<bool> SubmissionExistsAsync(Guid submissionId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Persists the review immediately. Returns <c>false</c> — storing nothing — when a review with the same <c>SubmissionId</c> already
    /// exists (the unique index, so two simultaneous retries cannot both win).
    /// </summary>
    Task<bool> TryAddReviewAsync(EmployeeReview review, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ReviewRatingCount>> CountByEmployeeAndRatingAsync(EmployeeReviewQuery query, CancellationToken cancellationToken = default);

    /// <summary>Newest first.</summary>
    Task<IReadOnlyList<StoredReviewRow>> ListReviewsAsync(EmployeeReviewQuery query, bool withCommentOnly, int take, CancellationToken cancellationToken = default);

    /// <summary>True when <paramref name="employeeId"/> is someone <paramref name="scope"/> allows the caller to see.</summary>
    Task<bool> IsInScopeAsync(ReviewScope scope, Guid employeeId, CancellationToken cancellationToken = default);

    /// <summary>Employees, within <paramref name="scope"/>, who have at least one review — the employee picker.</summary>
    Task<IReadOnlyList<Guid>> ListReviewedEmployeeIdsAsync(ReviewScope scope, CancellationToken cancellationToken = default);

    /// <summary>Departments, within <paramref name="scope"/>, that employees with reviews belong to — the department picker.</summary>
    Task<IReadOnlyList<ReviewDepartmentInfo>> ListDepartmentsAsync(ReviewScope scope, CancellationToken cancellationToken = default);
}
