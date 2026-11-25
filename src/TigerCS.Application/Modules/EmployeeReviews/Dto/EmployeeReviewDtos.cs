namespace TigerCS.Application.Modules.EmployeeReviews.Dto;

// ---- tablet (anonymous, device-token authenticated) ----

/// <summary>What a tablet may know about itself: its own name and the name of the employee it collects reviews for. No ids.</summary>
public sealed record TabletInfoDto(string DeviceName, string EmployeeName);

public enum TabletSessionStatus
{
    /// <summary>The credential is valid and the tablet may collect reviews.</summary>
    Ready,

    /// <summary>No such credential, or it was revoked or replaced.</summary>
    Invalid,

    /// <summary>The tablet is switched off, or its employee is no longer active.</summary>
    Inactive
}

public sealed record TabletSessionDto(TabletSessionStatus Status, TabletInfoDto? Tablet = null);

/// <summary>A customer's review as sent by the tablet. Never carries an employee or device id: both are resolved on the server from the credential.</summary>
public sealed record SubmitEmployeeReviewRequestDto(Guid SubmissionId, int Rating, string? Comment);

public enum SubmitEmployeeReviewOutcome
{
    Submitted,

    /// <summary>The same <c>SubmissionId</c> was already stored (a retry); nothing changed, and the customer's review is safe.</summary>
    AlreadyRecorded,
    InvalidDevice,
    InactiveDevice,
    InvalidSubmission,
    InvalidRating,
    InvalidComment
}

public sealed record SubmitEmployeeReviewResultDto(SubmitEmployeeReviewOutcome Outcome);

// ---- administration ----

public sealed record EmployeeReviewDeviceDto(
    Guid Id,
    string DeviceName,
    Guid EmployeeId,
    string EmployeeName,
    string? Department,
    bool EmployeeIsActive,
    bool IsActive,
    bool HasCredential,
    DateTime CreatedAtUtc,
    DateTime UpdatedAtUtc,
    DateTime? LastSeenAtUtc);

public sealed record EligibleReviewEmployeeDto(Guid EmployeeId, string DisplayName, string? Department);

public sealed record RegisterEmployeeReviewDeviceRequestDto(string? DeviceName, Guid EmployeeId);

public sealed record UpdateEmployeeReviewDeviceRequestDto(string? DeviceName, Guid EmployeeId);

public sealed record SetEmployeeReviewDeviceActiveRequestDto(bool IsActive);

/// <summary>The new device plus its credential — the only time the token is ever returned.</summary>
public sealed record EmployeeReviewDeviceCredentialDto(EmployeeReviewDeviceDto Device, string Token);

// ---- reporting ----

public sealed record EmployeeReviewFilterRequestDto(DateOnly? DateFrom, DateOnly? DateTo, Guid? EmployeeId, int? DepartmentId, int? Rating);

/// <summary>One bar of the five-star distribution (<c>Rating</c> 1–5).</summary>
public sealed record StarCountDto(int Rating, int Count);

public sealed record EmployeeReviewSummaryDto(int TotalReviews, double? AverageRating, IReadOnlyList<StarCountDto> Distribution);

public sealed record EmployeeReviewEmployeeRowDto(
    Guid EmployeeId,
    string EmployeeName,
    string? Department,
    int ReviewCount,
    double? AverageRating,
    IReadOnlyList<StarCountDto> Distribution);

public sealed record EmployeeReviewEntryDto(
    Guid ReviewId,
    Guid EmployeeId,
    string EmployeeName,
    string? Department,
    int Rating,
    string? Comment,
    DateTime CreatedAtUtc,
    DateTime CreatedAtUae);

public sealed record ReviewOptionDto(int Id, string Name);

public sealed record ReviewEmployeeOptionDto(Guid EmployeeId, string Name);

/// <summary>The filters as applied (the period defaults to the last 30 UAE days) and the picker options inside the caller's scope.</summary>
public sealed record EmployeeReviewFiltersDto(
    DateOnly DateFrom,
    DateOnly DateTo,
    Guid? EmployeeId,
    int? DepartmentId,
    int? Rating,
    IReadOnlyList<ReviewEmployeeOptionDto> Employees,
    IReadOnlyList<ReviewOptionDto> Departments);

public sealed record EmployeeReviewsOverviewDto(
    EmployeeReviewFiltersDto Filters,
    EmployeeReviewSummaryDto Summary,
    IReadOnlyList<EmployeeReviewEmployeeRowDto> Employees,
    IReadOnlyList<EmployeeReviewEntryDto> Comments);

public sealed record EmployeeReviewDetailDto(
    EmployeeReviewFiltersDto Filters,
    Guid EmployeeId,
    string EmployeeName,
    string? Department,
    EmployeeReviewSummaryDto Summary,
    IReadOnlyList<EmployeeReviewEntryDto> Reviews);
