using TigerCS.Domain.Modules.Ticketing;

namespace TigerCS.Application.Modules.Ticketing.Services;

/// <summary>
/// The ONE definition of "Genesys is the source of truth for who handles this conversation" for the Genesys MESSAGING channels.
///
/// <para>
/// <b>Rule.</b> A ticket that originates from a Genesys messaging conversation (WhatsApp, web live chat, Instagram, Facebook, generic social DM, and the
/// legacy combined WhatsApp / Live Chat id) never receives a provisional human owner from TigerCS. It is created and queued exactly as before (department
/// routing, Call Center first, the AI Agent as recorded owner) and then WAITS: no Round Robin at creation, handoff, human follow-up, conversation end,
/// request-type classification, reconciliation or backfill. A person gets it only when Genesys confirms who actually handles the conversation
/// (<c>routing.agentId</c> connected, or a mapped person who actually wrote to the customer), or when an authorised person assigns / retries it.
/// An unknown, unmapped or ineligible employee is flagged for manual assignment — never replaced by the next name in a rotation.
/// </para>
///
/// <para>
/// <b>Scope.</b> Genesys-originated messaging tickets only. <see cref="WellKnownChannels.Phone"/> is NOT in the set (voice has its own rules and PR #307's
/// no-answer reconciler); the Queueing System, manually created tickets, e-mail/walk-in and every other integration never match, because the policy is always
/// evaluated against a Genesys conversation (a Genesys context at creation, a Genesys interaction afterwards), never against the channel id alone.
/// </para>
/// </summary>
public static class GenesysMessagingAssignmentPolicy
{
    // The Genesys-enabled channels of the catalogue (ChannelReferenceData) other than Phone. Website (8) and Mobile App (9) are not Genesys
    // conversations, and Walk-in / Kiosk (5) is never one, so none of them is listed.
    private static readonly HashSet<byte> MessagingChannels =
    [
        WellKnownChannels.WhatsAppOrLiveChat,       // 3 - legacy, inactive: historical Genesys interactions still resolve
        WellKnownChannels.SocialMediaDirectMessage, // 4 - generic social DM (X, Telegram, ...)
        WellKnownChannels.WhatsApp,                 // 6
        WellKnownChannels.LiveChat,                 // 7 - the website's live-chat widget (Genesys web messaging)
        WellKnownChannels.Instagram,                // 10
        WellKnownChannels.Facebook                  // 11
    ];

    /// <summary>True for a Genesys messaging channel (never Phone). Evaluate it only for a Genesys-sourced conversation.</summary>
    public static bool IsMessagingChannel(byte? channelId) => channelId is { } id && MessagingChannels.Contains(id);
}
