using TigerCS.Application.Abstractions;
using TigerCS.Application.Authorization;
using TigerCS.Application.Modules.IdentityAndAccess.Abstractions;
using TigerCS.Application.Modules.Ticketing.Abstractions;
using TigerCS.Application.Modules.Ticketing.Dto;
using TigerCS.Application.Modules.WorkflowConfiguration.Abstractions;
using TigerCS.Domain.Modules.IdentityAndAccess;
using TigerCS.Domain.Modules.Ticketing;

namespace TigerCS.Application.Modules.Ticketing.Services;

/// <summary>
/// Assignment (MVP-API-Contracts.md §3.5) and department transfer (§3.6) —
/// this increment's items 2 and 3. Both write their history/audit and the
/// domain state change in one real transaction (item 2/3's "atomically").
/// </summary>
public sealed class TicketAssignmentAppService(
    ITicketRepository ticketRepository,
    ITicketAssignmentRepository ticketAssignmentRepository,
    IUserDepartmentAssignmentRepository userDepartmentAssignmentRepository,
    IDepartmentRepository departmentRepository,
    ITicketingUnitOfWork unitOfWork,
    IAuditEntryWriter auditWriter,
    TimeProvider timeProvider,
    IDepartmentWorkflowSettingsRepository departmentWorkflowSettingsRepository,
    TicketAutoAssignmentService autoAssignmentService,
    ITicketWorkflowEventRepository? workflowEventRepository = null,
    ITicketInteractionHandlerRepository? handlerRepository = null)
{
    /// <summary>
    /// PR correction — the caller's role alone decides Assign authority now;
    /// there is no more self-claim path. CS Agent and Department Employee
    /// hold no assignment capability at all (see TicketRoleSets' remarks).
    /// </summary>
    public async Task<TicketMutationResult> AssignAsync(
        Guid callerEmployeeId,
        IReadOnlyCollection<string> callerRoles,
        long ticketId,
        AssignTicketRequestDto request,
        CancellationToken cancellationToken = default)
    {
        var ticket = await ticketRepository.GetByIdAsync(ticketId, cancellationToken);
        if (ticket is null)
        {
            return TicketMutationResult.Failure(TicketMutationOutcome.NotFound);
        }

        var authorized = await AuthorizationGate.EvaluateAsync(callerRoles, async () =>
            callerRoles.Any(TicketRoleSets.AssignCrossDepartment.Contains)
            || (callerRoles.Any(TicketRoleSets.AssignWithinOwnDepartment.Contains)
                && await userDepartmentAssignmentRepository.ExistsAsync(callerEmployeeId, ticket.CurrentDepartmentId, cancellationToken)));

        if (!authorized)
        {
            return TicketMutationResult.Failure(TicketMutationOutcome.Forbidden);
        }

        // Closed-ticket immutability (PR correction): rejected before the
        // transaction is even opened, so no database round-trip of any kind
        // occurs for this outcome — the domain layer's own EnsureNotClosed()
        // guard (Ticket.AssignTo) is a defense-in-depth backstop, not the
        // only line of defense.
        if (ticket.TicketStatus == TicketStatus.Closed)
        {
            return TicketMutationResult.Failure(TicketMutationOutcome.TicketClosed);
        }

        // Workflow/Automation phase 2 — department workflow settings can
        // only NARROW what the role sets above already allow (fail-safe
        // rule: a provisional "allowed" seed never grants anything; existing
        // approved authorization stays authoritative). A department with no
        // settings row behaves exactly as before.
        var settings = await departmentWorkflowSettingsRepository.GetByDepartmentIdAsync(
            ticket.CurrentDepartmentId, cancellationToken);
        if (settings is { AllowAssignment: false })
        {
            return TicketMutationResult.Failure(TicketMutationOutcome.DisabledByDepartmentSettings);
        }

        if (ticket.CurrentOwnerEmployeeId is not null && settings is { AllowInternalReassignment: false })
        {
            return TicketMutationResult.Failure(TicketMutationOutcome.DisabledByDepartmentSettings);
        }

        // Taking a ticket out of the department queue stays a plain assignment. Handing an OWNED ticket to someone else
        // is a hand-over and must say why — the reason is what lets "unable to resolve" be reviewed later, so it is
        // never defaulted or inferred. (Assigning to the current owner changes nothing and needs no reason.)
        if (ticket.CurrentOwnerEmployeeId is { } currentOwner
            && currentOwner != request.AssignedEmployeeId
            && request.ReasonCode is (null or TicketTransferReason.Unspecified or TicketTransferReason.SystemRouting))
        {
            return TicketMutationResult.Failure(TicketMutationOutcome.TransferReasonRequired);
        }

        // MVP-API-Contracts.md §3.5's own validation: AssignedEmployeeId
        // must be an active member of the ticket's CurrentDepartmentId —
        // checked against the department the assignment target actually
        // needs to belong to, never trusted from the caller's own claims.
        if (!await userDepartmentAssignmentRepository.ExistsAsync(request.AssignedEmployeeId, ticket.CurrentDepartmentId, cancellationToken))
        {
            return TicketMutationResult.Failure(TicketMutationOutcome.EmployeeNotInDepartment);
        }

        ticketRepository.SetRowVersion(ticket, request.RowVersion);

        var now = timeProvider.GetUtcNow().UtcDateTime;
        var previousOwnerEmployeeId = ticket.CurrentOwnerEmployeeId;

        await using var transaction = await unitOfWork.BeginTransactionAsync(cancellationToken);

        var currentAssignment = await ticketAssignmentRepository.GetCurrentAsync(ticketId, cancellationToken);
        currentAssignment?.MarkSuperseded();

        ticket.AssignTo(request.AssignedEmployeeId);

        await ticketAssignmentRepository.AddAsync(
            new TicketAssignment(ticketId, request.AssignedEmployeeId, ticket.CurrentDepartmentId, now, callerEmployeeId),
            cancellationToken);

        var correlationId = Guid.NewGuid();
        await auditWriter.WriteAsync(
            callerEmployeeId, "Assign", "Ticket", ticketId.ToString(),
            beforeValue: previousOwnerEmployeeId?.ToString(), afterValue: request.AssignedEmployeeId.ToString(),
            correlationId, cancellationToken);

        // Ticket transfer tracking: only a REassignment is a hand-over. Taking a ticket out of the department queue
        // (no previous owner) is the ordinary first assignment, and giving it to its current owner again changes nothing.
        if (previousOwnerEmployeeId is { } previousOwner && previousOwner != request.AssignedEmployeeId && workflowEventRepository is not null)
        {
            await workflowEventRepository.AddAsync(
                TicketWorkflowEvent.ForTransfer(
                    ticketId, now, callerEmployeeId, request.ReasonCode ?? TicketTransferReason.Unspecified,
                    ticket.CurrentDepartmentId, ticket.CurrentDepartmentId, request.AssignedEmployeeId,
                    request.Reason, correlationId),
                cancellationToken);
        }

        try
        {
            await unitOfWork.SaveChangesAsync(cancellationToken);
        }
        catch (TicketConcurrentlyModifiedException)
        {
            return TicketMutationResult.Failure(TicketMutationOutcome.ConcurrencyConflict);
        }

        await transaction.CommitAsync(cancellationToken);
        return TicketMutationResult.Success(TicketQueryAppService.ToDetailDto(ticket));
    }

    /// <summary>
    /// Transfer authority is the Call Center layer, scoped per ticket — never global: a CS Manager may transfer any ticket; a CS Agent or
    /// CS Supervisor only a ticket currently assigned to them, and a CS Supervisor also a ticket of a department they belong to.
    /// Department Head, Department Employee and every other role are forbidden. After the move, the target department's rule or Round Robin
    /// picks the assignee (never a random employee).
    /// </summary>
    public async Task<TicketMutationResult> TransferAsync(
        Guid callerEmployeeId,
        IReadOnlyCollection<string> callerRoles,
        long ticketId,
        TransferTicketRequestDto request,
        CancellationToken cancellationToken = default)
    {
        var ticket = await ticketRepository.GetByIdAsync(ticketId, cancellationToken);
        if (ticket is null)
        {
            return TicketMutationResult.Failure(TicketMutationOutcome.NotFound);
        }

        var transferAuthorized = await AuthorizationGate.EvaluateAsync(callerRoles, async () =>
            callerRoles.Any(TicketRoleSets.TransferAnyTicket.Contains)
            || (callerRoles.Any(TicketRoleSets.Transfer.Contains)
                && (ticket.CurrentOwnerEmployeeId == callerEmployeeId
                    || await TicketOwnershipRules.IsActingInteractionHandlerAsync(
                        handlerRepository, userDepartmentAssignmentRepository, callerEmployeeId, callerRoles, ticket, cancellationToken)
                    || (callerRoles.Contains(Roles.CsSupervisor)
                        && await userDepartmentAssignmentRepository.ExistsAsync(callerEmployeeId, ticket.CurrentDepartmentId, cancellationToken)))));

        if (!transferAuthorized)
        {
            return TicketMutationResult.Failure(TicketMutationOutcome.Forbidden);
        }

        // Closed-ticket immutability (PR correction) — see AssignAsync's
        // identical remark; rejected before any transaction or write.
        if (ticket.TicketStatus == TicketStatus.Closed)
        {
            return TicketMutationResult.Failure(TicketMutationOutcome.TicketClosed);
        }

        if (request.TargetDepartmentId == ticket.CurrentDepartmentId)
        {
            return TicketMutationResult.Failure(TicketMutationOutcome.AlreadyInTargetDepartment);
        }

        // Workflow/Automation phase 2 — same narrowing-only rule as
        // AssignAsync: the SOURCE department's settings may disable
        // transferring tickets out of it; nothing here widens the CS-Manager
        // role gate above.
        var sourceSettings = await departmentWorkflowSettingsRepository.GetByDepartmentIdAsync(
            ticket.CurrentDepartmentId, cancellationToken);
        if (sourceSettings is { AllowTransferToOtherDepartments: false })
        {
            return TicketMutationResult.Failure(TicketMutationOutcome.DisabledByDepartmentSettings);
        }

        var targetDepartment = await departmentRepository.GetByIdAsync(request.TargetDepartmentId, cancellationToken);
        if (targetDepartment is null || !targetDepartment.IsActive)
        {
            return TicketMutationResult.Failure(TicketMutationOutcome.TargetDepartmentInactive);
        }

        // Transfer-and-assign: the explicit assignee must belong to the
        // department the ticket is MOVING TO — the same "assignee belongs to
        // the ticket's (resulting) department" rule AssignAsync enforces,
        // checked against the target rather than the source, and checked
        // before any write so a wrong choice changes nothing.
        if (request.AssignToEmployeeId is { } assignee
            && !await userDepartmentAssignmentRepository.ExistsAsync(assignee, request.TargetDepartmentId, cancellationToken))
        {
            return TicketMutationResult.Failure(TicketMutationOutcome.EmployeeNotInDepartment);
        }

        ticketRepository.SetRowVersion(ticket, request.RowVersion);

        var previousDepartmentId = ticket.CurrentDepartmentId;
        var previousOwnerEmployeeId = ticket.CurrentOwnerEmployeeId;
        var now = timeProvider.GetUtcNow().UtcDateTime;

        // One correlation id ties the transfer audit entry to the
        // re-evaluation entry the automation writes below, so the two read as
        // one operation in history.
        var correlationId = Guid.NewGuid();

        await using var transaction = await unitOfWork.BeginTransactionAsync(cancellationToken);

        // Department FIRST — the responsible department is the primary
        // assignment, and TransferToDepartment also clears the employee owner
        // (the previous department's employee cannot stay accountable). The
        // ticket is therefore never without a responsible department: it moves
        // straight from department B to department C.
        ticket.TransferToDepartment(request.TargetDepartmentId);

        await auditWriter.WriteAsync(
            callerEmployeeId, "Transfer", "Ticket", ticketId.ToString(),
            beforeValue: $"DepartmentId={previousDepartmentId};AssignedEmployeeId={previousOwnerEmployeeId?.ToString() ?? "DepartmentQueue"}",
            afterValue: $"DepartmentId={request.TargetDepartmentId};Reason={request.Reason}",
            correlationId, cancellationToken);

        if (request.AssignToEmployeeId is { } explicitAssignee)
        {
            // Transfer-and-assign: the CS Manager named who takes it in the
            // new department (validated above). Recorded exactly like a
            // standalone Assign — a superseding TicketAssignment row in the
            // NEW department plus an "Assign" audit entry under the transfer's
            // correlation id — so history reads "transferred, then assigned"
            // as one operation. The target department's automatic rules are
            // deliberately not run: an explicit choice is not to be overridden
            // by a rule.
            var currentAssignment = await ticketAssignmentRepository.GetCurrentAsync(ticketId, cancellationToken);
            currentAssignment?.MarkSuperseded();

            ticket.AssignTo(explicitAssignee);

            await ticketAssignmentRepository.AddAsync(
                new TicketAssignment(ticketId, explicitAssignee, request.TargetDepartmentId, now, callerEmployeeId),
                cancellationToken);

            await auditWriter.WriteAsync(
                callerEmployeeId, "Assign", "Ticket", ticketId.ToString(),
                beforeValue: previousOwnerEmployeeId?.ToString() ?? "DepartmentQueue",
                afterValue: $"{explicitAssignee};DepartmentId={request.TargetDepartmentId};ViaTransfer=true",
                correlationId, cancellationToken);
        }
        else
        {
            // ...THEN re-evaluate the assignment automation against the new
            // department + the ticket's request type. Every non-assignable case
            // (no rule, rule targets someone outside department C, settings
            // disable assignment) leaves the ticket in the NEW department's
            // queue — audited, and never a randomly picked employee.
            var configured = await autoAssignmentService.ApplyAsync(
                ticket, now, correlationId, AutoAssignmentTrigger.DepartmentTransfer, cancellationToken);

            // No rule decided: the target department's Round Robin picks among its eligible active operational users.
            await autoAssignmentService.ApplyCategoryRoutingAsync(
                ticket, configured, now, correlationId, AutoAssignmentTrigger.DepartmentTransfer, cancellationToken);
        }

        // Ticket transfer tracking: one typed event per transfer — who moved it, to which department and (when one took
        // it, explicitly or by the target department's rule) which employee, why, and when. Same transaction and
        // correlation id as the Transfer/Assign audit entries above.
        if (workflowEventRepository is not null)
        {
            await workflowEventRepository.AddAsync(
                TicketWorkflowEvent.ForTransfer(
                    ticketId, now, callerEmployeeId, request.ReasonCode ?? TicketTransferReason.Unspecified,
                    previousDepartmentId, request.TargetDepartmentId, ticket.CurrentOwnerEmployeeId,
                    request.Reason, correlationId),
                cancellationToken);
        }

        try
        {
            await unitOfWork.SaveChangesAsync(cancellationToken);
        }
        catch (TicketConcurrentlyModifiedException)
        {
            return TicketMutationResult.Failure(TicketMutationOutcome.ConcurrencyConflict);
        }

        await transaction.CommitAsync(cancellationToken);
        return TicketMutationResult.Success(TicketQueryAppService.ToDetailDto(ticket));
    }
}
