namespace TigerCS.Domain.Modules.Ticketing;

/// <summary>How a customer relates to a property.</summary>
public enum PropertyRelationship : byte
{
    Owner = 1,
    Tenant = 2,
    Representative = 3,
}

/// <summary>
/// A project / unit an authorised employee associated with a customer by hand.
///
/// <para>
/// <b>Never verified ownership.</b> A row here is a manual association, held by Tiger Ticketing only: it is shown with a "Manually
/// added" indicator and is never presented as CRM or PACT ownership. Nothing is written to Tiger CRM, PACT or Tasleeh; the verified
/// units a customer's tickets or CRM record carry are a separate list that this one can neither edit nor remove.
/// </para>
///
/// <para>One row per customer, project and unit (case-insensitively) - duplicates are refused by the service and by a unique index.</para>
/// </summary>
public class CustomerPropertyAssociation
{
    public const int CustomerKeyMaxLength = 300;
    public const int ProjectNameMaxLength = 200;
    public const int UnitNumberMaxLength = 100;

    public long CustomerPropertyAssociationId { get; private set; }
    public string CustomerKey { get; private set; } = string.Empty;
    public Guid CustomerProfileId { get; private set; }
    public string ProjectName { get; private set; } = string.Empty;
    public string UnitNumber { get; private set; } = string.Empty;
    public PropertyRelationship Relationship { get; private set; }
    public DateTime CreatedAtUtc { get; private set; }
    public Guid CreatedByEmployeeId { get; private set; }
    public DateTime UpdatedAtUtc { get; private set; }
    public Guid UpdatedByEmployeeId { get; private set; }
    public byte[] RowVersion { get; private set; } = [];

    private CustomerPropertyAssociation() { }

    public CustomerPropertyAssociation(
        string customerKey, Guid customerProfileId, string projectName, string unitNumber, PropertyRelationship relationship,
        Guid employeeId, DateTime nowUtc)
    {
        if (string.IsNullOrWhiteSpace(customerKey) || customerKey.Length > CustomerKeyMaxLength) throw new ArgumentException("CustomerKey is required.", nameof(customerKey));
        if (customerProfileId == Guid.Empty) throw new ArgumentException("CustomerProfileId is required.", nameof(customerProfileId));
        if (employeeId == Guid.Empty) throw new ArgumentException("An employee is required.", nameof(employeeId));

        CustomerKey = customerKey;
        CustomerProfileId = customerProfileId;
        CreatedAtUtc = nowUtc;
        CreatedByEmployeeId = employeeId;
        Update(projectName, unitNumber, relationship, employeeId, nowUtc);
    }

    public void Update(string projectName, string unitNumber, PropertyRelationship relationship, Guid employeeId, DateTime nowUtc)
    {
        var project = projectName?.Trim() ?? string.Empty;
        var unit = unitNumber?.Trim() ?? string.Empty;
        if (project.Length is 0 or > ProjectNameMaxLength) throw new ArgumentException($"A project is required, at most {ProjectNameMaxLength} characters.", nameof(projectName));
        if (unit.Length is 0 or > UnitNumberMaxLength) throw new ArgumentException($"A unit is required, at most {UnitNumberMaxLength} characters.", nameof(unitNumber));
        if (!Enum.IsDefined(relationship)) throw new ArgumentOutOfRangeException(nameof(relationship));
        if (employeeId == Guid.Empty) throw new ArgumentException("An employee is required.", nameof(employeeId));

        ProjectName = project;
        UnitNumber = unit;
        Relationship = relationship;
        UpdatedByEmployeeId = employeeId;
        UpdatedAtUtc = nowUtc;
    }

    /// <summary>The case-insensitive identity of a project / unit pair - what "the same property" means for duplicate prevention.</summary>
    public static string PairKey(string? projectName, string? unitNumber) =>
        (projectName?.Trim().ToUpperInvariant() ?? string.Empty) + "\u001f" + (unitNumber?.Trim().ToUpperInvariant() ?? string.Empty);
}
