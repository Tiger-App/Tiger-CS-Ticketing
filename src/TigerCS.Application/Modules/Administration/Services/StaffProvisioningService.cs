using TigerCS.Application.Modules.Administration.Dto;
using TigerCS.Application.Modules.IdentityAndAccess.Abstractions;
using TigerCS.Domain.Modules.IdentityAndAccess;

namespace TigerCS.Application.Modules.Administration.Services;

/// <summary>One person to be provisioned. The department is named by its stable <c>Code</c> (never by a new record) and the role must be an existing fixed role.</summary>
public sealed record StaffRosterEntry(string FullName, string Email, string DepartmentCode, string Role, string? UserName = null)
{
    /// <summary>The Identity user name the account is looked up and created by: the explicit <see cref="UserName"/> (department accounts have no email), otherwise the email.</summary>
    public string Identifier => (UserName ?? Email).Trim();

    /// <summary>The email stored on the account: none for a department-level account.</summary>
    public string? AccountEmail => UserName is null ? Email.Trim() : null;
}

/// <summary>
/// The staff requested for provisioning. Customer Services users are CS Agents (the existing front-line role). Registration users are
/// Department Employees: the existing non-administrative role that "works and resolves tickets assigned to their department"
/// (<see cref="Roles.Descriptions"/>), it carries no management, assignment, close or administration authority, and it is already part of the
/// Round Robin eligible set for every department except the Call Center. No new role is invented and no administrative privilege is given.
/// </summary>
public static class StaffRoster
{
    public const string CustomerServiceDepartmentCode = "CS";
    public const string RegistrationDepartmentCode = "REG";

    /// <summary>
    /// Department-level (shared) accounts: not people. Same existing non-administrative role, primary membership of their own department,
    /// no email. They are ordinary Identity users, deliberately NOT configured service identities (that list confines an account to the
    /// integration surface, which would stop it working tickets), so the Round Robin exclusion of service identities is untouched.
    /// </summary>
    public static readonly IReadOnlyList<StaffRosterEntry> DepartmentAccounts =
    [
        new("Accounting", "", "ACC", Roles.DepartmentEmployee, UserName: "Accounting"),
        new("Facilities Management", "", "FM", Roles.DepartmentEmployee, UserName: "FacilitiesManagement"),
        new("Leasing Customer Services", "", "LCS", Roles.DepartmentEmployee, UserName: "LeasingCustomerServices")
    ];

    /// <summary>Identity user names of the department-level accounts, so reports can tell them apart from individual employees.</summary>
    public static readonly IReadOnlyCollection<string> DepartmentAccountUserNames =
        DepartmentAccounts.Select(a => a.Identifier).ToList();

    public static readonly IReadOnlyList<StaffRosterEntry> RequestedStaff =
    [
        new("Zakarya Azzouz", "customerservice1@tigergroup.ae", CustomerServiceDepartmentCode, Roles.CsAgent),
        new("Mohammad Al Kazzaz", "Customerservice3@tigergroup.ae", CustomerServiceDepartmentCode, Roles.CsAgent),
        new("Assel Azhibayeva", "customerservice2@tigergroup.ae", CustomerServiceDepartmentCode, Roles.CsAgent),
        new("Nada Emad", "n.eid@tigergroup.ae", RegistrationDepartmentCode, Roles.DepartmentEmployee),
        new("Gyl Quintana", "Gyl.Quintana@tigergroup.ae", RegistrationDepartmentCode, Roles.DepartmentEmployee),
        new("Marielle De Lara", "Marielle.Bulanadi@tigergroup.ae", RegistrationDepartmentCode, Roles.DepartmentEmployee),
        new("Nickole Gayle", "Nickole.Dejesus@tigergroup.ae", RegistrationDepartmentCode, Roles.DepartmentEmployee)
    ];
}

