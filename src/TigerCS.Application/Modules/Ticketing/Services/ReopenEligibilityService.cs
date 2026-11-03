using TigerCS.Application.Modules.Ticketing.Abstractions;
using TigerCS.Application.Modules.WorkflowConfiguration.Abstractions;
using TigerCS.Domain.Modules.Ticketing;
using TigerCS.Domain.Modules.WorkflowConfiguration;

namespace TigerCS.Application.Modules.Ticketing.Services;

/// <summary>
/// Why a ticket is, or is not, reopenable right now — the lifecycle half of
/// the approved Reopen rule, evaluated in the order the rule states it.
/// Deliberately a distinct value per reason: both callers map these onto
/// their own outcome vocabularies, and a caller that collapsed them would
/// tell the user "not eligible" for an expired window.
/// </summary>
public enum ReopenEligibility
{
    /// <summary>Every lifecycle condition holds — a reopen would be allowed, subject to the caller's own permission and the routing input they supply.</summary>
    Eligible,

    /// <summary>The ticket is not Closed. Resolved is not reopenable — it is still being worked and is corrected in place.</summary>
    NotClosed,

    /// <summary>Closed, but not as Resolved — Cancelled, Rejected and Duplicate are terminal dispositions.</summary>
    OutcomeNotReopenable,

    /// <summary>The ticket's request type (or its pinned workflow version) switches Reopen off entirely.</summary>
    NotAllowedForRequestType,

    /// <summary>Closed, but the lifecycle history carries no transition into Closed — data damage, treated as not eligible rather than substituting some other timestamp.</summary>
    ClosureMomentUnknown,

    /// <summary>The ISSUE-011 window, measured from closure, has passed.</summary>
    WindowExpired
}

/// <summary>
/// The one place the approved Reopen rule's lifecycle eligibility is
/// computed. Both Reopen surfaces read it — <c>TicketLifecycleAppService</c>
/// before performing a reopen, and <c>TicketApprovalAppService</c> before
/// accepting (and again before granting) a
/// <see cref="ApprovalType.ReopenApproval"/> request — so a Reopen Approval
/// can never be raised, or granted, for a reopen the lifecycle service would
/// then refuse.
///
/// <para>
/// <b>It duplicates nothing.</b> The rule itself still lives in
/// <see cref="ReopenPolicy"/>; the request type's capability still comes from
/// <see cref="WorkflowCapabilities"/>; the closure moment still comes from
/// the <c>TicketStatusHistory</c> row Close writes
/// (<see cref="ITicketStatusHistoryRepository.GetLatestTransitionIntoAsync"/>),
/// because a Closed ticket carries no closure timestamp column and the
/// resolution timestamp is an earlier, different moment. This type only
/// sequences those three, once, so the two callers cannot drift.
/// </para>
///
/// <para>
/// <b>Permission is deliberately absent.</b> Nothing here asks who the
/// caller is: the role gate (<c>TicketRoleSets.Reopen</c>), the
/// resource-level visibility check, the target department and the
/// concurrency token all stay with the caller that performs or requests the
/// action. This answers only "could this ticket be reopened at all".
/// </para>
/// </summary>
public sealed class ReopenEligibilityService(
    ITicketStatusHistoryRepository statusHistoryRepository,
    IRequestTypeRepository requestTypeRepository,
    IWorkflowTemplateRepository workflowTemplateRepository,
    ReopenPolicy reopenPolicy)
{
    /// <summary>The start of the reopen window at <paramref name="nowUtc"/>: a ticket closed before it can no longer be reopened.</summary>
    public DateTime WindowStartUtc(DateTime nowUtc) => nowUtc - reopenPolicy.Window;

    /// <summary>
    /// Evaluates the lifecycle rule against <paramref name="nowUtc"/>, which
    /// the caller passes in rather than this service reading the clock — the
    /// lifecycle service uses one instant for the window check and for every
    /// row it writes in the same transaction, and a second clock read here
    /// would let those disagree.
    ///
    /// <para>
    /// <paramref name="closedForCustomerInactivity"/> is read by the caller from
    /// the ticket's current resolution. Only the direct-Reopen path passes it;
    /// the Reopen Approval path does not, so approvals remain Resolved-only.
    /// </para>
    /// </summary>
    public async Task<ReopenEligibility> EvaluateAsync(
        Ticket ticket, DateTime nowUtc, CancellationToken cancellationToken = default, bool closedForCustomerInactivity = false)
    {
        ArgumentNullException.ThrowIfNull(ticket);

        if (ticket.TicketStatus is not TicketStatus.Closed)
        {
            return ReopenEligibility.NotClosed;
        }

        if (!ReopenPolicy.IsReopenableOutcome(ticket.ResolutionOutcome, closedForCustomerInactivity))
        {
            return ReopenEligibility.OutcomeNotReopenable;
        }

        // Workflow/Automation phase 2 — a request type may switch Reopen off
        // entirely. This gate only ever narrows: where reopen stays allowed
        // (or the ticket has no request type), the remaining rules are the
        // enforcement point, exactly as before.
        var capabilities = await ResolveCapabilitiesAsync(ticket, cancellationToken);
        if (capabilities is { CanReopen: false })
        {
            return ReopenEligibility.NotAllowedForRequestType;
        }

        var closedAt = await statusHistoryRepository.GetLatestTransitionIntoAsync(
            ticket.TicketId, TicketStatusDimension.TicketStatus, (byte)TicketStatus.Closed, cancellationToken);
        if (closedAt is null)
        {
            return ReopenEligibility.ClosureMomentUnknown;
        }

        return reopenPolicy.IsWithinWindow(closedAt.OccurredAtUtc, nowUtc)
            ? ReopenEligibility.Eligible
            : ReopenEligibility.WindowExpired;
    }

    /// <summary>
    /// The ticket's effective workflow capabilities, or null when the ticket
    /// carries no request type — null means "no workflow configuration
    /// applies", never "everything forbidden".
    ///
    /// <para>
    /// The ticket's PINNED version is authoritative: publishing a newer
    /// version never changes what an existing ticket may do. A legacy ticket
    /// that carries a request type but no pinned version falls back to the
    /// workflow's currently Published version — the same resolution the
    /// lifecycle service has always used.
    /// </para>
    /// </summary>
    private async Task<WorkflowCapabilities?> ResolveCapabilitiesAsync(Ticket ticket, CancellationToken cancellationToken)
    {
        if (ticket.RequestTypeId is not { } requestTypeId)
        {
            return null;
        }

        var requestType = await requestTypeRepository.GetByIdAsync(requestTypeId, cancellationToken);
        if (requestType is null)
        {
            return null;
        }

        var template = ticket.WorkflowTemplateId is { } pinnedVersionId
            ? await workflowTemplateRepository.GetByIdAsync(pinnedVersionId, cancellationToken)
            : await workflowTemplateRepository.GetPublishedAsync(requestType.WorkflowId, cancellationToken);

        return template is null ? null : WorkflowCapabilities.Resolve(template, requestType);
    }
}
