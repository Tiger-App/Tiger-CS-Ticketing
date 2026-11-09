using TigerCS.Application.Modules.Notifications.Abstractions;
using TigerCS.Application.Modules.Ticketing.Abstractions;
using TigerCS.Domain.Modules.Ticketing;

namespace TigerCS.Application.Modules.Notifications.Services;

/// <summary>
/// Resolves the customer a ticket's notifications go to — name and email —
/// from the ticket's own persisted data and nothing else.
///
/// <para>
/// <b>Sources, in order.</b> No column on <c>Tickets</c> is a general
/// "customer email", so the address is looked up across the places each kind
/// of ticket keeps one. The first <i>valid</i> address wins; an invalid value
/// is skipped, never used:
/// </para>
/// <list type="number">
///   <item>
///   <see cref="TicketRequesterSnapshot.SnapshotContactChannel"/> — the CRM
///   contact verified at ticket creation (ADR-0007's immutable snapshot).
///   It is an untyped "phone or email" value, so it counts only when it is
///   unambiguously an email address.
///   </item>
///   <item>
///   <see cref="TicketInteraction.CustomerEmail"/> — the email Genesys
///   supplied with the inquiry, captured on the originating interaction
///   (falling back to the most recent interaction that carries one).
///   </item>
///   <item>
///   <see cref="Ticket.ExternalCustomerEmail"/> — the verified external
///   customer's email (PACT) snapshotted when the ticket was created.
///   </item>
///   <item>
///   <see cref="CustomerContactDetail.Email"/> — the email an agent typed in
///   for a manually entered customer (New Ticket / Customer Details), matched
///   by the canonical form of the originating interaction's phone number.
///   </item>
/// </list>
/// <para>
/// <b>Never guesses.</b> A ticket with none yields
/// <see cref="CustomerContact.SkipReason"/> = <see cref="CustomerEmailSkipReasons.NoCustomerEmail"/>;
/// a value that exists but is not a usable address yields
/// <see cref="CustomerEmailSkipReasons.InvalidCustomerEmail"/>. There is no
/// fallback mailbox, and the reason never echoes the value itself. Every
/// notification handler — customer lifecycle emails and the Ticket Details
/// email display alike — goes through this one resolver, so they cannot
/// disagree about who the customer is.
/// </para>
/// </summary>
public sealed class CustomerContactResolver(
    ITicketRequesterSnapshotRepository snapshotRepository,
    ITicketInteractionRepository interactionRepository,
    ICustomerContactDetailRepository? contactDetailRepository = null)
{
    public async Task<CustomerContact> ResolveAsync(Ticket ticket, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(ticket);

        var snapshot = await snapshotRepository.GetByTicketIdAsync(ticket.TicketId, cancellationToken);
        var interactions = await interactionRepository.ListByTicketIdAsync(ticket.TicketId, cancellationToken);
        var originating = interactions.FirstOrDefault(i => i.IsOriginatingInteraction);

        var manual = await ManualContactAsync(originating, interactions, cancellationToken);

        // The name sources keep their order; the external snapshot's and the agent-entered name only fill a gap (the same facts the
        // Ticket Details header shows), so a ticket that used to greet "Dear Customer" now greets by name when any source has one.
        var displayName = FirstNonBlank(
            snapshot?.SnapshotContactDisplayName,
            ticket.CrmBuyerCustomerName,
            originating?.CustomerName,
            interactions.Select(i => i.CustomerName).LastOrDefault(CustomerNameValidator.IsValid),
            ticket.ExternalCustomerName,
            manual?.Name);

        var candidates = new List<string?>
        {
            snapshot?.SnapshotContactChannel,
            originating?.CustomerEmail
        };
        candidates.AddRange(interactions
            .OrderByDescending(i => i.CreatedAtUtc)
            .Where(i => !i.IsOriginatingInteraction)
            .Select(i => i.CustomerEmail));

        // Added after the existing sources, so their priority is unchanged.
        candidates.Add(ticket.ExternalCustomerEmail);
        candidates.Add(manual?.Email);

        var sawCandidate = false;
        foreach (var candidate in candidates)
        {
            var normalized = CustomerEmailAddress.Normalize(candidate);
            if (normalized is null)
            {
                continue;
            }

            sawCandidate = true;
            if (CustomerEmailAddress.IsValid(normalized))
            {
                return CustomerContact.Deliverable(displayName, normalized) with { Language = LanguageFor(displayName) };
            }
        }

        return CustomerContact.NotDeliverable(
            displayName,
            sawCandidate ? CustomerEmailSkipReasons.InvalidCustomerEmail : CustomerEmailSkipReasons.NoCustomerEmail)
            with { Language = LanguageFor(displayName) };
    }

    /// <summary>
    /// No ticket stores a preferred language, so the email language follows the customer's name: a name written mostly in
    /// Arabic script gets the Arabic (right-to-left) email, anything else English.
    /// </summary>
    /// <summary>The contact details an agent saved for the originating interaction's phone number (a manually entered customer), or null.</summary>
    private async Task<CustomerContactDetail?> ManualContactAsync(
        TicketInteraction? originating, IReadOnlyList<TicketInteraction> interactions, CancellationToken cancellationToken)
    {
        if (contactDetailRepository is null)
        {
            return null;
        }

        var phoneKey = CustomerPhoneNumber.Normalize(
            originating?.CustomerPhone ?? interactions.Select(i => i.CustomerPhone).FirstOrDefault(p => !string.IsNullOrWhiteSpace(p)));
        if (phoneKey.Length == 0 || phoneKey.Length > CustomerContactDetail.PhoneKeyMaxLength)
        {
            return null;
        }

        return await contactDetailRepository.GetByPhoneKeyAsync(phoneKey, cancellationToken);
    }

    public static string LanguageFor(string? displayName)
    {
        if (string.IsNullOrWhiteSpace(displayName))
        {
            return "en";
        }

        var letters = displayName.Count(char.IsLetter);
        var arabic = displayName.Count(c => c is >= '\u0600' and <= '\u06FF' or >= '\u0750' and <= '\u077F');
        return letters > 0 && arabic * 2 >= letters ? "ar" : "en";
    }

    private static string? FirstNonBlank(params string?[] values) =>
        values.Select(CustomerNameValidator.Sanitize).FirstOrDefault(v => v is not null);
}

/// <param name="DisplayName">Customer name for the greeting, when any source recorded one.</param>
/// <param name="EmailAddress">A validated, trimmed address — or <c>null</c> when none is usable.</param>
/// <param name="SkipReason">A <see cref="CustomerEmailSkipReasons"/> code when <see cref="EmailAddress"/> is <c>null</c>.</param>
/// <param name="Language">Email language, <c>en</c> or <c>ar</c>.</param>
public sealed record CustomerContact(string? DisplayName, string? EmailAddress, string? SkipReason, string Language = "en")
{
    public bool IsDeliverable => EmailAddress is not null;

    public static CustomerContact Deliverable(string? displayName, string emailAddress) => new(displayName, emailAddress, null);

    public static CustomerContact NotDeliverable(string? displayName, string skipReason) => new(displayName, null, skipReason);
}
