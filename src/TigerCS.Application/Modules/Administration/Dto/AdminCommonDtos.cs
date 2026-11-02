namespace TigerCS.Application.Modules.Administration.Dto;

/// <summary>How an administration command landed — the API maps these to 200/201, 404, 400 and 409.</summary>
public enum AdminOutcome
{
    Success,
    NotFound,

    /// <summary>The request violated a validation rule; <c>Errors</c> lists every reason in management-readable wording.</summary>
    ValidationFailed,

    /// <summary>The request conflicts with current state (a duplicate name, a referenced record, an immutable version).</summary>
    Conflict,

    /// <summary>A new password failed ASP.NET Core Identity's password validators; <c>Errors</c> lists their descriptions. The API answers 422.</summary>
    PasswordPolicyViolation,

    /// <summary>The caller is not allowed to make this change (self-service permission edits, granting what they do not hold, administrator-only permissions). The API answers 403.</summary>
    Forbidden
}

public sealed record AdminResult<T>(AdminOutcome Outcome, T? Value = default, IReadOnlyList<string>? Errors = null)
{
    public bool IsSuccess => Outcome == AdminOutcome.Success;

    public static AdminResult<T> Success(T value) => new(AdminOutcome.Success, value);

    public static AdminResult<T> NotFound() => new(AdminOutcome.NotFound);

    public static AdminResult<T> Invalid(params string[] errors) => new(AdminOutcome.ValidationFailed, default, errors);

    public static AdminResult<T> Invalid(IReadOnlyList<string> errors) => new(AdminOutcome.ValidationFailed, default, errors);

    public static AdminResult<T> Conflict(params string[] errors) => new(AdminOutcome.Conflict, default, errors);

    public static AdminResult<T> Forbidden(params string[] errors) => new(AdminOutcome.Forbidden, default, errors);
}

public sealed record AdminResult(AdminOutcome Outcome, IReadOnlyList<string>? Errors = null)
{
    public bool IsSuccess => Outcome == AdminOutcome.Success;

    public static AdminResult Success() => new(AdminOutcome.Success);

    public static AdminResult NotFound() => new(AdminOutcome.NotFound);

    public static AdminResult Invalid(params string[] errors) => new(AdminOutcome.ValidationFailed, errors);

    public static AdminResult Conflict(params string[] errors) => new(AdminOutcome.Conflict, errors);

    public static AdminResult PasswordPolicyViolation(IReadOnlyList<string> errors) => new(AdminOutcome.PasswordPolicyViolation, errors);
}

public sealed record SetActiveRequestDto(bool IsActive, string? Reason = null);

/// <summary>A name-resolved reference — the administration UI shows names, never raw ids, but keeps the id for links and forms.</summary>
public sealed record NamedReferenceDto(int Id, string Name);

public sealed record NamedEmployeeDto(Guid EmployeeId, string DisplayName, bool IsActive);
