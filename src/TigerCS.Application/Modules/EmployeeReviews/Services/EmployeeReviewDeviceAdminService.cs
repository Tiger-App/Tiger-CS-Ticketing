using TigerCS.Application.Abstractions;
using TigerCS.Application.Modules.Administration.Dto;
using TigerCS.Application.Modules.EmployeeReviews.Abstractions;
using TigerCS.Application.Modules.EmployeeReviews.Dto;
using TigerCS.Domain.Modules.EmployeeReviews;

namespace TigerCS.Application.Modules.EmployeeReviews.Services;

/// <summary>
/// Administration of review tablets: register, assign to an existing Customer Service employee, rename, switch on/off, issue a new
/// activation code (an already-activated tablet only after an explicit confirmation) or revoke the credential. A tablet starts with no credential: the administrator receives an 8-character activation code
/// (valid 24 hours, one use), returned exactly once and never stored, logged or audited — only a salted hash is stored. The tablet trades the
/// code for its own long device token (see <see cref="EmployeeReviewActivationService"/>); the administrator never sees that token.
/// Reassigning a tablet changes where <i>future</i> reviews go; stored reviews keep their own employee.
/// </summary>
public sealed class EmployeeReviewDeviceAdminService(
    IEmployeeReviewRepository repository,
    IAuditEntryWriter auditWriter,
    TimeProvider timeProvider)
{
    private const string EntityType = "EmployeeReviewDevice";

    public async Task<IReadOnlyList<EmployeeReviewDeviceDto>> ListAsync(CancellationToken cancellationToken = default)
    {
        var rows = await repository.ListDevicesAsync(cancellationToken);
        return [.. rows.Select(r => ToDto(r.Device, r.Employee)).OrderBy(d => d.DeviceName, StringComparer.CurrentCultureIgnoreCase)];
    }

    public async Task<IReadOnlyList<EligibleReviewEmployeeDto>> ListEligibleEmployeesAsync(CancellationToken cancellationToken = default)
    {
        var employees = await repository.ListEligibleEmployeesAsync(cancellationToken);
        return [.. employees.Select(e => new EligibleReviewEmployeeDto(e.EmployeeId, e.DisplayName, e.PrimaryDepartment))
            .OrderBy(e => e.DisplayName, StringComparer.CurrentCultureIgnoreCase)];
    }

    public async Task<AdminResult<EmployeeReviewActivationCodeDto>> RegisterAsync(
        Guid actorEmployeeId, RegisterEmployeeReviewDeviceRequestDto request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var errors = await ValidateAsync(request.DeviceName, request.EmployeeId, checkEmployee: true, excludeDeviceId: null, cancellationToken);
        if (errors.Count > 0)
        {
            return errors.Any(e => e.Contains("already", StringComparison.Ordinal))
                ? AdminResult<EmployeeReviewActivationCodeDto>.Conflict(errors.ToArray())
                : AdminResult<EmployeeReviewActivationCodeDto>.Invalid(errors);
        }

        var now = timeProvider.GetUtcNow().UtcDateTime;
        var expiresAt = now + ActivationCode.Lifetime;
        var (code, salt, hash) = await NewActivationCodeAsync(now, cancellationToken);
        var device = new EmployeeReviewDevice(request.DeviceName!, request.EmployeeId, hash, salt, expiresAt, now);
        await repository.AddDeviceAsync(device, cancellationToken);
        await auditWriter.WriteAsync(
            actorEmployeeId, "AdminRegisterReviewDevice", EntityType, device.Id.ToString(), null,
            $"name={device.DeviceName};employee={device.EmployeeId};activationExpires={expiresAt:O}", Guid.NewGuid(), cancellationToken);
        await repository.SaveChangesAsync(cancellationToken);

        return AdminResult<EmployeeReviewActivationCodeDto>.Success(
            new EmployeeReviewActivationCodeDto(await ToDtoAsync(device, cancellationToken), code, expiresAt));
    }

    public async Task<AdminResult<EmployeeReviewDeviceDto>> UpdateAsync(
        Guid actorEmployeeId, Guid deviceId, UpdateEmployeeReviewDeviceRequestDto request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var device = await repository.GetDeviceAsync(deviceId, cancellationToken);
        if (device is null)
        {
            return AdminResult<EmployeeReviewDeviceDto>.NotFound();
        }

        var employeeChanged = device.EmployeeId != request.EmployeeId;
        var errors = await ValidateAsync(request.DeviceName, request.EmployeeId, employeeChanged, deviceId, cancellationToken);
        if (errors.Count > 0)
        {
            return errors.Any(e => e.Contains("already", StringComparison.Ordinal))
                ? AdminResult<EmployeeReviewDeviceDto>.Conflict(errors.ToArray())
                : AdminResult<EmployeeReviewDeviceDto>.Invalid(errors);
        }

        var now = timeProvider.GetUtcNow().UtcDateTime;
        var before = $"name={device.DeviceName};employee={device.EmployeeId}";
        device.Rename(request.DeviceName!, now);
        if (employeeChanged)
        {
            device.Reassign(request.EmployeeId, now);
        }

        await auditWriter.WriteAsync(
            actorEmployeeId, employeeChanged ? "AdminReassignReviewDevice" : "AdminUpdateReviewDevice", EntityType, device.Id.ToString(), before,
            $"name={device.DeviceName};employee={device.EmployeeId}", Guid.NewGuid(), cancellationToken);
        await repository.SaveChangesAsync(cancellationToken);
        return AdminResult<EmployeeReviewDeviceDto>.Success(await ToDtoAsync(device, cancellationToken));
    }

    public async Task<AdminResult<EmployeeReviewDeviceDto>> SetActiveAsync(
        Guid actorEmployeeId, Guid deviceId, bool isActive, CancellationToken cancellationToken = default)
    {
        var device = await repository.GetDeviceAsync(deviceId, cancellationToken);
        if (device is null)
        {
            return AdminResult<EmployeeReviewDeviceDto>.NotFound();
        }

        if (isActive && device.TokenHash is null)
        {
            return AdminResult<EmployeeReviewDeviceDto>.Conflict("The tablet has no active credential (it was revoked or is not activated yet). Issue a new activation code to bring it back.");
        }

        var before = device.IsActive;
        device.SetActive(isActive, timeProvider.GetUtcNow().UtcDateTime);
        await auditWriter.WriteAsync(
            actorEmployeeId, isActive ? "AdminActivateReviewDevice" : "AdminDeactivateReviewDevice", EntityType, device.Id.ToString(),
            $"active={before}", $"active={isActive}", Guid.NewGuid(), cancellationToken);
        await repository.SaveChangesAsync(cancellationToken);
        return AdminResult<EmployeeReviewDeviceDto>.Success(await ToDtoAsync(device, cancellationToken));
    }

    /// <summary>
    /// Issues a new 8-character activation code (valid 24 hours, one use), returned once.
    /// <list type="bullet">
    /// <item>A tablet with <b>no credential</b> (never activated, code expired, revoked) simply gets a fresh code and is switched on; nothing is lost.</item>
    /// <item>A tablet that is <b>already activated</b> is a different matter: its current device token stops working the moment the new code is issued,
    /// so the tablet stops collecting reviews until someone redeems the code. That is only done when the administrator explicitly confirms it
    /// (<paramref name="confirmReplaceActiveCredential"/>); without it the request is refused with a conflict and nothing changes.</item>
    /// </list>
    /// </summary>
    public async Task<AdminResult<EmployeeReviewActivationCodeDto>> IssueActivationCodeAsync(
        Guid actorEmployeeId, Guid deviceId, bool confirmReplaceActiveCredential = false, CancellationToken cancellationToken = default)
    {
        var device = await repository.GetDeviceAsync(deviceId, cancellationToken);
        if (device is null)
        {
            return AdminResult<EmployeeReviewActivationCodeDto>.NotFound();
        }

        var replacesActiveCredential = device.TokenHash is not null;
        if (replacesActiveCredential && !confirmReplaceActiveCredential)
        {
            return AdminResult<EmployeeReviewActivationCodeDto>.Conflict(
                "This tablet is already activated. Issuing a new activation code switches its current credential off immediately, and it will stop "
                + "collecting reviews until the new code is entered on it. Confirm the re-activation to continue.");
        }

        var now = timeProvider.GetUtcNow().UtcDateTime;
        var expiresAt = now + ActivationCode.Lifetime;
        var (code, salt, hash) = await NewActivationCodeAsync(now, cancellationToken);
        device.IssueActivationCode(hash, salt, expiresAt, replacesActiveCredential, now);
        await auditWriter.WriteAsync(
            actorEmployeeId,
            replacesActiveCredential ? "AdminReactivateReviewDevice" : "AdminIssueReviewDeviceActivationCode",
            EntityType, device.Id.ToString(),
            replacesActiveCredential ? "credential active" : null,
            replacesActiveCredential ? $"credential switched off (confirmed);activationExpires={expiresAt:O}" : $"activationExpires={expiresAt:O}",
            Guid.NewGuid(), cancellationToken);
        await repository.SaveChangesAsync(cancellationToken);
        return AdminResult<EmployeeReviewActivationCodeDto>.Success(
            new EmployeeReviewActivationCodeDto(await ToDtoAsync(device, cancellationToken), code, expiresAt));
    }

    /// <summary>Removes the credential and switches the tablet off. Reviews it already collected are untouched.</summary>
    public async Task<AdminResult<EmployeeReviewDeviceDto>> RevokeAsync(
        Guid actorEmployeeId, Guid deviceId, CancellationToken cancellationToken = default)
    {
        var device = await repository.GetDeviceAsync(deviceId, cancellationToken);
        if (device is null)
        {
            return AdminResult<EmployeeReviewDeviceDto>.NotFound();
        }

        device.Revoke(timeProvider.GetUtcNow().UtcDateTime);
        await auditWriter.WriteAsync(
            actorEmployeeId, "AdminRevokeReviewDevice", EntityType, device.Id.ToString(), null, "credential revoked", Guid.NewGuid(), cancellationToken);
        await repository.SaveChangesAsync(cancellationToken);
        return AdminResult<EmployeeReviewDeviceDto>.Success(await ToDtoAsync(device, cancellationToken));
    }

    /// <summary>A fresh code with its salt and hash. Re-draws in the (≈ 1 in 10^11) case that it equals the code of another tablet still waiting to be activated.</summary>
    private async Task<(string Code, byte[] Salt, byte[] Hash)> NewActivationCodeAsync(DateTime nowUtc, CancellationToken cancellationToken)
    {
        var pending = await repository.ListRedeemableActivationDevicesAsync(nowUtc, cancellationToken);
        while (true)
        {
            var code = ActivationCode.Generate();
            if (pending.Any(p => ActivationCode.Matches(code, p.ActivationCodeSalt!, p.ActivationCodeHash!)))
            {
                continue;
            }

            var salt = ActivationCode.NewSalt();
            return (code, salt, ActivationCode.Hash(code, salt));
        }
    }

    private async Task<List<string>> ValidateAsync(
        string? name, Guid employeeId, bool checkEmployee, Guid? excludeDeviceId, CancellationToken cancellationToken)
    {
        var errors = new List<string>();
        var trimmed = name?.Trim();
        if (string.IsNullOrEmpty(trimmed))
        {
            errors.Add("Enter a tablet name.");
        }
        else if (trimmed.Length > EmployeeReviewDevice.NameMaxLength)
        {
            errors.Add($"The tablet name is at most {EmployeeReviewDevice.NameMaxLength} characters.");
        }
        else
        {
            var devices = await repository.ListDevicesAsync(cancellationToken);
            if (devices.Any(d => d.Device.Id != excludeDeviceId && string.Equals(d.Device.DeviceName, trimmed, StringComparison.OrdinalIgnoreCase)))
            {
                errors.Add("A tablet with this name already exists.");
            }
        }

        if (checkEmployee)
        {
            var eligible = await repository.ListEligibleEmployeesAsync(cancellationToken);
            if (employeeId == Guid.Empty || eligible.All(e => e.EmployeeId != employeeId))
            {
                errors.Add("Choose an active Customer Service employee.");
            }
        }

        return errors;
    }

    private async Task<EmployeeReviewDeviceDto> ToDtoAsync(EmployeeReviewDevice device, CancellationToken cancellationToken) =>
        ToDto(device, await repository.GetEmployeeInfoAsync(device.EmployeeId, cancellationToken));

    private EmployeeReviewDeviceDto ToDto(EmployeeReviewDevice d, ReviewEmployeeInfo? employee) => new(
        d.Id, d.DeviceName, d.EmployeeId, employee?.DisplayName ?? "Unknown employee", employee?.PrimaryDepartment,
        employee?.IsActive ?? false, d.IsActive, d.TokenHash is not null, d.CreatedAtUtc, d.UpdatedAtUtc, d.LastSeenAtUtc,
        d.HasRedeemableActivationCode(timeProvider.GetUtcNow().UtcDateTime), d.ActivationCodeExpiresAtUtc);
}
