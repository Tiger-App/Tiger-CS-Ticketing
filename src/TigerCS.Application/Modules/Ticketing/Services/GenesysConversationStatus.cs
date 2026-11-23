namespace TigerCS.Application.Modules.Ticketing.Services;

/// <summary>
/// The processing state of a Genesys conversation, <b>separate from</b> <c>TicketStatus</c>, from who owns the ticket and from Assignment History.
/// It is DERIVED at read time from facts already stored (interaction end, confirmed answering agent / handler / accepted handoff, handling state),
/// so it needs no column, no migration and cannot drift from the evidence. It never assigns anyone and never changes ownership.
///
/// <list type="bullet">
/// <item><see cref="AwaitingGenesysUpdate"/> - a person is needed (or expected) but Genesys has not yet confirmed one. Nobody is guessed at: the ticket keeps
/// its existing owner (AI Agent / system) and is NOT presented as "Unassigned".</item>
/// <item><see cref="InProgress"/> - the conversation is open and the AI / existing owner is handling it; no person is expected yet.</item>
/// <item><see cref="Assigned"/> - Genesys confirmed a connected human (routing agent, accepted handoff or recorded handler).</item>
/// <item><see cref="Ended"/> - a conversation-level end was recorded. Ending never closes, reopens or reassigns the ticket.</item>
/// </list>
///
/// Contract limit (documented, not invented): Genesys sends no separate "conversation active" or "awaiting" event, so <see cref="InProgress"/> is inferred from
/// "open and handled by the AI", and <see cref="AwaitingGenesysUpdate"/> from "human required, not yet confirmed".
/// </summary>
public static class GenesysConversationStatus
{
    public const string AwaitingGenesysUpdate = "AwaitingGenesysUpdate";
    public const string InProgress = "InProgress";
    public const string Assigned = "Assigned";
    public const string Ended = "Ended";

    /// <param name="isEnded">The interaction has a recorded conversation-level end.</param>
    /// <param name="humanConfirmed">A confirmed human connection / accepted handoff / recorded handler (the existing <c>HumanAccepted</c> fact).</param>
    /// <param name="handlingState">The existing handling state: Ai, AiEnded, HumanFollowUp, WaitingForHuman, Human.</param>
    public static string Derive(bool isEnded, bool humanConfirmed, string? handlingState)
    {
        if (isEnded)
        {
            return Ended;
        }

        if (humanConfirmed || handlingState == "Human")
        {
            return Assigned;
        }

        return handlingState is "HumanFollowUp" or "WaitingForHuman" ? AwaitingGenesysUpdate : InProgress;
    }

    /// <summary>Compact label for the UI. Unknown / null renders as an em dash.</summary>
    public static string Label(string? status) => status switch
    {
        AwaitingGenesysUpdate => "Awaiting Genesys Update",
        InProgress => "Conversation In Progress",
        Assigned => "Assigned (Genesys-confirmed agent)",
        Ended => "Conversation Ended",
        null or "" => "—",
        _ => status
    };
}
