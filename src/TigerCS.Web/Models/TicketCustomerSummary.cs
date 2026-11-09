using TigerCS.Application.Modules.Ticketing.CustomerNames;
using TigerCS.Application.Modules.Ticketing.Dto;

namespace TigerCS.Web.Models;

/// <summary>How well the ticket knows who the customer is, in the order the page trusts the evidence.</summary>
public enum CustomerLookupState
{
    /// <summary>A Tiger CRM buyer snapshot is on the ticket: name, project and unit are confirmed.</summary>
    VerifiedCrm,

    /// <summary>An external source (PACT / Tasleeh) confirmed the customer and unit.</summary>
    VerifiedExternal,

    /// <summary>The ticket was opened while the CRM check could not complete; it is provisional until reconciled.</summary>
    PendingCrm,

    /// <summary>A project or unit was typed in by a person; nothing confirmed it.</summary>
    ManualEntry,

    /// <summary>Only a phone number (and maybe a name from the call) is known; no registered customer matched.</summary>
    PhoneOnly,

    /// <summary>Nothing on the ticket identifies the customer.</summary>
    NotIdentified
}

/// <summary>
/// The Customer Summary band of Ticket Details as plain facts: who the customer is, how sure the system is, and what to do when it is
/// not sure. Pure presentation of data the page already loaded — it performs no lookup and decides no business rule, so a failed or
/// empty lookup is stated as exactly that and never as a name.
/// </summary>
/// <param name="Name">The customer's name, or a plain statement that there is none ("Unnamed caller", "Customer not identified").</param>
/// <param name="HasName">True when <paramref name="Name"/> is a real name (so initials and a profile link make sense).</param>
/// <param name="Phone">The phone number on record, or null.</param>
/// <param name="ProjectUnit">"T-1204 · Tiger Tower", or null when neither is on the ticket.</param>
/// <param name="UnitIsConfirmed">True when the project/unit came from CRM or an external source; false when a person typed it.</param>
/// <param name="State">Which evidence the identity rests on.</param>
/// <param name="StateLabel">The badge text — the existing verification wording, unchanged.</param>
/// <param name="StateCss">The badge colour key: verified, external or unverified.</param>
/// <param name="Hint">One short sentence for the agent when the identity is not confirmed; null when it is.</param>
/// <param name="Email">The customer's valid email address, or null when the ticket has none (the page then says "Not available").</param>
/// <param name="ProfileHref">The Customer Profile for this customer, or null when there is nothing to open.</param>
/// <param name="NameSource">Where the name came from. A <see cref="CustomerNameSource.Submitted"/> name is known but says nothing about a verified identity; the verification badge alone speaks to that.</param>
/// <param name="NameNote">"Name as provided · not verified" beside a name nobody verified; null for a verified name or no name.</param>
public sealed record TicketCustomerSummary(
    string Name,
    bool HasName,
    string? Phone,
    string? ProjectUnit,
    bool UnitIsConfirmed,
    CustomerLookupState State,
    string StateLabel,
    string StateCss,
    string? Hint,
    string? ProfileHref,
    CustomerNameSource NameSource = CustomerNameSource.None,
    string? NameNote = null,
    string? Email = null)
{
    public static TicketCustomerSummary Build(
        TicketDetailDto ticket, string name, bool hasName, string verificationLabel, string? phone, string? profileHref,
        CustomerNameSource? nameSource = null, string? email = null)
    {
        var hasCrm = ticket.CrmBuyerUnitId is not null;
        var hasExternal = !hasCrm && !string.IsNullOrWhiteSpace(ticket.CustomerVerificationSource);
        var hasManualUnit = !hasCrm && !hasExternal
            && (!string.IsNullOrWhiteSpace(ticket.ManualProjectName) || !string.IsNullOrWhiteSpace(ticket.ManualUnitNumber));
        var pending = ticket.VerificationStatus == "PendingCrmVerification";

        var state = hasCrm ? CustomerLookupState.VerifiedCrm
            : hasExternal ? CustomerLookupState.VerifiedExternal
            : pending ? CustomerLookupState.PendingCrm
            : hasManualUnit ? CustomerLookupState.ManualEntry
            : hasName || !string.IsNullOrWhiteSpace(phone) ? CustomerLookupState.PhoneOnly
            : CustomerLookupState.NotIdentified;

        var unit = ticket.CrmBuyerUnitNumber ?? ticket.ManualUnitNumber;
        var project = ticket.CrmBuyerProjectName ?? ticket.ManualProjectName;
        var projectUnit = string.IsNullOrWhiteSpace(unit) && string.IsNullOrWhiteSpace(project)
            ? null
            : string.IsNullOrWhiteSpace(project) ? unit : string.IsNullOrWhiteSpace(unit) ? project : $"{unit} · {project}";

        var hint = state switch
        {
            CustomerLookupState.PendingCrm =>
                "The customer check could not be completed when this ticket was opened, so these details are not confirmed yet.",
            CustomerLookupState.ManualEntry =>
                "The project and unit were typed in; they have not been checked against the customer records.",
            CustomerLookupState.PhoneOnly =>
                "This phone number is not matched to a registered customer.",
            CustomerLookupState.NotIdentified =>
                "Nothing on this ticket identifies the customer yet — no customer match and no phone number.",
            _ => null
        };

        var css = state switch
        {
            CustomerLookupState.VerifiedCrm => "verified",
            CustomerLookupState.VerifiedExternal => "external",
            _ => "unverified"
        };

        // A caller that does not say where the name came from gets the cautious reading: a name is "provided", never "verified".
        var source = !hasName ? CustomerNameSource.None : nameSource ?? CustomerNameSource.Submitted;
        var note = source == CustomerNameSource.Submitted ? "Name as provided · not verified" : null;

        return new TicketCustomerSummary(
            name, hasName, string.IsNullOrWhiteSpace(phone) ? null : phone.Trim(), projectUnit,
            state is CustomerLookupState.VerifiedCrm or CustomerLookupState.VerifiedExternal,
            state, verificationLabel, css, hint, profileHref, source, note,
            string.IsNullOrWhiteSpace(email) ? null : email.Trim());
    }
}
