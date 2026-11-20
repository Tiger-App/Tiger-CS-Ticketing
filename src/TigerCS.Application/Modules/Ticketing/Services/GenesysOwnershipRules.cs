using TigerCS.Domain.Modules.Ticketing;

namespace TigerCS.Application.Modules.Ticketing.Services;

/// <summary>
/// The ONE switch for "do the Genesys AI-ownership rules apply to this channel?". They do for EVERY channel (Phone, WhatsApp, web chat, e-mail ...):
/// the AI Agent owns what it handles first; ending an AI conversation never triggers Round Robin; an unresolved ticket waits in the Call Center queue with the AI
/// Agent still the owner; a person gets it only by actually accepting / answering (the real employee, never a fallback agent) or by an authorised manual assignment.
/// A channel is exempted only by adding its id to <see cref="ExemptChannels"/> together with an explicitly approved channel-specific requirement (none exists today).
/// </summary>
public static class GenesysOwnershipRules
{
    /// <summary>Channels with an explicitly approved, channel-specific ownership requirement. Empty: voice and WhatsApp (and every other channel) follow the same rules.</summary>
    private static readonly HashSet<byte> ExemptChannels = [];

    public static bool AppliesTo(byte? channelId) => channelId is not { } id || !ExemptChannels.Contains(id);

    /// <summary>Phone is the one channel where "the call ended" is not evidence on its own (the answering agent can still be reported after <c>ended</c>).</summary>
    public static bool IsVoice(byte? channelId) => channelId == WellKnownChannels.Phone;
}
