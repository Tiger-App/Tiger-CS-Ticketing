namespace TigerCS.Web.Models;

/// <summary>How loudly the "What needs your attention?" card speaks.</summary>
public enum AttentionLevel
{
    /// <summary>Something is wrong or someone is waiting: overdue SLA, a customer waiting for a human, an unclassified ticket.</summary>
    Urgent,

    /// <summary>A decision or step is expected from somebody, but nothing is overdue.</summary>
    Attention,

    /// <summary>Nothing for this viewer to do right now; the card says what the ticket is waiting on.</summary>
    Info,

    /// <summary>The ticket is finished.</summary>
    Done
}

/// <summary>
/// A pointer from the guidance to something the page already offers.
/// <see cref="Key"/> is either an action key (<c>assign</c>, <c>resolve</c> …,
/// matching a header disclosure's <c>data-action</c>) or a tab key (<c>tab-approvals</c> …).
/// </summary>
/// <param name="Href">A page to open instead of an in-page action or tab — used where the next step lives elsewhere (an administration screen).</param>
public sealed record GuidanceTarget(string Key, string Label, string? Href = null)
{
    public bool IsTab => Href is null && Key.StartsWith("tab-", StringComparison.Ordinal);
}

/// <param name="Level">Urgent conditions that are true alongside the headline.</param>
/// <param name="Text">Plain-language statement.</param>
public sealed record GuidanceAlert(AttentionLevel Level, string Text);

/// <summary>An action the viewer cannot take on this ticket right now, and why.</summary>
public sealed record UnavailableAction(string Label, string Reason);

/// <summary>What the "What needs your attention?" card renders.</summary>
public sealed record TicketGuidance(
    AttentionLevel Level,
    string Headline,
    string Reason,
    GuidanceTarget? Primary,
    IReadOnlyList<GuidanceAlert> Alerts,
    IReadOnlyList<UnavailableAction> Unavailable);

/// <summary>An approval cycle awaiting a decision.</summary>
public sealed record PendingApprovalFact(string Label, string DecidedBy, bool ViewerCanDecide);

/// <summary>An approval the request type requires that nobody has requested yet.</summary>
public sealed record RequiredApprovalFact(string Label, string DecidedBy, bool BlocksWork, bool ViewerCanRequest);

/// <summary>
/// Everything the guidance reads, already computed by the page model:
/// the ticket's own state, and the permission flags the page already derives from
/// <see cref="TicketActions"/>. Nothing here is a new rule — the guidance only
/// words what the existing workflow, assignment, verification, approval and
/// permission state already says.
/// </summary>
public sealed record TicketGuidanceFacts(
    string TicketStatus,
    bool IsClassified,
    bool HasOwner,
    bool ViewerIsOwner,
    string? OwnerName,
    string DepartmentName,
    int ReopenCount,
    bool IsReopenEligible,
    bool PendingCrmVerification,
    string HandoffState,
    DateTime? HandoffRequestedAtUtc,
    string? HandlingState,
    bool SlaBreached,
    string? SlaBreachText,
    bool FirstResponsePending,
    bool ResolutionClockPaused,
    bool ViewerIsReadOnly,
    bool CanClassify,
    bool CanAssign,
    bool CanTransfer,
    bool CanChangeStatus,
    bool CanResolve,
    bool CanClose,
    bool CanEscalate,
    bool CanReopen,
    bool CanRequestDowngrade,
    bool CanRecordFirstResponse,
    bool HasPendingDowngrade,
    bool CanDecideDowngrade,
    IReadOnlyList<PendingApprovalFact> PendingApprovals,
    IReadOnlyList<RequiredApprovalFact> RequiredApprovals,
    bool ReopenApproved,
    DateTime NowUtc,
    ClassifyNotice? ClassifyNotice = null,
    bool HumanAccepted = false,
    bool HumanResponded = false);

