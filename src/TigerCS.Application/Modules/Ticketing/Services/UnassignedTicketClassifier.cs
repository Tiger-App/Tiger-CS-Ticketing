using TigerCS.Domain.Modules.Ticketing;

namespace TigerCS.Application.Modules.Ticketing.Services;

/// <summary>
/// Why an Open ticket with no employee owner has none — and whether TigerCS may put an owner on it. The SAME taxonomy is used by the
/// read-only production diagnostic (<c>Diagnose_TigerCS_PROD_RemainingOpenUnassigned_ReadOnly.sql</c>), so its counts and the reconciler's
/// actions can be compared one to one.
/// </summary>
public enum UnassignedTicketClass
{
    /// <summary>Not Open (InProgress, PendingCustomer, Resolved, Closed): never touched.</summary>
    NotOpen,

    /// <summary>Already has an employee owner: never touched, never redistributed.</summary>
    HasOwner,

    /// <summary>An approval is pending; the approval process decides first.</summary>
    PendingApproval,

    /// <summary>Every Genesys interaction of the ticket is a Phone call: <c>VoiceCallReconciler</c> is the only reconciliation job responsible for it. This job never touches it.</summary>
    HandledByVoiceReconciler,

    /// <summary>The Call Center transferred it onward (or it originated there and left): the destination's department queue is where it belongs.</summary>
    TransferredOutOfCallCenter,

    /// <summary>
    /// A department transfer found nobody eligible in the destination, so the ticket waits Unassigned there (and nobody has been assigned since).
    /// It is dealt by the DESTINATION department's Round Robin as soon as one of its employees is eligible - and is never pulled back to the Call Center.
    /// </summary>
    TransferredAwaitingDepartmentAssignment,

    /// <summary>In the Call Center and a person deliberately released it to the queue (an audited Unassign by a person): left for them.</summary>
    ReleasedByPerson,

    /// <summary>A mapped Genesys agent actually answered (interaction handler row) but the ticket carries no owner: assign to THAT agent.</summary>
    HumanAnsweredNotAssigned,

    /// <summary>A Genesys phone call was picked up by an agent id that maps to no Ticketing user: a person answered, so Round Robin.</summary>
    HumanAnsweredAgentNotMapped,

    /// <summary>Genesys asked for a person (human follow-up flag, open handoff or human transcript evidence) but the ticket never reached the Call Center: routing failed or never ran.</summary>
    HumanRequiredNotRouted,

    /// <summary>A person is needed and the ticket is in the Call Center, but nobody was picked (no eligible agent at the time, a lost race, a crash).</summary>
    HumanRequiredInCallCenterUnassigned,

    /// <summary>Created by hand / an integration (no Genesys conversation): always human work, but it sits in a department queue.</summary>
    ManualOrIntegrationNotRouted,

    /// <summary>A live AI conversation with recent activity: protected, never touched.</summary>
    ActiveAiConversation,

    /// <summary>A "live" AI conversation silent for longer than the stale threshold with no human evidence: its end event never arrived. Reported; assigned only when explicitly enabled.</summary>
    StaleAiNoEndEvent,

    /// <summary>The AI conversation ended / was closed for inactivity with no human need: a completed AI ticket, preserved.</summary>
    AiEndedNoHumanNeed,
}

/// <summary>What the reconciler does with a class.</summary>
public enum UnassignedTicketAction
{
    /// <summary>Leave exactly as it is.</summary>
    None,

    /// <summary>Direct assignment to the agent who answered.</summary>
    AssignToAnsweringAgent,

    /// <summary>Move to the Call Center if needed and deal to the next eligible agent by Round Robin.</summary>
    RoundRobinCallCenter,

    /// <summary>Deal to the next eligible employee of the ticket's CURRENT department by that department's Round Robin (a transfer that found nobody).</summary>
    RoundRobinDepartment,
}

