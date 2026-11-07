using TigerCS.Application.Abstractions;
using TigerCS.Application.Modules.CustomerVerification.Abstractions;
using TigerCS.Application.Modules.Notifications.Services;
using TigerCS.Application.Modules.Ticketing.Abstractions;
using TigerCS.Application.Modules.Ticketing.Dto;
using TigerCS.Domain.Modules.Ticketing;

namespace TigerCS.Application.Modules.Ticketing.Services;

/// <summary>
/// <b>Take Ownership</b>: a Call Center agent takes an unassigned, human-handling ticket that is still sitting in another department (or in the
/// Call Center queue). In ONE transaction the ticket moves to the Call Center, is assigned exclusively to the caller, and the move is recorded
/// - nothing else about it changes.
///
/// <list type="bullet">
/// <item><b>Preserved, untouched:</b> originating department (write-once), request type, category, priority, status, verification, SLA state and
/// clocks (no SLA code runs), resolution, interactions, notes, approvals and every earlier audit / workflow / status-history row.</item>
/// <item><b>Recorded:</b> a <c>Transfer</c> audit entry and a <c>TicketTransferred</c> workflow event (when the department changes - reason
/// <see cref="TicketTransferReason.SystemRouting"/>, so it is neither counted as a person's hand-over nor as an escalation, exactly like
/// the Call Center-first routing it continues), a <c>TakeOwnership</c> audit entry, and a current <see cref="TicketAssignment"/> row whose
/// assigning actor is the agent themself - a human act, so it can never be mistaken for an automatic pre-assignment a later Genesys event
/// may replace.</item>
/// <item><b>Exactly one owner:</b> the claim writes under the ticket's row version AND the unique current-assignment index. Of two agents
/// racing for the same ticket, one commits; the other gets <see cref="TicketMutationOutcome.ConcurrencyConflict"/> (and
/// <see cref="TicketMutationOutcome.TicketAlreadyOwned"/> on retry). A ticket another employee owns is never taken.</item>
/// <item><b>Afterwards</b> the agent is an ordinary owner: Resolve, Close and Transfer follow the existing ownership rules and validations.</item>
/// </list>
///
/// Who and which: <see cref="TicketClaimPolicy"/>. The bulk Call Center backlog distribution uses the same eligibility, so it never competes
/// with a claim: a claimed ticket is owned and simply drops out of its plan (its Execute gates then ask for a fresh Preview).
/// </summary>
public sealed class TicketClaimAppService(
    ITicketRepository ticketRepository,
    ITicketAssignmentRepository ticketAssignmentRepository,
    ITicketingUnitOfWork unitOfWork,
    IAuditEntryWriter auditWriter,
    TimeProvider timeProvider,
    TicketClaimPolicy claimPolicy,
    TicketAutoAssignmentService autoAssignmentService,
    ITicketWorkflowEventRepository? workflowEventRepository = null,
    IOutboxWriter? outboxWriter = null)
{
    private readonly TicketNotificationEvents? _notificationEvents = TicketNotificationEvents.CreateOrNull(outboxWriter, auditWriter);

    public async Task<TicketMutationResult> TakeOwnershipAsync(
        Guid callerEmployeeId,
        IReadOnlyCollection<string> callerRoles,
        long ticketId,
        TakeOwnershipRequestDto request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var ticket = await ticketRepository.GetByIdAsync(ticketId, cancellationToken);
        if (ticket is null)
        {
            return TicketMutationResult.Failure(TicketMutationOutcome.NotFound);
        }

        // Taking a ticket you already hold changes nothing (a double click, a retry after a lost response): success, no writes.
        if (ticket.CurrentOwnerEmployeeId == callerEmployeeId
            && ticket.TicketStatus is not (TicketStatus.Closed or TicketStatus.Resolved)
            && await claimPolicy.IsEligibleClaimerAsync(callerEmployeeId, callerRoles, cancellationToken))
        {
            return TicketMutationResult.Success(TicketQueryAppService.ToDetailDto(ticket));
        }

        var decision = await claimPolicy.DecideAsync(callerEmployeeId, callerRoles, ticketId, cancellationToken);
        switch (decision.Denial)
        {
            case TicketClaimDenial.NotPermitted: return TicketMutationResult.Failure(TicketMutationOutcome.Forbidden);
            case TicketClaimDenial.NotFound: return TicketMutationResult.Failure(TicketMutationOutcome.NotFound);
            case TicketClaimDenial.Terminal: return TicketMutationResult.Failure(TicketMutationOutcome.ClaimNotAllowedForStatus);
            case TicketClaimDenial.Owned: return TicketMutationResult.Failure(TicketMutationOutcome.TicketAlreadyOwned);
            case TicketClaimDenial.ActiveAiConversation: return TicketMutationResult.Failure(TicketMutationOutcome.ClaimBlockedByActiveAiConversation);
            case TicketClaimDenial.PendingApproval: return TicketMutationResult.Failure(TicketMutationOutcome.ClaimBlockedByPendingApproval);
        }

        var callCenter = await autoAssignmentService.ResolveCallCenterAsync(cancellationToken);
        if (callCenter is null)
        {
            return TicketMutationResult.Failure(TicketMutationOutcome.Forbidden);
        }

        // The version this decision was made on. If a caller supplied the version they read, that one is enforced instead (stale = 409).
        if (request.RowVersion is { Length: > 0 } clientVersion)
        {
            ticketRepository.SetRowVersion(ticket, clientVersion);
        }

        var now = timeProvider.GetUtcNow().UtcDateTime;
        var correlationId = Guid.NewGuid();
        var fromDepartmentId = ticket.CurrentDepartmentId;
        var movesToCallCenter = fromDepartmentId != callCenter.DepartmentId;

        await using var transaction = await unitOfWork.BeginTransactionAsync(cancellationToken);

        // Nothing is READ inside this transaction on purpose: the decision above already proved there is no owner and no current assignment,
        // and the writes below are guarded by the ticket's row version and by the unique current-assignment index. Only writes remain, so a
        // concurrent claim can only ever lose at SaveChanges, where it is answered as a conflict.

        if (movesToCallCenter)
        {
            // Department first (clears the owner), then the owner - the same order as a transfer-and-assign.
            ticket.TransferToDepartment(callCenter.DepartmentId);

            await auditWriter.WriteAsync(
                callerEmployeeId, "Transfer", "Ticket", ticketId.ToString(),
                beforeValue: $"DepartmentId={fromDepartmentId};AssignedEmployeeId=DepartmentQueue",
                afterValue: $"DepartmentId={callCenter.DepartmentId};Reason=Call Center agent took ownership;"
                    + $"OriginatingDepartmentId={ticket.OriginatingDepartmentId} (unchanged);Status={ticket.TicketStatus} (unchanged)",
                correlationId, cancellationToken);

            if (workflowEventRepository is not null)
            {
                await workflowEventRepository.AddAsync(
                    TicketWorkflowEvent.ForTransfer(
                        ticketId, now, callerEmployeeId, TicketTransferReason.SystemRouting,
                        fromDepartmentId, callCenter.DepartmentId, callerEmployeeId,
                        "Call Center agent took ownership of an unassigned ticket", correlationId),
                    cancellationToken);
            }
        }

        ticket.AssignTo(callerEmployeeId);

        await ticketAssignmentRepository.AddAsync(
            new TicketAssignment(ticketId, callerEmployeeId, callCenter.DepartmentId, now, assigningActorEmployeeId: callerEmployeeId),
            cancellationToken);

        await auditWriter.WriteAsync(
            callerEmployeeId, "TakeOwnership", "Ticket", ticketId.ToString(),
            beforeValue: $"DepartmentId={fromDepartmentId};AssignedEmployeeId=DepartmentQueue",
            afterValue: $"AssignedEmployeeId={callerEmployeeId};DepartmentId={callCenter.DepartmentId};Mode=SelfTake;"
                + $"OriginatingDepartmentId={ticket.OriginatingDepartmentId};Status={ticket.TicketStatus}",
            correlationId, cancellationToken);

        if (_notificationEvents is not null)
        {
            // A self-assignment: the customer learns the ticket has an owner; no colleague is told.
            await _notificationEvents.QueueAssignedAsync(ticket, previousOwnerEmployeeId: null, callerEmployeeId, callerEmployeeId, correlationId, now, cancellationToken);
        }

        try
        {
            await unitOfWork.SaveChangesAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is TicketConcurrentlyModifiedException or DuplicateWriteException)
        {
            // Lost the race: another agent (or any other write) got there first. The winner stands; nothing of this attempt was written.
            return TicketMutationResult.Failure(TicketMutationOutcome.ConcurrencyConflict);
        }

        await transaction.CommitAsync(cancellationToken);
        return TicketMutationResult.Success(TicketQueryAppService.ToDetailDto(ticket));
    }
}