/// <summary>
/// Chooses the single most useful "next step" for the person opening a ticket, plus any
/// other urgent conditions and the reasons an action is not available.
///
/// <para>
/// Pure and side-effect free so each scenario (new, unassigned, AI-handled, pending
/// customer, resolved, closed, reopened) is unit-testable without rendering the page.
/// The headline is the highest-priority condition that is true; everything else that is
/// urgent is listed as an alert rather than competing with it.
/// </para>
/// </summary>
public static class TicketNextAction
{
    public const string Closed = "Closed";
    public const string Resolved = "Resolved";

    public static TicketGuidance Build(TicketGuidanceFacts f)
    {
        var alerts = BuildAlerts(f);
        var (level, headline, reason, primary) = Choose(f);

        // An overdue SLA lifts anything that would otherwise be informational to Urgent — but
        // never a finished ticket.
        if (f.SlaBreached && level is AttentionLevel.Info or AttentionLevel.Attention
            && f.TicketStatus is not (Closed or Resolved))
        {
            level = AttentionLevel.Urgent;
        }

        // The headline already says it; do not repeat it as an alert.
        alerts = [.. alerts.Where(a => !string.Equals(a.Text, reason, StringComparison.Ordinal) && !string.Equals(a.Text, headline, StringComparison.Ordinal))];

        return new TicketGuidance(level, headline, reason, primary, alerts, BuildUnavailable(f));
    }

