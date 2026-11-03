using TigerCS.Application.Modules.GenesysIntegration.Dto;

namespace TigerCS.Application.Modules.GenesysIntegration.Services;

/// <summary>
/// Maps a normalized <see cref="GenesysChannel"/> onto the stable
/// <c>Channel.Code</c> of the configured channel catalogue — the single
/// place the two vocabularies meet.
///
/// <para>
/// <b>Codes, not ids, and one table rather than scattered comparisons.</b>
/// The channel row itself is configuration (Admin → Configuration →
/// Channels): the resolver looks the code up through
/// <c>IChannelRepository</c>, so an administrator renaming or re-ordering a
/// channel changes nothing here, and a channel that has been deactivated
/// makes ingestion report a configuration problem instead of silently
/// recording an inquiry against a retired channel. TigerCS records the
/// originating channel for audit and reporting only — it never reproduces
/// Genesys' channel orchestration.
/// </para>
/// </summary>
public static class GenesysChannelResolver
{
    /// <summary>The <c>Channel.Code</c> each normalized channel is recorded under. An unknown enum value is a programming error, not a configuration one.</summary>
    public static string CodeFor(GenesysChannel channel) => channel switch
    {
        GenesysChannel.Phone => "PHONE",
        // The website's live chat widget — the "Live Chat" channel, not the
        // "Website" browsing channel (which is not a Genesys conversation).
        GenesysChannel.WebsiteChat => "LIVE_CHAT",
        GenesysChannel.WhatsApp => "WHATSAPP",
        GenesysChannel.SocialMedia => "SOCIAL_DM",
        GenesysChannel.Instagram => "INSTAGRAM",
        GenesysChannel.Facebook => "FACEBOOK",
        _ => throw new ArgumentOutOfRangeException(nameof(channel), channel, "Unknown normalized Genesys channel.")
    };
}
