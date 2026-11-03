using TigerCS.Application.Modules.GenesysIntegration.Dto;

namespace TigerCS.Application.Modules.GenesysIntegration.Services;

/// <summary>
/// Which real Genesys event proves a person — not the AI — has taken a conversation. Recorded on the audit trail so "why did this ticket enter
/// the Call Center" is always answerable from an actual interaction fact.
/// </summary>
public enum GenesysHumanSignal
{
    /// <summary>No evidence of a person: the ticket is, or may still be, AI-handled and is left exactly where it is.</summary>
    None = 0,

    /// <summary>Genesys said so explicitly (<c>humanFollowUpRequired: true</c>).</summary>
    ExplicitFlag = 1,

    /// <summary>A voice call created at pickup naming an agent who maps to a Ticketing user: a mapped human accepted the call.</summary>
    PhonePickupByMappedAgent = 2,

    /// <summary>Genesys reported a mapped agent as <b>connected</b> (<c>routing.agentId</c>) — never merely ringing or offered.</summary>
    MappedAgentConnected = 3,

    /// <summary>Genesys reported that an agent took outstanding human work (<c>handoff.assignedAgentId</c>).</summary>
    HandoffAgentAssigned = 4,

    /// <summary>The transcript carries a line written by a <c>HumanAgent</c> (never <c>VirtualAgent</c>).</summary>
    HumanTranscriptLine = 5,

    /// <summary>The AI conversation ended and the AI neither resolved nor closed the ticket: nobody finished it, so a person must.</summary>
    AiEndedUnresolved = 6
}

/// <summary>
/// The one definition of "this Genesys conversation is being handled by a person", shared by ticket creation and by every later update, so a
/// phone call, a chat and a WhatsApp conversation are judged by the same facts.
///
/// <para>
/// What counts: an explicit <c>humanFollowUpRequired</c>, a mapped agent <b>connected</b> to the conversation, an agent taking pending human
/// work, a <c>HumanAgent</c> transcript line, and — only at creation of a <b>Phone</b> ticket, which by contract begins at pickup — a mapped
/// agent named on the inquiry. What does not: no agent, an unmapped id (it may be a virtual agent or bot — nothing is guessed from it), a
/// queue change, a ringing/offered agent, <c>VirtualAgent</c>/<c>Customer</c>/<c>System</c> lines, a handoff that was cancelled. Where there
/// is no evidence the answer is <see cref="GenesysHumanSignal.None"/> and an active AI conversation is never moved.
/// </para>
/// </summary>
public static class GenesysHumanHandlingEvidence
{
    /// <summary>Creation: the signal carried by the inquiry. <paramref name="agentIsMappedHuman"/> is the result of resolving <c>agentId</c> to a Ticketing user.</summary>
    public static GenesysHumanSignal ForInquiry(GenesysInquiryDto inquiry, bool agentIsMappedHuman)
    {
        ArgumentNullException.ThrowIfNull(inquiry);

        if (inquiry.HumanFollowUpRequired == true)
        {
            return GenesysHumanSignal.ExplicitFlag;
        }

        return inquiry.Channel == GenesysChannel.Phone && agentIsMappedHuman
            ? GenesysHumanSignal.PhonePickupByMappedAgent
            : GenesysHumanSignal.None;
    }

    /// <summary>Update: the signal carried by one update. <paramref name="routingAgentIsMappedHuman"/> is the result of resolving <c>routing.agentId</c>.</summary>
    public static GenesysHumanSignal ForUpdate(GenesysTicketUpdateDto request, bool routingAgentIsMappedHuman)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (request.HumanFollowUpRequired == true)
        {
            return GenesysHumanSignal.ExplicitFlag;
        }

        if (routingAgentIsMappedHuman)
        {
            return GenesysHumanSignal.MappedAgentConnected;
        }

        if (request.Handoff is { Required: not false } handoff && !string.IsNullOrWhiteSpace(handoff.AssignedAgentId))
        {
            return GenesysHumanSignal.HandoffAgentAssigned;
        }

        return HasHumanLine(request.Transcript) || HasHumanLine(request.Ended?.Transcript)
            ? GenesysHumanSignal.HumanTranscriptLine
            : GenesysHumanSignal.None;
    }

    /// <summary>
    /// The Genesys id of the person who actually wrote to the customer: the <c>senderId</c> of the FIRST <c>HumanAgent</c> line this update
    /// carries (they received the interaction first), or null when there is none. Evidence of WHO answered, so the ticket goes to that
    /// person - not to the next name in a rotation.
    /// </summary>
    public static string? FirstHumanSenderId(GenesysTicketUpdateDto request)
    {
        ArgumentNullException.ThrowIfNull(request);

        return (request.Transcript ?? [])
            .Concat(request.Ended?.Transcript ?? [])
            .Where(m => string.Equals(m.Sender?.Trim(), "HumanAgent", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(m.SenderId))
            .OrderBy(m => m.SentAtUtc)
            .Select(m => m.SenderId!.Trim())
            .FirstOrDefault();
    }

    /// <summary>
    /// Every distinct <c>senderId</c> of a <c>HumanAgent</c> line this update carries, earliest writer first. Each is evidence that a person
    /// wrote to the customer (resolved - GUID or the strict e-mail fallback - and recorded as a handler by the caller).
    /// </summary>
    public static IReadOnlyList<(string SenderId, DateTime FirstSentAtUtc)> HumanSenderIds(GenesysTicketUpdateDto request)
    {
        ArgumentNullException.ThrowIfNull(request);

        return (request.Transcript ?? [])
            .Concat(request.Ended?.Transcript ?? [])
            .Where(m => string.Equals(m.Sender?.Trim(), "HumanAgent", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(m.SenderId))
            .GroupBy(m => m.SenderId!.Trim(), StringComparer.OrdinalIgnoreCase)
            .Select(g => (SenderId: g.Key, FirstSentAtUtc: g.Min(m => m.SentAtUtc)))
            .OrderBy(x => x.FirstSentAtUtc)
            .ToList();
    }

    private static bool HasHumanLine(IReadOnlyList<GenesysTranscriptMessageDto>? messages) =>
        messages is not null
        && messages.Any(m => string.Equals(m.Sender?.Trim(), "HumanAgent", StringComparison.OrdinalIgnoreCase)
            || string.Equals(m.Sender?.Trim(), "Agent", StringComparison.OrdinalIgnoreCase));
}