    private static (AttentionLevel Level, string Headline, string Reason, GuidanceTarget? Primary) Choose(TicketGuidanceFacts f)
    {
        if (f.TicketStatus == Closed)
        {
            if (f.ReopenApproved && f.CanReopen)
            {
                return (AttentionLevel.Attention, "Reopen request was approved",
                    "The ticket stays closed until you perform the Reopen action and choose the department that takes it on.",
                    new GuidanceTarget("reopen", "Reopen ticket"));
            }

            if (f.IsReopenEligible && f.CanReopen)
            {
                return (AttentionLevel.Done, "This ticket is closed",
                    "It is still inside the reopen window. Reopen it only if the customer's problem has come back.",
                    null);
            }

            if (f.IsReopenEligible && f.RequiredApprovals.FirstOrDefault(a => a.ViewerCanRequest) is { } reopenRequest)
            {
                return (AttentionLevel.Done, "This ticket is closed",
                    $"You cannot reopen it yourself, but you can request approval from {reopenRequest.DecidedBy}.",
                    new GuidanceTarget("tab-approvals", "Request approval"));
            }

            return (AttentionLevel.Done, "This ticket is closed",
                "No further action is needed. It is read-only.", null);
        }

        if (f.ViewerIsReadOnly)
        {
            return (AttentionLevel.Info, "You have view-only access",
                $"This ticket is {StatusPhrase(f.TicketStatus)}. You can read everything but cannot change it.", null);
        }

        // ---- Things that block or wait on a person ----
        if (!f.IsClassified && f.CanClassify && f.ClassifyNotice is { } noOptions)
        {
            // Classify is never recommended as something to do when it has nothing valid to choose from: the card says
            // why, and points at the existing administrative path (when the viewer may use it) instead.
            return (AttentionLevel.Urgent, noOptions.Headline, noOptions.Detail,
                noOptions.AdminHref is { } adminHref ? new GuidanceTarget("admin", noOptions.AdminLabel ?? "Open Administration", adminHref) : null);
        }

        if (!f.IsClassified)
        {
            return f.CanClassify
                ? (AttentionLevel.Urgent, "Classify this ticket",
                    "Nobody has set its category, request type or priority yet. A person must classify it before the right workflow and SLA can start.",
                    new GuidanceTarget("classify", "Classify"))
                : (AttentionLevel.Urgent, "This ticket still needs classifying",
                    "Its category, request type and priority are not set, and you cannot classify it. Ask someone in " + f.DepartmentName + ".",
                    null);
        }

        if (f.PendingApprovals.FirstOrDefault(a => a.ViewerCanDecide) is { } mine)
        {
            return (AttentionLevel.Urgent, $"Your decision is needed: {mine.Label}",
                "An approval is waiting on you. The ticket cannot move on until it is approved or rejected.",
                new GuidanceTarget("tab-approvals", "Review approval"));
        }

        if (f.HasPendingDowngrade && f.CanDecideDowngrade)
        {
            return (AttentionLevel.Attention, "Decide the priority downgrade request",
                "Someone asked to lower this ticket's priority. Nothing changes until it is approved.",
                new GuidanceTarget("pending-downgrade", "Review request"));
        }

        if (f.HandoffState == "WaitingForAgent")
        {
            var waited = f.HandoffRequestedAtUtc is { } at ? $" for {TicketDisplay.WaitingSinceLabel(at, f.NowUtc)}" : "";
            return f.CanAssign && !f.HasOwner
                ? (AttentionLevel.Urgent, "The customer is waiting for a person",
                    $"A human agent was requested{waited} and nobody has taken it. Assign the ticket so someone picks it up.",
                    new GuidanceTarget("assign", "Assign"))
                : (AttentionLevel.Urgent, "The customer is waiting for a person",
                    $"A human agent was requested{waited} and nobody has taken it yet. Open the conversation to see why.",
                    new GuidanceTarget("conversation", "Read the conversation"));
        }

        if (f.PendingApprovals.Count > 0)
        {
            var waiting = f.PendingApprovals[0];
            return (AttentionLevel.Info, $"Waiting for approval: {waiting.Label}",
                $"{waiting.DecidedBy} must decide. There is nothing for you to approve.", new GuidanceTarget("tab-approvals", "See approval"));
        }

        if (f.RequiredApprovals.FirstOrDefault(a => a.BlocksWork) is { } required)
        {
            return required.ViewerCanRequest
                ? (AttentionLevel.Attention, $"Request approval: {required.Label}",
                    $"This request type needs {required.Label} before work can continue. {required.DecidedBy} will decide.",
                    new GuidanceTarget("tab-approvals", "Request approval"))
                : (AttentionLevel.Info, $"Waiting for approval: {required.Label}",
                    $"It has not been requested yet and you cannot request it. {required.DecidedBy} decides it.",
                    new GuidanceTarget("tab-approvals", "See approvals"));
        }

        if (f.TicketStatus == Resolved)
        {
            return f.CanClose
                ? (AttentionLevel.Attention, "Check the resolution, then close the ticket",
                    "The department has resolved it. Closing completes the ticket and sends the customer review request.",
                    new GuidanceTarget("close", "Close ticket"))
                : (AttentionLevel.Info, "Resolved — waiting for the ticket to be closed",
                    "The department has resolved it. Closing is done by the customer service team, so nothing is needed from you.",
                    null);
        }

        // ---- AI is still the only participant ----
        if (f.HandlingState == "Ai")
        {
            return (AttentionLevel.Info, "AI is handling this customer",
                "No person is in the conversation. Step in only if the customer asks for one or the AI hands over.",
                new GuidanceTarget("conversation", "Read the conversation"));
        }

        // ---- The AI is no longer the participant, but no person has taken over ----
        // "AiEnded" / "HumanFollowUp" come from the conversation; an employee being ASSIGNED is not acceptance and not a reply, so it is worded as follow-up
        // still to be done - never as "AI is handling" (the conversation is over) and never as "{owner} is handling" (they have not started).
        if (f.HandlingState is "HumanFollowUp" or "AiEnded" && !f.HumanAccepted && !f.HumanResponded)
        {
            var followUp = f.HandlingState == "HumanFollowUp";
            if (!f.HasOwner)
            {
                var headline = followUp ? "Human follow-up required" : "The AI conversation has ended";
                var detail = followUp
                    ? $"The AI conversation needs a person and nobody has been assigned. It is waiting in the {f.DepartmentName} queue."
                    : "The AI finished the conversation and no human follow-up was requested. Assign it only if the customer still needs help.";
                return f.CanAssign
                    ? (followUp ? AttentionLevel.Urgent : AttentionLevel.Info, headline, detail, new GuidanceTarget("assign", "Assign"))
                    : (followUp ? AttentionLevel.Urgent : AttentionLevel.Info, headline, detail, null);
            }

            if (f.ViewerIsOwner)
            {
                return (AttentionLevel.Attention, "This ticket is assigned to you for human follow-up",
                    "The AI conversation has ended. You have not replied to the customer yet — read the conversation, then contact them.",
                    new GuidanceTarget("conversation", "Read the conversation"));
            }

            var assignee = f.OwnerName ?? "An employee";
            return (AttentionLevel.Info, $"{assignee} is assigned for human follow-up",
                "The AI conversation has ended. They have been assigned but have not yet accepted the interaction or replied to the customer.", null);
        }

        if (!f.HasOwner)
        {
            return f.CanAssign
                ? (AttentionLevel.Attention, "Assign this ticket",
                    $"It is waiting in the {f.DepartmentName} queue and nobody owns it yet.",
                    new GuidanceTarget("assign", "Assign"))
                : (AttentionLevel.Attention, "Waiting for someone to take this ticket",
                    $"It is in the {f.DepartmentName} queue with no owner. You cannot assign it — someone with assignment permission must.",
                    f.CanEscalate ? new GuidanceTarget("escalate", "Escalate") : null);
        }

        if (f.TicketStatus == "PendingCustomer")
        {
            return (AttentionLevel.Info, "Waiting for the customer",
                f.CanChangeStatus
                    ? "Nothing to do until they reply. When they do, move the ticket back to In Progress."
                    : "Nothing to do until they reply.",
                f.CanChangeStatus ? new GuidanceTarget("status", "Change status") : null);
        }

        if (f.TicketStatus == "PendingThirdParty")
        {
            return (AttentionLevel.Info, "Waiting for a third party",
                f.CanChangeStatus
                    ? "This legacy status can only move back to In Progress, once you have what you were waiting for."
                    : "Nothing to do until they respond.",
                f.CanChangeStatus ? new GuidanceTarget("status", "Change status") : null);
        }

        // Open / In Progress with an owner.
        var reopened = f.ReopenCount > 0
            ? $"Reopened {(f.ReopenCount == 1 ? "once" : $"{f.ReopenCount} times")}. Read why in Activity. "
            : "";

        if (f.ViewerIsOwner)
        {
            if (f.FirstResponsePending && f.CanRecordFirstResponse)
            {
                return (AttentionLevel.Attention, "Contact the customer",
                    reopened + "No person has replied yet. If you already answered outside the system, record the first response.",
                    new GuidanceTarget("conversation", "Read the conversation"));
            }

            return f.CanResolve
                ? (AttentionLevel.Attention, "This ticket is yours — work it, then resolve it",
                    reopened + "Add notes as you go. Resolve it when the customer's request is dealt with.",
                    new GuidanceTarget("resolve", "Resolve"))
                : (AttentionLevel.Attention, "This ticket is yours",
                    reopened + "Add notes as you go.", new GuidanceTarget("tab-activity", "Add a note"));
        }

        var owner = f.OwnerName ?? "another employee";
        if (f.PendingCrmVerification)
        {
            return (AttentionLevel.Info, $"{owner} is handling this ticket",
                reopened + "The customer's details are still waiting for CRM verification.", null);
        }

        return (AttentionLevel.Info, $"{owner} is handling this ticket",
            reopened + "You can read it and add notes" + (f.CanChangeStatus || f.CanEscalate || f.CanAssign ? ", or step in using the actions above." : "."),
            null);
    }

