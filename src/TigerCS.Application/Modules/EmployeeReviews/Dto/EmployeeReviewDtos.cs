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

/// <summary>The activation code typed (or carried by the link) on the tablet. Exactly 8 characters; the server validates, never the browser.</summary>
public sealed record ActivateTabletRequestDto(string? Code);

public enum ActivateTabletOutcome
{
    /// <summary>The code was valid: the response carries the tablet's new permanent device token (the only time it is ever sent).</summary>
    Activated,

    /// <summary>Not a code that can be redeemed: wrong, malformed, expired, already used or replaced. These are deliberately indistinguishable.</summary>
    InvalidCode,

    /// <summary>The code is right but the tablet (or its employee) is switched off. The code is not used up.</summary>
    Inactive,

    /// <summary>Too many attempts from this client, or too many overall. The code was not even checked.</summary>
    TooManyAttempts
}

/// <summary><see cref="DeviceToken"/> is set only when <see cref="Outcome"/> is <see cref="ActivateTabletOutcome.Activated"/>.</summary>
public sealed record ActivateTabletResultDto(ActivateTabletOutcome Outcome, string? DeviceToken = null);

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
    DateTime? LastSeenAtUtc,
    bool ActivationPending = false,
    DateTime? ActivationCodeExpiresAtUtc = null);

public sealed record EligibleReviewEmployeeDto(Guid EmployeeId, string DisplayName, string? Department);

public sealed record RegisterEmployeeReviewDeviceRequestDto(string? DeviceName, Guid EmployeeId);

public sealed record UpdateEmployeeReviewDeviceRequestDto(string? DeviceName, Guid EmployeeId);

public sealed record SetEmployeeReviewDeviceActiveRequestDto(bool IsActive);

/// <summary>
/// Issuing a new activation code for a tablet that is ALREADY activated switches its current credential off immediately, so the administrator must
/// say so explicitly. For a tablet with no credential (never activated, expired code, revoked) nothing is switched off and no confirmation is needed.
/// </summary>
public sealed record IssueEmployeeReviewActivationCodeRequestDto(bool ConfirmReplaceActiveCredential = false);

/// <summary>The device plus its new 8-character activation code — the only time the code is ever returned. It is not the device token.</summary>
public sealed record EmployeeReviewActivationCodeDto(EmployeeReviewDeviceDto Device, string ActivationCode, DateTime ExpiresAtUtc);

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
