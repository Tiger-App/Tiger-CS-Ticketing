using System.Security.Cryptography;
using System.Text;

namespace TigerCS.Domain.Modules.Ticketing;

/// <summary>Where a customer-profile change came from. Stored as text so a new source never needs a schema change.</summary>
public static class CustomerChangeSources
{
    public const string Ui = "UI";
    public const string Api = "API";
    public const string AiAgent = "AIAgent";
    public const string CrmSync = "CrmSync";
    public const string Background = "Background";
    public const string Integration = "Integration";

    public static readonly IReadOnlyList<string> All = [Ui, Api, AiAgent, CrmSync, Background, Integration];

    /// <summary>The canonical spelling of a requested source, or <see cref="Api"/> when it is blank or unknown (a caller cannot invent a source).</summary>
    public static string Normalize(string? source) =>
        All.FirstOrDefault(s => string.Equals(s, source?.Trim(), StringComparison.OrdinalIgnoreCase)) ?? Api;
}

/// <summary>The audited fields. One row per field that actually changed.</summary>
public static class CustomerProfileFields
{
    public const string FullName = "FullName";
    public const string MobileNumber = "MobileNumber";
    public const string LandlineNumber = "LandlineNumber";
    public const string AdditionalPhone = "AdditionalPhone";
    public const string Email = "Email";
    public const string Nationality = "Nationality";
    public const string Country = "Country";
    public const string Emirate = "Emirate";
    public const string Location = "Location";
    public const string PreferredLanguage = "PreferredLanguage";
    public const string ProjectName = "ProjectName";
    public const string UnitNumber = "UnitNumber";
    public const string Property = "Property";

    /// <summary>The customer-facing label of a field, for the history timeline.</summary>
    public static string Label(string field) => field switch
    {
        FullName => "Full name",
        MobileNumber => "Mobile number",
        LandlineNumber => "Landline number",
        AdditionalPhone => "Additional phone",
        Email => "Email address",
        Nationality => "Nationality",
        Country => "Country",
        Emirate => "Emirate",
        Location => "Location",
        PreferredLanguage => "Preferred language",
        ProjectName => "Project",
        UnitNumber => "Unit number",
        Property => "Property / unit",
        _ => field,
    };
}

/// <summary>
/// One audited change to one field of a customer's profile: who changed what, from what to what, when, from where and why.
///
/// <para>
/// <b>Append-only.</b> Nothing in the application updates or deletes these rows, there is no endpoint that could, and the database
/// itself rejects an UPDATE or DELETE (a trigger created by the migration). History is therefore trustworthy evidence.
/// </para>
///
/// <para>
/// <b>Stable identity.</b> A row belongs to <see cref="CustomerProfileId"/> - minted once per customer and never reused or changed - and
/// also records the directory <see cref="CustomerKey"/> the customer had at the time. Changing a customer's phone number or name never
/// moves or orphans their history, because neither is part of the identifier.
/// </para>
///
/// <para>
/// <b>Idempotent.</b> <see cref="ChangeKey"/> is a hash of the operation's correlation id, the field and the values; it is unique, so a
/// retried request (same correlation id) can never record the same change twice.
/// </para>
///
/// <para>
/// <b>Personal data.</b> Old and new values are personal data. They live only in this table (protected by the history permission) and
/// are never written to application logs.
/// </para>
/// </summary>
public class CustomerProfileChange
{
    public const int CustomerKeyMaxLength = 300;
    public const int FieldNameMaxLength = 100;
    public const int ValueMaxLength = 1000;
    public const int ModifiedByNameMaxLength = 200;
    public const int SourceMaxLength = 50;
    public const int ReasonMaxLength = 500;
    public const int ChangeKeyLength = 64;

    public long CustomerProfileChangeId { get; private set; }
    public Guid CustomerProfileId { get; private set; }
    public string CustomerKey { get; private set; } = string.Empty;
    public string FieldName { get; private set; } = string.Empty;
    public string? OldValue { get; private set; }
    public string? NewValue { get; private set; }
    public Guid ModifiedByEmployeeId { get; private set; }
    public string ModifiedByName { get; private set; } = string.Empty;
    public DateTime ModifiedAtUtc { get; private set; }
    public string Source { get; private set; } = string.Empty;
    public string? ChangeReason { get; private set; }
    public long? RelatedTicketId { get; private set; }
    public Guid CorrelationId { get; private set; }
    public string ChangeKey { get; private set; } = string.Empty;

    private CustomerProfileChange() { }

    public CustomerProfileChange(
        Guid customerProfileId, string customerKey, string fieldName, string? oldValue, string? newValue,
        Guid modifiedByEmployeeId, string modifiedByName, DateTime modifiedAtUtc, string source, string? changeReason,
        long? relatedTicketId, Guid correlationId)
    {
        if (customerProfileId == Guid.Empty) throw new ArgumentException("CustomerProfileId is required.", nameof(customerProfileId));
        if (string.IsNullOrWhiteSpace(customerKey) || customerKey.Length > CustomerKeyMaxLength) throw new ArgumentException("CustomerKey is required.", nameof(customerKey));
        if (string.IsNullOrWhiteSpace(fieldName) || fieldName.Length > FieldNameMaxLength) throw new ArgumentException("FieldName is required.", nameof(fieldName));
        if (modifiedByEmployeeId == Guid.Empty) throw new ArgumentException("ModifiedByEmployeeId is required.", nameof(modifiedByEmployeeId));
        if (correlationId == Guid.Empty) throw new ArgumentException("CorrelationId is required.", nameof(correlationId));
        if (string.Equals(oldValue, newValue, StringComparison.Ordinal)) throw new ArgumentException("Only an actual change is recorded.");

        CustomerProfileId = customerProfileId;
        CustomerKey = customerKey;
        FieldName = fieldName;
        OldValue = Clip(oldValue, ValueMaxLength);
        NewValue = Clip(newValue, ValueMaxLength);
        ModifiedByEmployeeId = modifiedByEmployeeId;
        ModifiedByName = Clip(string.IsNullOrWhiteSpace(modifiedByName) ? "Unknown" : modifiedByName.Trim(), ModifiedByNameMaxLength)!;
        ModifiedAtUtc = modifiedAtUtc;
        Source = Clip(CustomerChangeSources.Normalize(source), SourceMaxLength)!;
        ChangeReason = Clip(string.IsNullOrWhiteSpace(changeReason) ? null : changeReason.Trim(), ReasonMaxLength);
        RelatedTicketId = relatedTicketId;
        CorrelationId = correlationId;
        ChangeKey = ComputeKey(correlationId, customerProfileId, fieldName, oldValue, newValue);
    }

    /// <summary>The idempotency key of one field change inside one operation: a SHA-256 of what identifies it, as lowercase hex.</summary>
    public static string ComputeKey(Guid correlationId, Guid customerProfileId, string fieldName, string? oldValue, string? newValue)
    {
        var text = string.Join('\u001f', correlationId.ToString("N"), customerProfileId.ToString("N"), fieldName, oldValue ?? "\u0000", newValue ?? "\u0000");
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
    }

    private static string? Clip(string? value, int max) => value is null ? null : value.Length <= max ? value : value[..max];
}