    /// <summary>Urgent or notable conditions that are true regardless of which one is the headline.</summary>
    private static List<GuidanceAlert> BuildAlerts(TicketGuidanceFacts f)
    {
        var alerts = new List<GuidanceAlert>();
        if (f.TicketStatus is Closed)
        {
            return alerts;
        }

        if (f.SlaBreached)
        {
            alerts.Add(new GuidanceAlert(AttentionLevel.Urgent, f.SlaBreachText ?? "The SLA has been breached."));
        }

        if (!f.IsClassified)
        {
            alerts.Add(new GuidanceAlert(AttentionLevel.Urgent, "Needs human classification."));
        }

        if (!f.HasOwner && f.TicketStatus != Resolved)
        {
            alerts.Add(new GuidanceAlert(AttentionLevel.Attention, "No one owns this ticket yet."));
        }

        if (f.HandoffState == "WaitingForAgent")
        {
            alerts.Add(new GuidanceAlert(AttentionLevel.Urgent, "The customer asked for a person and is still waiting."));
        }

        if (f.PendingApprovals.Count > 0)
        {
            alerts.Add(new GuidanceAlert(AttentionLevel.Attention, $"Approval pending: {string.Join(", ", f.PendingApprovals.Select(a => a.Label))}."));
        }

        if (f.HasPendingDowngrade)
        {
            alerts.Add(new GuidanceAlert(AttentionLevel.Attention, "A priority downgrade is awaiting approval."));
        }

        if (f.PendingCrmVerification)
        {
            alerts.Add(new GuidanceAlert(AttentionLevel.Attention, "Customer details are waiting for CRM verification."));
        }

        if (f.ResolutionClockPaused)
        {
            alerts.Add(new GuidanceAlert(AttentionLevel.Info, "The resolution clock is paused."));
        }

        return alerts;
    }