/// <summary>Everything the classification looks at, gathered per ticket by the repository (set-based) — no customer data.</summary>
public sealed record UnassignedTicketEvidence(
    long TicketId,
    string TicketNumber,
    TicketStatus Status,
    Guid? OwnerEmployeeId,
    int CurrentDepartmentId,
    int OriginatingDepartmentId,
    DateTime CreatedAtUtc,
    bool PendingApproval,
    // True when the ticket has at least one Genesys-sourced interaction.
    bool HasGenesysInteraction,
    // A Genesys interaction with no EndedAtUtc and no InactivityClosedAtUtc.
    bool HasLiveGenesysInteraction,
    bool RequiresHumanFollowUp,
    bool OpenHandoff,
    // A HumanAgent transcript line, a first human response, or a human/agent workflow event.
    bool HumanEvidence,
    // A mapped agent answered: the Ticketing user and the Genesys id as Genesys sent it.
    Guid? AnsweringEmployeeId,
    string? AnsweringGenesysUserId,
    // A Phone interaction names a Genesys agent id that resolved to nobody (HandledByUserId is null).
    bool PhonePickupByUnmappedAgent,
    string? UnmappedGenesysAgentId,
    // Latest of interaction stamps / transcript / Genesys workflow events / ticket creation.
    DateTime LastActivityUtc,
    // The ticket was transferred to the Call Center and later moved on, or originated in the Call Center and is no longer there.
    bool LeftCallCenter,
    // A person (not the system) released an owner to the queue.
    bool ReleasedByPerson,
    // At least one Genesys interaction is live AND every live one is a Phone call. Only such a ticket can be released by the Phone stale rule;
    // a live chat / messaging / any other-channel conversation keeps the ticket protected. Default false = protected (safe for older callers).
    bool LiveGenesysIsPhoneOnly = false,
    // A department transfer into the CURRENT department left the ticket unassigned and no assignment has been made since (safe default false).
    bool AwaitingDepartmentAssignment = false,
    // VOICE: every Genesys interaction of the ticket is a Phone call. Only such a ticket follows the voice ownership rules (AI Agent keeps it, no Round Robin
    // while a human may still answer, no fallback agent); a ticket with any digital interaction keeps the previous behaviour (safe default false).
    bool GenesysIsPhoneOnly = false,
    // VOICE: when TigerCS RECEIVED the end of the call (EndReceivedAtUtc, else the reported end time) - the latest of them - once every Genesys interaction has
    // ended. The anchor of the no-answer reconciliation window. Null while any interaction is still live (or for a non-voice ticket).
    DateTime? VoiceEndReceivedAtUtc = null,
    // VOICE: a human DID answer (a connect event named an agent) but that agent could not be given the ticket (unmapped Genesys id, not a CS Agent ...): the
    // Genesys id from the ManualAssignmentRequired flag, when it connected, and the employee that id maps to NOW (null while still unmapped) - so a corrected
    // mapping is reconciled without Round Robin.
    string? FlaggedAnsweringGenesysUserId = null,
    DateTime? FlaggedAnsweringConnectedAtUtc = null,
    Guid? FlaggedAnsweringEmployeeId = null,
    // VOICE: a person ANSWERED (a recorded handler, a human transcript line, a first human response, an agent-connected / call-transferred / handoff-started
    // event, or a flagged answer). An agent merely ASSIGNED / offered the call (HandoffAssigned) is NOT an answer. Safe default false.
    bool HumanAnswered = false,
    // VOICE: when the recorded handler connected (Genesys' connected time), where recorded.
    DateTime? AnsweringConnectedAtUtc = null,
    // VOICE: the LAST agent Genesys OFFERED the call to, read from the offer events (GenesysAgentOffered) - never from the ticket owner. The Genesys id, the employee that
    // id maps to NOW (null while unmapped) and when it was offered. Null = no agent was ever offered the call.
    string? VoiceLastOfferedGenesysUserId = null,
    Guid? VoiceLastOfferedEmployeeId = null,
    DateTime? VoiceLastOfferedAtUtc = null);

public sealed record UnassignedTicketClassification(UnassignedTicketClass Class, UnassignedTicketAction Action);

