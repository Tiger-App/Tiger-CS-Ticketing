using TigerCS.Application.Abstractions;
using TigerCS.Application.Modules.Administration.Dto;
using TigerCS.Application.Modules.IdentityAndAccess.Abstractions;
using TigerCS.Application.Modules.IdentityAndAccess.Dto;
using TigerCS.Application.Modules.IdentityAndAccess.Services;
using TigerCS.Domain.Modules.IdentityAndAccess;

namespace TigerCS.Application.Modules.Administration.Services;

/// <summary>
/// User administration (Administration phase, SystemAdministrator only —
/// enforced at the API). Composes the EXISTING mechanisms rather than
/// re-implementing them: ASP.NET Core Identity for accounts/roles
/// (<see cref="IUserAccountManager"/>), <see cref="UserActivationAppService"/>
/// for activation (with its last-active-administrator guard), and
/// <see cref="DepartmentAssignmentService"/> for membership. Users are never
/// physically deleted: deactivation is the only retirement path, so every
/// historical ticket owner, approver and audit actor stays resolvable.
/// </summary>
public sealed class AdminUserAppService(
    IEmployeeRepository employeeRepository,
    IUserAccountManager accountManager,
    IUserRoleReader roleReader,
    IUserDepartmentAssignmentRepository assignmentRepository,
    IDepartmentRepository departmentRepository,
    DepartmentAssignmentService departmentAssignmentService,
    UserActivationAppService activationService,
    IIdentityUnitOfWork unitOfWork,
    IAuditEntryWriter auditWriter,
    TimeProvider timeProvider)
{
    public async Task<AdminUserListDto> ListAsync(
        string? search, bool includeInactive, int page, int pageSize, CancellationToken cancellationToken = default)
    {
        page = Math.Max(page, 1);
        pageSize = Math.Clamp(pageSize, 1, 100);

        IReadOnlyCollection<Guid>? narrowing = null;
        var employees = await employeeRepository.ListAsync(includeInactive, narrowing, cancellationToken);

        if (!string.IsNullOrWhiteSpace(search))
        {
            var accountMatches = await accountManager.SearchAccountIdsAsync(search, cancellationToken);
            var term = search.Trim();
            employees = employees
                .Where(e => e.DisplayName.Contains(term, StringComparison.OrdinalIgnoreCase) || accountMatches.Contains(e.EmployeeId))
                .ToList();
        }

        var pageItems = employees.Skip((page - 1) * pageSize).Take(pageSize).ToList();
        var accounts = await accountManager.GetAccountsAsync(pageItems.Select(e => e.EmployeeId).ToList(), cancellationToken);

        var items = new List<AdminUserDto>(pageItems.Count);
        foreach (var employee in pageItems)
        {
            items.Add(await ToDtoAsync(employee, accounts.GetValueOrDefault(employee.EmployeeId), cancellationToken));
        }

        return new AdminUserListDto(items, page, pageSize, employees.Count);
    }

    public async Task<AdminUserDto?> GetAsync(Guid employeeId, CancellationToken cancellationToken = default)
    {
        var employee = await employeeRepository.GetByIdAsync(employeeId, cancellationToken);
        if (employee is null)
        {
            return null;
        }

        var account = await accountManager.GetAccountAsync(employeeId, cancellationToken);
        return await ToDtoAsync(employee, account, cancellationToken);
    }

    public async Task<AdminResult<AdminUserDto>> CreateAsync(
        Guid actorEmployeeId, CreateUserRequestDto request, CancellationToken cancellationToken = default)
    {
        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(request.UserName))
        {
            errors.Add("User name is required.");
        }

        if (string.IsNullOrWhiteSpace(request.DisplayName))
        {
            errors.Add("Display name is required.");
        }

        if (string.IsNullOrWhiteSpace(request.InitialPassword))
        {
            errors.Add("An initial password is required.");
        }

        var unknownRoles = (request.Roles ?? []).Where(r => !Roles.All.Contains(r, StringComparer.Ordinal)).ToList();
        if (unknownRoles.Count > 0)
        {
            errors.Add($"Unknown role(s): {string.Join(", ", unknownRoles)}.");
        }

        Department? primaryDepartment = null;
        if (request.PrimaryDepartmentId is { } departmentId)
        {
            primaryDepartment = await departmentRepository.GetByIdAsync(departmentId, cancellationToken);
            if (primaryDepartment is null)
            {
                errors.Add("The selected department does not exist.");
            }
            else if (!primaryDepartment.IsActive)
            {
                errors.Add($"Department '{primaryDepartment.Name}' is inactive and cannot receive new members.");
            }
        }

        if (errors.Count > 0)
        {
            return AdminResult<AdminUserDto>.Invalid(errors);
        }

        // Identity creates the account and validates the initial password
        // with its own policy — the same mechanism the development seed uses.
        var created = await accountManager.CreateAccountAsync(request.UserName.Trim(), request.Email, request.InitialPassword, cancellationToken);
        if (!created.Succeeded)
        {
            return AdminResult<AdminUserDto>.Invalid(created.Errors);
        }

        var now = timeProvider.GetUtcNow().UtcDateTime;
        var employee = new Employee(created.EmployeeId, request.DisplayName.Trim(), request.IsGeynessStaff, now);
        await employeeRepository.AddAsync(employee, cancellationToken);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        if (request.Roles is { Count: > 0 })
        {
            var roleResult = await accountManager.SetRolesAsync(employee.EmployeeId, request.Roles, cancellationToken);
            if (!roleResult.Succeeded)
            {
                return AdminResult<AdminUserDto>.Invalid(roleResult.Errors);
            }
        }

        if (primaryDepartment is not null)
        {
            await departmentAssignmentService.AssignAsync(
                employee.EmployeeId, primaryDepartment.DepartmentId, isPrimary: true, actorEmployeeId, cancellationToken);
        }

        await auditWriter.WriteAsync(
            actorEmployeeId, "AdminCreateUser", "Employee", employee.EmployeeId.ToString(),
            beforeValue: null,
            afterValue: $"UserName={request.UserName.Trim()};DisplayName={employee.DisplayName};Roles={string.Join("|", request.Roles ?? [])};PrimaryDepartmentId={request.PrimaryDepartmentId}",
            Guid.NewGuid(), cancellationToken);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        return AdminResult<AdminUserDto>.Success((await GetAsync(employee.EmployeeId, cancellationToken))!);
    }

    public async Task<AdminResult<AdminUserDto>> UpdateProfileAsync(
        Guid actorEmployeeId, Guid employeeId, UpdateUserProfileRequestDto request, CancellationToken cancellationToken = default)
    {
        var employee = await employeeRepository.GetByIdAsync(employeeId, cancellationToken);
        if (employee is null)
        {
            return AdminResult<AdminUserDto>.NotFound();
        }

        if (string.IsNullOrWhiteSpace(request.DisplayName))
        {
            return AdminResult<AdminUserDto>.Invalid("Display name is required.");
        }

        var before = $"DisplayName={employee.DisplayName};IsGeynessStaff={employee.IsGeynessStaff}";
        employee.UpdateProfile(request.DisplayName, request.IsGeynessStaff);

        var emailResult = await accountManager.UpdateEmailAsync(employeeId, request.Email, cancellationToken);
        if (!emailResult.Succeeded)
        {
            return AdminResult<AdminUserDto>.Invalid(emailResult.Errors);
        }

        await auditWriter.WriteAsync(
            actorEmployeeId, "AdminUpdateUserProfile", "Employee", employeeId.ToString(),
            before, $"DisplayName={employee.DisplayName};IsGeynessStaff={employee.IsGeynessStaff};Email={request.Email}",
            Guid.NewGuid(), cancellationToken);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        return AdminResult<AdminUserDto>.Success((await GetAsync(employeeId, cancellationToken))!);
    }

    /// <summary>Activation/deactivation — delegated to the existing service so the last-active-administrator guard applies unchanged.</summary>
    public async Task<AdminResult<AdminUserDto>> SetActivationAsync(
        Guid actorEmployeeId, Guid employeeId, SetActiveRequestDto request, CancellationToken cancellationToken = default)
    {
        var result = await activationService.SetActivationAsync(
            employeeId, new ActivationRequestDto(request.IsActive, request.Reason), cancellationToken);

        switch (result.Outcome)
        {
            case ActivationOutcome.NotFound:
                return AdminResult<AdminUserDto>.NotFound();
            case ActivationOutcome.LastActiveAdministrator:
                return AdminResult<AdminUserDto>.Conflict("Cannot deactivate the last active System Administrator.");
        }

        await auditWriter.WriteAsync(
            actorEmployeeId, request.IsActive ? "AdminActivateUser" : "AdminDeactivateUser", "Employee", employeeId.ToString(),
            beforeValue: null, afterValue: $"IsActive={request.IsActive};Reason={request.Reason}", Guid.NewGuid(), cancellationToken);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        return AdminResult<AdminUserDto>.Success((await GetAsync(employeeId, cancellationToken))!);
    }

    public async Task<AdminResult<AdminUserDto>> SetRolesAsync(
        Guid actorEmployeeId, Guid employeeId, SetUserRolesRequestDto request, CancellationToken cancellationToken = default)
    {
        var employee = await employeeRepository.GetByIdAsync(employeeId, cancellationToken);
        if (employee is null)
        {
            return AdminResult<AdminUserDto>.NotFound();
        }

        var roles = (request.Roles ?? []).Distinct(StringComparer.Ordinal).ToList();
        var unknownRoles = roles.Where(r => !Roles.All.Contains(r, StringComparer.Ordinal)).ToList();
        if (unknownRoles.Count > 0)
        {
            return AdminResult<AdminUserDto>.Invalid($"Unknown role(s): {string.Join(", ", unknownRoles)}. Only the fixed TigerCS roles can be assigned.");
        }

        var currentRoles = await roleReader.GetRolesAsync(employeeId, cancellationToken);

        // Same protection the activation guard gives: the system never loses
        // its last active administrator through a role edit either.
        if (employee.IsActive
            && currentRoles.Contains(Roles.SystemAdministrator)
            && !roles.Contains(Roles.SystemAdministrator)
            && await employeeRepository.CountActiveInRoleAsync(Roles.SystemAdministrator, cancellationToken) <= 1)
        {
            return AdminResult<AdminUserDto>.Conflict("Cannot remove the System Administrator role from the last active System Administrator.");
        }

        var result = await accountManager.SetRolesAsync(employeeId, roles, cancellationToken);
        if (!result.Succeeded)
        {
            return AdminResult<AdminUserDto>.Invalid(result.Errors);
        }

        await auditWriter.WriteAsync(
            actorEmployeeId, "AdminSetUserRoles", "Employee", employeeId.ToString(),
            string.Join("|", currentRoles), string.Join("|", roles), Guid.NewGuid(), cancellationToken);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        return AdminResult<AdminUserDto>.Success((await GetAsync(employeeId, cancellationToken))!);
    }

    public async Task<AdminResult<AdminUserDto>> AddDepartmentAsync(
        Guid actorEmployeeId, Guid employeeId, AddDepartmentMembershipRequestDto request, CancellationToken cancellationToken = default)
    {
        var employee = await employeeRepository.GetByIdAsync(employeeId, cancellationToken);
        if (employee is null)
        {
            return AdminResult<AdminUserDto>.NotFound();
        }

        try
        {
            await departmentAssignmentService.AssignAsync(employeeId, request.DepartmentId, request.IsPrimary, actorEmployeeId, cancellationToken);
        }
        catch (KeyNotFoundException)
        {
            return AdminResult<AdminUserDto>.Invalid("The selected department does not exist.");
        }
        catch (DepartmentAssignmentException ex)
        {
            return AdminResult<AdminUserDto>.Conflict(ex.Message);
        }

        await auditWriter.WriteAsync(
            actorEmployeeId, "AdminAddDepartmentMembership", "Employee", employeeId.ToString(),
            beforeValue: null, afterValue: $"DepartmentId={request.DepartmentId};IsPrimary={request.IsPrimary}", Guid.NewGuid(), cancellationToken);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        return AdminResult<AdminUserDto>.Success((await GetAsync(employeeId, cancellationToken))!);
    }

    /// <summary>
    /// Sets, in one atomic save, the departments (besides the primary one) the user may access. Built on the existing membership model
    /// (<c>UserDepartmentAssignments</c>) - the same rows that already scope the user's ticket visibility - so nothing parallel exists to
    /// drift apart. The primary department is preserved as it is: never added, demoted or removed here, whatever the list says. Only
    /// active departments can be added; a department that is already held (even if now inactive) can stay or be removed.
    /// </summary>
    public async Task<AdminResult<AdminUserDto>> SetAccessibleDepartmentsAsync(
        Guid actorEmployeeId, Guid employeeId, SetAccessibleDepartmentsRequestDto request, CancellationToken cancellationToken = default)
    {
        var employee = await employeeRepository.GetByIdAsync(employeeId, cancellationToken);
        if (employee is null)
        {
            return AdminResult<AdminUserDto>.NotFound();
        }

        var wanted = (request.DepartmentIds ?? []).Distinct().ToHashSet();
        var current = await assignmentRepository.GetByEmployeeIdAsync(employeeId, cancellationToken);
        var primary = current.FirstOrDefault(a => a.IsPrimary);
        var held = current.ToDictionary(a => a.DepartmentId);

        var toAdd = wanted.Where(id => !held.ContainsKey(id) && id != primary?.DepartmentId).ToList();
        var toRemove = current.Where(a => !a.IsPrimary && !wanted.Contains(a.DepartmentId)).ToList();

        if (toAdd.Count > 0 && !employee.IsActive)
        {
            return AdminResult<AdminUserDto>.Conflict("A deactivated user cannot be given access to more departments.");
        }

        foreach (var departmentId in toAdd)
        {
            var department = await departmentRepository.GetByIdAsync(departmentId, cancellationToken);
            if (department is null)
            {
                return AdminResult<AdminUserDto>.Invalid($"Department {departmentId} does not exist.");
            }

            if (!department.IsActive)
            {
                return AdminResult<AdminUserDto>.Invalid($"The department '{department.Name}' is inactive and cannot be granted.");
            }
        }

        if (toAdd.Count == 0 && toRemove.Count == 0)
        {
            return AdminResult<AdminUserDto>.Success((await GetAsync(employeeId, cancellationToken))!);
        }

        var now = timeProvider.GetUtcNow().UtcDateTime;
        foreach (var departmentId in toAdd)
        {
            await assignmentRepository.AddAsync(new UserDepartmentAssignment(employeeId, departmentId, false, now, actorEmployeeId), cancellationToken);
        }

        foreach (var assignment in toRemove)
        {
            assignmentRepository.Remove(assignment);
        }

        await auditWriter.WriteAsync(
            actorEmployeeId, "AdminSetAccessibleDepartments", "Employee", employeeId.ToString(),
            beforeValue: string.Join(",", current.Select(a => a.DepartmentId).Order()),
            afterValue: string.Join(",", current.Where(a => a.IsPrimary || wanted.Contains(a.DepartmentId)).Select(a => a.DepartmentId).Concat(toAdd).Order()),
            Guid.NewGuid(), cancellationToken);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        return AdminResult<AdminUserDto>.Success((await GetAsync(employeeId, cancellationToken))!);
    }

    public async Task<AdminResult<AdminUserDto>> RemoveDepartmentAsync(
        Guid actorEmployeeId, Guid employeeId, int departmentId, CancellationToken cancellationToken = default)
    {
        var employee = await employeeRepository.GetByIdAsync(employeeId, cancellationToken);
        if (employee is null)
        {
            return AdminResult<AdminUserDto>.NotFound();
        }

        var assignment = await assignmentRepository.GetAsync(employeeId, departmentId, cancellationToken);
        if (assignment is null)
        {
            return AdminResult<AdminUserDto>.NotFound();
        }

        var all = await assignmentRepository.GetByEmployeeIdAsync(employeeId, cancellationToken);
        if (assignment.IsPrimary && all.Count > 1)
        {
            return AdminResult<AdminUserDto>.Conflict(
                "This is the user's primary department. Make another department primary before removing it.");
        }

        assignmentRepository.Remove(assignment);
        await auditWriter.WriteAsync(
            actorEmployeeId, "AdminRemoveDepartmentMembership", "Employee", employeeId.ToString(),
            $"DepartmentId={departmentId};IsPrimary={assignment.IsPrimary}", afterValue: null, Guid.NewGuid(), cancellationToken);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        return AdminResult<AdminUserDto>.Success((await GetAsync(employeeId, cancellationToken))!);
    }

    /// <summary>
    /// Replaces a user's password without knowing the old one — the "forgot
    /// my password" path, which only a System Administrator can take on
    /// someone's behalf. Identity's validators decide whether the new password
    /// is acceptable (<see cref="AdminOutcome.PasswordPolicyViolation"/>
    /// otherwise). On success the account's security stamp is rotated, so every
    /// session the user had is invalidated, and any lockout is lifted so the
    /// user can sign in with the new password immediately. Audited as
    /// <c>AdminResetUserPassword</c> with the reason only — never the password.
    /// </summary>
    public async Task<AdminResult> ResetPasswordAsync(
        Guid actorEmployeeId, Guid employeeId, ResetUserPasswordRequestDto request, CancellationToken cancellationToken = default)
    {
        var employee = await employeeRepository.GetByIdAsync(employeeId, cancellationToken);
        if (employee is null)
        {
            return AdminResult.NotFound();
        }

        if (string.IsNullOrWhiteSpace(request.NewPassword))
        {
            return AdminResult.PasswordPolicyViolation(["A new password is required."]);
        }

        var result = await accountManager.ResetPasswordAsync(employeeId, request.NewPassword, cancellationToken);
        switch (result.Outcome)
        {
            case PasswordChangeOutcome.UserNotFound:
                return AdminResult.NotFound();
            case PasswordChangeOutcome.PolicyViolation:
                return AdminResult.PasswordPolicyViolation(result.Errors);
        }

        await auditWriter.WriteAsync(
            actorEmployeeId, "AdminResetUserPassword", "User", employeeId.ToString(),
            beforeValue: null,
            afterValue: $"Reason={request.Reason};LockoutCleared=true;SecurityStampRotated=true",
            Guid.NewGuid(), cancellationToken);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        return AdminResult.Success();
    }

    /// <summary>
    /// Lifts a sign-in lock-out (lockout end date and failed-attempt count)
    /// through Identity without touching the password, roles or active status.
    /// Audited as <c>AdminUnlockUser</c>. Returns the refreshed user.
    /// </summary>
    public async Task<AdminResult<AdminUserDto>> UnlockAsync(
        Guid actorEmployeeId, Guid employeeId, CancellationToken cancellationToken = default)
    {
        var employee = await employeeRepository.GetByIdAsync(employeeId, cancellationToken);
        if (employee is null)
        {
            return AdminResult<AdminUserDto>.NotFound();
        }

        var account = await accountManager.GetAccountAsync(employeeId, cancellationToken);
        if (account is null)
        {
            return AdminResult<AdminUserDto>.NotFound();
        }

        if (!account.IsLockedOut)
        {
            return AdminResult<AdminUserDto>.Conflict("This account is not locked.");
        }

        await accountManager.UnlockAccountAsync(employeeId, cancellationToken);

        await auditWriter.WriteAsync(
            actorEmployeeId, "AdminUnlockUser", "User", employeeId.ToString(),
            beforeValue: "IsLockedOut=true", afterValue: "IsLockedOut=false;LockoutEnd=null;AccessFailedCount=0",
            Guid.NewGuid(), cancellationToken);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        return AdminResult<AdminUserDto>.Success((await GetAsync(employeeId, cancellationToken))!);
    }

    /// <summary>The live Identity password policy, so the UI never carries its own copy of the rules.</summary>
    public PasswordPolicyDto GetPasswordPolicy()
    {
        var p = accountManager.GetPasswordPolicy();
        return new(p.RequiredLength, p.RequireDigit, p.RequireLowercase, p.RequireUppercase, p.RequireNonAlphanumeric, p.RequiredUniqueChars);
    }

    private async Task<AdminUserDto> ToDtoAsync(Employee employee, UserAccountInfo? account, CancellationToken cancellationToken)
    {
        var roles = await roleReader.GetRolesAsync(employee.EmployeeId, cancellationToken);
        var assignments = await assignmentRepository.GetByEmployeeIdAsync(employee.EmployeeId, cancellationToken);
        var hasHistory = await employeeRepository.IsReferencedByHistoryAsync(employee.EmployeeId, cancellationToken);

        // Department NAMES, never ids: the navigation is loaded by the EF
        // repository; resolve through the department repository otherwise.
        var memberships = new List<DepartmentMembershipDto>();
        foreach (var assignment in assignments)
        {
            var name = assignment.Department?.Name
                ?? (await departmentRepository.GetByIdAsync(assignment.DepartmentId, cancellationToken))?.Name
                ?? "Unknown department";
            memberships.Add(new DepartmentMembershipDto(assignment.DepartmentId, name, assignment.IsPrimary));
        }

        return new AdminUserDto(
            employee.EmployeeId,
            account?.UserName ?? string.Empty,
            account?.Email,
            employee.DisplayName,
            employee.IsGeynessStaff,
            employee.IsActive,
            employee.DeactivatedAtUtc,
            employee.CreatedAtUtc,
            account?.IsLockedOut ?? false,
            roles.OrderBy(r => r, StringComparer.Ordinal).ToList(),
            memberships.OrderByDescending(m => m.IsPrimary).ThenBy(m => m.Name, StringComparer.Ordinal).ToList(),
            hasHistory);
    }
}
