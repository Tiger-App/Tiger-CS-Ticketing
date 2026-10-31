using TigerCS.Application.Modules.SlaAndEscalation.Dto;

namespace TigerCS.Web.Models;

/// <summary>
/// Display mappings for TigerCS.Api's fixed enum-like fields. Priority is a
/// documented, fixed 4-value scale (1=Critical..4=Low) repeated identically
/// across every DTO and controller doc comment in the Api, so hardcoding it
/// here is not a guess. Ticket/verification/SLA status strings are likewise
/// the literal, closed value sets the Api's own XML docs enumerate — not
/// invented labels.
/// </summary>
public static class TicketDisplay
{
    /// <summary>A UTC instant as UAE time (UTC+4, no daylight saving): "22 Oct 2026 21:40 (UAE)".</summary>
    public static string UaeTime(DateTime utc) =>
        DateTime.SpecifyKind(utc, DateTimeKind.Utc).AddHours(4).ToString("d MMM yyyy HH:mm", System.Globalization.CultureInfo.InvariantCulture) + " (UAE)";

    /// <summary>UAE (UTC+4, no daylight saving) "HH:mm" for a chat line.</summary>
    public static string UaeClock(DateTime utc) =>
        DateTime.SpecifyKind(utc, DateTimeKind.Utc).AddHours(4).ToString("HH:mm", System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>UAE "d MMM yyyy" — the calendar day a chat line falls on.</summary>
    public static string UaeDate(DateTime utc) =>
        DateTime.SpecifyKind(utc, DateTimeKind.Utc).AddHours(4).ToString("ddd d MMM yyyy", System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>UAE "d MMM yyyy HH:mm", without a zone suffix (the caller labels the zone once).</summary>
    public static string UaeDateTime(DateTime utc) =>
        DateTime.SpecifyKind(utc, DateTimeKind.Utc).AddHours(4).ToString("d MMM yyyy HH:mm", System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>Gold filled stars then grey empty ones for a 1–5 rating, as plain text characters.</summary>
    public static string StarsFor(int rating) => new string('★', Math.Clamp(rating, 0, 5)) + new string('☆', 5 - Math.Clamp(rating, 0, 5));

    /// <summary>"Original resolution" for cycle 0, "After reopen 1" … afterwards.</summary>
    public static string ReviewCycleLabel(int cycle) => cycle <= 0 ? "Original resolution" : $"After reopen {cycle}";

    /// <summary>
    /// The responsible department — the ticket's PRIMARY assignment. Every
    /// operational ticket always has one, so this never renders a "none"
    /// state; only the human-readable name can be missing.
    /// </summary>
    public static string AssignedDepartmentLabel(int currentDepartmentId, string? departmentName)
        => departmentName ?? UnknownDepartmentLabel;

    /// <summary>Neutral wording used when a department's name cannot be resolved — never a raw numeric id.</summary>
    public const string UnknownDepartmentLabel = "Unknown department";

    /// <summary>Neutral wording for a queued ticket whose department name cannot be resolved.</summary>
    public const string UnknownDepartmentQueueLabel = "Department queue";

    /// <summary>
    /// Who the ticket is assigned to — the SECONDARY assignment. A null
    /// <paramref name="currentOwnerEmployeeId"/> means only that no specific
    /// employee holds it, never that the ticket is ownerless: it falls back to
    /// the responsible department's queue ("Facility Management Queue"), which
    /// is a real, accountable destination. "Unassigned" is deliberately never
    /// produced here — it misrepresents a queued ticket as having no owner.
    /// </summary>
    public static string AssignedToLabel(
        Guid? currentOwnerEmployeeId, string? ownerName, int currentDepartmentId, string? departmentName)
        => currentOwnerEmployeeId is not { } ownerId
            ? departmentName is null ? UnknownDepartmentQueueLabel : $"{departmentName} Queue (Unassigned)"
            : ownerName ?? $"Employee #{ownerId.ToString()[..8]}";

    /// <summary>True when the ticket sits in its department queue rather than with a named employee — for styling only, never for the label text.</summary>
    public static bool IsDepartmentQueue(Guid? currentOwnerEmployeeId) => currentOwnerEmployeeId is null;

    // ---- Pending human work (any channel) ----

    /// <summary>
    /// The work item's own status, in agent-facing words. Deliberately
    /// distinct wording from the TICKET's status, because the two are
    /// separate: an interaction can be Completed here while its ticket is
    /// still In Progress.
    /// </summary>
    public static string HandoffStatusLabel(string status) => status switch
    {
        "NotRequired" => "No human needed",
        "WaitingForAgent" => "Waiting for agent",
        "Assigned" => "Assigned",
        "InProgress" => "In progress",
        "Completed" => "Completed",
        "Cancelled" => "Cancelled",
        _ => status
    };

    public static string HandoffStatusCssKey(string status) => status switch
    {
        "WaitingForAgent" => "waiting",
        "Assigned" => "assigned",
        "InProgress" => "inprogress",
        "Completed" => "completed",
        "Cancelled" => "cancelled",
        _ => "waiting"
    };

    /// <summary>
    /// Who handles a Genesys interaction, in agent-facing words. Null (an
    /// interaction Ticketing created itself) renders as nothing — the caller
    /// skips the row — and an unknown value is shown verbatim rather than
    /// guessed at.
    /// </summary>
    public static string HandlingStateLabel(string? state, string? humanAgentName = null) => state switch
    {
        "Ai" => "AI (virtual agent) — no human involved",
        "WaitingForHuman" => "AI — a human was requested and has not yet taken it",
        "Human" => string.IsNullOrWhiteSpace(humanAgentName) ? "Human agent" : $"Human agent — {humanAgentName}",
        null or "" => "—",
        _ => state
    };

    /// <summary>
    /// Who or what initiated a human handoff, exactly as Genesys stated it.
    /// An unstated trigger says so — it is never defaulted to "customer".
    /// </summary>
    public static string HandoffInitiatorLabel(string? trigger) => trigger switch
    {
        "CustomerRequestedHuman" => "Customer asked for a human",
        "AiEscalated" => "AI escalated — it could not resolve the request",
        "RoutingDecision" => "Business rule / routing decision",
        "AiConnectionLost" => "AI conversation ended without a human taking it",
        "AgentTransfer" => "Transferred by another agent",
        null or "" => "Not stated by Genesys",
        _ => trigger
    };

    /// <summary>
    /// The activity-feed wording for a <c>HandoffRequested</c> event. The event
    /// note is written as "{Trigger} — {reason}" (or "Handoff requested — …"
    /// when Genesys stated no trigger), so the sentence follows the stated
    /// initiator instead of always claiming the customer asked.
    /// </summary>
    /// <summary>The label for a recorded transfer reason (the <c>TicketTransferReason</c> name the Api returns).</summary>
    public static string TransferReasonLabel(string? reason) => reason switch
    {
        "UnableToResolve" => "Unable to resolve",
        "NeedsEscalation" => "Needs escalation",
        "WrongDepartment" => "Wrong department",
        "CustomerRequest" => "Customer request",
        "SystemRouting" => "Automatic routing",
        "Other" => "Other reason",
        _ => "Reason not recorded"
    };

    public static string HandoffRequestedActivity(string? note)
    {
        var trigger = note?.Split(" — ", 2)[0].Trim();
        return trigger switch
        {
            "CustomerRequestedHuman" => "customer asked for a human agent",
            "AiEscalated" => "AI escalated the conversation to a human agent",
            "RoutingDecision" => "a routing rule requested a human agent",
            "AiConnectionLost" => "the AI conversation ended without a human agent taking it",
            "AgentTransfer" => "an agent transferred the interaction to another human agent",
            _ => "a human agent was requested"
        };
    }

    /// <summary>
    /// How the human is expected to continue. Null renders as "Not specified"
    /// rather than defaulting to Callback: which mode applies on which channel
    /// is Genesys' behaviour to state, and inventing one here would be the
    /// phone-shaped assumption this design exists to avoid.
    /// </summary>
    public static string HandoffModeLabel(string? mode) => mode switch
    {
        null or "" => "Not specified",
        "Callback" => "Callback",
        "ContinueChat" => "Continue chat",
        "ReplyInChannel" => "Reply in channel",
        "HumanTakeover" => "Human takeover",
        _ => mode
    };

    /// <summary>A "3 h ago" / "2 d ago" style age for list cells, coarsest honest unit — "just now" under a minute.</summary>
    public static string AgoLabel(DateTime atUtc, DateTime nowUtc)
    {
        var age = nowUtc - atUtc;
        if (age < TimeSpan.Zero)
        {
            age = TimeSpan.Zero;
        }

        return age.TotalMinutes < 1 ? "just now"
            : age.TotalHours < 1 ? $"{(int)age.TotalMinutes} min ago"
            : age.TotalDays < 1 ? $"{(int)age.TotalHours} h ago"
            : age.TotalDays < 30 ? $"{(int)age.TotalDays} d ago"
            : atUtc.ToLocalTime().ToString("MMM d, yyyy");
    }

    /// <summary>Up to two initials for an avatar chip — the first letters of the first and last words, or "?" for an empty name.</summary>
    public static string Initials(string? name)
    {
        var parts = (name ?? string.Empty).Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(p => char.IsLetterOrDigit(p[0])).ToArray();
        return parts.Length switch
        {
            0 => "?",
            1 => parts[0][..Math.Min(2, parts[0].Length)].ToUpperInvariant(),
            _ => $"{parts[0][0]}{parts[^1][0]}".ToUpperInvariant(),
        };
    }

    /// <summary>How long a customer has been waiting for a human, in the coarsest unit that is still honest.</summary>
    public static string WaitingSinceLabel(DateTime requestedAtUtc, DateTime nowUtc)
    {
        var waited = nowUtc - requestedAtUtc;
        if (waited < TimeSpan.Zero)
        {
            waited = TimeSpan.Zero;
        }

        return waited.TotalMinutes < 1 ? "Just now"
            : waited.TotalHours < 1 ? $"{(int)waited.TotalMinutes} min"
            : waited.TotalDays < 1 ? $"{(int)waited.TotalHours} h {waited.Minutes} min"
            : $"{(int)waited.TotalDays} d {waited.Hours} h";
    }

    /// <summary>
    /// The priority tier's name — or <c>Not set</c> for an Unclassified
    /// ticket, whose priority is genuinely null because nobody has judged its
    /// urgency yet. Never coalesced into a tier: showing "Medium" for a
    /// ticket nobody has read would be a claim the system is not entitled to
    /// make.
    /// </summary>
    public static string PriorityLabel(byte? priorityId) => priorityId switch
    {
        null => "Not set",
        1 => "Critical",
        2 => "High",
        3 => "Medium",
        4 => "Low",
        _ => $"Priority {priorityId}"
    };

    /// <summary>
    /// The badge modifier for <see cref="PriorityLabel"/>. A null priority
    /// gets its own neutral key rather than falling through to
    /// <c>medium</c> — the badge must not look like a judged tier.
    /// </summary>
    public static string PriorityCssKey(byte? priorityId) => priorityId switch
    {
        null => "none",
        1 => "critical",
        2 => "high",
        3 => "medium",
        4 => "low",
        _ => "medium"
    };

    /// <summary>
    /// User-facing name for a customer-lookup source key ("Crm"/"Pact"/
    /// "Tasleeh" — CustomerLookupSource names, the same closed set the Api's
    /// customer-lookup DTOs document). Used both by the New Ticket wizard's
    /// source cards and by Ticket Details' "Verified via …" line.
    /// </summary>
    public static string LookupSourceLabel(string source) => source switch
    {
        "Crm" => "Tiger CRM",
        "Pact" => "PACT",
        "Tasleeh" => "Tasleeh",
        _ => source
    };

    /// <summary>The Open Backlog Ageing bucket names (Dashboard Phase 1) — keyed on the Api's <c>BacklogAgeBucket</c> enum names.</summary>
    public static string BacklogAgeLabel(string bucket) => bucket switch
    {
        "Under24Hours" => "< 24h",
        "OneToThreeDays" => "1–3 days",
        "ThreeToSevenDays" => "3–7 days",
        "OverSevenDays" => "> 7 days",
        _ => bucket
    };

    /// <summary>The Dashboard closure KPI names — keyed on the Api's <c>TicketClosureCategory</c> enum names.</summary>
    public static string ClosureCategoryLabel(string category) => category switch
    {
        "Reviewed" => "Closed: reviewed by customer",
        "NotReviewed" => "Closed: not reviewed",
        "ClosedByAi" => "Closed by AI",
        "ClosedByAgent" => "Closed by agent",
        _ => category
    };

    /// <summary>The approval type as people say it ("Accounting Approval"); an unknown type is shown as stored.</summary>
    public static string ApprovalTypeLabel(string type) => type switch
    {
        "AccountingApproval" => "Accounting Approval",
        "CustomerServiceApproval" => "Customer Service Approval",
        "ReopenApproval" => "Reopen Approval",
        _ => type
    };

    public static string TicketStatusLabel(string ticketStatus) => ticketStatus switch
    {
        "Open" => "Open",
        "InProgress" => "In Progress",
        "PendingCustomer" => "Pending Customer",
        "PendingThirdParty" => "Pending Third Party",
        "Resolved" => "Resolved",
        "Closed" => "Closed",
        _ => ticketStatus
    };

    public static string TicketStatusCssKey(string ticketStatus) => ticketStatus switch
    {
        "Open" => "open",
        "InProgress" => "inprogress",
        "PendingCustomer" or "PendingThirdParty" => "pending",
        "Resolved" => "resolved",
        "Closed" => "closed",
        _ => "open"
    };

    public static string VerificationStatusLabel(string verificationStatus) => verificationStatus switch
    {
        "Unverified" => "Unverified",
        "PendingCrmVerification" => "Pending CRM Verification",
        "Verified" => "Verified",
        _ => verificationStatus
    };

    /// <summary>Customer Details/Profile's CustomerProfileDto.Status — only "Found" means live CRM data actually populated the Overview/Contact Info/Units tabs.</summary>
    public static string CustomerProfileStatusMessage(string status) => status switch
    {
        "NotCrmVerified" => "This ticket is not CRM-verified — there is no customer profile to show.",
        "CrmUnavailable" => "Live CRM data is unavailable right now.",
        "AmbiguousCustomerMatch" => "Multiple CRM customer records were found for this phone number — profile details are unavailable.",
        "NotFoundInCrm" => "CRM no longer has a matching record for this customer.",
        "NoPhoneOnRecord" => "No phone number is on record for this ticket, so CRM cannot be queried for this customer.",
        _ => "Customer profile is unavailable right now."
    };

    public static string SlaStateLabel(string slaState) => slaState switch
    {
        "Running" => "Running",
        "Paused" => "Paused",
        "Met" => "Met",
        "Breached" => "Breached",
        "NotApplicable" => "Not applicable",
        _ => slaState
    };

    public static string SlaStateCssKey(string slaState) => slaState switch
    {
        "Breached" => "breached",
        "Met" => "met",
        "Paused" => "paused",
        "NotApplicable" => "na",
        _ => "running"
    };

    public static string EscalationLevelLabel(string escalationLevel) => escalationLevel switch
    {
        "None" => "None",
        "Level1" => "Level 1",
        "Level2" => "Level 2",
        "Level3" => "Level 3",
        "Level4" => "Level 4",
        _ => escalationLevel
    };

    public static string ResolutionOutcomeLabel(byte? resolutionOutcome) => resolutionOutcome switch
    {
        1 => "Resolved",
        2 => "Cancelled",
        3 => "Rejected",
        4 => "Duplicate",
        _ => "—"
    };

    /// <summary>A short, readable form of a due/overdue TimeSpan, e.g. "2h 15m" or "38m".</summary>
    public static string FormatDuration(TimeSpan span)
    {
        var abs = span.Duration();
        if (abs.TotalDays >= 1)
        {
            return $"{(int)abs.TotalDays}d {abs.Hours}h";
        }

        if (abs.TotalHours >= 1)
        {
            return $"{(int)abs.TotalHours}h {abs.Minutes}m";
        }

        return $"{Math.Max(1, abs.Minutes)}m";
    }

    /// <summary>
    /// A single badge label for an SLA summary. TigerCS.Api returns only due
    /// dates and breach booleans (no "remaining time"/"breach duration"
    /// field) — the countdown/overdue-by text here is computed from those,
    /// not invented.
    /// </summary>
    /// <summary>A target expressed in minutes as people read it: "15 min", "4 h", "24 h", "3 days".</summary>
    public static string FormatMinutes(int minutes)
    {
        if (minutes < 60)
        {
            return $"{minutes} min";
        }

        if (minutes % (24 * 60) == 0)
        {
            var days = minutes / (24 * 60);
            return days == 1 ? "1 day" : $"{days} days";
        }

        return minutes % 60 == 0 ? $"{minutes / 60} h" : $"{minutes / 60} h {minutes % 60} min";
    }

    public static (string Label, string CssKey) SlaBadgeText(TicketSlaSummaryResponseDto sla, DateTime nowUtc)
    {
        if (sla.SlaState == "Breached")
        {
            var dueAt = sla.ResolutionBreached ? sla.ResolutionDueAtUtc : sla.FirstResponseDueAtUtc;
            return dueAt is DateTime due
                ? ($"Breached {FormatDuration(nowUtc - due)}", "breached")
                : ("Breached", "breached");
        }

        if (sla.SlaState == "Running")
        {
            var dueAt = sla.ResolutionDueAtUtc ?? sla.FirstResponseDueAtUtc;
            return dueAt is DateTime due
                ? ($"Due in {FormatDuration(due - nowUtc)}", "running")
                : ("Running", "running");
        }

        if (sla.SlaState == "Met")
        {
            return ("Met", "met");
        }

        if (sla.SlaState == "Paused")
        {
            return ("Paused", "paused");
        }

        // Never an unexplained "Not applicable": no running SLA means no period
        // has been opened, and Ticket Details states exactly why.
        return ("Not started", "na");
    }
}