public enum StaffProvisioningStatus
{
    /// <summary>Dry run: the account does not exist and every prerequisite is in place, so apply would create it.</summary>
    WouldCreate,

    /// <summary>Created by this run through the existing administration create-user path.</summary>
    Created,

    /// <summary>An account with this email/user name exists and already has the requested department and role. Nothing was written.</summary>
    AlreadyExists,

    /// <summary>Nothing was written and a person must look: the account exists with a different role/department, is deactivated or incomplete, or a prerequisite (department, role) is missing.</summary>
    Skipped
}

public sealed record StaffProvisioningLine(
    StaffRosterEntry Entry,
    StaffProvisioningStatus Status,
    string Detail,
    Guid? EmployeeId,
    string? DepartmentName,
    IReadOnlyList<string> Roles);

/// <summary>
/// Idempotent provisioning of a fixed roster through the EXISTING administration create-user path
/// (<see cref="AdminUserAppService.CreateAsync"/>: Identity account + Employee + role + primary department + audit entry).
/// It adds nothing new to authentication: no password handling of its own (the caller supplies the initial password exactly as an
/// administrator does in User Management; Identity's policy validates it), no new department, no new role.
/// <para>
/// Safety rules: an existing account is never modified, only reported; a person is matched by email or user name, case-insensitively, so
/// re-running creates nothing twice; departments are resolved by existing <c>Code</c> and an inactive or missing one skips the user.
/// </para>
/// </summary>
public sealed class StaffProvisioningService(
    AdminUserAppService userService,
    IUserAccountManager accountManager,
    IUserRoleReader roleReader,
    IEmployeeRepository employeeRepository,
    IDepartmentRepository departmentRepository,
    IUserDepartmentAssignmentRepository assignmentRepository)
{
    /// <summary>Read-only: what apply would do. Writes nothing.</summary>
    public Task<IReadOnlyList<StaffProvisioningLine>> PlanAsync(
        IReadOnlyList<StaffRosterEntry> roster, CancellationToken cancellationToken = default) =>
        RunAsync(roster, apply: false, actorEmployeeId: null, passwordFor: null, cancellationToken);

    /// <summary>
    /// Creates the missing users. <paramref name="actorEmployeeId"/> must be an existing active System Administrator: the audit entry names the
    /// administrator who authorised the provisioning, as for any user created in User Management.
    /// <paramref name="passwordFor"/> supplies the initial password per user; it is passed to Identity and never stored or logged here.
    /// </summary>
    public Task<IReadOnlyList<StaffProvisioningLine>> ApplyAsync(
        IReadOnlyList<StaffRosterEntry> roster, Guid actorEmployeeId, Func<StaffRosterEntry, string> passwordFor,
        CancellationToken cancellationToken = default) =>
        RunAsync(roster, apply: true, actorEmployeeId, passwordFor, cancellationToken);

    private async Task<IReadOnlyList<StaffProvisioningLine>> RunAsync(
        IReadOnlyList<StaffRosterEntry> roster, bool apply, Guid? actorEmployeeId, Func<StaffRosterEntry, string>? passwordFor,
        CancellationToken cancellationToken)
    {
        var lines = new List<StaffProvisioningLine>(roster.Count);

        // A roster that lists the same email twice must not create it twice.
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var entry in roster)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var email = entry.Identifier;

            if (!seen.Add(email))
            {
                lines.Add(Skip(entry, "Duplicate email/user name within the roster; listed once."));
                continue;
            }

            if (!Roles.All.Contains(entry.Role, StringComparer.Ordinal))
            {
                lines.Add(Skip(entry, $"Role '{entry.Role}' does not exist; no role is invented."));
                continue;
            }

            var department = (await departmentRepository.ListAsync(activeOnly: false, cancellationToken))
                .Where(d => string.Equals(d.Code, entry.DepartmentCode, StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (department.Count != 1)
            {
                lines.Add(Skip(entry, department.Count == 0
                    ? $"Department with code '{entry.DepartmentCode}' does not exist; none is created."
                    : $"Department code '{entry.DepartmentCode}' is ambiguous."));
                continue;
            }

            var dept = department[0];
            var existing = await FindExistingAsync(email, cancellationToken);
            if (existing is not null)
            {
                lines.Add(await DescribeExistingAsync(entry, dept, existing, cancellationToken));
                continue;
            }

            if (!dept.IsActive)
            {
                lines.Add(Skip(entry, $"Department '{dept.Name}' is inactive and cannot receive new members."));
                continue;
            }

            if (!apply)
            {
                lines.Add(new StaffProvisioningLine(entry, StaffProvisioningStatus.WouldCreate,
                    "Account does not exist; would be created.", null, dept.Name, [entry.Role]));
                continue;
            }

            var result = await userService.CreateAsync(
                actorEmployeeId!.Value,
                new CreateUserRequestDto(entry.UserName is null ? email.ToLowerInvariant() : email, entry.AccountEmail, entry.FullName.Trim(), IsGeynessStaff: false,
                    passwordFor!(entry), [entry.Role], dept.DepartmentId),
                cancellationToken);

            lines.Add(result.Outcome == AdminOutcome.Success
                ? new StaffProvisioningLine(entry, StaffProvisioningStatus.Created,
                    "Created through the administration create-user path.", result.Value!.EmployeeId, dept.Name, result.Value.Roles)
                : Skip(entry, "Create failed: " + string.Join("; ", result.Errors ?? [])));
        }

        return lines;
    }

    private static StaffProvisioningLine Skip(StaffRosterEntry entry, string detail) =>
        new(entry, StaffProvisioningStatus.Skipped, detail, null, null, []);

    private async Task<UserAccountInfo?> FindExistingAsync(string email, CancellationToken cancellationToken)
    {
        var ids = await accountManager.SearchAccountIdsAsync(email, cancellationToken);
        if (ids.Count == 0)
        {
            return null;
        }

        var accounts = await accountManager.GetAccountsAsync(ids.ToList(), cancellationToken);
        return accounts.Values.FirstOrDefault(a =>
            string.Equals(a.UserName, email, StringComparison.OrdinalIgnoreCase)
            || string.Equals(a.Email, email, StringComparison.OrdinalIgnoreCase));
    }

    private async Task<StaffProvisioningLine> DescribeExistingAsync(
        StaffRosterEntry entry, Department dept, UserAccountInfo account, CancellationToken cancellationToken)
    {
        var employee = await employeeRepository.GetByIdAsync(account.EmployeeId, cancellationToken);
        var roles = (await roleReader.GetRolesAsync(account.EmployeeId, cancellationToken)).ToList();
        var inDepartment = await assignmentRepository.ExistsAsync(account.EmployeeId, dept.DepartmentId, cancellationToken);

        if (employee is null)
        {
            return new(entry, StaffProvisioningStatus.Skipped,
                "An Identity account exists without an Employee profile; needs manual review (not modified).", account.EmployeeId, null, roles);
        }

        if (!employee.IsActive)
        {
            return new(entry, StaffProvisioningStatus.Skipped,
                "Account exists but is deactivated; not reactivated automatically.", account.EmployeeId, inDepartment ? dept.Name : null, roles);
        }

        if (!inDepartment || !roles.Contains(entry.Role, StringComparer.Ordinal))
        {
            return new(entry, StaffProvisioningStatus.Skipped,
                $"Account exists but is not {entry.Role} in {dept.Name} (roles: {(roles.Count == 0 ? "none" : string.Join(", ", roles))}); existing assignments left untouched.",
                account.EmployeeId, inDepartment ? dept.Name : null, roles);
        }

        return new(entry, StaffProvisioningStatus.AlreadyExists,
            "Account already exists with the requested department and role; nothing written.", account.EmployeeId, dept.Name, roles);
    }
}
