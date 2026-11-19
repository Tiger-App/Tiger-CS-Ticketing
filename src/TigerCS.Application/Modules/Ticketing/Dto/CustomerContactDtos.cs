namespace TigerCS.Application.Modules.Ticketing.Dto;

/// <summary>
/// Save a customer's name and e-mail in Tiger Ticketing (<c>PUT api/customers/contact</c>). Creates the record for the phone
/// when there is none and updates it when there is — never a second record for the same number.
/// </summary>
/// <param name="PhoneNumber">The customer's phone in any written form; matched on its canonical digits.</param>
/// <param name="Name">Required, at most 200 characters, and a real name (not a placeholder such as "UNKNOWN").</param>
/// <param name="Email">Optional; when given it must be a valid address of at most 256 characters.</param>
/// <param name="CustomerKey">The Customers-directory key of the customer the name belongs to, when the caller knows it (the Customer Profile and Ticket Details do). It anchors the change in that customer's history; it is only used when the caller can open that customer, otherwise the change is recorded under the phone number's own identity.</param>
/// <param name="Reason">Why, for the change history.</param>
/// <param name="RelatedTicketId">The ticket the change was made from, for the change history.</param>
/// <param name="CorrelationId">The operation's id: sending the same one again is a retry and is recorded once.</param>
/// <param name="Source">Where the change came from (UI, API, AIAgent, CrmSync, Background, Integration); anything else is recorded as the API.</param>
public sealed record SaveCustomerContactRequestDto(
    string? PhoneNumber, string? Name, string? Email, string? CustomerKey = null, string? Reason = null, long? RelatedTicketId = null,
    Guid? CorrelationId = null, string? Source = null);

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
