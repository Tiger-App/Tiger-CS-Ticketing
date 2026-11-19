namespace TigerCS.Domain.Modules.Ticketing;

/// <summary>
/// The phone number, project and unit an authorised user entered by hand for a customer of the Customers directory, held by Tiger
/// Ticketing only.
///
/// <para>
/// TigerCS has no customer table: a customer is derived from the tickets that name them (see <c>CustomerIdentity</c>), and the phone,
/// project and unit the directory shows are read from the latest ticket. This row is the correction layered on top. It is keyed by the
/// customer's directory identity - the same <c>CrmBuyerCustomerId</c> / <c>ExternalSource + ExternalCustomerId</c> / canonical
/// intake phone the directory groups by - so editing a customer can never split or merge customers, and a customer keeps their tickets,
/// history and verification whatever is entered here. The identity columns are stored as components, not just as the key string, so a
/// query can join on them without building text.
/// </para>
///
/// <para>
/// Each of <see cref="PhoneNumber"/>, <see cref="ProjectName"/> and <see cref="UnitNumber"/> is independent: null means "no override,
/// show what the tickets say". It is local only - nothing is written to Tiger CRM, PACT or Tasleeh, and no ticket is touched.
/// <see cref="PhoneLocation"/> is the phone's place (<see cref="CustomerLocation"/>) worked out once, at save time, by the same rule the
/// directory applies to ticket phones, so the dashboard's Customers by Location can follow an edit without a database translation of
/// that rule.
/// </para>
/// </summary>
public class CustomerProfileOverride
{
    public const int CustomerKeyMaxLength = 300;
    public const int IdentityPartMaxLength = 200;
    public const int PhoneNumberMaxLength = 32;
    public const int ProjectNameMaxLength = 200;
    public const int UnitNumberMaxLength = 100;
    public const int FullNameMaxLength = 200;
    public const int EmailMaxLength = 256;
    public const int ProfileTextMaxLength = 100;
    public const int LocationMaxLength = 200;
    public const int AdditionalPhonesMaxLength = 400;
    public const int MaxAdditionalPhones = 10;

    public long CustomerProfileOverrideId { get; private set; }

    /// <summary>
    /// The customer's stable identifier, minted once with this row and never changed. History and property associations hang off it, so
    /// neither the customer's name, nor their phone number, nor even a later change of directory key can detach them.
    /// </summary>
    public Guid CustomerProfileId { get; private set; } = Guid.NewGuid();

    /// <summary>The directory key (<c>crm:{id}</c>, <c>ext:{source}:{id}</c>, <c>phone:{digits}</c>) - unique, one row per customer.</summary>
    public string CustomerKey { get; private set; } = string.Empty;

    /// <summary>The identity kind (1 Tiger CRM, 2 PACT / Tasleeh, 3 phone only) and its components, exactly as the directory groups tickets.</summary>
    public int IdentityKind { get; private set; }

    public int? CrmBuyerCustomerId { get; private set; }
    public string? ExternalSource { get; private set; }
    public string? ExternalCustomerId { get; private set; }

    /// <summary>The canonical phone of a phone-only customer's identity (not the edited number below).</summary>
    public string? IdentityPhone { get; private set; }

    /// <summary>The edited number, normalized (<c>+971501234567</c>), or null when the phone is not overridden.</summary>
    public string? PhoneNumber { get; private set; }

    /// <summary>The place of <see cref="PhoneNumber"/>; null when the phone is not overridden.</summary>
    public CustomerLocation? PhoneLocation { get; private set; }

    public string? ProjectName { get; private set; }
    public string? UnitNumber { get; private set; }

    // Profile details entered by hand. Null = not edited (the tickets / CRM value, if any, is shown). Local to Tiger Ticketing.
    public string? FullName { get; private set; }
    public string? Email { get; private set; }
    public string? LandlineNumber { get; private set; }

    /// <summary>Further numbers, normalized (<c>+971…</c>) and comma-separated - a normalized number never contains a comma.</summary>
    public string? AdditionalPhones { get; private set; }
    public string? Nationality { get; private set; }
    public string? Country { get; private set; }

    /// <summary>The emirate the customer says they live in. Entered, never inferred from a phone number's area code.</summary>
    public string? Emirate { get; private set; }
    public string? Location { get; private set; }
    public string? PreferredLanguage { get; private set; }

    public DateTime CreatedAtUtc { get; private set; }
    public DateTime UpdatedAtUtc { get; private set; }
    public Guid UpdatedByEmployeeId { get; private set; }

    public byte[] RowVersion { get; private set; } = [];

    private CustomerProfileOverride() { }

