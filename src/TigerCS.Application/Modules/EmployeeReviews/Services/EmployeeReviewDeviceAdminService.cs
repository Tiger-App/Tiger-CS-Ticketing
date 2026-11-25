using TigerCS.Application.Abstractions;
using TigerCS.Application.Modules.Administration.Dto;
using TigerCS.Application.Modules.EmployeeReviews.Abstractions;
using TigerCS.Application.Modules.EmployeeReviews.Dto;
using TigerCS.Domain.Modules.EmployeeReviews;

namespace TigerCS.Application.Modules.EmployeeReviews.Services;

/// <summary>
/// Administration of review tablets: register, assign to an existing Customer Service employee, rename, switch on/off, replace or
/// revoke the credential. The credential is returned exactly once (on register / regenerate); it is never stored, logged or audited —
/// only its hash is stored. Reassigning a tablet changes where <i>future</i> reviews go; stored reviews keep their own employee.
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

    public async Task<AdminResult<EmployeeReviewDeviceCredentialDto>> RegisterAsync(
        Guid actorEmployeeId, RegisterEmployeeReviewDeviceRequestDto request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var errors = await ValidateAsync(request.DeviceName, request.EmployeeId, checkEmployee: true, excludeDeviceId: null, cancellationToken);
        if (errors.Count > 0)
        {
            return errors.Any(e => e.Contains("already", StringComparison.Ordinal))
                ? AdminResult<EmployeeReviewDeviceCredentialDto>.Conflict(errors.ToArray())
                : AdminResult<EmployeeReviewDeviceCredentialDto>.Invalid(errors);
        }

        var now = timeProvider.GetUtcNow().UtcDateTime;
        var token = DeviceToken.Generate();
        var device = new EmployeeReviewDevice(request.DeviceName!, request.EmployeeId, DeviceToken.Hash(token)!, now);
        await repository.AddDeviceAsync(device, cancellationToken);
        await auditWriter.WriteAsync(
            actorEmployeeId, "AdminRegisterReviewDevice", EntityType, device.Id.ToString(), null,
            $"name={device.DeviceName};employee={device.EmployeeId}", Guid.NewGuid(), cancellationToken);
        await repository.SaveChangesAsync(cancellationToken);

        return AdminResult<EmployeeReviewDeviceCredentialDto>.Success(new EmployeeReviewDeviceCredentialDto(await ToDtoAsync(device, cancellationToken), token));
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
            return AdminResult<EmployeeReviewDeviceDto>.Conflict("The tablet's credential was revoked. Regenerate the credential to bring it back.");
        }

        var before = device.IsActive;
        device.SetActive(isActive, timeProvider.GetUtcNow().UtcDateTime);
        await auditWriter.WriteAsync(
            actorEmployeeId, isActive ? "AdminActivateReviewDevice" : "AdminDeactivateReviewDevice", EntityType, device.Id.ToString(),
            $"active={before}", $"active={isActive}", Guid.NewGuid(), cancellationToken);
        await repository.SaveChangesAsync(cancellationToken);
        return AdminResult<EmployeeReviewDeviceDto>.Success(await ToDtoAsync(device, cancellationToken));
    }

    /// <summary>Issues a new credential (the old one stops working at once) and switches the tablet on. Returned once.</summary>
    public async Task<AdminResult<EmployeeReviewDeviceCredentialDto>> RegenerateTokenAsync(
        Guid actorEmployeeId, Guid deviceId, CancellationToken cancellationToken = default)
    {
        var device = await repository.GetDeviceAsync(deviceId, cancellationToken);
        if (device is null)
        {
            return AdminResult<EmployeeReviewDeviceCredentialDto>.NotFound();
        }

        var now = timeProvider.GetUtcNow().UtcDateTime;
        var token = DeviceToken.Generate();
        device.ReplaceToken(DeviceToken.Hash(token)!, now);
        device.SetActive(true, now);
        await auditWriter.WriteAsync(
            actorEmployeeId, "AdminRegenerateReviewDeviceToken", EntityType, device.Id.ToString(), null, "credential replaced", Guid.NewGuid(), cancellationToken);
        await repository.SaveChangesAsync(cancellationToken);
        return AdminResult<EmployeeReviewDeviceCredentialDto>.Success(new EmployeeReviewDeviceCredentialDto(await ToDtoAsync(device, cancellationToken), token));
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

    private static EmployeeReviewDeviceDto ToDto(EmployeeReviewDevice d, ReviewEmployeeInfo? employee) => new(
        d.Id, d.DeviceName, d.EmployeeId, employee?.DisplayName ?? "Unknown employee", employee?.PrimaryDepartment,
        employee?.IsActive ?? false, d.IsActive, d.TokenHash is not null, d.CreatedAtUtc, d.UpdatedAtUtc, d.LastSeenAtUtc);
}
