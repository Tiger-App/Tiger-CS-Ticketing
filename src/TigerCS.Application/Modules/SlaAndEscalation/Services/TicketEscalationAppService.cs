using TigerCS.Application.Abstractions;
using TigerCS.Application.Authorization;
using TigerCS.Application.Modules.CustomerVerification.Abstractions;
using TigerCS.Application.Modules.IdentityAndAccess.Abstractions;
using TigerCS.Application.Modules.SlaAndEscalation.Abstractions;
using TigerCS.Application.Modules.SlaAndEscalation.Dto;
using TigerCS.Application.Modules.Ticketing.Abstractions;
using TigerCS.Application.Modules.Ticketing.Services;
using TigerCS.Domain.Modules.IdentityAndAccess;
using TigerCS.Domain.Modules.SlaAndEscalation;
using TigerCS.Domain.Modules.Ticketing;

namespace TigerCS.Application.Modules.SlaAndEscalation.Services;

/// <summary>
/// Manual escalation (MVP-API-Contracts.md §5.7, FR-ESC-01, backlog S-11).
///
/// <para>
/// <b>Escalation is a dimension, not a status.</b> Nothing here reads or
/// writes <c>TicketStatus</c>, <c>ResolutionOutcome</c> or
/// <c>DuplicateOfTicketId</c>: a ticket escalated to any level stays exactly
/// as Open or InProgress as it was, which is the whole reason ADR-0008 split
/// the five dimensions apart in the first place ("a ticket may remain In
/// Progress while escalated" cannot be expressed in a single-status model).
/// </para>
///
/// <para>
/// <b>Level 4 is manual-only and doubly gated.</b> MVP-ERD.md §2.17 restricts
/// <c>ManualLevel4</c> rows to a CS Manager or GM actor, and
/// MVP-API-Contracts.md §5.7 answers a violation with <c>403</c>. Because the
/// contract carries <c>Level</c> and <c>TriggerType</c> as two independent
/// client-supplied fields, the role gate alone is not sufficient: a CS Agent
/// could otherwise send <c>Level = 4</c> with <c>TriggerType = ManualFlag</c>
/// and reach Level 4 without ever meeting the gate. The pairing is therefore
/// rejected as well — closing a bypass of the approved rule, not adding a new
/// one.
/// </para>
/// </summary>
public sealed class TicketEscalationAppService(
    ITicketRepository ticketRepository,
    ITicketEscalationRepository escalationRepository,
    ITicketStatusHistoryRepository statusHistoryRepository,
    IUserDepartmentAssignmentRepository userDepartmentAssignmentRepository,
    ITicketingUnitOfWork unitOfWork,
    IAuditEntryWriter auditWriter,
    TimeProvider timeProvider,
    ITicketInteractionHandlerRepository? handlerRepository = null,
    IServiceIdentityRegistry? serviceIdentities = null)
{
    public async Task<SlaOperationResult<TicketEscalationResponseDto>> EscalateAsync(
        Guid callerEmployeeId,
        IReadOnlyCollection<string> callerRoles,
        long ticketId,
        ManualEscalationRequestDto request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var ticket = await ticketRepository.GetByIdAsync(ticketId, cancellationToken);
        if (ticket is null)
        {
            return SlaOperationResult<TicketEscalationResponseDto>.Failure(SlaOperationOutcome.NotFound);
        }

        if (!Enum.TryParse<EscalationTriggerType>(request.TriggerType, ignoreCase: true, out var triggerType)
            || triggerType is not (EscalationTriggerType.ManualFlag or EscalationTriggerType.ManualLevel4))
        {
            // The two automatic trigger types are system-generated and are
            // deliberately not client-callable (MVP-API-Contracts.md §5.7:
            // automatic escalation is "not exposed as a client-callable
            // endpoint, since it's system-triggered").
            return SlaOperationResult<TicketEscalationResponseDto>.Failure(SlaOperationOutcome.InvalidRequest);
        }

        if (!Enum.IsDefined(typeof(EscalationLevel), request.Level) || request.Level == (byte)EscalationLevel.None)
        {
            return SlaOperationResult<TicketEscalationResponseDto>.Failure(SlaOperationOutcome.InvalidRequest);
        }

        var requestedLevel = (EscalationLevel)request.Level;

        // Checked before authorization so a mis-paired request is answered
        // the same way for every caller, rather than leaking which roles
        // would have passed the Level 4 gate.
        if ((requestedLevel == EscalationLevel.Level4) != (triggerType == EscalationTriggerType.ManualLevel4))
        {
            return SlaOperationResult<TicketEscalationResponseDto>.Failure(SlaOperationOutcome.EscalationLevelTriggerMismatch);
        }

        // Two server-side checks, both before anything is written: the caller's ROLE may escalate (IsAuthorizedAsync), and the caller may
        // act on THIS ticket (TicketVisibilityRule.PassesAgentScopeAsync - a restricted CS Agent needs full access to it). The API's
        // RequireTicketAccess filter applies the second check in front of the controller; it is repeated here so the service never relies
        // on its caller (a CS Agent's cross-department escalate right must not reach a ticket assigned to someone else).
        if (!await IsAuthorizedAsync(callerEmployeeId, callerRoles, ticket, triggerType, cancellationToken)
            || !await TicketVisibilityRule.PassesAgentScopeAsync(
                userDepartmentAssignmentRepository, handlerRepository, callerEmployeeId, callerRoles, ticket, serviceIdentities, cancellationToken))
        {
            return SlaOperationResult<TicketEscalationResponseDto>.Failure(SlaOperationOutcome.Forbidden);
        }

        if (ticket.TicketStatus == TicketStatus.Closed)
        {
            return SlaOperationResult<TicketEscalationResponseDto>.Failure(SlaOperationOutcome.TicketClosed);
        }

        if (requestedLevel <= ticket.EscalationLevel)
        {
            return SlaOperationResult<TicketEscalationResponseDto>.Failure(SlaOperationOutcome.EscalationLevelNotAnAdvance);
        }

        ticketRepository.SetRowVersion(ticket, request.RowVersion);

        var nowUtc = timeProvider.GetUtcNow().UtcDateTime;
        var correlationId = Guid.NewGuid();
        var previousLevel = ticket.EscalationLevel;

        await using var transaction = await unitOfWork.BeginTransactionAsync(cancellationToken);

        try
        {
            ticket.RaiseEscalationLevel(requestedLevel);
        }
        catch (TicketClosedException)
        {
            return SlaOperationResult<TicketEscalationResponseDto>.Failure(SlaOperationOutcome.TicketClosed);
        }
        catch (EscalationLevelCannotBeLoweredException)
        {
            return SlaOperationResult<TicketEscalationResponseDto>.Failure(SlaOperationOutcome.EscalationLevelNotAnAdvance);
        }

        var escalation = TicketEscalation.RaiseManual(ticketId, request.Level, triggerType, nowUtc);
        await escalationRepository.AddAsync(escalation, cancellationToken);

        await statusHistoryRepository.AddAsync(
            new TicketStatusHistory(
                ticketId, TicketStatusDimension.EscalationLevel, (byte)previousLevel, request.Level,
                callerEmployeeId, actorIsSystem: false, request.Note, correlationId, nowUtc),
            cancellationToken);

        await auditWriter.WriteAsync(
            callerEmployeeId, "EscalateTicket", nameof(TicketEscalation), ticketId.ToString(),
            beforeValue: $"{{\"escalationLevel\":\"{previousLevel}\"}}",
            afterValue:
                $"{{\"escalationLevel\":\"{requestedLevel}\",\"level\":{request.Level},"
                + $"\"triggerType\":\"{triggerType}\",\"notifiedRoles\":\"{escalation.NotifiedRoles}\"}}",
            correlationId, cancellationToken);

        try
        {
            await unitOfWork.SaveChangesAsync(cancellationToken);
        }
        catch (TicketConcurrentlyModifiedException)
        {
            return SlaOperationResult<TicketEscalationResponseDto>.Failure(SlaOperationOutcome.ConcurrencyConflict);
        }

        await transaction.CommitAsync(cancellationToken);

        return SlaOperationResult<TicketEscalationResponseDto>.Success(SlaQueryAppService.ToDto(escalation));
    }

    /// <summary>
    /// MVP-API-Contracts.md §5.7's two-tier rule: "Agent and above for
    /// <c>ManualFlag</c>; CS Manager or GM only for <c>ManualLevel4</c>".
    /// Both tiers are permission rules, so both run through the gate and pick
    /// up the ADR-0024 override without naming the role.
    /// </summary>
    private Task<bool> IsAuthorizedAsync(
        Guid callerEmployeeId,
        IReadOnlyCollection<string> callerRoles,
        Ticket ticket,
        EscalationTriggerType triggerType,
        CancellationToken cancellationToken) =>
        AuthorizationGate.EvaluateAsync(callerRoles, async () =>
        {
            if (triggerType == EscalationTriggerType.ManualLevel4)
            {
                // Deliberately not widened by ownership or department
                // membership: MVP-ERD.md §2.17 names the two roles and no
                // other basis.
                return callerRoles.Any(SlaRoleSets.ManualLevel4Escalate.Contains);
            }

            if (Roles.IsReadOnlyCaller(callerRoles) || !callerRoles.Any(SlaRoleSets.ManualEscalate.Contains))
            {
                return false;
            }

            if (ticket.CurrentOwnerEmployeeId == callerEmployeeId)
            {
                return true;
            }

            // Department-side roles are scoped to the ticket's own
            // department (Security-Architecture.md §3); the CS layer is
            // inherently cross-department.
            if (callerRoles.Contains(Roles.DepartmentEmployee) || callerRoles.Contains(Roles.DepartmentHead))
            {
                return await userDepartmentAssignmentRepository.ExistsAsync(
                    callerEmployeeId, ticket.CurrentDepartmentId, cancellationToken);
            }

            return true;
        });
}
