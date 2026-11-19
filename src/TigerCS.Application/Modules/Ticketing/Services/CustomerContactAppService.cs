using System.Net.Mail;
using System.Text.Json;
using TigerCS.Application.Abstractions;
using TigerCS.Application.Modules.Notifications.Services;
using TigerCS.Application.Modules.Ticketing.Abstractions;
using TigerCS.Application.Modules.Ticketing.Dto;
using TigerCS.Domain.Modules.IdentityAndAccess;
using TigerCS.Domain.Modules.Ticketing;

namespace TigerCS.Application.Modules.Ticketing.Services;

/// <summary>
/// Saves a customer's name and e-mail as typed by an agent — the New Ticket "manual entry" form and the Customer Details
/// "Edit name" action. One rule for both: the record is looked up by the phone's canonical form, updated when it exists and
/// created when it does not, so a number can never end up with two records.
///
/// <para>
/// <b>Local only.</b> This touches <see cref="CustomerContactDetail"/> and the audit trail, nothing else: no CRM / PACT / Tasleeh
/// call, no ticket row, no verification status, unit association or phone number. Authorization is the caller's role (read-only
/// roles can never write) on top of the endpoint's own policy and permission.
/// </para>
/// </summary>
public sealed class CustomerContactAppService(
    ICustomerContactDetailRepository repository,
    ITicketingUnitOfWork unitOfWork,
    IAuditEntryWriter auditWriter,
    TimeProvider timeProvider,
    CustomerDirectoryAppService directoryAppService,
    ICustomerProfileOverrideRepository overrides,
    CustomerProfileAuditService profileAudit)
{
    public async Task<CustomerContactResult> SaveAsync(
        Guid callerEmployeeId,
        IReadOnlyCollection<string> callerRoles,
        SaveCustomerContactRequestDto request,
        CancellationToken cancellationToken = default)
    {
        if (Roles.IsReadOnlyCaller(callerRoles))
        {
            return CustomerContactResult.Forbidden();
        }

        var errors = Validate(request, out var phoneKey, out var name, out var email);
        if (errors.Count > 0)
        {
            return CustomerContactResult.Invalid(errors);
        }

        var now = timeProvider.GetUtcNow().UtcDateTime;
        var existing = await repository.GetByPhoneKeyAsync(phoneKey, cancellationToken);
        var created = existing is null;
        var before = existing is null ? null : JsonSerializer.Serialize(new { existing.Name, existing.Email });
        var oldName = existing?.Name;
        var oldEmail = existing?.Email;

        // The customer this belongs to, for the change history: the key the caller names when they can open that customer, else the
        // identity of the phone number itself.
        var identity = CustomerIdentity.Phone(phoneKey);
        if (CustomerIdentity.TryParse(request.CustomerKey, out var named)
            && (await directoryAppService.GetProfileAsync(callerEmployeeId, callerRoles, named.Key, cancellationToken)).Outcome == CustomerDirectoryProfileOutcome.Success)
        {
            identity = named;
        }

        var correlationId = request.CorrelationId is { } given && given != Guid.Empty ? given : Guid.NewGuid();
        var anchor = await overrides.GetByKeyAsync(identity.Key, cancellationToken);
        if (anchor is not null && request.CorrelationId is not null && await profileAudit.AlreadyRecordedAsync(anchor.CustomerProfileId, correlationId, cancellationToken))
        {
            return CustomerContactResult.Success(new CustomerContactDto(phoneKey, existing?.Name ?? name, existing?.Email ?? email, existing?.UpdatedAtUtc ?? now, false));   // a retry
        }

        await using var transaction = await unitOfWork.BeginTransactionAsync(cancellationToken);

        if (existing is null)
        {
            existing = new CustomerContactDetail(phoneKey, name, email, callerEmployeeId, now);
            await repository.AddAsync(existing, cancellationToken);
        }
        else
        {
            existing.Update(name, email, callerEmployeeId, now);
        }

        // Names and addresses are personal data: the audit row records that the change happened and who made it, plus the
        // before/after values the audit trail already keeps for other edits.
        await auditWriter.WriteAsync(
            callerEmployeeId, created ? "CreateCustomerContact" : "UpdateCustomerContact", "CustomerContactDetail", phoneKey,
            before, JsonSerializer.Serialize(new { name, email }), Guid.NewGuid(), cancellationToken);

        // The customer's change history: the name and e-mail as separate rows, only when they actually changed, in the same transaction.
        if (!string.Equals(oldName, name, StringComparison.Ordinal) || !string.Equals(oldEmail, email, StringComparison.Ordinal))
        {
            if (anchor is null)
            {
                anchor = new CustomerProfileOverride(
                    identity.Key, (int)identity.Kind, identity.CrmBuyerCustomerId, identity.ExternalSource, identity.ExternalCustomerId,
                    identity.Kind == CustomerIdentityKind.Phone ? identity.PhoneNumber : null, callerEmployeeId, now);
                await overrides.AddAsync(anchor, cancellationToken);
            }

            await profileAudit.RecordAsync(
                anchor.CustomerProfileId, identity.Key,
                new CustomerChangeContext(callerEmployeeId, request.Source ?? CustomerChangeSources.Api, request.Reason, request.RelatedTicketId, correlationId),
                [new CustomerFieldChange(CustomerProfileFields.FullName, oldName, name), new CustomerFieldChange(CustomerProfileFields.Email, oldEmail, email)],
                cancellationToken);
        }

        await unitOfWork.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return CustomerContactResult.Success(new CustomerContactDto(phoneKey, existing.Name, existing.Email, existing.UpdatedAtUtc, created));
    }

    /// <summary>The saved name/e-mail for each phone (any written form) that has a record, keyed by canonical phone.</summary>
    public async Task<IReadOnlyDictionary<string, CustomerContactDto>> GetByPhonesAsync(
        IEnumerable<string?> phoneNumbers, CancellationToken cancellationToken = default)
    {
        var keys = phoneNumbers
            .Select(CustomerPhoneNumber.Normalize)
            .Where(k => k.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .ToList();
        if (keys.Count == 0)
        {
            return new Dictionary<string, CustomerContactDto>();
        }

        var rows = await repository.ListByPhoneKeysAsync(keys, cancellationToken);
        return rows.ToDictionary(r => r.Key, r => new CustomerContactDto(r.Value.PhoneKey, r.Value.Name, r.Value.Email, r.Value.UpdatedAtUtc, false));
    }

    internal static Dictionary<string, string[]> Validate(
        SaveCustomerContactRequestDto request, out string phoneKey, out string name, out string? email)
    {
        var errors = new Dictionary<string, string[]>();

        phoneKey = CustomerPhoneNumber.Normalize(request.PhoneNumber);
        if (phoneKey.Length == 0)
        {
            errors["phoneNumber"] = ["A phone number is required."];
        }
        else if (phoneKey.Length > CustomerContactDetail.PhoneKeyMaxLength)
        {
            errors["phoneNumber"] = ["The phone number is too long."];
        }

        name = request.Name?.Trim() ?? string.Empty;
        if (name.Length == 0)
        {
            errors["name"] = ["Customer name is required."];
        }
        else if (name.Length > CustomerContactDetail.NameMaxLength)
        {
            errors["name"] = [$"Customer name must be at most {CustomerContactDetail.NameMaxLength} characters."];
        }
        else if (!CustomerNameValidator.IsValid(name))
        {
            errors["name"] = ["Enter the customer's real name."];
        }

        email = string.IsNullOrWhiteSpace(request.Email) ? null : request.Email.Trim();
        if (email is not null && !IsValidEmail(email))
        {
            errors["email"] = ["Enter a valid email address, or leave it blank."];
        }

        return errors;
    }

    private static bool IsValidEmail(string email) =>
        email.Length <= CustomerContactDetail.EmailMaxLength
        && !email.Any(char.IsWhiteSpace)
        && MailAddress.TryCreate(email, out var parsed)
        && string.Equals(parsed.Address, email, StringComparison.OrdinalIgnoreCase)
        && parsed.Host.Contains('.');
}
