using TigerCS.Domain.Modules.Ticketing;

namespace TigerCS.Application.Modules.Ticketing.Services;

/// <summary>
/// The "AI (virtual agent)" display rule. A ticket is shown as handled by the AI virtual agent - instead of "Unassigned - awaiting CS Agent" - only
/// while ALL of these hold: it is Open or In Progress; no employee owns it; its Genesys conversation is still recorded as live (ACTIVE_AI: no end
/// event, no inactivity closure); nobody asked for or took human handling; and the last recorded activity is at most <see cref="DisplayWindow"/> ago.
///
/// This is DISPLAY ONLY. It never creates an assignment, never writes an end timestamp and never closes the ticket. Beyond the window the
/// approved Stale Phone rule (<see cref="CsAgentBackfillClassifier.IsStalePhoneAi"/>, run automatically by the StalePhoneAssignment process) deals
/// an eligible Phone ticket to a CS Agent; Chat and every other channel keep their existing protection and are simply not labelled AI any more.
/// </summary>
public static class AiVirtualAgentRule
{
    /// <summary>The approved 6-hour window (the same figure as <see cref="CsAgentBackfillOptions.DefaultStalePhoneAiAfterHours"/>).</summary>
    public static readonly TimeSpan DisplayWindow = TimeSpan.FromHours(CsAgentBackfillOptions.DefaultStalePhoneAiAfterHours);

    /// <summary>The wording shown in the Assigned To field.</summary>
    public const string Label = "AI (virtual agent)";

    public static bool IsActive(UnassignedTicketEvidence e, DateTime nowUtc)
    {
        ArgumentNullException.ThrowIfNull(e);
        return e.OwnerEmployeeId is null
            && (e.Status is TicketStatus.Open or TicketStatus.InProgress)
            && e.HasGenesysInteraction
            && e.HasLiveGenesysInteraction
            && !(e.RequiresHumanFollowUp || e.OpenHandoff || e.HumanEvidence || e.AnsweringEmployeeId is not null || e.PhonePickupByUnmappedAgent)
            && nowUtc - e.LastActivityUtc <= DisplayWindow;
    }
}
