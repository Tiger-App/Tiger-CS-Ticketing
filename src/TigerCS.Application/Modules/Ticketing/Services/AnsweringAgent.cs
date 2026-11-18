using TigerCS.Domain.Modules.Ticketing;

namespace TigerCS.Application.Modules.Ticketing.Services;

/// <summary>
/// Why the agent Genesys reported as having ANSWERED an interaction cannot be given the ticket. <see cref="None"/> means they can. Anything
/// else leaves the ticket in the Call Center queue, flagged for manual assignment — never handed to somebody else (see
/// <see cref="TicketAutoAssignmentService.ApplyAnsweringAgentAsync"/>).
/// </summary>
public enum AnsweringAgentIssue
{
    None = 0,

    /// <summary>The Genesys User ID matches no <c>AspNetUsers.GenesysUserId</c>.</summary>
    NotMapped,

    /// <summary>The mapped Ticketing user is deactivated.</summary>
    Inactive,

    /// <summary>The mapped Ticketing account is locked out.</summary>
    LockedOut,

    /// <summary>The mapped user is a service / integration identity, not a person.</summary>
    ServiceAccount,

    /// <summary>The mapped user is not a member of the Call Center department, so they cannot own a Call Center ticket.</summary>
    NotCallCenterMember,

    /// <summary>The mapped user holds no operational role (read-only, supervisory or administrative only).</summary>
    NoOperationalRole,

    /// <summary>The mapped user is a Call Center member with an operational role but not the CS Agent role; the Call Center gives tickets to CS Agents only.</summary>
    NotCsAgent,

    /// <summary>More than one Ticketing user carries this Genesys User ID (the unique index is missing). Never resolved to either user; flagged for a data fix.</summary>
    AmbiguousMapping,

    /// <summary>The id Genesys sent is blank or malformed.</summary>
    InvalidId
}

/// <summary>
/// The agent Genesys reported as <b>connected</b> (answered / accepted — never one that is merely ringing, offered or queued), as far as
/// TigerCS could resolve it. <see cref="EmployeeId"/> is null whenever resolution already failed (<see cref="Issue"/> says why).
/// Built server-side from the Genesys User ID; never taken from a client-supplied employee id.
/// </summary>
public sealed record AnsweringAgent(string GenesysUserId, Guid? EmployeeId, AnsweringAgentIssue Issue = AnsweringAgentIssue.None);

public enum AnswerAssignmentOutcome
{
    /// <summary>The ticket was given directly to the answering agent (routed to the Call Center first when it was elsewhere).</summary>
    AssignedToAnsweringAgent,

    /// <summary>The answering agent already owns the ticket: nothing was written (a redelivered event).</summary>
    AlreadyOwnedByAnsweringAgent,

    /// <summary>A person already owns the ticket (not an automatic pre-assignment): human ownership is never overwritten.</summary>
    HumanOwnerKept,

    /// <summary>The answering agent cannot take the ticket: it is in the Call Center queue, unowned, flagged for manual assignment.</summary>
    QueuedForManualAssignment,

    /// <summary>The answering agent cannot take the ticket: it is flagged for staff AND given to the next eligible CS Agent of the shared rotation (final business rule - never left unassigned).</summary>
    AssignedByRoundRobinFallback,

    /// <summary>The same unusable answering agent was already flagged on this ticket: nothing new was written.</summary>
    AlreadyFlagged,

    /// <summary>The rule does not apply: the ticket is closed or resolved, a person transferred it, there is no Call Center, or the agent is unusable on a ticket that is not human-managed.</summary>
    NotApplicable,

    /// <summary>A concurrent writer changed the ticket or its assignment first; nothing was applied and a redelivery is safe.</summary>
    ConcurrencyConflict
}

/// <summary>The result of one answering-agent decision. <c>Wrote</c> is true when the call changed the ticket, its assignment or its history (the caller must save).</summary>
public sealed record AnswerAssignmentResult(
    AnswerAssignmentOutcome Outcome,
    Guid? AssignedEmployeeId = null,
    AnsweringAgentIssue Issue = AnsweringAgentIssue.None,
    string? Detail = null,
    bool Wrote = false);

/// <summary>
/// What the Genesys event that reported the agent as connected says about itself. <paramref name="EventAtUtc"/> is when the agent connected as
/// Genesys reports it (null: arrival time), used to ignore older, out-of-order events. <paramref name="AllowOwnerTransfer"/> lets an agent Genesys
/// connects LATER take over from an owner Genesys itself set (a call transfer); a person's ownership is never moved either way.
/// </summary>
public sealed record GenesysAnswerEvent(
    DateTime? EventAtUtc, string? ConversationId, bool AllowOwnerTransfer, byte? ChannelId = null, DateTime? EvidenceAtUtc = null);

/// <summary>
/// Channel-specific wording for an ownership change caused by a person taking a Genesys conversation. A WhatsApp, chat or social conversation is
/// <i>accepted</i>, never "answered" like a phone call — the same sentence for every channel told a supervisor that a WhatsApp conversation was
/// a voice call. Pure; the channel id is the interaction's own <see cref="WellKnownChannels"/> value, an unknown one reads as a neutral "Conversation".
/// </summary>
public static class AnswerWording
{
    /// <summary>"Call" for voice; "WhatsApp conversation", "Live chat conversation", … for everything else.</summary>
    public static string Noun(byte? channelId) => channelId switch
    {
        WellKnownChannels.Phone => "Call",
        WellKnownChannels.WhatsApp or WellKnownChannels.WhatsAppOrLiveChat => "WhatsApp conversation",
        WellKnownChannels.LiveChat => "Live chat conversation",
        WellKnownChannels.Website or WellKnownChannels.AppOrWebsite => "Website chat conversation",
        WellKnownChannels.MobileApp => "App conversation",
        WellKnownChannels.Instagram => "Instagram conversation",
        WellKnownChannels.Facebook => "Facebook conversation",
        WellKnownChannels.SocialMediaDirectMessage => "Social media conversation",
        _ => "Conversation"
    };

    private static string Verb(byte? channelId) => channelId == WellKnownChannels.Phone ? "answered" : "accepted";

    /// <summary>"Call answered by a Call Center agent — routed to Call Center" / "WhatsApp conversation accepted by a Call Center agent — routed to Call Center".</summary>
    public static string RoutedReason(byte? channelId) => $"{Noun(channelId)} {Verb(channelId)} by a Call Center agent — routed to Call Center";

    /// <summary>The typed transfer event's note.</summary>
    public static string TransferNote(byte? channelId) => $"{Noun(channelId)} {Verb(channelId)} by a Call Center agent — Call Center first";

    /// <summary>The ownership-history reason for the first human taking the conversation.</summary>
    public static string DirectHandlerReason(byte? channelId) => channelId == WellKnownChannels.Phone
        ? "Genesys direct call: answered by the human agent"
        : $"Genesys {Noun(channelId).ToLowerInvariant()}: accepted by the human agent";

    /// <summary>The ownership-history reason for the next human taking it after a transfer.</summary>
    public static string TransferHandlerReason(byte? channelId) => channelId == WellKnownChannels.Phone
        ? "Genesys call transfer: the next human agent answered"
        : $"Genesys {Noun(channelId).ToLowerInvariant()} transfer: the next human agent accepted";
}
