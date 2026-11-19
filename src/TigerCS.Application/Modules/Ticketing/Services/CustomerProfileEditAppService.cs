using System.Net.Mail;
using TigerCS.Application.Modules.CustomerVerification.Abstractions;
using TigerCS.Application.Modules.Ticketing.Abstractions;
using TigerCS.Application.Modules.Ticketing.CustomerNames;
using TigerCS.Application.Modules.Ticketing.Dto;
using TigerCS.Domain.Modules.IdentityAndAccess;
using TigerCS.Domain.Modules.Ticketing;

namespace TigerCS.Application.Modules.Ticketing.Services;

/// <summary>
/// Edits what a Customers-directory customer is shown with: name, mobile and landline numbers, additional numbers, e-mail, nationality,
/// country, emirate, location, preferred language, and the primary project and unit.
///
/// <para>
/// <b>Local only.</b> The edit is stored as a <see cref="CustomerProfileOverride"/> layered over what the customer's tickets say: no
/// ticket is touched, nothing is sent to Tiger CRM, PACT or Tasleeh, and the customer's identity (so their tickets, history and
/// verification) never changes. Editing a customer therefore can never reopen a closed ticket or change a ticket's owner, SLA or closure
/// metadata - this service has no ticket repository to do it with. The historical snapshot on every ticket stays as it was captured.
/// Who may call it is the endpoint's <c>Customers.Edit</c> permission; this service additionally refuses read-only roles, only edits a
/// customer the caller can see in the directory, and validates every value.
/// </para>
///
/// <para>
/// <b>Audit.</b> Every field that actually changed is recorded as its own row (see <see cref="CustomerProfileAuditService"/>) in the same
/// database transaction as the change, all sharing one correlation id. Unchanged fields write nothing. The correlation id the caller sends
/// is also the idempotency key: the same id again is a retry and is recorded once.
/// </para>
///
/// <para>
/// <b>Semantics.</b> Phone, project and unit keep the original rule (blank clears the edit). Every other field is "null = leave it as it
/// is, empty = clear". A name or e-mail that is the same as the one the tickets already give is not stored as an edit.
/// </para>
/// </summary>
public sealed class CustomerProfileEditAppService(
    CustomerDirectoryAppService directoryAppService,
    ICustomerProfileOverrideRepository repository,
    ProjectUnitValidator projectUnitValidator,
    CustomerProfileAuditService auditService,
    ITicketingUnitOfWork unitOfWork,
    TimeProvider timeProvider)
{
    public const string AuditAction = CustomerProfileAuditService.AuditAction;
    public const string AuditEntityType = CustomerProfileAuditService.AuditEntityType;

    /// <summary>The seven emirates. An emirate is only ever one the customer or an agent states; it is never derived from a phone number.</summary>
    public static readonly IReadOnlyList<string> Emirates =
        ["Abu Dhabi", "Dubai", "Sharjah", "Ajman", "Umm Al Quwain", "Ras Al Khaimah", "Fujairah"];

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

        var profile = profileResult.Response;
        var state = profile.Edit ?? new CustomerProfileEditStateDto(null, null, null, null, null, null, null);
        var details = profile.Details;

        // ---- validate every submitted value -------------------------------------------------------------------------------------------
        var errors = new Dictionary<string, string[]>();
        var phone = ValidatePhone(request.PhoneNumber, nameof(request.PhoneNumber), errors);
        var (project, unit) = await projectUnitValidator.ValidateAsync(
            request.ProjectName, request.UnitNumber, nameof(request.ProjectName), nameof(request.UnitNumber), errors, cancellationToken);
        var fullName = ValidateName(request.FullName, errors);
        var email = ValidateEmail(request.Email, errors);
        var landline = ValidateLandline(request.LandlineNumber, errors);
        var additional = ValidateAdditionalPhones(request.AdditionalPhones, errors);
        var nationality = ValidateText(request.Nationality, nameof(request.Nationality), CustomerProfileOverride.ProfileTextMaxLength, errors);
        var country = ValidateText(request.Country, nameof(request.Country), CustomerProfileOverride.ProfileTextMaxLength, errors);
        var emirate = ValidateEmirate(request.Emirate, errors);
        var location = ValidateText(request.Location, nameof(request.Location), CustomerProfileOverride.LocationMaxLength, errors);
        var language = ValidateText(request.PreferredLanguage, nameof(request.PreferredLanguage), CustomerProfileOverride.ProfileTextMaxLength, errors);
        if (request.Reason is { Length: > CustomerProfileChange.ReasonMaxLength })
        {
            errors[nameof(request.Reason)] = [$"A reason is at most {CustomerProfileChange.ReasonMaxLength} characters."];
        }

        if (errors.Count > 0)
        {
            return CustomerProfileEditResult.Invalid(errors);
        }

        var correlationId = request.CorrelationId is { } given && given != Guid.Empty ? given : Guid.NewGuid();
        var context = new CustomerChangeContext(callerEmployeeId, request.Source ?? CustomerChangeSources.Api, request.Reason, request.RelatedTicketId, correlationId);

        var existing = await repository.GetByKeyAsync(identity.Key, cancellationToken);

        // A retry of an operation that was already recorded: answered as done, recorded once.
        if (existing is not null && request.CorrelationId is not null && await auditService.AlreadyRecordedAsync(existing.CustomerProfileId, correlationId, cancellationToken))
        {
            return CustomerProfileEditResult.Success(identity.Key, Effective(state, existing), changed: false);
        }

        // ---- what each override should be after this request -------------------------------------------------------------------------
        // Phone: blank clears; a number that is the one the tickets already show (written however) is not stored as an edit.
        var desiredPhone = phone is not null && state.DerivedPhoneNumber is { } derivedPhone && CustomerPhoneNumber.AreSameNumber(phone, derivedPhone) ? null : phone;
        var desiredProject = project;
        var desiredUnit = unit;
        var desiredName = Choose(fullName, existing?.FullName, details?.DerivedFullName);
        var desiredEmail = Choose(email, existing?.Email, details?.DerivedEmail);
        var desiredLandline = Choose(landline, existing?.LandlineNumber, null);
        var desiredAdditional = additional ?? existing?.AdditionalPhoneList ?? [];
        var desiredNationality = Choose(nationality, existing?.Nationality, null);
        var desiredCountry = Choose(country, existing?.Country, null);
        var desiredEmirate = Choose(emirate, existing?.Emirate, null);
        var desiredLocation = Choose(location, existing?.Location, null);
        var desiredLanguage = Choose(language, existing?.PreferredLanguage, null);

        var beforeName = details?.FullName;
        var beforeEmail = details?.Email;
        var changes = new List<CustomerFieldChange>
        {
            new(CustomerProfileFields.FullName, beforeName, desiredName ?? details?.DerivedFullName),
            new(CustomerProfileFields.Email, beforeEmail, desiredEmail ?? details?.DerivedEmail),
            PhoneChange(CustomerProfileFields.MobileNumber, state.PhoneNumber, desiredPhone ?? state.DerivedPhoneNumber),
            PhoneChange(CustomerProfileFields.LandlineNumber, existing?.LandlineNumber, desiredLandline),
            new(CustomerProfileFields.Nationality, existing?.Nationality, desiredNationality),
            new(CustomerProfileFields.Country, existing?.Country, desiredCountry),
            new(CustomerProfileFields.Emirate, existing?.Emirate, desiredEmirate),
            new(CustomerProfileFields.Location, existing?.Location, desiredLocation),
            new(CustomerProfileFields.PreferredLanguage, existing?.PreferredLanguage, desiredLanguage),
            new(CustomerProfileFields.ProjectName, state.ProjectName, desiredProject ?? state.DerivedProjectName),
            new(CustomerProfileFields.UnitNumber, state.UnitNumber, desiredUnit ?? state.DerivedUnitNumber),
        };

        // Additional numbers are a list: each one added or removed is its own change, so the history reads "added +971… / removed +971…".
        var beforeAdditional = existing?.AdditionalPhoneList ?? [];
        changes.AddRange(desiredAdditional
            .Where(n => !beforeAdditional.Any(b => CustomerPhoneNumber.AreSameNumber(b, n)))
            .Select(n => new CustomerFieldChange(CustomerProfileFields.AdditionalPhone, null, n)));
        changes.AddRange(beforeAdditional
            .Where(b => !desiredAdditional.Any(n => CustomerPhoneNumber.AreSameNumber(b, n)))
            .Select(b => new CustomerFieldChange(CustomerProfileFields.AdditionalPhone, b, null)));

        var rawChanged = existing is null
            ? desiredPhone is not null || desiredProject is not null || desiredUnit is not null || desiredName is not null || desiredEmail is not null
              || desiredLandline is not null || desiredAdditional.Count > 0 || desiredNationality is not null || desiredCountry is not null
              || desiredEmirate is not null || desiredLocation is not null || desiredLanguage is not null
            : existing.PhoneNumber != desiredPhone || existing.ProjectName != desiredProject || existing.UnitNumber != desiredUnit
              || existing.FullName != desiredName || existing.Email != desiredEmail || existing.LandlineNumber != desiredLandline
              || !existing.AdditionalPhoneList.SequenceEqual(desiredAdditional) || existing.Nationality != desiredNationality
              || existing.Country != desiredCountry || existing.Emirate != desiredEmirate || existing.Location != desiredLocation
              || existing.PreferredLanguage != desiredLanguage;
        if (!rawChanged)
        {
            return CustomerProfileEditResult.Success(identity.Key, Effective(state, existing), changed: false);
        }

        var now = timeProvider.GetUtcNow().UtcDateTime;
        try
        {
            await using var transaction = await unitOfWork.BeginTransactionAsync(cancellationToken);

            if (existing is null)
            {
                existing = new CustomerProfileOverride(
                    identity.Key, (int)identity.Kind, identity.CrmBuyerCustomerId, identity.ExternalSource, identity.ExternalCustomerId,
                    identity.Kind == CustomerIdentityKind.Phone ? identity.PhoneNumber : null, callerEmployeeId, now);
                await repository.AddAsync(existing, cancellationToken);
            }

            existing.Set(desiredPhone, desiredProject, desiredUnit, callerEmployeeId, now);
            existing.SetDetails(
                desiredName, desiredEmail, desiredLandline, desiredAdditional, desiredNationality, desiredCountry, desiredEmirate,
                desiredLocation, desiredLanguage, callerEmployeeId, now);

            // The row is kept even when everything is cleared: it anchors the customer's stable identifier and with it their history.
            await auditService.RecordAsync(existing.CustomerProfileId, identity.Key, context, changes, cancellationToken);

            await unitOfWork.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is TicketConcurrentlyModifiedException or DuplicateWriteException)
        {
            // Someone saved this customer first (or the same operation arrived twice at once). Nothing of this request was written.
            return CustomerProfileEditResult.Failure(CustomerProfileEditOutcome.Conflict);
        }

        return CustomerProfileEditResult.Success(identity.Key, Effective(state, existing) with { EditedAtUtc = now }, changed: true);
    }

    /// <summary>The phone / project / unit state after the override <paramref name="edit"/> is applied (what the directory now shows).</summary>
    private static CustomerProfileEditStateDto Effective(CustomerProfileEditStateDto state, CustomerProfileOverride? edit) =>
        edit is null
            ? state
            : state with { OverridePhoneNumber = edit.PhoneNumber, OverrideProjectName = edit.ProjectName, OverrideUnitNumber = edit.UnitNumber, EditedAtUtc = edit.UpdatedAtUtc };

    /// <summary>Null = leave as is; blank = clear; a value equal to what the tickets already give is not stored as an edit.</summary>
    private static string? Choose(string? requested, string? current, string? derived)
    {
        if (requested is null)
        {
            return current;
        }

        return requested.Length == 0 || (derived is not null && string.Equals(requested, derived, StringComparison.OrdinalIgnoreCase)) ? null : requested;
    }

    /// <summary>A phone change counts only when it is a different number: "+971501234567" over "971501234567" is the same number written differently.</summary>
    private static CustomerFieldChange PhoneChange(string field, string? before, string? after) =>
        !string.IsNullOrWhiteSpace(before) && !string.IsNullOrWhiteSpace(after) && CustomerPhoneNumber.AreSameNumber(before, after)
            ? new(field, before, before)
            : new(field, before, after);

    private static string? ValidatePhone(string? input, string field, Dictionary<string, string[]> errors)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            return null;   // blank = no edit: the number the tickets captured is shown again
        }

        if (!CustomerProfileEntry.TryNormalizePhone(input, out var normalized, out var error))
        {
            errors[field] = [error!];
            return null;
        }

        return normalized;
    }

    /// <summary>A landline field takes a landline: a number that is plainly a UAE mobile belongs in the mobile field.</summary>
    private static string? ValidateLandline(string? input, Dictionary<string, string[]> errors)
    {
        if (input is null)
        {
            return null;   // leave as is (resolved by Choose)
        }

        if (string.IsNullOrWhiteSpace(input))
        {
            return string.Empty;
        }

        if (!CustomerProfileEntry.TryNormalizePhone(input, out var normalized, out var error))
        {
            errors[nameof(UpdateCustomerProfileRequestDto.LandlineNumber)] = [error!];
            return null;
        }

        if (UaePhoneNumbers.ToInternationalDigits(CustomerPhoneNumber.Normalize(normalized)) is { } uae && UaePhoneNumbers.IsMobileNational(uae[UaePhoneNumbers.CountryCode.Length..]))
        {
            errors[nameof(UpdateCustomerProfileRequestDto.LandlineNumber)] = ["That is a UAE mobile number. Enter it as the mobile number, or give the landline."];
            return null;
        }

        return normalized;
    }

    private static IReadOnlyList<string>? ValidateAdditionalPhones(IReadOnlyList<string>? input, Dictionary<string, string[]> errors)
    {
        if (input is null)
        {
            return null;   // leave as is
        }

        var result = new List<string>();
        foreach (var raw in input.Where(i => !string.IsNullOrWhiteSpace(i)))
        {
            if (!CustomerProfileEntry.TryNormalizePhone(raw, out var normalized, out var error))
            {
                errors[nameof(UpdateCustomerProfileRequestDto.AdditionalPhones)] = [$"\"{raw.Trim()}\": {error}"];
                return null;
            }

            if (!result.Any(r => CustomerPhoneNumber.AreSameNumber(r, normalized)))
            {
                result.Add(normalized);
            }
        }

        if (result.Count > CustomerProfileOverride.MaxAdditionalPhones)
        {
            errors[nameof(UpdateCustomerProfileRequestDto.AdditionalPhones)] = [$"At most {CustomerProfileOverride.MaxAdditionalPhones} additional phone numbers."];
            return null;
        }

        return result;
    }

    private static string? ValidateName(string? input, Dictionary<string, string[]> errors)
    {
        if (input is null)
        {
            return null;
        }

        var name = input.Trim();
        if (name.Length == 0)
        {
            return string.Empty;
        }

        if (name.Length > CustomerProfileOverride.FullNameMaxLength)
        {
            errors[nameof(UpdateCustomerProfileRequestDto.FullName)] = [$"A name is at most {CustomerProfileOverride.FullNameMaxLength} characters."];
            return null;
        }

        if (CustomerNameResolver.FirstValid(name) is null)
        {
            errors[nameof(UpdateCustomerProfileRequestDto.FullName)] = ["Enter the customer's real name."];
            return null;
        }

        return name;
    }

    private static string? ValidateEmail(string? input, Dictionary<string, string[]> errors)
    {
        if (input is null)
        {
            return null;
        }

        var email = input.Trim();
        if (email.Length == 0)
        {
            return string.Empty;
        }

        if (email.Length > CustomerProfileOverride.EmailMaxLength
            || !MailAddress.TryCreate(email, out var parsed) || !string.Equals(parsed.Address, email, StringComparison.OrdinalIgnoreCase) || !email.Contains('.', StringComparison.Ordinal))
        {
            errors[nameof(UpdateCustomerProfileRequestDto.Email)] = ["Enter a valid e-mail address, for example name@example.com."];
            return null;
        }

        return email;
    }

    private static string? ValidateText(string? input, string field, int max, Dictionary<string, string[]> errors)
    {
        if (input is null)
        {
            return null;
        }

        var text = input.Trim();
        if (text.Length > max)
        {
            errors[field] = [$"At most {max} characters."];
            return null;
        }

        return text;
    }

    private static string? ValidateEmirate(string? input, Dictionary<string, string[]> errors)
    {
        if (input is null)
        {
            return null;
        }

        var text = input.Trim();
        if (text.Length == 0)
        {
            return string.Empty;
        }

        var match = Emirates.FirstOrDefault(e => string.Equals(e, text, StringComparison.OrdinalIgnoreCase));
        if (match is null)
        {
            errors[nameof(UpdateCustomerProfileRequestDto.Emirate)] = [$"Choose one of: {string.Join(", ", Emirates)}."];
        }

        return match;
    }
}
