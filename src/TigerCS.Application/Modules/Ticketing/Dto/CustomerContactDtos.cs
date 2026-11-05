namespace TigerCS.Application.Modules.Ticketing.Dto;

/// <summary>
/// Save a customer's name and e-mail in Tiger Ticketing (<c>PUT api/customers/contact</c>). Creates the record for the phone
/// when there is none and updates it when there is — never a second record for the same number.
/// </summary>
/// <param name="PhoneNumber">The customer's phone in any written form; matched on its canonical digits.</param>
/// <param name="Name">Required, at most 200 characters, and a real name (not a placeholder such as "UNKNOWN").</param>
/// <param name="Email">Optional; when given it must be a valid address of at most 256 characters.</param>
public sealed record SaveCustomerContactRequestDto(string? PhoneNumber, string? Name, string? Email);

/// <summary>The saved record: what every customer display now shows for this phone.</summary>
public sealed record CustomerContactDto(string PhoneKey, string Name, string? Email, DateTime UpdatedAtUtc, bool Created);

public enum CustomerContactOutcome
{
    Success,
    ValidationFailed,
    Forbidden,
}

public sealed record CustomerContactResult(
    CustomerContactOutcome Outcome, CustomerContactDto? Response = null, IReadOnlyDictionary<string, string[]>? Errors = null)
{
    public static CustomerContactResult Success(CustomerContactDto response) => new(CustomerContactOutcome.Success, response);

    public static CustomerContactResult Invalid(IReadOnlyDictionary<string, string[]> errors) =>
        new(CustomerContactOutcome.ValidationFailed, null, errors);

    public static CustomerContactResult Forbidden() => new(CustomerContactOutcome.Forbidden);
}
