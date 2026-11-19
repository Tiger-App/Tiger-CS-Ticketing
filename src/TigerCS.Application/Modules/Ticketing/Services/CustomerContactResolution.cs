using TigerCS.Application.Modules.Ticketing.CustomerNames;
using TigerCS.Domain.Modules.Ticketing;

namespace TigerCS.Application.Modules.Ticketing.Services;

/// <summary>Everything any surface knows about who a customer is; the surface fills what it has and the one rule below picks.</summary>
/// <param name="ProfileName">The name entered on the customer's profile (<see cref="CustomerProfileOverride.FullName"/>).</param>
/// <param name="ProfileEmail">The e-mail entered on the profile.</param>
/// <param name="ProfilePhone">The phone entered on the profile.</param>
/// <param name="ManualContactName">The name an agent saved with the older phone-keyed contact record (<see cref="CustomerContactDetail"/>).</param>
/// <param name="ManualContactEmail">The e-mail saved with that record.</param>
/// <param name="ManualContactUpdatedAtUtc">When that record was last saved.</param>
/// <param name="ProfileUpdatedAtUtc">When the profile was last edited.</param>
/// <param name="CrmName">The CRM Buyer snapshot name.</param>
/// <param name="ExternalName">The verified external (PACT / Tasleeh) snapshot name.</param>
/// <param name="SubmittedNames">Names a conversation, call or form supplied, best first.</param>
/// <param name="DerivedEmails">E-mails the tickets and conversations captured, best first.</param>
/// <param name="DerivedPhones">Phones the tickets and conversations captured, best first.</param>
public sealed record CustomerContactInputs(
    string? ProfileName = null,
    string? ProfileEmail = null,
    string? ProfilePhone = null,
    DateTime? ProfileUpdatedAtUtc = null,
    string? ManualContactName = null,
    string? ManualContactEmail = null,
    DateTime? ManualContactUpdatedAtUtc = null,
    string? CrmName = null,
    string? ExternalName = null,
    IReadOnlyList<string?>? SubmittedNames = null,
    IReadOnlyList<string?>? DerivedEmails = null,
    IReadOnlyList<string?>? DerivedPhones = null);

/// <summary>The one answer to "what is this customer called, and how do we reach them".</summary>
public sealed record ResolvedCustomerContact(string? Name, CustomerNameSource NameSource, bool NameIsEdited, string? Email, string? Phone);

/// <summary>
/// THE rule for the name, e-mail and phone every surface shows for a customer - Customer Directory, Customer Profile, Ticket Details,
/// Related Tickets - so they cannot disagree. Before this existed each surface collected its own candidates and Ticket Details could print
/// "Unnamed caller" for a customer whose profile showed a name.
///
/// <para>
/// <b>Name.</b> What a person deliberately entered wins: the profile edit and the older agent contact record, whichever was saved last.
/// After that the evidence ranking of <see cref="CustomerNameResolver"/> applies unchanged (CRM, then verified external, then a submitted
/// name). A placeholder ("UNKNOWN", "N/A") is never a name. When nothing qualifies the result has no name and the caller says so honestly.
/// </para>
///
/// <para>
/// <b>E-mail and phone.</b> The same precedence: an entered value, then what the tickets captured.
/// </para>
///
/// <para>
/// A pure function of its inputs: it reads nothing and writes nothing, so a ticket's historical snapshot is only ever <i>read</i> here -
/// a corrected profile is shown beside it, never written over it.
/// </para>
/// </summary>
public static class CustomerContactResolution
{
    public static ResolvedCustomerContact Resolve(CustomerContactInputs inputs)
    {
        var profileName = CustomerNameResolver.FirstValid(inputs.ProfileName);
        var contactName = CustomerNameResolver.FirstValid(inputs.ManualContactName);

        // The more recently saved deliberate entry wins; an entry with no timestamp loses to one that has it.
        var editedFirst = profileName is not null && contactName is not null
            ? (inputs.ManualContactUpdatedAtUtc ?? DateTime.MinValue) > (inputs.ProfileUpdatedAtUtc ?? DateTime.MinValue)
                ? new[] { contactName, profileName }
                : new[] { profileName, contactName }
            : new[] { profileName, contactName };
        var edited = editedFirst.FirstOrDefault(n => n is not null);

        ResolvedCustomerContact result;
        if (edited is not null)
        {
            result = new(edited, CustomerNameSource.Submitted, true, null, null);
        }
        else
        {
            var derived = CustomerNameResolver.Resolve(inputs.CrmName, inputs.ExternalName, (inputs.SubmittedNames ?? []).ToArray());
            result = new(derived.Name, derived.Source, false, null, null);
        }

        var email = FirstText(inputs.ProfileEmail, inputs.ManualContactEmail)
            ?? FirstText((inputs.DerivedEmails ?? []).ToArray());
        var phone = FirstText(inputs.ProfilePhone) ?? FirstText((inputs.DerivedPhones ?? []).ToArray());
        return result with { Email = email, Phone = phone };
    }

    private static string? FirstText(params string?[] candidates) =>
        candidates.FirstOrDefault(c => !string.IsNullOrWhiteSpace(c))?.Trim();

    /// <summary>
    /// Every contact-record key a customer may have been saved under, from every number they were seen on (including the edited one). A
    /// lookup tries all of them and keeps the freshest record, so a name saved under any of the customer's numbers is found by every surface.
    /// </summary>
    public static IReadOnlyList<string> ContactKeys(IEnumerable<string?> phones) =>
        phones.SelectMany(CustomerPhoneNumber.LookupKeys).Distinct(StringComparer.Ordinal).ToList();
}