    public CustomerProfileOverride(
        string customerKey, int identityKind, int? crmBuyerCustomerId, string? externalSource, string? externalCustomerId,
        string? identityPhone, Guid updatedByEmployeeId, DateTime nowUtc)
    {
        if (string.IsNullOrWhiteSpace(customerKey) || customerKey.Length > CustomerKeyMaxLength)
        {
            throw new ArgumentException($"CustomerKey is required and at most {CustomerKeyMaxLength} characters.", nameof(customerKey));
        }

        if (identityKind is < 1 or > 3)
        {
            throw new ArgumentOutOfRangeException(nameof(identityKind), "IdentityKind must be 1 (CRM), 2 (external) or 3 (phone).");
        }

        if ((externalSource?.Length ?? 0) > IdentityPartMaxLength || (externalCustomerId?.Length ?? 0) > IdentityPartMaxLength
            || (identityPhone?.Length ?? 0) > PhoneNumberMaxLength)
        {
            throw new ArgumentException("An identity component is too long.");
        }

        CustomerKey = customerKey;
        IdentityKind = identityKind;
        CrmBuyerCustomerId = crmBuyerCustomerId;
        ExternalSource = externalSource;
        ExternalCustomerId = externalCustomerId;
        IdentityPhone = identityPhone;
        CreatedAtUtc = nowUtc;
        UpdatedAtUtc = nowUtc;
        UpdatedByEmployeeId = updatedByEmployeeId == Guid.Empty ? throw new ArgumentException("UpdatedByEmployeeId is required.", nameof(updatedByEmployeeId)) : updatedByEmployeeId;
    }

    /// <summary>True when nothing is overridden. The row is still kept: it anchors <see cref="CustomerProfileId"/> and with it the customer's history.</summary>
    public bool IsEmpty => PhoneNumber is null && ProjectName is null && UnitNumber is null && FullName is null && Email is null
        && LandlineNumber is null && AdditionalPhones is null && Nationality is null && Country is null && Emirate is null
        && Location is null && PreferredLanguage is null;

    /// <summary>The additional phone numbers, in the order they were entered.</summary>
    public IReadOnlyList<string> AdditionalPhoneList =>
        string.IsNullOrEmpty(AdditionalPhones) ? [] : AdditionalPhones.Split(',', StringSplitOptions.RemoveEmptyEntries);

    /// <summary>
    /// Replaces the profile details. A null / blank value clears that edit. Values are validated and normalized by the application service;
    /// only the column limits are guarded here.
    /// </summary>
    public void SetDetails(
        string? fullName, string? email, string? landlineNumber, IReadOnlyCollection<string>? additionalPhones, string? nationality,
        string? country, string? emirate, string? location, string? preferredLanguage, Guid updatedByEmployeeId, DateTime nowUtc)
    {
        if (updatedByEmployeeId == Guid.Empty)
        {
            throw new ArgumentException("UpdatedByEmployeeId is required.", nameof(updatedByEmployeeId));
        }

        var extra = (additionalPhones ?? []).Select(p => p.Trim()).Where(p => p.Length > 0).ToList();
        if (extra.Count > MaxAdditionalPhones || extra.Any(p => p.Contains(',')))
        {
            throw new ArgumentException($"At most {MaxAdditionalPhones} additional phone numbers, none containing a comma.", nameof(additionalPhones));
        }

        FullName = Limit(fullName, FullNameMaxLength, nameof(fullName));
        Email = Limit(email, EmailMaxLength, nameof(email));
        LandlineNumber = Limit(landlineNumber, PhoneNumberMaxLength, nameof(landlineNumber));
        AdditionalPhones = extra.Count == 0 ? null : Limit(string.Join(',', extra), AdditionalPhonesMaxLength, nameof(additionalPhones));
        Nationality = Limit(nationality, ProfileTextMaxLength, nameof(nationality));
        Country = Limit(country, ProfileTextMaxLength, nameof(country));
        Emirate = Limit(emirate, ProfileTextMaxLength, nameof(emirate));
        Location = Limit(location, LocationMaxLength, nameof(location));
        PreferredLanguage = Limit(preferredLanguage, ProfileTextMaxLength, nameof(preferredLanguage));
        UpdatedByEmployeeId = updatedByEmployeeId;
        UpdatedAtUtc = nowUtc;
    }

    private static string? Limit(string? value, int max, string name)
    {
        var trimmed = string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        return trimmed is not null && trimmed.Length > max ? throw new ArgumentException($"{name} is longer than {max} characters.", name) : trimmed;
    }

    /// <summary>
    /// Replaces the three overrides. A null value clears that override (the customer shows what their tickets say again). Values are
    /// expected to be already validated and normalized by the application service; only the column limits are guarded here.
    /// </summary>
    public void Set(string? phoneNumber, string? projectName, string? unitNumber, Guid updatedByEmployeeId, DateTime nowUtc)
    {
        if (updatedByEmployeeId == Guid.Empty)
        {
            throw new ArgumentException("UpdatedByEmployeeId is required.", nameof(updatedByEmployeeId));
        }

        var phone = string.IsNullOrWhiteSpace(phoneNumber) ? null : phoneNumber.Trim();
        var project = string.IsNullOrWhiteSpace(projectName) ? null : projectName.Trim();
        var unit = string.IsNullOrWhiteSpace(unitNumber) ? null : unitNumber.Trim();
        if ((phone?.Length ?? 0) > PhoneNumberMaxLength || (project?.Length ?? 0) > ProjectNameMaxLength || (unit?.Length ?? 0) > UnitNumberMaxLength)
        {
            throw new ArgumentException("A value is longer than its column allows.");
        }

        PhoneNumber = phone;
        PhoneLocation = phone is null ? null : CustomerPhoneLocation.Classify(phone);
        ProjectName = project;
        UnitNumber = unit;
        UpdatedByEmployeeId = updatedByEmployeeId;
        UpdatedAtUtc = nowUtc;
    }
}