public static class UnassignedTicketClassifier
{
    /// <param name="evidence">Gathered facts.</param>
    /// <param name="callCenterDepartmentId">The active Call Center, or null when none is configured.</param>
    /// <param name="nowUtc">Clock for staleness.</param>
    /// <param name="staleAiAfter">Silence after which a live AI conversation with no human evidence is "stale". Null = never stale (always protected).</param>
    /// <param name="actOnStaleAi">Whether a stale AI ticket is treated as human work (off by default: it is an AI ticket nobody asked a person for).</param>
    public static UnassignedTicketClassification Classify(
        UnassignedTicketEvidence evidence, int? callCenterDepartmentId, DateTime nowUtc, TimeSpan? staleAiAfter = null, bool actOnStaleAi = false)
    {
        ArgumentNullException.ThrowIfNull(evidence);

        UnassignedTicketClassification Of(UnassignedTicketClass c, UnassignedTicketAction a = UnassignedTicketAction.None) => new(c, a);

        if (evidence.Status != TicketStatus.Open) return Of(UnassignedTicketClass.NotOpen);
        if (evidence.OwnerEmployeeId is not null) return Of(UnassignedTicketClass.HasOwner);
        if (evidence.PendingApproval) return Of(UnassignedTicketClass.PendingApproval);

        // VOICE is owned by VoiceCallReconciler (end-of-call window, confirmed answer, first response): never classified for action here.
        if (evidence.GenesysIsPhoneOnly) return Of(UnassignedTicketClass.HandledByVoiceReconciler);

        var inCallCenter = callCenterDepartmentId is { } cc && evidence.CurrentDepartmentId == cc;
        if (!inCallCenter && evidence.AwaitingDepartmentAssignment)
        {
            return Of(UnassignedTicketClass.TransferredAwaitingDepartmentAssignment, UnassignedTicketAction.RoundRobinDepartment);
        }

        if (!inCallCenter && evidence.LeftCallCenter) return Of(UnassignedTicketClass.TransferredOutOfCallCenter);
        if (inCallCenter && evidence.ReleasedByPerson) return Of(UnassignedTicketClass.ReleasedByPerson);

        // A person answered. A mapped answering agent owns it; an unmapped one means "a person, not a bot" -> the next agent in the rotation.
        if (evidence.AnsweringEmployeeId is not null)
        {
            return Of(UnassignedTicketClass.HumanAnsweredNotAssigned, UnassignedTicketAction.AssignToAnsweringAgent);
        }

        if (evidence.PhonePickupByUnmappedAgent)
        {
            return Of(UnassignedTicketClass.HumanAnsweredAgentNotMapped, UnassignedTicketAction.RoundRobinCallCenter);
        }

        if (evidence.RequiresHumanFollowUp || evidence.OpenHandoff || evidence.HumanEvidence)
        {
            return inCallCenter
                ? Of(UnassignedTicketClass.HumanRequiredInCallCenterUnassigned, UnassignedTicketAction.RoundRobinCallCenter)
                : Of(UnassignedTicketClass.HumanRequiredNotRouted, UnassignedTicketAction.RoundRobinCallCenter);
        }

        if (!evidence.HasGenesysInteraction)
        {
            return inCallCenter
                ? Of(UnassignedTicketClass.HumanRequiredInCallCenterUnassigned, UnassignedTicketAction.RoundRobinCallCenter)
                : Of(UnassignedTicketClass.ManualOrIntegrationNotRouted, UnassignedTicketAction.RoundRobinCallCenter);
        }

        if (!evidence.HasLiveGenesysInteraction)
        {
            return Of(UnassignedTicketClass.AiEndedNoHumanNeed);
        }

        var stale = staleAiAfter is { } threshold && nowUtc - evidence.LastActivityUtc > threshold;
        if (!stale) return Of(UnassignedTicketClass.ActiveAiConversation);

        return actOnStaleAi
            ? Of(UnassignedTicketClass.StaleAiNoEndEvent, UnassignedTicketAction.RoundRobinCallCenter)
            : Of(UnassignedTicketClass.StaleAiNoEndEvent);
    }
}

