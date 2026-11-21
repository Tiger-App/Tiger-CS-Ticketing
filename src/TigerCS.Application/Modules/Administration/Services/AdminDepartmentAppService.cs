using TigerCS.Application.Abstractions;
using TigerCS.Application.Modules.Administration.Abstractions;
using TigerCS.Application.Modules.Administration.Dto;
using TigerCS.Application.Modules.IdentityAndAccess.Abstractions;
using TigerCS.Application.Modules.IdentityAndAccess.Services;
using TigerCS.Application.Modules.WorkflowConfiguration.Abstractions;
using TigerCS.Domain.Modules.IdentityAndAccess;

namespace TigerCS.Application.Modules.Administration.Services;

/// <summary>
/// Department administration. Departments are never physically deleted —
/// tickets reference them as current and originating department (both FKs
/// Restrict) — so deactivation is the only retirement path; an inactive
/// department stays visible wherever history names it and is simply never
/// offered for new work. Membership goes through the existing
/// <see cref="DepartmentAssignmentService"/>.
/// </summary>
public sealed class AdminDepartmentAppService(
    IDepartmentRepository departmentRepository,
    IUserDepartmentAssignmentRepository assignmentRepository,
    IEmployeeRepository employeeRepository,
    IUserRoleReader roleReader,
    IRequestTypeRepository requestTypeRepository,
    DepartmentAssignmentService departmentAssignmentService,
    IIdentityUnitOfWork unitOfWork,
    IAuditEntryWriter auditWriter,
    IDepartmentDeactivationDependencyReader dependencyReader)
{
    public async Task<IReadOnlyList<AdminDepartmentDto>> ListAsync(bool includeInactive, CancellationToken cancellationToken = default)
    {
        var departments = await departmentRepository.ListAsync(activeOnly: !includeInactive, cancellationToken);
        var result = new List<AdminDepartmentDto>(departments.Count);
        foreach (var department in departments)
        {
            var members = await assignmentRepository.GetByDepartmentIdAsync(department.DepartmentId, activeEmployeesOnly: true, cancellationToken);
            var ticketReferences = await departmentRepository.CountTicketReferencesAsync(department.DepartmentId, cancellationToken);
            var requestTypes = await requestTypeRepository.ListAsync(department.DepartmentId, includeInactive: true, cancellationToken);
            result.Add(new AdminDepartmentDto(
                department.DepartmentId, department.Name, department.Code, department.IsActive,
                members.Count, ticketReferences, requestTypes.Count));
        }

        return result;
    }

    public async Task<AdminDepartmentDetailDto?> GetAsync(int departmentId, CancellationToken cancellationToken = default)
    {
        var department = await departmentRepository.GetByIdAsync(departmentId, cancellationToken);
        if (department is null)
        {
            return null;
        }

        var assignments = await assignmentRepository.GetByDepartmentIdAsync(departmentId, activeEmployeesOnly: false, cancellationToken);
        var members = new List<DepartmentMemberDto>();
        foreach (var assignment in assignments)
        {
            // The EF repository loads the Employee navigation; resolve through
            // the employee repository otherwise — names, never ids.
            var employee = assignment.Employee ?? await employeeRepository.GetByIdAsync(assignment.EmployeeId, cancellationToken);
            var roles = await roleReader.GetRolesAsync(assignment.EmployeeId, cancellationToken);
            members.Add(new DepartmentMemberDto(
                assignment.EmployeeId, employee?.DisplayName ?? "Unknown user", assignment.IsPrimary, employee?.IsActive ?? false, roles.ToList()));
        }

        members = members.OrderBy(m => m.DisplayName, StringComparer.Ordinal).ToList();

        var ticketReferences = await departmentRepository.CountTicketReferencesAsync(departmentId, cancellationToken);
        var requestTypes = await requestTypeRepository.ListAsync(departmentId, includeInactive: true, cancellationToken);

        return new AdminDepartmentDetailDto(
            department.DepartmentId, department.Name, department.Code, department.IsActive, ticketReferences, requestTypes.Count, members);
    }

    public async Task<AdminResult<AdminDepartmentDetailDto>> CreateAsync(
        Guid actorEmployeeId, SaveDepartmentRequestDto request, CancellationToken cancellationToken = default)
    {
        var errors = await ValidateAsync(request, excludeDepartmentId: null, cancellationToken);
        if (errors.Count > 0)
        {
            return AdminResult<AdminDepartmentDetailDto>.Invalid(errors);
        }

        var department = new Department(request.Name.Trim(), request.Code.Trim().ToUpperInvariant());
        await departmentRepository.AddAsync(department, cancellationToken);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        await auditWriter.WriteAsync(
            actorEmployeeId, "AdminCreateDepartment", "Department", department.DepartmentId.ToString(),
            beforeValue: null, afterValue: $"Name={department.Name};Code={department.Code}", Guid.NewGuid(), cancellationToken);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        return AdminResult<AdminDepartmentDetailDto>.Success((await GetAsync(department.DepartmentId, cancellationToken))!);
    }

    public async Task<AdminResult<AdminDepartmentDetailDto>> UpdateAsync(
        Guid actorEmployeeId, int departmentId, SaveDepartmentRequestDto request, CancellationToken cancellationToken = default)
    {
        var department = await departmentRepository.GetByIdAsync(departmentId, cancellationToken);
        if (department is null)
        {
            return AdminResult<AdminDepartmentDetailDto>.NotFound();
        }

        var errors = await ValidateAsync(request, departmentId, cancellationToken);
        if (errors.Count > 0)
        {
            return AdminResult<AdminDepartmentDetailDto>.Invalid(errors);
        }

        var before = $"Name={department.Name};Code={department.Code}";
        department.Rename(request.Name, request.Code);

        await auditWriter.WriteAsync(
            actorEmployeeId, "AdminUpdateDepartment", "Department", departmentId.ToString(),
            before, $"Name={department.Name};Code={department.Code}", Guid.NewGuid(), cancellationToken);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        return AdminResult<AdminDepartmentDetailDto>.Success((await GetAsync(departmentId, cancellationToken))!);
    }

    public async Task<AdminResult<AdminDepartmentDetailDto>> SetActivationAsync(
        Guid actorEmployeeId, int departmentId, SetActiveRequestDto request, CancellationToken cancellationToken = default)
    {
        var department = await departmentRepository.GetByIdAsync(departmentId, cancellationToken);
        if (department is null)
        {
            return AdminResult<AdminDepartmentDetailDto>.NotFound();
        }

        // Safety guard: a department with live routing or approval dependencies cannot be switched off - doing so would strand work
        // (new tickets rejected at routing, approvals nobody can decide). Nothing changes until every blocker is resolved.
        if (!request.IsActive && department.IsActive)
        {
            var blockers = await dependencyReader.GetBlockersAsync(departmentId, cancellationToken);
            if (blockers.Count > 0)
            {
                return AdminResult<AdminDepartmentDetailDto>.Conflict(
                    [$"'{department.Name}' cannot be deactivated while it has unresolved dependencies.", .. blockers]);
            }
        }

        var before = department.IsActive;
        if (request.IsActive)
        {
            department.Activate();
        }
        else
        {
            department.Deactivate();
        }

        await auditWriter.WriteAsync(
            actorEmployeeId, request.IsActive ? "AdminActivateDepartment" : "AdminDeactivateDepartment", "Department", departmentId.ToString(),
            $"IsActive={before}", $"IsActive={department.IsActive};Reason={request.Reason}", Guid.NewGuid(), cancellationToken);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        return AdminResult<AdminDepartmentDetailDto>.Success((await GetAsync(departmentId, cancellationToken))!);
    }

    public async Task<AdminResult<AdminDepartmentDetailDto>> AddMemberAsync(
        Guid actorEmployeeId, int departmentId, AddDepartmentMemberRequestDto request, CancellationToken cancellationToken = default)
    {
        var department = await departmentRepository.GetByIdAsync(departmentId, cancellationToken);
        if (department is null)
        {
            return AdminResult<AdminDepartmentDetailDto>.NotFound();
        }

        if (!department.IsActive)
        {
            return AdminResult<AdminDepartmentDetailDto>.Conflict($"'{department.Name}' is inactive and cannot receive new members.");
        }

        if (await employeeRepository.GetByIdAsync(request.EmployeeId, cancellationToken) is null)
        {
            return AdminResult<AdminDepartmentDetailDto>.Invalid("The selected user does not exist.");
        }

        try
        {
            await departmentAssignmentService.AssignAsync(request.EmployeeId, departmentId, request.IsPrimary, actorEmployeeId, cancellationToken);
        }
        catch (DepartmentAssignmentException ex)
        {
            return AdminResult<AdminDepartmentDetailDto>.Conflict(ex.Message);
        }

        await auditWriter.WriteAsync(
            actorEmployeeId, "AdminAddDepartmentMember", "Department", departmentId.ToString(),
            beforeValue: null, afterValue: $"EmployeeId={request.EmployeeId};IsPrimary={request.IsPrimary}", Guid.NewGuid(), cancellationToken);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        return AdminResult<AdminDepartmentDetailDto>.Success((await GetAsync(departmentId, cancellationToken))!);
    }

    public async Task<AdminResult<AdminDepartmentDetailDto>> RemoveMemberAsync(
        Guid actorEmployeeId, int departmentId, Guid employeeId, CancellationToken cancellationToken = default)
    {
        var department = await departmentRepository.GetByIdAsync(departmentId, cancellationToken);
        if (department is null)
        {
            return AdminResult<AdminDepartmentDetailDto>.NotFound();
        }

        var assignment = await assignmentRepository.GetAsync(employeeId, departmentId, cancellationToken);
        if (assignment is null)
        {
            return AdminResult<AdminDepartmentDetailDto>.NotFound();
        }

        var all = await assignmentRepository.GetByEmployeeIdAsync(employeeId, cancellationToken);
        if (assignment.IsPrimary && all.Count > 1)
        {
            return AdminResult<AdminDepartmentDetailDto>.Conflict(
                "This is the user's primary department. Make another department primary before removing this membership.");
        }

        assignmentRepository.Remove(assignment);
        await auditWriter.WriteAsync(
            actorEmployeeId, "AdminRemoveDepartmentMember", "Department", departmentId.ToString(),
            $"EmployeeId={employeeId};IsPrimary={assignment.IsPrimary}", afterValue: null, Guid.NewGuid(), cancellationToken);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        return AdminResult<AdminDepartmentDetailDto>.Success((await GetAsync(departmentId, cancellationToken))!);
    }

    private async Task<List<string>> ValidateAsync(SaveDepartmentRequestDto request, int? excludeDepartmentId, CancellationToken cancellationToken)
    {
        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(request.Name))
        {
            errors.Add("Name is required.");
        }
        else if (await departmentRepository.NameExistsAsync(request.Name.Trim(), excludeDepartmentId, cancellationToken))
        {
            errors.Add($"A department named '{request.Name.Trim()}' already exists.");
        }

        if (string.IsNullOrWhiteSpace(request.Code))
        {
            errors.Add("Code is required.");
        }
        else if (request.Code.Trim().Length > 10)
        {
            errors.Add("Code must be at most 10 characters.");
        }
        else if (await departmentRepository.CodeExistsAsync(request.Code.Trim().ToUpperInvariant(), excludeDepartmentId, cancellationToken))
        {
            errors.Add($"A department with code '{request.Code.Trim().ToUpperInvariant()}' already exists.");
        }

        return errors;
    }
}
