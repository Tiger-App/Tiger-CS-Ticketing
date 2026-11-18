using System.Text.Json;
using TigerCS.Application.Abstractions;
using TigerCS.Application.Modules.Ticketing.Abstractions;
using TigerCS.Application.Modules.Ticketing.Dto;
using TigerCS.Domain.Modules.IdentityAndAccess;
using TigerCS.Domain.Modules.Ticketing;

namespace TigerCS.Application.Modules.Ticketing.Services;

/// <summary>
/// Edits the phone number, project and unit a Customers-directory customer is shown with.
///
/// <para>
/// <b>Local only.</b> The edit is stored as a <see cref="CustomerProfileOverride"/> layered over what the customer's tickets say: no
/// ticket is touched, nothing is sent to Tiger CRM, PACT or Tasleeh, and the customer's identity (so their tickets, history and
/// verification) never changes. Who may call it is the endpoint's <c>Customers.Edit</c> permission; this service additionally refuses
/// read-only roles, only edits a customer the caller can see in the directory, and validates every value.
/// </para>
///
/// <para>
/// <b>Project and unit.</b> They are entered together. The project is matched to the catalogue of projects Tiger Ticketing already knows
/// (case-insensitively, keeping the catalogue's spelling). Where the catalogue can say something, the unit is checked against it: a unit
/// recorded only under a different project is refused; a unit the catalogue has never seen cannot be judged and is accepted. Nothing is
/// invented to fill the gap.
/// </para>
///
/// <para>
/// <b>Audit.</b> One entry per change: the customer's key, the values the directory showed before and the values it shows now, who
/// changed it and when (the audit entry's own actor and timestamp).
/// </para>
/// </summary>
public sealed class CustomerProfileEditAppService(
    CustomerDirectoryAppService directoryAppService,
    ICustomerProfileOverrideRepository repository,
    IProjectCatalogRepository projectCatalog,
    ITicketingUnitOfWork unitOfWork,
    IAuditEntryWriter auditWriter,
    TimeProvider timeProvider)
{
    public const string AuditAction = "UpdateCustomerProfile";
    public const string AuditEntityType = "Customer";

    public async Task<CustomerProfileEditResult> UpdateAsync(
        Guid callerEmployeeId,
        IReadOnlyCollection<string> callerRoles,
        string customerKey,
        UpdateCustomerProfileRequestDto request,
        CancellationToken cancellationToken = default)
    {
        if (Roles.IsReadOnlyCaller(callerRoles))
        {
            return CustomerProfileEditResult.Failure(CustomerProfileEditOutcome.Forbidden);
        }

        if (!CustomerIdentity.TryParse(customerKey, out var identity))
        {
            return CustomerProfileEditResult.Failure(CustomerProfileEditOutcome.InvalidKey);
        }

        // Only a customer the caller can see in the directory (same visibility as the list and the profile) can be edited.
        var profileResult = await directoryAppService.GetProfileAsync(callerEmployeeId, callerRoles, identity.Key, cancellationToken);
        if (profileResult.Outcome != CustomerDirectoryProfileOutcome.Success || profileResult.Response is null)
        {
            return CustomerProfileEditResult.Failure(CustomerProfileEditOutcome.NotFound);
        }

        var state = profileResult.Response.Edit ?? new CustomerProfileEditStateDto(null, null, null, null, null, null, null);

        var errors = new Dictionary<string, string[]>();
        var phone = ValidatePhone(request.PhoneNumber, errors);
        var (project, unit) = await ValidateProjectAndUnitAsync(request.ProjectName, request.UnitNumber, errors, cancellationToken);
        if (errors.Count > 0)
        {
            return CustomerProfileEditResult.Invalid(errors);
        }

        var before = new CustomerProfileEditStateDto(
            state.DerivedPhoneNumber, state.DerivedProjectName, state.DerivedUnitNumber,
            state.OverridePhoneNumber, state.OverrideProjectName, state.OverrideUnitNumber, state.EditedAtUtc);
        var after = before with { OverridePhoneNumber = phone, OverrideProjectName = project, OverrideUnitNumber = unit };

        var changed = before.PhoneNumber != after.PhoneNumber || before.ProjectName != after.ProjectName || before.UnitNumber != after.UnitNumber
            || before.OverridePhoneNumber != after.OverridePhoneNumber || before.OverrideProjectName != after.OverrideProjectName
            || before.OverrideUnitNumber != after.OverrideUnitNumber;
        if (!changed)
        {
            return CustomerProfileEditResult.Success(identity.Key, after, changed: false);
        }

        var now = timeProvider.GetUtcNow().UtcDateTime;
        await using var transaction = await unitOfWork.BeginTransactionAsync(cancellationToken);

        var existing = await repository.GetByKeyAsync(identity.Key, cancellationToken);
        if (existing is null && (phone is not null || project is not null || unit is not null))
        {
            existing = new CustomerProfileOverride(
                identity.Key, (int)identity.Kind, identity.CrmBuyerCustomerId, identity.ExternalSource, identity.ExternalCustomerId,
                identity.Kind == CustomerIdentityKind.Phone ? identity.PhoneNumber : null, callerEmployeeId, now);
            existing.Set(phone, project, unit, callerEmployeeId, now);
            await repository.AddAsync(existing, cancellationToken);
        }
        else if (existing is not null)
        {
            existing.Set(phone, project, unit, callerEmployeeId, now);
            if (existing.IsEmpty)
            {
                repository.Remove(existing);
            }
        }

        // Personal data, so the entry says what changed from what to what, and who did it; the audit trail stamps the time.
        await auditWriter.WriteAsync(
            callerEmployeeId, AuditAction, AuditEntityType, identity.Key,
            Describe(before), Describe(after), Guid.NewGuid(), cancellationToken);

        await unitOfWork.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return CustomerProfileEditResult.Success(identity.Key, after with { EditedAtUtc = now }, changed: true);
    }

    private static string Describe(CustomerProfileEditStateDto state) =>
        JsonSerializer.Serialize(new { state.PhoneNumber, state.ProjectName, state.UnitNumber });

    private static string? ValidatePhone(string? input, Dictionary<string, string[]> errors)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            return null;   // blank = no edit: the number the tickets captured is shown again
        }

        if (!CustomerProfileEntry.TryNormalizePhone(input, out var normalized, out var error))
        {
            errors[nameof(UpdateCustomerProfileRequestDto.PhoneNumber)] = [error!];
            return null;
        }

        return normalized;
    }

    private async Task<(string? Project, string? Unit)> ValidateProjectAndUnitAsync(
        string? projectInput, string? unitInput, Dictionary<string, string[]> errors, CancellationToken cancellationToken)
    {
        var project = string.IsNullOrWhiteSpace(projectInput) ? null : projectInput.Trim();
        var unit = string.IsNullOrWhiteSpace(unitInput) ? null : unitInput.Trim();
        if (project is null && unit is null)
        {
            return (null, null);   // both blank = no edit
        }

        if (project is null || unit is null)
        {
            errors[project is null ? nameof(UpdateCustomerProfileRequestDto.ProjectName) : nameof(UpdateCustomerProfileRequestDto.UnitNumber)] =
                ["Project and unit are entered together: add the missing one, or clear both."];
            return (null, null);
        }

        var valid = true;
        if (project.Length > CustomerProfileOverride.ProjectNameMaxLength || CustomerFieldText.Clean(project) is null)
        {
            errors[nameof(UpdateCustomerProfileRequestDto.ProjectName)] =
                [project.Length > CustomerProfileOverride.ProjectNameMaxLength
                    ? $"A project name is at most {CustomerProfileOverride.ProjectNameMaxLength} characters."
                    : "Enter the project's real name."];
            valid = false;
        }

        if (unit.Length > CustomerProfileOverride.UnitNumberMaxLength || CustomerFieldText.Clean(unit) is null)
        {
            errors[nameof(UpdateCustomerProfileRequestDto.UnitNumber)] =
                [unit.Length > CustomerProfileOverride.UnitNumberMaxLength
                    ? $"A unit number is at most {CustomerProfileOverride.UnitNumberMaxLength} characters."
                    : "Enter the unit's real number."];
            valid = false;
        }

        if (!valid)
        {
            return (null, null);
        }

        // Match the project to the catalogue (keeping its spelling), then check the unit against it where the catalogue can judge.
        var projects = await projectCatalog.ListProjectsAsync(cancellationToken);
        var known = projects.FirstOrDefault(p => string.Equals(p.Name, project, StringComparison.OrdinalIgnoreCase));
        if (known is null)
        {
            return (project, unit);   // a project the catalogue has not seen: nothing to judge the unit against
        }

        project = known.Name;
        var unitsOfProject = await projectCatalog.ListUnitNumbersAsync(project, cancellationToken);
        var match = unitsOfProject.FirstOrDefault(u => string.Equals(u, unit, StringComparison.OrdinalIgnoreCase));
        if (match is not null)
        {
            return (project, match);
        }

        foreach (var other in projects.Where(p => !string.Equals(p.Name, project, StringComparison.OrdinalIgnoreCase)))
        {
            var otherUnits = await projectCatalog.ListUnitNumbersAsync(other.Name, cancellationToken);
            if (otherUnits.Any(u => string.Equals(u, unit, StringComparison.OrdinalIgnoreCase)))
            {
                errors[nameof(UpdateCustomerProfileRequestDto.UnitNumber)] =
                    [$"Unit {unit} belongs to project {other.Name}, not {project}. Choose the right project or unit."];
                return (null, null);
            }
        }

        return (project, unit);   // not recorded under any project: it cannot be judged, so it is accepted as entered
    }
}