/// <summary>
/// The answering agent recorded in a <see cref="WorkflowEventType.ManualAssignmentRequired"/> note: <c>[GenesysUserId=&lt;id&gt;;Issue=&lt;issue&gt;;ConnectedAtUtc=&lt;ISO&gt;]</c>.
/// Written by <see cref="TicketAutoAssignmentService"/> when the agent a call connected to cannot be given the ticket; read back by the evidence repository so a
/// corrected Genesys-id mapping can be reconciled without Round Robin.
/// </summary>
public sealed record FlaggedAnswer(string GenesysUserId, string Issue, DateTime? ConnectedAtUtc)
{
    public static FlaggedAnswer? TryParse(string? note)
    {
        if (string.IsNullOrWhiteSpace(note))
        {
            return null;
        }

        var open = note.LastIndexOf("[GenesysUserId=", StringComparison.Ordinal);
        var close = open < 0 ? -1 : note.IndexOf(']', open);
        if (open < 0 || close < 0)
        {
            return null;
        }

        var parts = note[(open + 1)..close].Split(';', StringSplitOptions.RemoveEmptyEntries)
            .Select(p => p.Split('=', 2)).Where(kv => kv.Length == 2).ToDictionary(kv => kv[0], kv => kv[1], StringComparer.Ordinal);
        if (!parts.TryGetValue("GenesysUserId", out var id) || string.IsNullOrWhiteSpace(id))
        {
            return null;
        }

        DateTime? connected = parts.TryGetValue("ConnectedAtUtc", out var raw)
            && DateTime.TryParse(raw, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.RoundtripKind, out var at)
            ? DateTime.SpecifyKind(at, DateTimeKind.Utc)
            : null;
        return new FlaggedAnswer(id.Trim(), parts.GetValueOrDefault("Issue") ?? string.Empty, connected);
    }
}

/// <summary>
/// An agent a Genesys call was OFFERED to, as written into the note of a <see cref="WorkflowEventType.GenesysAgentOffered"/> event by <c>GenesysAgentOfferRecorder</c>:
/// <c>[GenesysUserId=&lt;id&gt;;OfferedAtUtc=&lt;iso&gt;;Source=&lt;source&gt;]</c>. Read back by the evidence repository so the voice reconciler knows the LAST agent offered.
/// </summary>
public sealed record OfferedAgent(string GenesysUserId, DateTime? OfferedAtUtc, string Source)
{
    public static string Format(string genesysUserId, DateTime offeredAtUtc, string source) =>
        $"[GenesysUserId={genesysUserId};OfferedAtUtc={offeredAtUtc.ToUniversalTime():O};Source={source}]";

    public static OfferedAgent? TryParse(string? note)
    {
        if (string.IsNullOrWhiteSpace(note))
        {
            return null;
        }

        var open = note.LastIndexOf("[GenesysUserId=", StringComparison.Ordinal);
        var close = open < 0 ? -1 : note.IndexOf(']', open);
        if (open < 0 || close < 0)
        {
            return null;
        }

        var parts = note[(open + 1)..close].Split(';', StringSplitOptions.RemoveEmptyEntries)
            .Select(p => p.Split('=', 2)).Where(kv => kv.Length == 2).ToDictionary(kv => kv[0], kv => kv[1], StringComparer.Ordinal);
        if (!parts.TryGetValue("GenesysUserId", out var id) || string.IsNullOrWhiteSpace(id))
        {
            return null;
        }

        DateTime? offeredAt = parts.TryGetValue("OfferedAtUtc", out var raw)
            && DateTime.TryParse(raw, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.RoundtripKind, out var at)
            ? DateTime.SpecifyKind(at, DateTimeKind.Utc)
            : null;
        return new OfferedAgent(id.Trim(), offeredAt, parts.GetValueOrDefault("Source") ?? string.Empty);
    }
}
