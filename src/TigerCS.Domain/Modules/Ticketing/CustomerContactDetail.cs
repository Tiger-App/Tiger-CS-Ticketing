namespace TigerCS.Domain.Modules.Ticketing;

/// <summary>
/// The name and e-mail an agent entered or corrected by hand for a customer, held by Tiger Ticketing only.
///
/// <para>
/// TigerCS has no customer table: a customer is derived from the tickets that name them (see <c>CustomerIdentity</c>). Every
/// ticket does carry the intake phone, though, and the canonical phone (<see cref="CustomerPhoneNumber.Normalize"/>) is the one
/// fact all of a customer's identities share. This row is therefore keyed by that canonical phone — one row per real number, so
/// "+971501234567" and "971 50 123 4567" can never become two records — and every read that shows a customer name or e-mail
/// (Customers list, Customer Details, Ticket Details) looks it up by phone.
/// </para>
///
/// <para>
/// It is deliberately local. Nothing here is written to Tiger CRM, PACT or Tasleeh, and it never touches a ticket's verification
/// status, unit association or phone number: the name shown is "what an agent told us", not a verified identity.
/// </para>
/// </summary>
public class CustomerContactDetail
{
    public const int NameMaxLength = 200;
    public const int EmailMaxLength = 256;
    public const int PhoneKeyMaxLength = 32;

    public long CustomerContactDetailId { get; private set; }

    /// <summary>The canonical (digits-only) phone number — unique.</summary>
    public string PhoneKey { get; private set; } = string.Empty;

    public string Name { get; private set; } = string.Empty;
    public string? Email { get; private set; }

    public DateTime CreatedAtUtc { get; private set; }
    public DateTime UpdatedAtUtc { get; private set; }
    public Guid UpdatedByEmployeeId { get; private set; }

    public byte[] RowVersion { get; private set; } = [];

    private CustomerContactDetail() { }

    public CustomerContactDetail(string phoneKey, string name, string? email, Guid updatedByEmployeeId, DateTime nowUtc)
    {
        if (string.IsNullOrEmpty(phoneKey) || phoneKey.Length > PhoneKeyMaxLength || !phoneKey.All(char.IsAsciiDigit))
        {
            throw new ArgumentException("PhoneKey must be the canonical digits-only phone number.", nameof(phoneKey));
        }

        PhoneKey = phoneKey;
        CreatedAtUtc = nowUtc;
        Update(name, email, updatedByEmployeeId, nowUtc);
    }

    public void Update(string name, string? email, Guid updatedByEmployeeId, DateTime nowUtc)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Trim().Length > NameMaxLength)
        {
            throw new ArgumentException($"Name is required and at most {NameMaxLength} characters.", nameof(name));
        }

        var trimmedEmail = string.IsNullOrWhiteSpace(email) ? null : email.Trim();
        if (trimmedEmail is { Length: > EmailMaxLength })
        {
            throw new ArgumentException($"Email is at most {EmailMaxLength} characters.", nameof(email));
        }

        if (updatedByEmployeeId == Guid.Empty)
        {
            throw new ArgumentException("UpdatedByEmployeeId is required.", nameof(updatedByEmployeeId));
        }

        Name = name.Trim();
        Email = trimmedEmail;
        UpdatedByEmployeeId = updatedByEmployeeId;
        UpdatedAtUtc = nowUtc;
    }
}
