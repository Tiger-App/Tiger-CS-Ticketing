using TigerCS.Application.Modules.CustomerVerification.Abstractions;
using TigerCS.Application.Modules.Ticketing.Abstractions;
using TigerCS.Application.Modules.Ticketing.Dto;
using TigerCS.Domain.Modules.IdentityAndAccess;
using TigerCS.Domain.Modules.Ticketing;

namespace TigerCS.Application.Modules.Ticketing.Services;

/// <summary>
/// Adds, edits and removes the properties and units manually associated with a customer, from the Customer Profile.
///
/// <para>
/// <b>Manual means manual.</b> An association created here is stored in Tiger Ticketing only, carries the relationship the agent chose
/// (Owner, Tenant or Representative) and is always shown as "Manually added" - never as verified CRM or PACT ownership. This service has no
/// CRM, PACT or Tasleeh dependency, so it cannot modify an ownership record there; the verified units on a customer's CRM record and
/// tickets are a separate list it can neither edit nor remove.
/// </para>
///
/// <para>
/// <b>Duplicates</b> are refused: the same project and unit (case-insensitively) twice on one customer, or a unit the customer already has
/// as a verified unit. Each add, change and removal is audited as a <c>Property</c> change in the same transaction as the change itself.
/// </para>
/// </summary>
public sealed class CustomerPropertyAppService(
    CustomerDirectoryAppService directoryAppService,
    ICustomerProfileOverrideRepository overrides,
    ICustomerPropertyAssociationRepository associations,
    ProjectUnitValidator projectUnitValidator,
    CustomerProfileAuditService auditService,
    ITicketingUnitOfWork unitOfWork,
    TimeProvider timeProvider)
{
    public async Task<CustomerPropertyResult> AddAsync(
        Guid callerEmployeeId, IReadOnlyCollection<string> callerRoles, string customerKey, CustomerPropertyRequestDto request,
        CancellationToken cancellationToken = default) =>
        await ChangeAsync(callerEmployeeId, callerRoles, customerKey, associationId: null, remove: false, request, cancellationToken);

    public async Task<CustomerPropertyResult> UpdateAsync(
        Guid callerEmployeeId, IReadOnlyCollection<string> callerRoles, string customerKey, long associationId, CustomerPropertyRequestDto request,
        CancellationToken cancellationToken = default) =>
        await ChangeAsync(callerEmployeeId, callerRoles, customerKey, associationId, remove: false, request, cancellationToken);

    public async Task<CustomerPropertyResult> RemoveAsync(
        Guid callerEmployeeId, IReadOnlyCollection<string> callerRoles, string customerKey, long associationId, CustomerPropertyRequestDto? request,
        CancellationToken cancellationToken = default) =>
        await ChangeAsync(callerEmployeeId, callerRoles, customerKey, associationId, remove: true, request ?? new CustomerPropertyRequestDto(null, null, null), cancellationToken);

    private async Task<CustomerPropertyResult> ChangeAsync(
        Guid callerEmployeeId, IReadOnlyCollection<string> callerRoles, string customerKey, long? associationId, bool remove,
        CustomerPropertyRequestDto request, CancellationToken cancellationToken)
    {
        if (Roles.IsReadOnlyCaller(callerRoles))
        {
            return CustomerPropertyResult.Failure(CustomerPropertyOutcome.Forbidden);
        }

        if (!CustomerIdentity.TryParse(customerKey, out var identity))
        {
            return CustomerPropertyResult.Failure(CustomerPropertyOutcome.InvalidKey);
        }

        var profileResult = await directoryAppService.GetProfileAsync(callerEmployeeId, callerRoles, identity.Key, cancellationToken);
        if (profileResult.Outcome != CustomerDirectoryProfileOutcome.Success || profileResult.Response is null)
        {
            return CustomerPropertyResult.Failure(CustomerPropertyOutcome.NotFound);
        }

        var profile = profileResult.Response;
        var anchor = await overrides.GetByKeyAsync(identity.Key, cancellationToken);
        var existingRows = anchor is null ? [] : await associations.ListByProfileAsync(anchor.CustomerProfileId, cancellationToken);

        CustomerPropertyAssociation? target = null;
        if (associationId is { } id)
        {
            target = existingRows.FirstOrDefault(a => a.CustomerPropertyAssociationId == id);
            if (target is null)
            {
                // Not a manual association of THIS customer: a verified unit, another customer's row and a made-up id are all the same answer.
                return CustomerPropertyResult.Failure(CustomerPropertyOutcome.NotFound);
            }
        }

        var correlationId = request.CorrelationId is { } given && given != Guid.Empty ? given : Guid.NewGuid();
        if (anchor is not null && request.CorrelationId is not null && await auditService.AlreadyRecordedAsync(anchor.CustomerProfileId, correlationId, cancellationToken))
        {
            return CustomerPropertyResult.Success(ToDtos(existingRows), changed: false);   // a retry of an operation already recorded
        }

        string? project = null, unit = null;
        var relationship = target?.Relationship ?? PropertyRelationship.Owner;
        if (!remove)
        {
            var errors = new Dictionary<string, string[]>();
            (project, unit) = await projectUnitValidator.ValidateAsync(
                request.ProjectName ?? target?.ProjectName, request.UnitNumber ?? target?.UnitNumber,
                nameof(request.ProjectName), nameof(request.UnitNumber), errors, cancellationToken);
            if (errors.Count == 0 && (project is null || unit is null))
            {
                errors[project is null ? nameof(request.ProjectName) : nameof(request.UnitNumber)] = ["Choose the project and enter the unit."];
            }

            if (request.Relationship is not null || target is null)
            {
                if (!Enum.TryParse<PropertyRelationship>(request.Relationship, ignoreCase: true, out var parsed) || !Enum.IsDefined(parsed))
                {
                    errors[nameof(request.Relationship)] = ["Choose Owner, Tenant or Representative."];
                }
                else
                {
                    relationship = parsed;
                }
            }

            if (request.Reason is { Length: > CustomerProfileChange.ReasonMaxLength })
            {
                errors[nameof(request.Reason)] = [$"A reason is at most {CustomerProfileChange.ReasonMaxLength} characters."];
            }

            if (errors.Count > 0)
            {
                return CustomerPropertyResult.Invalid(errors);
            }

            var pair = CustomerPropertyAssociation.PairKey(project, unit);
            if (existingRows.Any(a => a.CustomerPropertyAssociationId != target?.CustomerPropertyAssociationId
                    && CustomerPropertyAssociation.PairKey(a.ProjectName, a.UnitNumber) == pair)
                || profile.Units.Any(u => IsVerified(u) && CustomerPropertyAssociation.PairKey(u.ProjectName, u.UnitNumber) == pair))
            {
                return CustomerPropertyResult.Failure(CustomerPropertyOutcome.Duplicate);
            }

            if (target is not null && target.ProjectName == project && target.UnitNumber == unit && target.Relationship == relationship)
            {
                return CustomerPropertyResult.Success(ToDtos(existingRows), changed: false);   // nothing actually changed: nothing is written
            }
        }

        var now = timeProvider.GetUtcNow().UtcDateTime;
        var context = new CustomerChangeContext(callerEmployeeId, request.Source ?? CustomerChangeSources.Api, request.Reason, request.RelatedTicketId, correlationId);
        try
        {
            await using var transaction = await unitOfWork.BeginTransactionAsync(cancellationToken);

            if (anchor is null)
            {
                anchor = new CustomerProfileOverride(
                    identity.Key, (int)identity.Kind, identity.CrmBuyerCustomerId, identity.ExternalSource, identity.ExternalCustomerId,
                    identity.Kind == CustomerIdentityKind.Phone ? identity.PhoneNumber : null, callerEmployeeId, now);
                await overrides.AddAsync(anchor, cancellationToken);
            }

            var before = target is null ? null : Describe(target.ProjectName, target.UnitNumber, target.Relationship);
            string? after;
            if (remove)
            {
                associations.Remove(target!);
                after = null;
            }
            else if (target is null)
            {
                await associations.AddAsync(new CustomerPropertyAssociation(identity.Key, anchor.CustomerProfileId, project!, unit!, relationship, callerEmployeeId, now), cancellationToken);
                after = Describe(project!, unit!, relationship);
            }
            else
            {
                target.Update(project!, unit!, relationship, callerEmployeeId, now);
                after = Describe(project!, unit!, relationship);
            }

            await auditService.RecordAsync(
                anchor.CustomerProfileId, identity.Key, context, [new CustomerFieldChange(CustomerProfileFields.Property, before, after)], cancellationToken);

            await unitOfWork.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        catch (DuplicateWriteException)
        {
            // The unique index caught what two simultaneous requests would have both allowed.
            return CustomerPropertyResult.Failure(CustomerPropertyOutcome.Duplicate);
        }
        catch (TicketConcurrentlyModifiedException)
        {
            return CustomerPropertyResult.Failure(CustomerPropertyOutcome.Conflict);
        }

        return CustomerPropertyResult.Success(ToDtos(await associations.ListByProfileAsync(anchor.CustomerProfileId, cancellationToken)), changed: true);
    }

    /// <summary>A unit from the customer's CRM record or verifying source - as opposed to one typed in on a ticket or added here.</summary>
    private static bool IsVerified(CustomerDirectoryUnitDto unit) => unit.Source is not ("Manual" or "Edited");

    private static string Describe(string project, string unit, PropertyRelationship relationship) => $"{project} / {unit} ({relationship})";

    private static IReadOnlyList<CustomerPropertyDto> ToDtos(IEnumerable<CustomerPropertyAssociation> rows) =>
        rows.Select(a => new CustomerPropertyDto(a.CustomerPropertyAssociationId, a.ProjectName, a.UnitNumber, a.Relationship.ToString(), false, a.UpdatedAtUtc)).ToList();
}
