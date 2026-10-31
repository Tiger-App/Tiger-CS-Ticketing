using TigerCS.Application.Abstractions;
using TigerCS.Application.Modules.IdentityAndAccess.Abstractions;
using TigerCS.Application.Modules.Reporting.Services;
using TigerCS.Application.Modules.Ticketing.Abstractions;
using TigerCS.Application.Modules.WorkflowConfiguration.Abstractions;
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

    /// <summary>Round Robin found no eligible active employee — the ticket stays in the department queue, visibly unassigned.</summary>
    NoEligibleEmployee
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
    CategoryRouted
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
    IAssignmentRotationRepository? rotationRepository = null)
{
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

    /// <summary>
    /// Call Center category routing: the ticket is already in the category's department; assign it to the next employee in that
    /// department's rotation — unless a configured request-type rule already decided (a specific assignee, an explicit queue, or
    /// assignment disabled), which always wins, or the ticket already has an owner. <paramref name="configured"/> is the result of
    /// <see cref="ApplyAsync"/> for the same ticket. Same single engine, same audit trail.
    /// </summary>
    public async Task<AutoAssignmentResult> ApplyCategoryRoutingAsync(
        Ticket ticket, AutoAssignmentResult configured, DateTime nowUtc, Guid correlationId, CancellationToken cancellationToken = default)
    {
        if (ticket.CurrentOwnerEmployeeId is not null
            || configured.Outcome is not (AutoAssignmentOutcome.NoRequestType or AutoAssignmentOutcome.NoRuleConfigured
                or AutoAssignmentOutcome.ConfiguredAssigneeNotInDepartment))
        {
            return configured;
        }

        var settings = await departmentWorkflowSettingsRepository.GetByDepartmentIdAsync(ticket.CurrentDepartmentId, cancellationToken);
        if (settings is { AllowAssignment: false })
        {
            return configured;
        }

        return await AssignRoundRobinAsync(ticket, nowUtc, correlationId, AutoAssignmentTrigger.CategoryRouted, ruleId: null, cancellationToken);
    }

    /// <summary>
    /// Assigns to the next eligible active member of the ticket's CURRENT department. The persisted cursor is row-locked for the
    /// caller's transaction, so concurrent assignments serialize and never pick the same slot; no eligible member (or no rotation
    /// store) leaves the ticket in the department queue with the reason audited — never a guess.
    /// </summary>
    private async Task<AutoAssignmentResult> AssignRoundRobinAsync(
        Ticket ticket, DateTime nowUtc, Guid correlationId, AutoAssignmentTrigger trigger, int? ruleId, CancellationToken cancellationToken)
    {
        var ruleText = ruleId is { } id ? $"Rule {id}" : "Category routing";
        if (rotationRepository is null)
        {
            return await QueueAsync(ticket, AutoAssignmentOutcome.NoEligibleEmployee,
                $"{ruleText}: Round Robin is not available", trigger, correlationId, cancellationToken);
        }

        var members = await userDepartmentAssignmentRepository.GetByDepartmentIdAsync(
            ticket.CurrentDepartmentId, activeEmployeesOnly: true, cancellationToken);
        var eligible = members.Select(m => m.EmployeeId).Distinct().ToList();
        if (eligible.Count == 0)
        {
            return await QueueAsync(ticket, AutoAssignmentOutcome.NoEligibleEmployee,
                $"{ruleText}: no eligible active employee in department {ticket.CurrentDepartmentId}; Status=Unassigned",
                trigger, correlationId, cancellationToken);
        }

        var rotation = await rotationRepository.GetForUpdateAsync(ticket.CurrentDepartmentId, nowUtc, cancellationToken);
        var next = DepartmentAssignmentRotation.PickNext(rotation.LastAssignedEmployeeId, eligible)!.Value;
        rotation.Advance(next, nowUtc);

        var previousOwnerEmployeeId = ticket.CurrentOwnerEmployeeId;
        ticket.AssignTo(next);

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
                + $"{(ruleId is { } r ? $"RuleId={r};" : string.Empty)}Trigger={trigger}",
            correlationId, cancellationToken);

        return new AutoAssignmentResult(AutoAssignmentOutcome.AssignedRoundRobin, next);
    }

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

        return true;
    }

    /// <summary>The safe fallback — the ticket stays unassigned in its department queue, and the why is audited as a system action so the queue outcome is as traceable as an assignment.</summary>
    private async Task<AutoAssignmentResult> QueueAsync(
        Ticket ticket,
        AutoAssignmentOutcome outcome,
        string reason,
        AutoAssignmentTrigger trigger,
        Guid correlationId,
        CancellationToken cancellationToken)
    {
        await auditWriter.WriteAsync(
            actorEmployeeId: null,
            "AutoAssign", "Ticket", ticket.TicketId.ToString(),
            beforeValue: ticket.CurrentOwnerEmployeeId?.ToString(),
            afterValue: $"DepartmentQueue;DepartmentId={ticket.CurrentDepartmentId};Trigger={trigger};Reason={reason}",
            correlationId, cancellationToken);

        return new AutoAssignmentResult(outcome);
    }
}
