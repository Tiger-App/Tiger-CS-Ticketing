using TigerCS.Application.Abstractions;
using TigerCS.Application.Modules.Administration.Dto;
using TigerCS.Application.Modules.IdentityAndAccess.Abstractions;
using TigerCS.Domain.Modules.IdentityAndAccess;

namespace TigerCS.Application.Modules.Administration.Services;

/// <summary>
/// Administration → Users: the departments an employee may TRANSFER tickets to. Kept apart from department membership (which is what lets an
/// employee view a department) so that viewing never grants transferring; every change is audited with the before and after lists.
/// </summary>
public sealed class AdminUserTransferDepartmentAppService(
    IEmployeeRepository employeeRepository,
    IDepartmentRepository departmentRepository,
    IUserTransferDepartmentRepository transferDepartments,
    IIdentityUnitOfWork unitOfWork,
    IAuditEntryWriter auditWriter,
    TimeProvider timeProvider)
{
    public const string AuditAction = "AdminSetTransferDepartments";

    public async Task<AdminResult<UserTransferDepartmentsDto>> GetAsync(Guid employeeId, CancellationToken cancellationToken = default)
    {
        if (await employeeRepository.GetByIdAsync(employeeId, cancellationToken) is null)
        {
            return AdminResult<UserTransferDepartmentsDto>.NotFound();
        }

        var ids = await transferDepartments.GetDepartmentIdsAsync(employeeId, cancellationToken);
        return AdminResult<UserTransferDepartmentsDto>.Success(new(employeeId, [.. ids.Order()]));
    }

    public async Task<AdminResult<UserTransferDepartmentsDto>> SetAsync(
        Guid actorEmployeeId, Guid employeeId, SetUserTransferDepartmentsRequestDto request, CancellationToken cancellationToken = default)
    {
        if (await employeeRepository.GetByIdAsync(employeeId, cancellationToken) is null)
        {
            return AdminResult<UserTransferDepartmentsDto>.NotFound();
        }

        var wanted = (request.DepartmentIds ?? []).Distinct().Order().ToList();
        var known = (await departmentRepository.ListAsync(activeOnly: true, cancellationToken)).Select(d => d.DepartmentId).ToHashSet();
        var unknown = wanted.Where(id => !known.Contains(id)).ToList();
        if (unknown.Count > 0)
        {
            return AdminResult<UserTransferDepartmentsDto>.Invalid($"Not an active department: {string.Join(", ", unknown)}.");
        }

        var existing = await transferDepartments.GetForEmployeeAsync(employeeId, cancellationToken);
        var before = existing.Select(r => r.DepartmentId).Order().ToList();
        if (before.SequenceEqual(wanted))
        {
            return AdminResult<UserTransferDepartmentsDto>.Success(new(employeeId, wanted));
        }

        var now = timeProvider.GetUtcNow().UtcDateTime;
        foreach (var row in existing.Where(r => !wanted.Contains(r.DepartmentId)))
        {
            transferDepartments.Remove(row);
        }

        foreach (var id in wanted.Where(id => before.All(b => b != id)))
        {
            await transferDepartments.AddAsync(new UserTransferDepartment(employeeId, id, actorEmployeeId, now), cancellationToken);
        }

        await auditWriter.WriteAsync(
            actorEmployeeId, AuditAction, "Employee", employeeId.ToString(),
            beforeValue: "DepartmentIds=" + string.Join(",", before), afterValue: "DepartmentIds=" + string.Join(",", wanted),
            Guid.NewGuid(), cancellationToken);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        return AdminResult<UserTransferDepartmentsDto>.Success(new(employeeId, wanted));
    }
}
