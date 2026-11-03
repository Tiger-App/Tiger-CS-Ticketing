using TigerCS.Application.Abstractions;
using TigerCS.Application.Authorization;
using TigerCS.Application.Modules.IdentityAndAccess.Abstractions;
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
    CategoryRouted,

    /// <summary>A new ticket that needs a person entered the Call Center, the first human-handling department for every channel.</summary>
    CallCenterIntake
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
    ITicketWorkflowEventRepository? workflowEventRepository = null)
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
        Ticket ticket, DateTime nowUtc, Guid correlationId, CancellationToken cancellationToken = default)
    {
        var settings = await departmentWorkflowSettingsRepository.GetByDepartmentIdAsync(ticket.CurrentDepartmentId, cancellationToken);
        if (settings is { AllowAssignment: false })
        {
            return await QueueAsync(ticket, AutoAssignmentOutcome.AssignmentDisabledForDepartment,
                "Call Center workflow settings disable assignment; Status=Unassigned", AutoAssignmentTrigger.CallCenterIntake, correlationId, cancellationToken);
        }

        return await AssignRoundRobinAsync(ticket, nowUtc, correlationId, AutoAssignmentTrigger.CallCenterIntake, ruleId: null, cancellationToken);
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
        Ticket ticket, DateTime nowUtc, Guid correlationId, string reason, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(ticket);

        if (ticket.TicketStatus is TicketStatus.Resolved or TicketStatus.Closed
            || await ResolveCallCenterAsync(cancellationToken) is not { } callCenter
            || callCenter.DepartmentId == ticket.CurrentDepartmentId)
        {
            return false;
        }

        if (ticket.CurrentOwnerEmployeeId is not null
            && !await TicketOwnershipRules.IsAutomaticPreAssignmentAsync(ticketAssignmentRepository, ticket, cancellationToken))
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

        await AssignCallCenterIntakeAsync(ticket, nowUtc, correlationId, cancellationToken);
        return true;
    }

    /// <summary>
    /// Round Robin as the default when no explicit rule decided (Transfer, Call Center category routing): runs only when the ticket
    /// has no owner and <paramref name="configured"/> (the result of <see cref="ApplyAsync"/>) was "nothing configured" — a specific
    /// assignee, an explicit department-queue rule or disabled assignment always wins. Same single engine, same audit trail.
    /// </summary>
    public async Task<AutoAssignmentResult> ApplyRoundRobinFallbackAsync(
        Ticket ticket, AutoAssignmentResult configured, AutoAssignmentTrigger trigger, DateTime nowUtc, Guid correlationId,
        CancellationToken cancellationToken = default)
    {
        if (rotationRepository is null
            || ticket.CurrentOwnerEmployeeId is not null
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

        return await AssignRoundRobinAsync(ticket, nowUtc, correlationId, trigger, ruleId: null, cancellationToken);
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
        Ticket ticket, DateTime nowUtc, Guid correlationId, AutoAssignmentTrigger trigger, int? ruleId, CancellationToken cancellationToken)
    {
        var ruleText = ruleId is { } id ? $"Rule {id}" : trigger == AutoAssignmentTrigger.CallCenterIntake ? "Call Center intake" : "Round Robin default";
        if (rotationRepository is null)
        {
            return await QueueAsync(ticket, AutoAssignmentOutcome.NoEligibleEmployee,
                $"{ruleText}: Round Robin is not available", trigger, correlationId, cancellationToken);
        }

        // The pool is the ticket's CURRENT department, and that department must exist and be active.
        if (departmentRepository is not null
            && await departmentRepository.GetByIdAsync(ticket.CurrentDepartmentId, cancellationToken) is not { IsActive: true })
        {
            return await QueueAsync(ticket, AutoAssignmentOutcome.NoEligibleEmployee,
                $"{ruleText}: department {ticket.CurrentDepartmentId} is missing or inactive; Status=Unassigned",
                trigger, correlationId, cancellationToken);
        }

        var members = await userDepartmentAssignmentRepository.GetByDepartmentIdAsync(
            ticket.CurrentDepartmentId, activeEmployeesOnly: true, cancellationToken);

        // The Call Center rotation is CS Agents only: Department Employee / Department Head members of the Call Center are never dealt
        // tickets (they cannot Transfer as owner). Every other department keeps the wider operational-role pool. When the department
        // repository is not wired (bare unit-test construction) the department cannot be identified and the wider pool applies.
        var csAgentsOnly = await IsCallCenterDepartmentAsync(ticket.CurrentDepartmentId, cancellationToken);
        var eligible = await FilterEligibleAsync(members.Select(m => m.EmployeeId).Distinct(), csAgentsOnly, cancellationToken);
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
    /// Narrows a department's active members to who may be handed work by rotation: not a service identity (the Genesys integration
    /// account), not locked out, and holding at least one operational role (<see cref="TicketRoleSets.RoundRobinEligible"/>) — so
    /// read-only users, supervisors/managers and administrators are skipped. A lookup that cannot answer excludes the person: rotation
    /// never guesses. (The three readers are optional only so unit tests can build the service bare; the composition root supplies all.)
    /// </summary>
    private async Task<List<Guid>> FilterEligibleAsync(IEnumerable<Guid> candidateIds, bool csAgentsOnly, CancellationToken cancellationToken)
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
                if (Roles.IsReadOnlyCaller(roles)
                    || !roles.Any(TicketRoleSets.RoundRobinEligible.Contains)
                    || (csAgentsOnly && !roles.Contains(Roles.CsAgent, StringComparer.Ordinal)))
                {
                    continue;
                }
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
    /// out, an operational (non read-only) role, and a member of the Call Center. Mapped-and-active is established earlier by the Genesys
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
            if (Roles.IsReadOnlyCaller(roles) || !roles.Any(TicketRoleSets.RoundRobinEligible.Contains))
            {
                return AnsweringAgentIssue.NoOperationalRole;
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
        bool routeToCallCenterIfAgentUnavailable, CancellationToken cancellationToken = default)
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

        // Ownership first: never move or flag a ticket somebody already owns.
        var previousOwner = ticket.CurrentOwnerEmployeeId;
        if (previousOwner is not null)
        {
            if (agent.EmployeeId == previousOwner)
            {
                return new AnswerAssignmentResult(AnswerAssignmentOutcome.AlreadyOwnedByAnsweringAgent, previousOwner);
            }

            if (!await TicketOwnershipRules.IsAutomaticPreAssignmentAsync(ticketAssignmentRepository, ticket, cancellationToken))
            {
                return new AnswerAssignmentResult(AnswerAssignmentOutcome.HumanOwnerKept, previousOwner);
            }
        }

        var issue = agent.Issue != AnsweringAgentIssue.None || agent.EmployeeId is null
            ? (agent.Issue != AnsweringAgentIssue.None ? agent.Issue : AnsweringAgentIssue.NotMapped)
            : await CheckAnsweringAgentAsync(agent.EmployeeId.Value, callCenter.DepartmentId, cancellationToken);

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

            return new AnswerAssignmentResult(AnswerAssignmentOutcome.AssignedToAnsweringAgent, agentId, Wrote: true);
        }

        // The answering agent cannot take it: keep it in the queue. Never a substitute person.
        if (previousOwner is not null && !wrote)
        {
            currentAssignment?.MarkSuperseded();
            ticket.ReleaseOwner();
            wrote = true;
            await auditWriter.WriteAsync(
                actorEmployeeId: null, "Unassign", "Ticket", ticket.TicketId.ToString(),
                beforeValue: previousOwner.ToString(),
                afterValue: $"DepartmentQueue;DepartmentId={ticket.CurrentDepartmentId};Reason=Automatic pre-assignment released — the agent who answered "
                    + $"({agent.GenesysUserId}) cannot take the ticket ({issue}); manual assignment required;Source={source}",
                correlationId, cancellationToken);
        }

        var marker = $"GenesysUserId={agent.GenesysUserId};Issue={issue}";
        var alreadyFlagged = workflowEventRepository is not null
            && (await workflowEventRepository.ListByTicketIdAsync(ticket.TicketId, cancellationToken)).Any(e =>
                e.EventType == WorkflowEventType.ManualAssignmentRequired && e.Note is not null && e.Note.Contains(marker, StringComparison.Ordinal));
        if (alreadyFlagged)
        {
            return new AnswerAssignmentResult(AnswerAssignmentOutcome.AlreadyFlagged, Issue: issue, Wrote: wrote);
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

        return new AnswerAssignmentResult(AnswerAssignmentOutcome.QueuedForManualAssignment, Issue: issue, Wrote: true);
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
