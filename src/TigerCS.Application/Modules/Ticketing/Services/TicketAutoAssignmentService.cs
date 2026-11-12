using TigerCS.Application.Abstractions;
using TigerCS.Application.Authorization;
using TigerCS.Application.Modules.IdentityAndAccess.Abstractions;
using TigerCS.Application.Modules.Notifications.Services;
using TigerCS.Application.Modules.Reporting.Services;
using TigerCS.Application.Modules.Ticketing.Abstractions;
using TigerCS.Application.Modules.WorkflowConfiguration.Abstractions;
using TigerCS.Domain.Modules.IdentityAndAccess;
using TigerCS.Domain.Modules.Ticketing;
using TigerCS.Domain.Modules.WorkflowConfiguration;

namespace TigerCS.Application.Modules.Ticketing.Services;

/// <summary>How one automatic-assignment attempt ended. Everything except <see cref="AssignedToPrimary"/> leaves the ticket in the department queue — the universal safe fallback; a random employee is never chosen.</summary>
public enum AutoAssignmentOutcome
{
    /// <summary>The ticket carries no request type — no automation applies; exactly the pre-phase-2 behavior.</summary>
    NoRequestType,

    /// <summary>No active assignment rule is configured for the request type — department queue.</summary>
    NoRuleConfigured,

    /// <summary>The rule explicitly says department queue.</summary>
    DepartmentQueueByRule,

    /// <summary>The department's workflow settings disable assignment — department queue.</summary>
    AssignmentDisabledForDepartment,

    /// <summary>The configured primary assignee is not an active member of the ticket's department — degraded safely to the department queue and audited, never guessed.</summary>
    ConfiguredAssigneeNotInDepartment,

    /// <summary>The ticket was assigned to the rule's primary assignee.</summary>
    AssignedToPrimary,

    /// <summary>The ticket was assigned to the next eligible employee in the department's Round Robin rotation.</summary>
    AssignedRoundRobin,

    /// <summary>Round Robin found no eligible active employee — the ticket stays unassigned, but the failure is recorded (audit + <see cref="WorkflowEventType.ManualAssignmentRequired"/>) and surfaced for a safe retry.</summary>
    NoEligibleEmployee,

    /// <summary>The ticket already had a CS Agent / owner; nothing was written.</summary>
    AlreadyAssigned,

    /// <summary>The ticket's previous CS Agent was kept (department transfer / reopen): the agent and the department are independent fields.</summary>
    PreservedCsAgent,

    /// <summary>The ticket is closed or resolved, so no assignment applies.</summary>
    NotApplicable,

    /// <summary>The ticket came back to the Call Center but its Original Human Agent cannot take it: it waits unassigned in the Call Center queue and no automatic path (rotation, reconciler, backfill, Genesys update) may assign it until an authorized person acts.</summary>
    HeldForManualAssignment
}

/// <summary>
/// Why the automation ran. The department is the primary assignment and the
/// employee only the secondary one, so the same rule evaluation happens both
/// when a ticket first gets its department and when a transfer moves it to a
/// new one — the audit trail must still say which of the two produced the
/// outcome.
/// </summary>
public enum AutoAssignmentTrigger
{
    /// <summary>The ticket was just created with its department and request type known.</summary>
    TicketCreated,

    /// <summary>A department transfer moved the ticket; the rule is re-evaluated against the NEW responsible department.</summary>
    DepartmentTransfer,

    /// <summary>A request type was classified onto an existing ticket that was already in the responsible department (so no transfer happened); the rule for that request type is evaluated.</summary>
    RequestTypeClassified,

    /// <summary>A Call Center user selected a category that belongs to another department; the ticket was routed there.</summary>
    CategoryRouted,

    /// <summary>A new ticket that needs a person entered the Call Center, the first human-handling department for every channel.</summary>
    CallCenterIntake,

    /// <summary>Authorized staff retried a failed CS Agent assignment.</summary>
    ManualRetry,

    /// <summary>The one-time batch backfill gave an existing active ticket its CS Agent.</summary>
    Backfill
}

/// <summary>The outcome plus what was assigned, for the caller's response/logging. <paramref name="TeamMemberEmployeeIds"/> carries the configured team members (excluding the primary) for a team rule.</summary>
public sealed record AutoAssignmentResult(
    AutoAssignmentOutcome Outcome,
    Guid? AssignedEmployeeId = null,
    IReadOnlyList<Guid>? TeamMemberEmployeeIds = null);

