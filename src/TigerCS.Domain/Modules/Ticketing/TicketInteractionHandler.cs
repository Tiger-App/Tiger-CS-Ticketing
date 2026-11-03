namespace TigerCS.Domain.Modules.Ticketing;

/// <summary>
/// Which Genesys event named the agent that a <see cref="TicketInteractionHandler"/> row records. Stored for audit and so that evidence
/// of different strength is never confused: every value here is a statement by Genesys that the agent <b>is connected to / handling</b>
/// the interaction. A ringing/alerting event, a queue membership, or a handoff <i>assignment</i> is never one of these (see
/// <see cref="TicketInteractionHandler"/>).
/// </summary>
public enum InteractionHandlerSource : byte
{
    /// <summary>The ticket-creating event named the handling agent (a call answered / chat accepted before the ticket was made).</summary>
    Inquiry = 1,

    /// <summary>A routing update named the agent now <b>connected</b> to the interaction — the event that records a transfer.</summary>
    Routing = 2,

    /// <summary>A ticket update named the handling agent.</summary>
    Update = 3,

    /// <summary>The conversation-end event named the handling agent.</summary>
    ConversationEnd = 4,

    /// <summary>A <c>HumanAgent</c> transcript line whose <c>senderId</c> resolved to an eligible CS Agent (Genesys User ID, or the unique-e-mail fallback) - that person wrote to the customer.</summary>
    Transcript = 5,

    /// <summary>Copied from <c>TicketInteractions.HandledByUserId</c> when this table was introduced — the single handler the integration recorded before per-agent history existed.</summary>
    Legacy = 9
}

/// <summary>
/// One Ticketing user who <b>actually handled</b> a Genesys interaction. An interaction can have several (a call transferred from one
/// agent to another is handled by both), which <c>TicketInteractions.HandledByUserId</c> — first handler only, apply-if-absent — cannot
/// express. This table is the source of truth for "did this user handle a Genesys interaction on this ticket", and therefore for the
/// CS Agent's right to see the ticket.
///
/// <para>
/// <b>What is, and is not, proof of handling.</b> A row is written only when Genesys reports that an agent is <i>connected</i> to or
/// handling the interaction (<see cref="InteractionHandlerSource"/>) and that agent's Genesys User ID resolves, server-side, through
/// <c>AspNetUsers.GenesysUserId</c> to an active Ticketing user. It is never written for: a ringing/alerting event (those never reach
/// TigerCS), a queue or department membership, a handoff work-item assignment, the Genesys agent-screen-pop launch (which may fire while
/// the interaction is still alerting), or an agent id that does not map to a user (a bot or an unmapped agent). Rows are append-only
/// and unique per (interaction, user), so a redelivered event writes nothing.
/// </para>
///
/// <para>
/// <see cref="TicketId"/> is denormalized from the interaction on purpose: the visibility predicate runs on every ticket query and
/// should be a single index seek on (<see cref="HandledByUserId"/>, <see cref="TicketId"/>), not a join through the interaction.
/// </para>
/// </summary>
public class TicketInteractionHandler
{
    public const int GenesysAgentUserIdMaxLength = TicketInteraction.GenesysAgentUserIdMaxLength;

    public long TicketInteractionHandlerId { get; private set; }
    public long TicketInteractionId { get; private set; }
    public long TicketId { get; private set; }

    /// <summary>The Ticketing user (<c>AspNetUsers.Id</c>) that handled the interaction — resolved on the server, never client-supplied.</summary>
    public Guid HandledByUserId { get; private set; }

    /// <summary>The Genesys User ID exactly as Genesys sent it, kept next to the user it resolved to so the row stays auditable if the mapping later changes.</summary>
    public string GenesysAgentUserId { get; private set; } = string.Empty;

    public InteractionHandlerSource Source { get; private set; }

    /// <summary>When Genesys says the agent connected, where it supplied a time. Null when it did not — <see cref="RecordedAtUtc"/> is then the only clock.</summary>
    public DateTime? ConnectedAtUtc { get; private set; }

    /// <summary>When Ticketing recorded the row (its own clock).</summary>
    public DateTime RecordedAtUtc { get; private set; }

    private TicketInteractionHandler() { }

    public static TicketInteractionHandler Create(
        long ticketInteractionId,
        long ticketId,
        Guid handledByUserId,
        string genesysAgentUserId,
        InteractionHandlerSource source,
        DateTime? connectedAtUtc,
        DateTime recordedAtUtc)
    {
        if (handledByUserId == Guid.Empty)
        {
            throw new ArgumentException("HandledByUserId must be a resolved Ticketing user id.", nameof(handledByUserId));
        }

        if (string.IsNullOrWhiteSpace(genesysAgentUserId))
        {
            throw new ArgumentException("GenesysAgentUserId is required to record a handler.", nameof(genesysAgentUserId));
        }

        var trimmed = genesysAgentUserId.Trim();

        return new TicketInteractionHandler
        {
            TicketInteractionId = ticketInteractionId,
            TicketId = ticketId,
            HandledByUserId = handledByUserId,
            GenesysAgentUserId = trimmed.Length <= GenesysAgentUserIdMaxLength ? trimmed : trimmed[..GenesysAgentUserIdMaxLength],
            Source = source,
            ConnectedAtUtc = connectedAtUtc,
            RecordedAtUtc = recordedAtUtc
        };
    }
}