    /// <summary>
    /// The reasons a normally-visible action is not on offer. Worded from the same rules
    /// <see cref="TicketActions"/> applies — and never by naming a role, so the text cannot
    /// drift from the role sets.
    /// </summary>
    private static List<UnavailableAction> BuildUnavailable(TicketGuidanceFacts f)
    {
        var list = new List<UnavailableAction>();
        if (f.ViewerIsReadOnly)
        {
            list.Add(new UnavailableAction("All actions", "Your access is view-only."));
            return list;
        }

        if (f.TicketStatus == Closed)
        {
            if (!(f.IsReopenEligible && f.CanReopen))
            {
                list.Add(new UnavailableAction("Reopen",
                    !f.IsReopenEligible
                        ? "Only tickets closed as Resolved, and still inside the reopen window, can be reopened."
                        : "Reopening is limited to customer service. You can ask for approval on the Approvals tab where it is offered."));
            }

            return list;
        }

        if (f.TicketStatus == Resolved)
        {
            if (!f.CanClose)
            {
                list.Add(new UnavailableAction("Close ticket", "Closing is limited to customer service."));
            }

            list.Add(new UnavailableAction("Reopen", "A ticket can be reopened only after it has been closed."));
            return list;
        }

        if (!f.IsClassified && !f.CanClassify)
        {
            list.Add(new UnavailableAction("Classify", "Classifying needs access to this department's categories and request types."));
        }
        else if (!f.IsClassified && f.ClassifyNotice is { } classifyNotice)
        {
            list.Add(new UnavailableAction("Classify", classifyNotice.Short));
        }

        if (!f.CanAssign)
        {
            list.Add(new UnavailableAction("Assign", "Assigning needs assignment permission in this ticket's department."));
        }

        if (!f.CanResolve)
        {
            list.Add(new UnavailableAction("Resolve", "Resolving is done by the department team handling the ticket, not by customer service."));
        }

        if (!f.CanChangeStatus)
        {
            list.Add(new UnavailableAction("Change status", "Only the ticket's owner, a supervisor or the department head can change its status."));
        }

        if (!f.CanEscalate)
        {
            list.Add(new UnavailableAction("Escalate", "Escalating is limited to the owner and to supervisory roles."));
        }

        if (!f.CanTransfer)
        {
            list.Add(new UnavailableAction("Transfer", "Moving a ticket to another department is limited to managers."));
        }

        list.Add(new UnavailableAction("Close ticket", "Available once the ticket is Resolved."));

        if (f.HasPendingDowngrade)
        {
            list.Add(new UnavailableAction("Request priority downgrade", "A downgrade request is already awaiting approval."));
        }
        else if (!f.CanRequestDowngrade)
        {
            list.Add(new UnavailableAction("Request priority downgrade", "Needs a lower priority to exist, and permission to request it."));
        }

        return list;
    }

    private static string StatusPhrase(string status) => status switch
    {
        "PendingCustomer" => "waiting for the customer",
        "PendingThirdParty" => "waiting for a third party",
        _ => TicketDisplay.TicketStatusLabel(status).ToLowerInvariant()
    };
}