/// <summary>
/// Workflow/Automation phase 2 — executes the configured assignment rule
/// when a ticket is created with its department and request type known, so
/// the Call Center/CS agent never needs to know every department's staff:
/// Department + Request Type → rule → primary assignee, or the department
/// queue where no valid automatic target exists.
///
/// <para>
/// <b>System actions are recorded as system actions.</b> Every automatic
/// assignment writes its <c>TicketAssignments</c> row and audit entry with a
/// <c>null</c> acting employee — never as if the creating agent performed
/// the assignment — and names the rule it applied, so history reads
/// "automatically assigned by rule", distinguishable from any manual
/// (re)assignment.
/// </para>
///
/// <para>
/// <b>Fail-safe by construction.</b> A missing rule, an inactive rule, a
/// department whose settings disable assignment, or a configured assignee
/// who is no longer an active member of the department all degrade to the
/// department queue (unassigned, supervisor assigns manually) with the
/// reason audited. Ownership stays single: a team rule assigns its primary
/// as the one accountable owner; members are configuration the later
/// collaboration phase can surface.
/// </para>
/// </summary>
public sealed class TicketAutoAssignmentService(
    IRequestTypeAssignmentRuleRepository assignmentRuleRepository,
    IDepartmentWorkflowSettingsRepository departmentWorkflowSettingsRepository,
    IUserDepartmentAssignmentRepository userDepartmentAssignmentRepository,
    ITicketAssignmentRepository ticketAssignmentRepository,
    IAuditEntryWriter auditWriter,
    IAssignmentRotationRepository? rotationRepository = null,
    IDepartmentRepository? departmentRepository = null,
    IUserRoleReader? roleReader = null,
    IUserAccountManager? accountManager = null,
    IServiceIdentityRegistry? serviceIdentities = null,
    ITicketWorkflowEventRepository? workflowEventRepository = null,
    ITicketNoteRepository? ticketNoteRepository = null,
    TigerCS.Application.Modules.Collections.CollectionsOptions? collectionsOptions = null,
    IOutboxWriter? outboxWriter = null,
    ITicketAssignmentHistoryRepository? assignmentHistoryRepository = null)
{
    /// <summary>
    /// Notification events for every assignment made here (rule, Round Robin, preserved agent, confirmed handler, answering agent,
    /// self-assignment): the customer learns the ticket has an owner, the colleague learns it landed on their desk. Null when the host
    /// did not wire an Outbox writer (older fixtures) — then no event is recorded.
    /// </summary>
    private readonly TicketNotificationEvents? _notificationEvents = TicketNotificationEvents.CreateOrNull(outboxWriter, auditWriter);

    private Task NotifyAssignedAsync(
        Ticket ticket, Guid? previousOwnerEmployeeId, Guid assignedEmployeeId, Guid? actorEmployeeId, Guid correlationId, DateTime nowUtc,
        CancellationToken cancellationToken) =>
        _notificationEvents?.QueueAssignedAsync(ticket, previousOwnerEmployeeId, assignedEmployeeId, actorEmployeeId, correlationId, nowUtc, cancellationToken)
        ?? Task.CompletedTask;

    /// <summary>
    /// Runs inside the caller's creation transaction, after the ticket row
    /// exists (its real TicketId is needed for the assignment/audit rows) —
    /// the caller commits or rolls everything back together.
    /// </summary>
    public async Task<AutoAssignmentResult> ApplyAsync(
        Ticket ticket,
        DateTime nowUtc,
        Guid correlationId,
        AutoAssignmentTrigger trigger = AutoAssignmentTrigger.TicketCreated,
        CancellationToken cancellationToken = default)
    {
        if (ticket.RequestTypeId is not { } requestTypeId)
        {
            return new AutoAssignmentResult(AutoAssignmentOutcome.NoRequestType);
        }

        var settings = await departmentWorkflowSettingsRepository.GetByDepartmentIdAsync(
            ticket.CurrentDepartmentId, cancellationToken);
        if (settings is { AllowAssignment: false })
        {
            return await QueueAsync(ticket, AutoAssignmentOutcome.AssignmentDisabledForDepartment,
                "Department workflow settings disable assignment", trigger, correlationId, cancellationToken);
        }

        var rule = await assignmentRuleRepository.GetByRequestTypeIdAsync(requestTypeId, cancellationToken);
        if (rule is null || !rule.IsActive)
        {
            return await QueueAsync(ticket, AutoAssignmentOutcome.NoRuleConfigured,
                "No active assignment rule configured", trigger, correlationId, cancellationToken);
        }

        if (rule.Mode == AssignmentMode.RoundRobin)
        {
            return await AssignRoundRobinAsync(ticket, nowUtc, correlationId, trigger, rule.RequestTypeAssignmentRuleId, cancellationToken);
        }

        if (rule.Mode == AssignmentMode.DepartmentQueue || rule.PrimaryEmployeeId is not { } primaryEmployeeId)
        {
            return await QueueAsync(ticket, AutoAssignmentOutcome.DepartmentQueueByRule,
                $"Rule {rule.RequestTypeAssignmentRuleId} routes to the department queue", trigger, correlationId, cancellationToken);
        }

        // The configured target is re-validated at assignment time against
        // the ticket's actual current department — configuration that went
        // stale (the employee left the department) degrades to the queue.
        if (!await userDepartmentAssignmentRepository.ExistsAsync(primaryEmployeeId, ticket.CurrentDepartmentId, cancellationToken))
        {
            return await QueueAsync(ticket, AutoAssignmentOutcome.ConfiguredAssigneeNotInDepartment,
                $"Rule {rule.RequestTypeAssignmentRuleId}'s primary assignee is not an active member of department {ticket.CurrentDepartmentId}",
                trigger, correlationId, cancellationToken);
        }

        var previousOwnerEmployeeId = ticket.CurrentOwnerEmployeeId;
        ticket.AssignTo(primaryEmployeeId);

        var currentAssignment = await ticketAssignmentRepository.GetCurrentAsync(ticket.TicketId, cancellationToken);
        currentAssignment?.MarkSuperseded();

        // AssigningActorEmployeeId null = the system, not a person — the
        // same distinction the audit entry below makes with a null actor.
        await ticketAssignmentRepository.AddAsync(
            new TicketAssignment(ticket.TicketId, primaryEmployeeId, ticket.CurrentDepartmentId, nowUtc, assigningActorEmployeeId: null),
            cancellationToken);

        var teamMembers = rule.Mode == AssignmentMode.Team
            ? rule.Members.Select(m => m.EmployeeId).ToList()
            : null;

        await auditWriter.WriteAsync(
            actorEmployeeId: null,
            "AutoAssign", "Ticket", ticket.TicketId.ToString(),
            beforeValue: previousOwnerEmployeeId?.ToString(),
            afterValue: $"AssignedEmployeeId={primaryEmployeeId};DepartmentId={ticket.CurrentDepartmentId};Mode={rule.Mode};RuleId={rule.RequestTypeAssignmentRuleId};Trigger={trigger}"
                + (rule.TeamName is { } teamName ? $";Team={teamName}" : string.Empty),
            correlationId, cancellationToken);
        await NotifyAssignedAsync(ticket, previousOwnerEmployeeId, primaryEmployeeId, actorEmployeeId: null, correlationId, nowUtc, cancellationToken);

        return new AutoAssignmentResult(AutoAssignmentOutcome.AssignedToPrimary, primaryEmployeeId, teamMembers);
    }

    /// <summary>True when the employee is an active member of the Call Center department.</summary>
    public async Task<bool> IsCallCenterMemberAsync(Guid employeeId, CancellationToken cancellationToken = default) =>
        (await userDepartmentAssignmentRepository.GetByEmployeeIdAsync(employeeId, cancellationToken))
            .Any(a => string.Equals(a.Department?.Code, TeamPerformanceAppService.CallCenterDepartmentCode, StringComparison.OrdinalIgnoreCase));

    /// <summary>A Call Center user handing a ticket to a department they do not themselves belong to — the case round-robin routing serves (a member of that department self-assigns instead).</summary>
    public async Task<bool> IsCallCenterRoutingAsync(Guid employeeId, int departmentId, CancellationToken cancellationToken = default) =>
        await IsCallCenterMemberAsync(employeeId, cancellationToken)
        && !await userDepartmentAssignmentRepository.ExistsAsync(employeeId, departmentId, cancellationToken);

    /// <summary>The active Call Center department, or null when none is configured (callers then keep the pre-Call-Center-first behaviour and the gap is reported, never guessed).</summary>
    public async Task<Domain.Modules.IdentityAndAccess.Department?> ResolveCallCenterAsync(CancellationToken cancellationToken = default) =>
        departmentRepository is null
            ? null
            : (await departmentRepository.ListAsync(activeOnly: true, cancellationToken)).FirstOrDefault(d =>
                string.Equals(d.Code, TeamPerformanceAppService.CallCenterDepartmentCode, StringComparison.OrdinalIgnoreCase));

    /// <summary>True when the department is the Call Center.</summary>
    public async Task<bool> IsCallCenterDepartmentAsync(int departmentId, CancellationToken cancellationToken = default) =>
        departmentRepository is not null
        && await departmentRepository.GetByIdAsync(departmentId, cancellationToken) is { } department
        && string.Equals(department.Code, TeamPerformanceAppService.CallCenterDepartmentCode, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Call Center intake: the ticket (already sitting in the Call Center) is given to the next eligible Call Center agent in the
    /// persisted Round Robin rotation. Request-type rules are deliberately NOT consulted — they name employees of the destination
    /// department, and the Call Center agent's Transfer is what reaches them. A department whose settings disable assignment, or one
    /// with no eligible agent, leaves the ticket visibly unassigned in the Call Center queue (audited), never a guess.
    /// </summary>
    public async Task<AutoAssignmentResult> AssignCallCenterIntakeAsync(
        Ticket ticket, DateTime nowUtc, Guid correlationId, CancellationToken cancellationToken = default, string? requiredRole = null)
    {
        if (await IsHeldForManualAssignmentAsync(ticket, cancellationToken))
        {
            return new AutoAssignmentResult(AutoAssignmentOutcome.HeldForManualAssignment);
        }

        var settings = await departmentWorkflowSettingsRepository.GetByDepartmentIdAsync(ticket.CurrentDepartmentId, cancellationToken);
        if (settings is { AllowAssignment: false })
        {
            return await QueueAsync(ticket, AutoAssignmentOutcome.AssignmentDisabledForDepartment,
                "Call Center workflow settings disable assignment; Status=Unassigned", AutoAssignmentTrigger.CallCenterIntake, correlationId, cancellationToken,
                recordFailure: true, nowUtc);
        }

        return await AssignRoundRobinAsync(ticket, nowUtc, correlationId, AutoAssignmentTrigger.CallCenterIntake, ruleId: null, cancellationToken, requiredRole);
    }

    /// <summary>
    /// Call Center first for a ticket that ALREADY exists and has just been found to need a person (Genesys reporting
    /// <c>humanFollowUpRequired</c> after the ticket was created): the ticket moves to the Call Center (OriginatingDepartmentId, Request
    /// Type, status and SLA untouched) and ONE eligible Call Center agent is picked by Round Robin.
    ///
    /// <para>
    /// Returns false and changes nothing when the ticket is closed or resolved, already in the Call Center, owned by a person (only an
    /// automatic pre-assignment is released — <see cref="TicketOwnershipRules.IsAutomaticPreAssignmentAsync"/>), or no active Call Center
    /// exists. Idempotent: a second call finds the ticket already in the Call Center. Runs inside the caller's transaction.
    /// </para>
    /// </summary>
    public async Task<bool> RouteExistingTicketToCallCenterAsync(
        Ticket ticket, DateTime nowUtc, Guid correlationId, string reason, CancellationToken cancellationToken = default, string? requiredRole = null)
    {
        ArgumentNullException.ThrowIfNull(ticket);

        if (ticket.TicketStatus is TicketStatus.Resolved or TicketStatus.Closed
            || await ResolveCallCenterAsync(cancellationToken) is not { } callCenter
            || callCenter.DepartmentId == ticket.CurrentDepartmentId)
        {
            return false;
        }

        if (ticket.CurrentOwnerEmployeeId is not null
            && !await TicketOwnershipRules.IsAutomaticPreAssignmentAsync(ticketAssignmentRepository, ticket, cancellationToken, ticketNoteRepository))
        {
            return false;   // genuine human ownership is never overwritten
        }

        // Call Center is the FIRST stop, once. A ticket that was created in, or has already passed through, the Call Center and was then
        // transferred onward by a Call Center agent must not be pulled back by a later Genesys update (a transcript, the call ending, a
        // repeated agent-connected event) — that would undo the agent's Transfer and its destination assignment.
        if (ticket.OriginatingDepartmentId == callCenter.DepartmentId
            || (workflowEventRepository is not null
                && (await workflowEventRepository.ListByTicketIdAsync(ticket.TicketId, cancellationToken)).Any(e =>
                    e.EventType == WorkflowEventType.TicketTransferred && e.ToDepartmentId == callCenter.DepartmentId)))
        {
            return false;
        }

        var fromDepartmentId = ticket.CurrentDepartmentId;
        var releasedOwner = ticket.CurrentOwnerEmployeeId;
        if (await ticketAssignmentRepository.GetCurrentAsync(ticket.TicketId, cancellationToken) is { } current)
        {
            current.MarkSuperseded();
        }

        ticket.TransferToDepartment(callCenter.DepartmentId);   // clears the owner; OriginatingDepartmentId is write-once

        await auditWriter.WriteAsync(
            actorEmployeeId: null, "Transfer", "Ticket", ticket.TicketId.ToString(),
            beforeValue: $"DepartmentId={fromDepartmentId};AssignedEmployeeId={releasedOwner?.ToString() ?? "DepartmentQueue"}",
            afterValue: $"DepartmentId={callCenter.DepartmentId};Reason={reason}"
                + (releasedOwner is not null ? ";AutomaticPreAssignmentReleased=true" : string.Empty)
                + $";OriginatingDepartmentId={ticket.OriginatingDepartmentId} (unchanged)",
            correlationId, cancellationToken);

        if (workflowEventRepository is not null)
        {
            await workflowEventRepository.AddAsync(
                TicketWorkflowEvent.ForTransfer(
                    ticket.TicketId, nowUtc, actorEmployeeId: null, TicketTransferReason.SystemRouting,
                    fromDepartmentId, callCenter.DepartmentId, toEmployeeId: null, reason, correlationId),
                cancellationToken);
        }

        await AssignCallCenterIntakeAsync(ticket, nowUtc, correlationId, cancellationToken, requiredRole);
        return true;
    }

    /// <summary>
    /// Business rule: every active ticket has a CS Agent, whatever its department. After the department-specific rule (if any) has had
    /// its say, a ticket that still has no owner is given the next CS Agent of the ONE shared rotation — see
    /// <see cref="EnsureCsAgentAsync"/>. The department-queue outcomes (no rule, rule says queue, assignment disabled, stale assignee,
    /// nobody eligible in the department) no longer leave a ticket "Department Queue (Unassigned)".
    /// </summary>
    public Task<AutoAssignmentResult> ApplyRoundRobinFallbackAsync(
        Ticket ticket, AutoAssignmentResult configured, AutoAssignmentTrigger trigger, DateTime nowUtc, Guid correlationId,
        CancellationToken cancellationToken = default) =>
        configured.Outcome is AutoAssignmentOutcome.AssignedToPrimary or AutoAssignmentOutcome.AssignedRoundRobin
            ? Task.FromResult(configured)
            : EnsureCsAgentAsync(ticket, preferredOwnerEmployeeId: null, trigger, nowUtc, correlationId, cancellationToken);

    /// <summary>
    /// The shared rotation's current pool for read-only previews: the Call Center (null when none is configured), the eligible CS Agents in
    /// rotation order, and the persisted cursor (last agent dealt). Nothing is locked or written.
    /// </summary>
    public async Task<(Domain.Modules.IdentityAndAccess.Department? CallCenter, IReadOnlyList<Guid> Pool, Guid? Cursor)> GetSharedPoolAsync(
        CancellationToken cancellationToken = default)
    {
        if (await ResolveCallCenterAsync(cancellationToken) is not { } callCenter)
        {
            return (null, [], null);
        }

        var members = await userDepartmentAssignmentRepository.GetByDepartmentIdAsync(callCenter.DepartmentId, activeEmployeesOnly: true, cancellationToken);
        var pool = (await FilterEligibleAsync(members.Select(m => m.EmployeeId).Distinct(), callCenterOnly: true, cancellationToken)).Order().ToList();
        var cursor = rotationRepository is null ? null : await rotationRepository.PeekLastAssignedAsync(callCenter.DepartmentId, cancellationToken);
        return (callCenter, pool, cursor);
    }

    /// <summary>
    /// Re-assigns <paramref name="previousOwnerEmployeeId"/> (the CS Agent a reopened ticket had) when that person is still an eligible
    /// Call Center CS Agent; null when there is nothing to restore, so the caller moves on to the rule / the shared rotation.
    /// </summary>
    public async Task<AutoAssignmentResult?> RestorePreviousCsAgentAsync(
        Ticket ticket, Guid? previousOwnerEmployeeId, AutoAssignmentTrigger trigger, DateTime nowUtc, Guid correlationId,
        CancellationToken cancellationToken = default)
    {
        if (previousOwnerEmployeeId is not { } preferred
            || ticket.CurrentOwnerEmployeeId is not null
            || ticket.TicketStatus is TicketStatus.Resolved or TicketStatus.Closed
            || await ResolveCallCenterAsync(cancellationToken) is not { } callCenter
            || await CheckAnsweringAgentAsync(preferred, callCenter.DepartmentId, cancellationToken) != AnsweringAgentIssue.None)
        {
            return null;
        }

        ticket.AssignTo(preferred);
        (await ticketAssignmentRepository.GetCurrentAsync(ticket.TicketId, cancellationToken))?.MarkSuperseded();
        await ticketAssignmentRepository.AddAsync(
            new TicketAssignment(ticket.TicketId, preferred, ticket.CurrentDepartmentId, nowUtc, assigningActorEmployeeId: null),
            cancellationToken);
        await auditWriter.WriteAsync(
            actorEmployeeId: null, "AutoAssign", "Ticket", ticket.TicketId.ToString(),
            beforeValue: "Unassigned",
            afterValue: $"AssignedEmployeeId={preferred};DepartmentId={ticket.CurrentDepartmentId};Mode=PreservedCsAgent;Trigger={trigger}",
            correlationId, cancellationToken);
        await NotifyAssignedAsync(ticket, previousOwnerEmployeeId: null, preferred, actorEmployeeId: null, correlationId, nowUtc, cancellationToken);
        return new AutoAssignmentResult(AutoAssignmentOutcome.PreservedCsAgent, preferred);
    }

    /// <summary>
    /// <b>The single guarantee:</b> an active, human-handled ticket ends every routing path with a CS Agent.
    /// <list type="number">
    /// <item>Closed / Resolved ticket: nothing to do (AI-closed tickets never need an agent).</item>
    /// <item>Already owned: nothing written (no duplicate assignment rows or audit events).</item>
    /// <item><paramref name="preferredOwnerEmployeeId"/> (the previous CS Agent of a reopened ticket) is still an eligible CS Agent: kept.</item>
    /// <item>Otherwise the next eligible CS Agent of the shared rotation (one persisted cursor for all channels and departments).</item>
    /// <item>Nobody eligible: the failure is recorded and flagged for authorized staff — never silent.</item>
    /// </list>
    /// Runs inside the caller's transaction.
    /// </summary>
    public async Task<AutoAssignmentResult> EnsureCsAgentAsync(
        Ticket ticket, Guid? preferredOwnerEmployeeId, AutoAssignmentTrigger trigger, DateTime nowUtc, Guid correlationId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(ticket);

        if (ticket.TicketStatus is TicketStatus.Resolved or TicketStatus.Closed)
        {
            return new AutoAssignmentResult(AutoAssignmentOutcome.NotApplicable);
        }

        if (ticket.CurrentOwnerEmployeeId is { } existing)
        {
            return new AutoAssignmentResult(AutoAssignmentOutcome.AlreadyAssigned, existing);
        }

        // A Collections ticket is owned by its tower's collector or waits in the Collections queue for a person to assign it; the shared CS Agent rotation
        // (new tickets, the backfill, the retry) never gives it to a Call Center agent.
        if (collectionsOptions is not null
            && departmentRepository is not null
            && await departmentRepository.GetByIdAsync(ticket.CurrentDepartmentId, cancellationToken) is { } currentDepartment
            && string.Equals(currentDepartment.Code, collectionsOptions.CollectionsDepartmentCode, StringComparison.OrdinalIgnoreCase))
        {
            return new AutoAssignmentResult(AutoAssignmentOutcome.NotApplicable);
        }

        var callCenter = await ResolveCallCenterAsync(cancellationToken);

        // An exceptional return (Original Human Agent ineligible) is assigned only by an explicit authorized action: the authorized retry
        // (ManualRetry) or a manual assignment. Every automatic trigger leaves it in the Call Center queue.
        if (trigger != AutoAssignmentTrigger.ManualRetry && await IsHeldForManualAssignmentAsync(ticket, cancellationToken))
        {
            return new AutoAssignmentResult(AutoAssignmentOutcome.HeldForManualAssignment);
        }

        // A ticket waiting in the Call Center that has an Original Human Agent goes back to them (when still eligible) before the shared
        // rotation is consulted, so a retry, the reconciler or the backfill never deals it to someone else.
        var preferred = preferredOwnerEmployeeId;
        if (preferred is null
            && callCenter is not null
            && ticket.CurrentDepartmentId == callCenter.DepartmentId
            && ticket.ActiveOriginalHumanAgentId is { } originalAgent
            && await CheckOriginalAgentAsync(originalAgent, callCenter.DepartmentId, cancellationToken) == AnsweringAgentIssue.None)
        {
            preferred = originalAgent;
        }

        if (await RestorePreviousCsAgentAsync(ticket, preferred, trigger, nowUtc, correlationId, cancellationToken) is { } restored)
        {
            return restored;
        }

        if (callCenter is null)
        {
            return await QueueAsync(ticket, AutoAssignmentOutcome.NoEligibleEmployee,
                "Shared CS Agent rotation: no active Call Center department is configured; Status=Unassigned",
                trigger, correlationId, cancellationToken, recordFailure: true, nowUtc);
        }

        var settings = await departmentWorkflowSettingsRepository.GetByDepartmentIdAsync(callCenter.DepartmentId, cancellationToken);
        if (settings is { AllowAssignment: false })
        {
            return await QueueAsync(ticket, AutoAssignmentOutcome.AssignmentDisabledForDepartment,
                "Call Center workflow settings disable assignment; Status=Unassigned",
                trigger, correlationId, cancellationToken, recordFailure: true, nowUtc);
        }

        return await AssignRoundRobinAsync(
            ticket, nowUtc, correlationId, trigger, ruleId: null, cancellationToken, poolDepartmentId: callCenter.DepartmentId);
    }

    /// <summary>Call Center category routing (kept for existing callers): <see cref="ApplyRoundRobinFallbackAsync"/> with the category trigger.</summary>
    public Task<AutoAssignmentResult> ApplyCategoryRoutingAsync(
        Ticket ticket, AutoAssignmentResult configured, DateTime nowUtc, Guid correlationId,
        AutoAssignmentTrigger trigger = AutoAssignmentTrigger.CategoryRouted, CancellationToken cancellationToken = default) =>
        ApplyRoundRobinFallbackAsync(ticket, configured, trigger, nowUtc, correlationId, cancellationToken);

    /// <summary>
    /// Assigns to the next eligible active member of the ticket's CURRENT department. The persisted cursor is row-locked for the
    /// caller's transaction, so concurrent assignments serialize and never pick the same slot; no eligible member (or no rotation
    /// store) leaves the ticket in the department queue with the reason audited — never a guess.
    /// </summary>
    private async Task<AutoAssignmentResult> AssignRoundRobinAsync(
        Ticket ticket, DateTime nowUtc, Guid correlationId, AutoAssignmentTrigger trigger, int? ruleId, CancellationToken cancellationToken,
        string? requiredRole = null, int? poolDepartmentId = null)
    {
        // The pool (and its persisted cursor) is the ticket's CURRENT department unless the caller names another: the shared CS Agent
        // rotation names the Call Center, so the pool no longer depends on the ticket's department.
        var poolDepartment = poolDepartmentId ?? ticket.CurrentDepartmentId;
        var ruleText = ruleId is { } id ? $"Rule {id}" : poolDepartmentId is not null ? "Shared CS Agent rotation" : trigger == AutoAssignmentTrigger.CallCenterIntake ? "Call Center intake" : "Round Robin default";
        if (rotationRepository is null)
        {
            return await QueueAsync(ticket, AutoAssignmentOutcome.NoEligibleEmployee,
                $"{ruleText}: Round Robin is not available", trigger, correlationId, cancellationToken, recordFailure: true, nowUtc);
        }

        // The pool department must exist and be active.
        if (departmentRepository is not null
            && await departmentRepository.GetByIdAsync(poolDepartment, cancellationToken) is not { IsActive: true })
        {
            return await QueueAsync(ticket, AutoAssignmentOutcome.NoEligibleEmployee,
                $"{ruleText}: department {poolDepartment} is missing or inactive; Status=Unassigned",
                trigger, correlationId, cancellationToken, recordFailure: true, nowUtc);
        }

        var members = await userDepartmentAssignmentRepository.GetByDepartmentIdAsync(
            poolDepartment, activeEmployeesOnly: true, cancellationToken);
        // Call Center (department code CC) hands automatic work to CS Agents ONLY; every other department keeps the wider operational set.
        var callCenterOnly = await IsCallCenterDepartmentAsync(poolDepartment, cancellationToken);
        var eligible = await FilterEligibleAsync(members.Select(m => m.EmployeeId).Distinct(), callCenterOnly, cancellationToken, requiredRole);
        if (eligible.Count == 0)
        {
            return await QueueAsync(ticket, AutoAssignmentOutcome.NoEligibleEmployee,
                $"{ruleText}: no eligible active CS Agent in department {poolDepartment}; Status=Unassigned",
                trigger, correlationId, cancellationToken, recordFailure: true, nowUtc);
        }

        var rotation = await rotationRepository.GetForUpdateAsync(poolDepartment, nowUtc, cancellationToken);
        var next = DepartmentAssignmentRotation.PickNext(rotation.LastAssignedEmployeeId, eligible)!.Value;
        rotation.Advance(next, nowUtc);

        var previousOwnerEmployeeId = ticket.CurrentOwnerEmployeeId;
        ticket.AssignTo(next);
        if (callCenterOnly)
        {
            // The pool is the Call Center and FilterEligible admitted only CS Agents: the first one dealt a ticket is its Original Human Agent.
            await RecordOriginalHumanAgentAsync(ticket, next, "AutoAssigned", correlationId, cancellationToken);
        }

        var currentAssignment = await ticketAssignmentRepository.GetCurrentAsync(ticket.TicketId, cancellationToken);
        currentAssignment?.MarkSuperseded();
        await ticketAssignmentRepository.AddAsync(
            new TicketAssignment(ticket.TicketId, next, ticket.CurrentDepartmentId, nowUtc, assigningActorEmployeeId: null),
            cancellationToken);

        await auditWriter.WriteAsync(
            actorEmployeeId: null,
            "AutoAssign", "Ticket", ticket.TicketId.ToString(),
            beforeValue: previousOwnerEmployeeId?.ToString(),
            afterValue: $"AssignedEmployeeId={next};DepartmentId={ticket.CurrentDepartmentId};Mode={AssignmentMode.RoundRobin};"
                + $"{(ruleId is { } r ? $"RuleId={r};" : string.Empty)}{(poolDepartmentId is not null ? $"Pool=SharedCsAgents;PoolDepartmentId={poolDepartment};" : string.Empty)}Trigger={trigger}",
            correlationId, cancellationToken);
        await NotifyAssignedAsync(ticket, previousOwnerEmployeeId, next, actorEmployeeId: null, correlationId, nowUtc, cancellationToken);

        return new AutoAssignmentResult(AutoAssignmentOutcome.AssignedRoundRobin, next);
    }

    /// <summary>
    /// Records the Original Human Agent (write-once) when the employee is an eligible Call Center CS Agent
    /// (<see cref="CheckAnsweringAgentAsync"/>: not a service identity, active, CS Agent role, Call Center member). A ticket created by Genesys AI
    /// or an integration therefore starts with none, and the first eligible agent to be dealt, accept or answer it becomes the original.
    /// Returns true only when this call set it. Never overwrites, never throws for an ineligible person.
    /// </summary>
    public async Task<bool> RecordOriginalHumanAgentAsync(
        Ticket ticket, Guid employeeId, string source, Guid correlationId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(ticket);

        if (!ticket.OriginalAgentWorkflowEnabled
            || ticket.OriginalHumanAgentId is not null
            || await ResolveCallCenterAsync(cancellationToken) is not { } callCenter
            || await CheckOriginalAgentAsync(employeeId, callCenter.DepartmentId, cancellationToken) != AnsweringAgentIssue.None
            || !ticket.RecordOriginalHumanAgent(employeeId))
        {
            return false;
        }

        await auditWriter.WriteAsync(
            actorEmployeeId: null, "SetOriginalHumanAgent", "Ticket", ticket.TicketId.ToString(),
            beforeValue: null, afterValue: $"OriginalHumanAgentId={employeeId};Source={source}", correlationId, cancellationToken);
        return true;
    }

    /// <summary>
    /// A person created the ticket by hand and it is now in the Call Center: if they are an eligible Call Center CS Agent they own it (their own act,
    /// like a self-assignment) instead of the next agent of the rotation. Returns false, writing nothing, when the ticket already has an owner or the
    /// creator cannot take it, so the caller applies the ordinary Call Center intake.
    /// </summary>
    public async Task<bool> TryAssignManualCreatorInCallCenterAsync(
        Ticket ticket, Guid creatorEmployeeId, DateTime nowUtc, Guid correlationId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(ticket);

        if (ticket.CurrentOwnerEmployeeId is not null
            || ticket.TicketStatus is TicketStatus.Resolved or TicketStatus.Closed
            || await ResolveCallCenterAsync(cancellationToken) is not { } callCenter
            || ticket.CurrentDepartmentId != callCenter.DepartmentId
            || await departmentWorkflowSettingsRepository.GetByDepartmentIdAsync(callCenter.DepartmentId, cancellationToken) is { AllowAssignment: false }
            || await CheckOriginalAgentAsync(creatorEmployeeId, callCenter.DepartmentId, cancellationToken) != AnsweringAgentIssue.None)
        {
            return false;
        }

        ticket.AssignTo(creatorEmployeeId);
        await ticketAssignmentRepository.AddAsync(
            new TicketAssignment(ticket.TicketId, creatorEmployeeId, callCenter.DepartmentId, nowUtc, assigningActorEmployeeId: creatorEmployeeId),
            cancellationToken);
        await auditWriter.WriteAsync(
            creatorEmployeeId, "SelfAssign", "Ticket", ticket.TicketId.ToString(),
            beforeValue: "DepartmentQueue",
            afterValue: $"AssignedEmployeeId={creatorEmployeeId};DepartmentId={callCenter.DepartmentId};Trigger={AutoAssignmentTrigger.TicketCreated};Reason=Created by this agent;OriginalHumanAgentId={creatorEmployeeId}",
            correlationId, cancellationToken);
        await NotifyAssignedAsync(ticket, previousOwnerEmployeeId: null, creatorEmployeeId, actorEmployeeId: creatorEmployeeId, correlationId, nowUtc, cancellationToken);
        return true;
    }

    /// <summary>Marker inside the Manual Assignment Required event note that records a Call Center return whose Original Human Agent was ineligible.</summary>
    public const string OriginalAgentHoldMarker = "OriginalAgentUnavailable";

    /// <summary>
    /// Whether the Original Human Agent may take Call Center work NOW. Everything <see cref="CheckAnsweringAgentAsync"/> checks (not a service identity,
    /// account not locked out, Call Center member, CS Agent role), plus the one thing it does not: a DEACTIVATED employee keeps their department
    /// membership, so deactivation is checked against the department's active members. Used only for the Original Human Agent paths.
    /// </summary>
    private async Task<AnsweringAgentIssue> CheckOriginalAgentAsync(Guid employeeId, int callCenterDepartmentId, CancellationToken cancellationToken)
    {
        var issue = await CheckAnsweringAgentAsync(employeeId, callCenterDepartmentId, cancellationToken);
        if (issue != AnsweringAgentIssue.None)
        {
            return issue;
        }

        var active = await userDepartmentAssignmentRepository.GetByDepartmentIdAsync(callCenterDepartmentId, activeEmployeesOnly: true, cancellationToken);
        return active.Any(a => a.EmployeeId == employeeId) ? AnsweringAgentIssue.None : AnsweringAgentIssue.Inactive;
    }

    /// <summary>
    /// True while an unowned ticket is held for an explicit, authorized assignment: its last Call Center return found the Original Human Agent
    /// ineligible (<see cref="OriginalAgentHoldMarker"/>) and nobody has been assigned since. Automatic paths (rotation, intake, reconciler, backfill,
    /// Genesys update) must not assign it; a person's assignment, a claim or the authorized retry clears it by assigning (the hold is then moot).
    /// </summary>
    public async Task<bool> IsHeldForManualAssignmentAsync(Ticket ticket, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(ticket);

        if (ticket.CurrentOwnerEmployeeId is not null || ticket.ActiveOriginalHumanAgentId is null || workflowEventRepository is null)
        {
            return false;
        }

        var hold = (await workflowEventRepository.ListByTicketIdAsync(ticket.TicketId, cancellationToken))
            .Where(e => e.EventType == WorkflowEventType.ManualAssignmentRequired
                && e.Note is not null && e.Note.Contains(OriginalAgentHoldMarker, StringComparison.Ordinal))
            .OrderByDescending(e => e.OccurredAtUtc)
            .FirstOrDefault();
        if (hold is null)
        {
            return false;
        }

        return !(await ticketAssignmentRepository.ListByTicketIdAsync(ticket.TicketId, cancellationToken)).Any(a => a.AssignedAtUtc > hold.OccurredAtUtc);
    }

    /// <summary>How <see cref="ReturnToOriginalHumanAgentAsync"/> ended.</summary>
    public enum OriginalAgentReturn
    {
        /// <summary>The ticket has no Original Human Agent (e.g. an AI ticket nobody has owned): nothing was changed, the normal Call Center assignment applies.</summary>
        NoOriginalAgent,

        /// <summary>The ticket now belongs to its Original Human Agent.</summary>
        Assigned,

        /// <summary>The original agent already owned it: nothing written.</summary>
        AlreadyOwned,

        /// <summary>The original agent is inactive / ineligible: any owner carried over from the other department was released and the reason audited; the Call Center queue (shared rotation) applies.</summary>
        OriginalAgentIneligible
    }

    /// <summary>
    /// A ticket has just been transferred BACK to the Call Center: give it to its Original Human Agent when that person is still an active,
    /// eligible Call Center CS Agent — never to the general queue / rotation. A carried-over owner from the other department (a collector) is
    /// replaced; its assignment row is superseded, never deleted. When the original agent is no longer eligible the carried-over owner is
    /// released, the reason (<c>Reason=OriginalAgentIneligible(...)</c>) is audited, and the caller falls back to the Call Center queue.
    /// Original Human Agent itself is never modified. Runs inside the caller's transaction.
    /// </summary>
    public async Task<(OriginalAgentReturn Outcome, Guid? AssignedEmployeeId)> ReturnToOriginalHumanAgentAsync(
        Ticket ticket, Guid? actorEmployeeId, DateTime nowUtc, Guid correlationId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(ticket);

        if (ticket.ActiveOriginalHumanAgentId is not { } original
            || ticket.TicketStatus is TicketStatus.Resolved or TicketStatus.Closed
            || await ResolveCallCenterAsync(cancellationToken) is not { } callCenter
            || ticket.CurrentDepartmentId != callCenter.DepartmentId)
        {
            return (OriginalAgentReturn.NoOriginalAgent, null);
        }

        var previousOwner = ticket.CurrentOwnerEmployeeId;
        if (previousOwner == original)
        {
            return (OriginalAgentReturn.AlreadyOwned, original);
        }

        var issue = await CheckOriginalAgentAsync(original, callCenter.DepartmentId, cancellationToken);
        var current = await ticketAssignmentRepository.GetCurrentAsync(ticket.TicketId, cancellationToken);
        if (issue != AnsweringAgentIssue.None)
        {
            // Exceptional return: the ticket waits UNASSIGNED in the Call Center queue (no rotation is run here) until an authorized person assigns it.
            // The owner carried over from the other department is released (its row superseded, never deleted), the reason is audited, and a
            // Manual Assignment Required event carrying OriginalAgentHoldMarker suspends every automatic assignment path for this ticket.
            if (previousOwner is not null)
            {
                current?.MarkSuperseded();
                ticket.ReleaseOwner();
            }

            await auditWriter.WriteAsync(
                actorEmployeeId, "ReturnToCallCenterQueue", "Ticket", ticket.TicketId.ToString(),
                beforeValue: previousOwner?.ToString() ?? "DepartmentQueue",
                afterValue: $"DepartmentQueue;DepartmentId={callCenter.DepartmentId};OriginalHumanAgentId={original};Reason=OriginalAgentIneligible({issue});"
                    + "AutomaticAssignmentSuspended=true;RequiresAuthorizedAssignment=true",
                correlationId, cancellationToken);

            if (workflowEventRepository is not null)
            {
                await workflowEventRepository.AddAsync(
                    new TicketWorkflowEvent(
                        ticket.TicketId, WorkflowEventType.ManualAssignmentRequired, nowUtc, actorEmployeeId,
                        ticketApprovalId: null,
                        $"Returned to the Call Center but its Original Human Agent cannot take it ({Describe(issue)}). Left in the Call Center queue; automatic "
                        + $"assignment is suspended until an authorized person assigns it. [{OriginalAgentHoldMarker};OriginalHumanAgentId={original};Issue={issue}]",
                        correlationId),
                    cancellationToken);
            }

            return (OriginalAgentReturn.OriginalAgentIneligible, null);
        }

        current?.MarkSuperseded();
        ticket.AssignTo(original);
        await ticketAssignmentRepository.AddAsync(
            new TicketAssignment(ticket.TicketId, original, callCenter.DepartmentId, nowUtc, assigningActorEmployeeId: null),
            cancellationToken);
        await auditWriter.WriteAsync(
            actorEmployeeId, "AutoAssign", "Ticket", ticket.TicketId.ToString(),
            beforeValue: previousOwner?.ToString() ?? "DepartmentQueue",
            afterValue: $"AssignedEmployeeId={original};DepartmentId={callCenter.DepartmentId};Mode=ReturnToOriginalHumanAgent;OriginalHumanAgentId={original};Trigger={AutoAssignmentTrigger.DepartmentTransfer}",
            correlationId, cancellationToken);
        await NotifyAssignedAsync(ticket, previousOwner, original, actorEmployeeId, correlationId, nowUtc, cancellationToken);
        return (OriginalAgentReturn.Assigned, original);
    }

    /// <summary>The roles that may be handed work automatically: only <see cref="Roles.CsAgent"/> in the Call Center, the operational set elsewhere.</summary>
    private static IReadOnlyCollection<string> RoleSetFor(bool callCenter) => callCenter ? TicketRoleSets.CallCenterAutoAssignable : TicketRoleSets.RoundRobinEligible;

    /// <summary>
    /// Narrows a department's active members to who may be handed work by rotation: not a service identity (the Genesys integration
    /// account), not locked out, and holding at least one operational role (<see cref="TicketRoleSets.RoundRobinEligible"/>) — so
    /// read-only users, supervisors/managers and administrators are skipped. A lookup that cannot answer excludes the person: rotation
    /// never guesses. (The three readers are optional only so unit tests can build the service bare; the composition root supplies all.)
    /// </summary>
    private async Task<List<Guid>> FilterEligibleAsync(IEnumerable<Guid> candidateIds, bool callCenterOnly, CancellationToken cancellationToken, string? requiredRole = null)
    {
        var eligible = new List<Guid>();
        foreach (var id in candidateIds)
        {
            if (serviceIdentities?.IsServiceIdentity(id) == true)
            {
                continue;
            }

            if (accountManager is not null
                && await accountManager.GetAccountAsync(id, cancellationToken) is not { IsLockedOut: false })
            {
                continue;
            }

            if (roleReader is not null)
            {
                var roles = await roleReader.GetRolesAsync(id, cancellationToken);
                if (Roles.IsReadOnlyCaller(roles) || !roles.Any(RoleSetFor(callCenterOnly).Contains))
                {
                    continue;
                }

                // Optional narrowing (the reconciler deals only to CS Agents, the role that can Transfer as owner).
                if (requiredRole is not null && !roles.Contains(requiredRole, StringComparer.Ordinal))
                {
                    continue;
                }
            }
            else if (requiredRole is not null)
            {
                continue;   // a role that cannot be verified is not assumed
            }

            eligible.Add(id);
        }

        return eligible;
    }

    // ------------------------------------------------------------------------------------------------------------------------------
    // Direct assignment to the agent who ANSWERED a Genesys interaction
    // ------------------------------------------------------------------------------------------------------------------------------

    /// <summary>
    /// Whether the person Genesys reported as having answered can be given a Call Center ticket: not a service identity, account not locked
    /// out, the CS Agent role (and not read-only), and a member of the Call Center. Mapped-and-active is established earlier by the Genesys
    /// resolver (<see cref="AnsweringAgentIssue.NotMapped"/> / <see cref="AnsweringAgentIssue.Inactive"/>). A lookup that cannot answer
    /// counts AGAINST the person — the ticket then waits in the queue for a human to assign; it is never guessed onto somebody.
    /// </summary>
    public async Task<AnsweringAgentIssue> CheckAnsweringAgentAsync(
        Guid employeeId, int callCenterDepartmentId, CancellationToken cancellationToken = default)
    {
        if (serviceIdentities?.IsServiceIdentity(employeeId) == true)
        {
            return AnsweringAgentIssue.ServiceAccount;
        }

        if (accountManager is not null)
        {
            var account = await accountManager.GetAccountAsync(employeeId, cancellationToken);
            if (account is null)
            {
                return AnsweringAgentIssue.Inactive;
            }

            if (account.IsLockedOut)
            {
                return AnsweringAgentIssue.LockedOut;
            }
        }

        if (!await userDepartmentAssignmentRepository.ExistsAsync(employeeId, callCenterDepartmentId, cancellationToken))
        {
            return AnsweringAgentIssue.NotCallCenterMember;
        }

        if (roleReader is not null)
        {
            var roles = await roleReader.GetRolesAsync(employeeId, cancellationToken);
            if (Roles.IsReadOnlyCaller(roles))
            {
                return AnsweringAgentIssue.NoOperationalRole;
            }

            // The Call Center gives tickets to CS Agents only - the confirmed answering agent included.
            if (!roles.Any(TicketRoleSets.CallCenterAutoAssignable.Contains))
            {
                return AnsweringAgentIssue.NotCsAgent;
            }
        }

        return AnsweringAgentIssue.None;
    }

    /// <summary>
    /// <b>The answering agent owns the ticket.</b> A mapped, eligible Call Center agent whom Genesys reported as CONNECTED to the interaction
    /// (answered / accepted — the caller must never pass a ringing, offered or queue-only agent) is given the ticket directly, in the
    /// Call Center. Round Robin is NOT consulted and its cursor is not advanced: the person who picked up the phone is the owner, not the
    /// next name in the rotation. Round Robin remains only for a human handoff nobody has accepted yet.
    ///
    /// <list type="bullet">
    /// <item><b>One owner, never overwritten.</b> A person who already owns the ticket (manual assignment, self-assignment, an earlier answer)
    /// keeps it. Only an AUTOMATIC pre-assignment (Round Robin / rule, still Open, no human response — <see cref="TicketOwnershipRules"/>) yields
    /// to the agent who actually answered; its assignment row is superseded, never deleted. The same agent reported twice writes nothing.</item>
    /// <item><b>The assignment is the agent's own act.</b> The row and the audit entry carry the answering agent as actor (like a
    /// self-assignment), so the result is human ownership and a later Genesys event can never mistake it for an automatic pre-assignment.</item>
    /// <item><b>Unusable answering agent</b> (unmapped, deactivated, locked out, service account, not a Call Center member, no operational
    /// role): the ticket is left in the Call Center queue — a stale automatic pre-assignment is released so it is not left with somebody who did
    /// not answer — and a <see cref="WorkflowEventType.ManualAssignmentRequired"/> event plus an audit entry flag it for a supervisor. Written
    /// once per (ticket, agent, reason). The ticket is never given to anyone else.</item>
    /// <item><b>Once a person has transferred the ticket out, Genesys no longer moves it.</b> Any transfer that is not system routing makes this
    /// a no-op, so a late "agent connected" update cannot pull the ticket back to the Call Center or take it from its new owner.</item>
    /// <item>Status, SLA, priority, request type and every existing history row are untouched; only the department (when it was elsewhere) and
    /// the owner move, each with its audit entry and a typed event.</item>
    /// </list>
    /// Runs inside the caller's transaction; the caller saves and handles a concurrency conflict (<see cref="AnswerAssignmentOutcome.ConcurrencyConflict"/>).
    /// <c>routeToCallCenterIfAgentUnavailable</c>: when the answering agent is unusable and the ticket is NOT already in the Call Center, true moves it
    /// there to be flagged (a human interaction is on it); false leaves it exactly where it is, writing nothing (nothing says a person is handling it).
    /// </summary>
    public async Task<AnswerAssignmentResult> ApplyAnsweringAgentAsync(
        Ticket ticket, AnsweringAgent agent, string source, DateTime nowUtc, Guid correlationId, Guid? reportedByEmployeeId,
        bool routeToCallCenterIfAgentUnavailable, CancellationToken cancellationToken = default,
        bool firstResponseIsFromTheAnsweringAgent = false, GenesysAnswerEvent? genesysEvent = null)
    {
        ArgumentNullException.ThrowIfNull(ticket);
        ArgumentNullException.ThrowIfNull(agent);

        if (ticket.TicketStatus is TicketStatus.Closed or TicketStatus.Resolved)
        {
            return new AnswerAssignmentResult(AnswerAssignmentOutcome.NotApplicable, Detail: "The ticket is closed or resolved.");
        }

        var callCenter = await ResolveCallCenterAsync(cancellationToken);
        if (callCenter is null)
        {
            return new AnswerAssignmentResult(AnswerAssignmentOutcome.NotApplicable, Detail: "No active Call Center department.");
        }

        // A person moved it on (Transfer / escalation), or it already passed through the Call Center and left it: that move and the new owner
        // stand. A late Genesys event must never undo them. (Routing INTO the Call Center by the system does not count as a person's transfer.)
        if (await HasLeftOrBeenTransferredByPersonAsync(ticket, callCenter.DepartmentId, cancellationToken))
        {
            return new AnswerAssignmentResult(AnswerAssignmentOutcome.NotApplicable, Detail: "A person transferred the ticket; it is left as they placed it.");
        }

        // Genesys history of this ticket (empty without a history store or a Genesys event), read once.
        var genesysHistory = genesysEvent is not null && assignmentHistoryRepository is not null
            ? (await assignmentHistoryRepository.ListByTicketIdAsync(ticket.TicketId, cancellationToken)).Where(h => h.Source is not null).ToList()
            : [];
        var eventAtUtc = genesysEvent?.EventAtUtc is { } reported && reported < nowUtc ? reported : nowUtc;

        // An event older than (or as old as) the last human assignment Genesys already made is stale or out of order: it must never overwrite a
        // newer valid assignment. The same agent again is a duplicate and is answered as AlreadyOwned below, so only a DIFFERENT agent is stale.
        if (genesysEvent is not null
            && agent.EmployeeId != ticket.CurrentOwnerEmployeeId
            && genesysHistory.Where(h => h.Source is TicketAssignmentSource.GenesysDirectCall or TicketAssignmentSource.GenesysCallTransfer)
                .Select(h => h.SourceEventAtUtc ?? h.ChangedAtUtc).DefaultIfEmpty().Max() is var lastHumanEventAt
            && lastHumanEventAt != default
            && eventAtUtc <= lastHumanEventAt)
        {
            return new AnswerAssignmentResult(AnswerAssignmentOutcome.NotApplicable, Detail: "A newer Genesys assignment already exists; this older or duplicate event is ignored.");
        }

        AnsweringAgentIssue? computedIssue = null;
        async Task<AnsweringAgentIssue> IssueAsync() => computedIssue ??=
            agent.Issue != AnsweringAgentIssue.None || agent.EmployeeId is null
                ? (agent.Issue != AnsweringAgentIssue.None ? agent.Issue : AnsweringAgentIssue.NotMapped)
                : await CheckAnsweringAgentAsync(agent.EmployeeId.Value, callCenter.DepartmentId, cancellationToken);

        // Ownership first: never move or flag a ticket somebody already owns.
        var previousOwner = ticket.CurrentOwnerEmployeeId;
        if (previousOwner is not null)
        {
            if (agent.EmployeeId == previousOwner)
            {
                return new AnswerAssignmentResult(AnswerAssignmentOutcome.AlreadyOwnedByAnsweringAgent, previousOwner);
            }

            if (!await TicketOwnershipRules.IsAutomaticPreAssignmentAsync(ticketAssignmentRepository, ticket, cancellationToken, ticketNoteRepository, firstResponseIsFromTheAnsweringAgent)
                && !(genesysEvent is { AllowOwnerTransfer: true }
                    && await IssueAsync() == AnsweringAgentIssue.None
                    && await OwnerWasSetByGenesysAsync(ticket, previousOwner.Value, genesysHistory, cancellationToken)))
            {
                // A person's ownership (manual / supervisor assignment, Take Ownership) stands. Only an owner Genesys itself set may be moved
                // by the next human agent Genesys connects (a call transfer), and only to an eligible agent.
                return new AnswerAssignmentResult(AnswerAssignmentOutcome.HumanOwnerKept, previousOwner);
            }
        }

        var issue = await IssueAsync();

        if (issue != AnsweringAgentIssue.None && ticket.CurrentDepartmentId != callCenter.DepartmentId && !routeToCallCenterIfAgentUnavailable)
        {
            return new AnswerAssignmentResult(
                AnswerAssignmentOutcome.NotApplicable, Issue: issue,
                Detail: $"Answering agent unavailable ({issue}) and nothing says the ticket is human-managed; left where it is.");
        }

        var currentAssignment = await ticketAssignmentRepository.GetCurrentAsync(ticket.TicketId, cancellationToken);
        var wrote = false;

        // Call Center first. TransferToDepartment clears the owner, so a stale automatic pre-assignment in the old department is released.
        if (ticket.CurrentDepartmentId != callCenter.DepartmentId)
        {
            var fromDepartmentId = ticket.CurrentDepartmentId;
            ticket.TransferToDepartment(callCenter.DepartmentId);
            currentAssignment?.MarkSuperseded();
            currentAssignment = null;
            wrote = true;

            await auditWriter.WriteAsync(
                actorEmployeeId: null, "Transfer", "Ticket", ticket.TicketId.ToString(),
                beforeValue: $"DepartmentId={fromDepartmentId};AssignedEmployeeId={previousOwner?.ToString() ?? "DepartmentQueue"}",
                afterValue: $"DepartmentId={callCenter.DepartmentId};Reason=Call answered by a Call Center agent — routed to Call Center;"
                    + $"OriginatingDepartmentId={ticket.OriginatingDepartmentId} (unchanged);Source={source}"
                    + (previousOwner is not null ? ";AutomaticPreAssignmentReleased=true" : string.Empty),
                correlationId, cancellationToken);

            if (workflowEventRepository is not null)
            {
                await workflowEventRepository.AddAsync(
                    TicketWorkflowEvent.ForTransfer(
                        ticket.TicketId, nowUtc, actorEmployeeId: null, TicketTransferReason.SystemRouting,
                        fromDepartmentId, callCenter.DepartmentId, issue == AnsweringAgentIssue.None ? agent.EmployeeId : null,
                        "Call answered by a Call Center agent — Call Center first", correlationId),
                    cancellationToken);
            }
        }

        if (issue == AnsweringAgentIssue.None)
        {
            var agentId = agent.EmployeeId!.Value;
            currentAssignment?.MarkSuperseded();
            ticket.AssignTo(agentId);
            await RecordOriginalHumanAgentAsync(ticket, agentId, "AnsweredGenesysInteraction", correlationId, cancellationToken);

            // The answering agent is the assigning actor: a human act, like a self-assignment, so this is NOT an automatic pre-assignment.
            await ticketAssignmentRepository.AddAsync(
                new TicketAssignment(ticket.TicketId, agentId, callCenter.DepartmentId, nowUtc, assigningActorEmployeeId: agentId),
                cancellationToken);

            await auditWriter.WriteAsync(
                agentId, "AssignOnAnswer", "Ticket", ticket.TicketId.ToString(),
                beforeValue: previousOwner?.ToString() ?? "DepartmentQueue",
                afterValue: $"AssignedEmployeeId={agentId};DepartmentId={callCenter.DepartmentId};Mode=DirectToAnsweringAgent;"
                    + $"Trigger=GenesysAnswered;Source={source};GenesysUserId={agent.GenesysUserId}"
                    + (reportedByEmployeeId is { } by ? $";ReportedBy={by}" : string.Empty)
                    + (previousOwner is not null && ticket.CurrentDepartmentId == callCenter.DepartmentId && !wrote ? $";ReplacedAutomaticPreAssignment={previousOwner}" : string.Empty),
                correlationId, cancellationToken);
            await NotifyAssignedAsync(ticket, previousOwner, agentId, actorEmployeeId: agentId, correlationId, nowUtc, cancellationToken);

            if (genesysEvent is not null && assignmentHistoryRepository is not null)
            {
                // The previous assignee is the owner being replaced, else the AI Agent when it was the last recorded assignee.
                var previousAssignee = previousOwner ?? (genesysHistory.LastOrDefault() is { Source: TicketAssignmentSource.GenesysAiAgent } ai ? ai.NewEmployeeId : null);
                await assignmentHistoryRepository.AddAsync(
                    TicketAssignmentHistoryEntry.ForGenesys(
                        ticket.TicketId, callCenter.DepartmentId, previousAssignee, agentId,
                        previousAssignee is null ? TicketAssignmentSource.GenesysDirectCall : TicketAssignmentSource.GenesysCallTransfer,
                        changedByEmployeeId: null, nowUtc, eventAtUtc, genesysEvent.ConversationId,
                        previousAssignee is null ? "Genesys direct call: answered by the human agent" : "Genesys call transfer: the next human agent answered"),
                    cancellationToken);
            }

            return new AnswerAssignmentResult(AnswerAssignmentOutcome.AssignedToAnsweringAgent, agentId, Wrote: true);
        }

        // The answering agent cannot take it. Business rule (final): the ticket still needs a CS Agent, so it is flagged for staff AND given to
        // the next eligible CS Agent of the shared rotation - it never sits unassigned. An EXISTING eligible CS Agent is preserved (no re-deal),
        // but the problem is still flagged so the mapping / eligibility issue gets fixed.
        var keepEligibleOwner = previousOwner is not null && !wrote
            && await CheckAnsweringAgentAsync(previousOwner.Value, callCenter.DepartmentId, cancellationToken) == AnsweringAgentIssue.None;

        if (previousOwner is not null && !wrote && !keepEligibleOwner)
        {
            currentAssignment?.MarkSuperseded();
            ticket.ReleaseOwner();
            wrote = true;
            await auditWriter.WriteAsync(
                actorEmployeeId: null, "Unassign", "Ticket", ticket.TicketId.ToString(),
                beforeValue: previousOwner.ToString(),
                afterValue: $"DepartmentQueue;DepartmentId={ticket.CurrentDepartmentId};Reason=Automatic pre-assignment released — the agent who answered "
                    + $"({agent.GenesysUserId}) cannot take the ticket ({issue}); it is re-dealt to the next CS Agent;Source={source}",
                correlationId, cancellationToken);
        }

        var marker = $"GenesysUserId={agent.GenesysUserId};Issue={issue}";
        var alreadyFlagged = workflowEventRepository is not null
            && (await workflowEventRepository.ListByTicketIdAsync(ticket.TicketId, cancellationToken)).Any(e =>
                e.EventType == WorkflowEventType.ManualAssignmentRequired && e.Note is not null && e.Note.Contains(marker, StringComparison.Ordinal));
        if (alreadyFlagged)
        {
            // Flagged earlier (e.g. no CS Agent was available then): try the shared rotation again; nothing new is flagged.
            var retried = keepEligibleOwner ? null : await EnsureFallbackAgentAsync(ticket, nowUtc, correlationId, cancellationToken);
            return retried is { } again
                ? new AnswerAssignmentResult(AnswerAssignmentOutcome.AssignedByRoundRobinFallback, again, issue, Wrote: true)
                : new AnswerAssignmentResult(AnswerAssignmentOutcome.AlreadyFlagged, Issue: issue, Wrote: wrote);
        }

        var note = $"Answered by Genesys agent {agent.GenesysUserId} — {Describe(issue)}. Left in the Call Center queue for manual assignment. [{marker}]";
        if (workflowEventRepository is not null)
        {
            await workflowEventRepository.AddAsync(
                new TicketWorkflowEvent(
                    ticket.TicketId, WorkflowEventType.ManualAssignmentRequired, nowUtc, actorEmployeeId: null,
                    ticketApprovalId: null, note, correlationId),
                cancellationToken);
        }

        await auditWriter.WriteAsync(
            actorEmployeeId: null, "AnswerAssignmentSkipped", "Ticket", ticket.TicketId.ToString(),
            beforeValue: previousOwner?.ToString() ?? "DepartmentQueue",
            afterValue: $"DepartmentQueue;DepartmentId={ticket.CurrentDepartmentId};Status=Unassigned;ManualAssignmentRequired=true;"
                + $"GenesysUserId={agent.GenesysUserId};Issue={issue};Source={source}",
            correlationId, cancellationToken);

        if (keepEligibleOwner)
        {
            return new AnswerAssignmentResult(AnswerAssignmentOutcome.HumanOwnerKept, previousOwner, issue, "Existing eligible CS Agent preserved; the problem is flagged.", Wrote: true);
        }

        var fallbackAgent = await EnsureFallbackAgentAsync(ticket, nowUtc, correlationId, cancellationToken);
        return fallbackAgent is { } assigned
            ? new AnswerAssignmentResult(AnswerAssignmentOutcome.AssignedByRoundRobinFallback, assigned, issue, Wrote: true)
            : new AnswerAssignmentResult(AnswerAssignmentOutcome.QueuedForManualAssignment, Issue: issue, Wrote: true);
    }

    /// <summary>
    /// True when the current owner got the ticket from a Genesys connect event (the answering agent's own act, mirrored by the latest human Genesys
    /// history entry). A manual or supervisor assignment - even to the same person - carries another actor or is newer, so it is never "set by Genesys".
    /// Legacy tickets with no Genesys history are treated as person-owned: nothing is moved on a guess.
    /// </summary>
    private async Task<bool> OwnerWasSetByGenesysAsync(
        Ticket ticket, Guid owner, IReadOnlyList<TicketAssignmentHistoryEntry> genesysHistory, CancellationToken cancellationToken)
    {
        var lastHuman = genesysHistory.LastOrDefault(h => h.Source is TicketAssignmentSource.GenesysDirectCall or TicketAssignmentSource.GenesysCallTransfer);
        if (lastHuman is null || lastHuman.NewEmployeeId != owner)
        {
            return false;
        }

        var current = await ticketAssignmentRepository.GetCurrentAsync(ticket.TicketId, cancellationToken);
        return current is { IsCurrent: true } && current.AssignedEmployeeId == owner && current.AssigningActorEmployeeId == owner
            && current.AssignedAtUtc <= lastHuman.ChangedAtUtc;
    }

    /// <summary>
    /// <b>The AI Agent is the initial assignee - in the history only.</b> Records that the AI Agent (the Genesys integration service account) handled
    /// the conversation first. The ticket keeps NO owner (an ownerless AI ticket is how the rest of the system recognises AI handling: round robin,
    /// AI auto-close, the reconciler, dashboards and performance all rely on it), and the creator and audit trail are untouched. Idempotent: written
    /// once per ticket. Runs in the caller's transaction. Returns whether it wrote.
    /// </summary>
    public async Task<bool> RecordAiAgentAssignmentAsync(
        Ticket ticket, Guid aiAgentEmployeeId, string? genesysConversationId, DateTime? eventAtUtc, DateTime nowUtc, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(ticket);

        if (assignmentHistoryRepository is null || aiAgentEmployeeId == Guid.Empty)
        {
            return false;
        }

        var existing = await assignmentHistoryRepository.ListByTicketIdAsync(ticket.TicketId, cancellationToken);
        if (existing.Any(h => h.Source is not null))
        {
            return false;   // already recorded (a redelivery), or a human assignment already exists
        }

        await assignmentHistoryRepository.AddAsync(
            TicketAssignmentHistoryEntry.ForGenesys(
                ticket.TicketId, ticket.CurrentDepartmentId, previousEmployeeId: null, aiAgentEmployeeId, TicketAssignmentSource.GenesysAiAgent,
                changedByEmployeeId: null, nowUtc, eventAtUtc, genesysConversationId, "AI Agent handled the conversation"),
            cancellationToken);
        return true;
    }

    /// <summary>Shared-rotation fallback for a ticket still without an owner; returns the agent, or null when none could be assigned (the failure is then recorded by the engine).</summary>
    private async Task<Guid?> EnsureFallbackAgentAsync(Ticket ticket, DateTime nowUtc, Guid correlationId, CancellationToken cancellationToken)
    {
        if (ticket.CurrentOwnerEmployeeId is not null)
        {
            return null;
        }

        var result = await EnsureCsAgentAsync(ticket, null, AutoAssignmentTrigger.CallCenterIntake, nowUtc, correlationId, cancellationToken);
        return result.Outcome is AutoAssignmentOutcome.AssignedRoundRobin or AutoAssignmentOutcome.PreservedCsAgent ? result.AssignedEmployeeId : null;
    }

    /// <summary>
    /// Backfill priority 2: the CS Agent Genesys confirmed as having HANDLED the interaction owns the ticket - assigned in place (no department
    /// move, no cursor advance, as a system action). Returns null (nothing written) when that person is not an eligible Call Center CS Agent,
    /// so the caller falls back to the shared rotation; AlreadyAssigned / NotApplicable when there is nothing to do.
    /// </summary>
    public async Task<AutoAssignmentResult?> AssignConfirmedHandlerAsync(
        Ticket ticket, Guid handlerEmployeeId, AutoAssignmentTrigger trigger, DateTime nowUtc, Guid correlationId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(ticket);

        if (ticket.TicketStatus is TicketStatus.Resolved or TicketStatus.Closed)
        {
            return new AutoAssignmentResult(AutoAssignmentOutcome.NotApplicable);
        }

        if (ticket.CurrentOwnerEmployeeId is { } existing)
        {
            return new AutoAssignmentResult(AutoAssignmentOutcome.AlreadyAssigned, existing);
        }

        if (await IsHeldForManualAssignmentAsync(ticket, cancellationToken))
        {
            return new AutoAssignmentResult(AutoAssignmentOutcome.HeldForManualAssignment);
        }

        if (await ResolveCallCenterAsync(cancellationToken) is not { } callCenter
            || await CheckAnsweringAgentAsync(handlerEmployeeId, callCenter.DepartmentId, cancellationToken) != AnsweringAgentIssue.None)
        {
            return null;
        }

        ticket.AssignTo(handlerEmployeeId);
        await RecordOriginalHumanAgentAsync(ticket, handlerEmployeeId, "ConfirmedHandler", correlationId, cancellationToken);
        (await ticketAssignmentRepository.GetCurrentAsync(ticket.TicketId, cancellationToken))?.MarkSuperseded();
        await ticketAssignmentRepository.AddAsync(
            new TicketAssignment(ticket.TicketId, handlerEmployeeId, ticket.CurrentDepartmentId, nowUtc, assigningActorEmployeeId: null),
            cancellationToken);
        await auditWriter.WriteAsync(
            actorEmployeeId: null, "AutoAssign", "Ticket", ticket.TicketId.ToString(),
            beforeValue: "Unassigned",
            afterValue: $"AssignedEmployeeId={handlerEmployeeId};DepartmentId={ticket.CurrentDepartmentId};Mode=ConfirmedHandler;Trigger={trigger}",
            correlationId, cancellationToken);
        await NotifyAssignedAsync(ticket, previousOwnerEmployeeId: null, handlerEmployeeId, actorEmployeeId: handlerEmployeeId, correlationId, nowUtc, cancellationToken);
        return new AutoAssignmentResult(AutoAssignmentOutcome.PreservedCsAgent, handlerEmployeeId);
    }

    /// <summary>
    /// True when a PERSON has transferred the ticket (any transfer event that is not system routing, escalations included), or the ticket
    /// has already been in the Call Center and is no longer there. Either way a later "agent connected" must not move it again.
    /// </summary>
    private async Task<bool> HasLeftOrBeenTransferredByPersonAsync(Ticket ticket, int callCenterDepartmentId, CancellationToken cancellationToken)
    {
        if (workflowEventRepository is null)
        {
            return false;
        }

        var events = await workflowEventRepository.ListByTicketIdAsync(ticket.TicketId, cancellationToken);
        return events.Any(e => e.EventType == WorkflowEventType.TicketTransferred && e.TransferReasonCode != (byte)TicketTransferReason.SystemRouting)
            || (ticket.CurrentDepartmentId != callCenterDepartmentId
                && events.Any(e => e.EventType == WorkflowEventType.TicketTransferred && e.ToDepartmentId == callCenterDepartmentId));
    }

    private static string Describe(AnsweringAgentIssue issue) => issue switch
    {
        AnsweringAgentIssue.NotMapped => "that Genesys user is not mapped to a Ticketing user",
        AnsweringAgentIssue.Inactive => "the mapped Ticketing user is deactivated",
        AnsweringAgentIssue.LockedOut => "the mapped Ticketing account is locked out",
        AnsweringAgentIssue.ServiceAccount => "the mapped user is a service account",
        AnsweringAgentIssue.NotCallCenterMember => "the mapped user is not a Call Center member",
        AnsweringAgentIssue.NoOperationalRole => "the mapped user holds no operational role",
        AnsweringAgentIssue.AmbiguousMapping => "more than one Ticketing user carries that Genesys id (fix AspNetUsers.GenesysUserId)",
        AnsweringAgentIssue.NotCsAgent => "the mapped user does not hold the CS Agent role, which the Call Center requires",
        _ => "the agent id is not usable"
    };

    /// <summary>
    /// Self-assignment at manual creation: the CS Agent who created the ticket owns it, so they can see and work it.
    ///
    /// <para>
    /// It only ever fills a gap. It does nothing when the ticket already has an owner, when the configured request-type rule made a decision
    /// (<see cref="AutoAssignmentOutcome.AssignedToPrimary"/>, or an explicit <see cref="AutoAssignmentOutcome.DepartmentQueueByRule"/>),
    /// when the department's workflow settings disable assignment, or when the creator is not an active member of the ticket's current
    /// department (the same invariant every assignment honours). A request type with no rule, no request type at all, or a rule whose
    /// configured assignee went stale degrade to the department queue today; those are the cases it fills. Written as the creator's own act
    /// (assignment row and audit actor), unlike rule assignment which is a system action.
    /// </para>
    /// </summary>
    public async Task<bool> TrySelfAssignAsync(
        Ticket ticket,
        Guid creatorEmployeeId,
        AutoAssignmentOutcome automaticOutcome,
        DateTime nowUtc,
        Guid correlationId,
        CancellationToken cancellationToken = default)
    {
        if (ticket.CurrentOwnerEmployeeId is not null
            || automaticOutcome is not (AutoAssignmentOutcome.NoRequestType
                or AutoAssignmentOutcome.NoRuleConfigured
                or AutoAssignmentOutcome.ConfiguredAssigneeNotInDepartment))
        {
            return false;
        }

        var settings = await departmentWorkflowSettingsRepository.GetByDepartmentIdAsync(ticket.CurrentDepartmentId, cancellationToken);
        if (settings is { AllowAssignment: false }
            || !await userDepartmentAssignmentRepository.ExistsAsync(creatorEmployeeId, ticket.CurrentDepartmentId, cancellationToken))
        {
            return false;
        }

        ticket.AssignTo(creatorEmployeeId);
        await RecordOriginalHumanAgentAsync(ticket, creatorEmployeeId, "SelfAssignedAtCreation", correlationId, cancellationToken);

        var currentAssignment = await ticketAssignmentRepository.GetCurrentAsync(ticket.TicketId, cancellationToken);
        currentAssignment?.MarkSuperseded();
        await ticketAssignmentRepository.AddAsync(
            new TicketAssignment(ticket.TicketId, creatorEmployeeId, ticket.CurrentDepartmentId, nowUtc, assigningActorEmployeeId: creatorEmployeeId),
            cancellationToken);

        await auditWriter.WriteAsync(
            creatorEmployeeId, "SelfAssign", "Ticket", ticket.TicketId.ToString(),
            beforeValue: "DepartmentQueue",
            afterValue: $"AssignedEmployeeId={creatorEmployeeId};DepartmentId={ticket.CurrentDepartmentId};Trigger={AutoAssignmentTrigger.TicketCreated};Reason=Created by this agent",
            correlationId, cancellationToken);
        await NotifyAssignedAsync(ticket, previousOwnerEmployeeId: null, creatorEmployeeId, actorEmployeeId: creatorEmployeeId, correlationId, nowUtc, cancellationToken);

        return true;
    }

    /// <summary>Marker that identifies the one failure event per ticket (the reconciler and retries must not stack duplicates).</summary>
    public const string AssignmentFailureMarker = "CsAgentAssignmentFailed";

    /// <summary>
    /// The safe fallback — the ticket stays unassigned, and the why is audited as a system action so the outcome is as traceable as an
    /// assignment. With <paramref name="recordFailure"/> the failure is ALSO recorded as a <see cref="WorkflowEventType.ManualAssignmentRequired"/>
    /// workflow event (once per ticket until it is assigned), which is what authorized staff see and what the retry endpoint clears.
    /// </summary>
    private async Task<AutoAssignmentResult> QueueAsync(
        Ticket ticket,
        AutoAssignmentOutcome outcome,
        string reason,
        AutoAssignmentTrigger trigger,
        Guid correlationId,
        CancellationToken cancellationToken,
        bool recordFailure = false,
        DateTime nowUtc = default)
    {
        await auditWriter.WriteAsync(
            actorEmployeeId: null,
            "AutoAssign", "Ticket", ticket.TicketId.ToString(),
            beforeValue: ticket.CurrentOwnerEmployeeId?.ToString(),
            afterValue: $"DepartmentQueue;DepartmentId={ticket.CurrentDepartmentId};Trigger={trigger};Reason={reason}",
            correlationId, cancellationToken);

        if (recordFailure && workflowEventRepository is not null)
        {
            var alreadyRecorded = (await workflowEventRepository.ListByTicketIdAsync(ticket.TicketId, cancellationToken)).Any(e =>
                e.EventType == WorkflowEventType.ManualAssignmentRequired && e.Note is not null
                && e.Note.Contains(AssignmentFailureMarker, StringComparison.Ordinal));
            if (!alreadyRecorded)
            {
                await workflowEventRepository.AddAsync(
                    new TicketWorkflowEvent(
                        ticket.TicketId, WorkflowEventType.ManualAssignmentRequired, nowUtc == default ? DateTime.UtcNow : nowUtc,
                        actorEmployeeId: null, ticketApprovalId: null,
                        $"No CS Agent could be assigned automatically. {reason}. Retry or assign manually. [{AssignmentFailureMarker};Trigger={trigger}]",
                        correlationId),
                    cancellationToken);
            }
        }

        return new AutoAssignmentResult(outcome);
    }
}
