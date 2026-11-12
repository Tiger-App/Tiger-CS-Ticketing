using TigerCS.Application.Modules.Collections.TowerAssignments;
using TigerCS.Application.Modules.CustomerVerification.Abstractions;
using TigerCS.Application.Abstractions;
using TigerCS.Application.Authorization;
using TigerCS.Application.Modules.IdentityAndAccess.Abstractions;
using TigerCS.Application.Modules.IdentityAndAccess.Services;
using TigerCS.Application.Modules.Notifications.Services;
using TigerCS.Domain.Modules.IdentityAndAccess;
using TigerCS.Domain.Modules.IdentityAndAccess.Permissions;
using TigerCS.Application.Modules.Ticketing.Abstractions;
using TigerCS.Application.Modules.Ticketing.Dto;
using TigerCS.Application.Modules.WorkflowConfiguration.Abstractions;
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
    IUserPermissionOverrideRepository? permissionOverrides = null,
    ITicketInteractionHandlerRepository? handlerRepository = null,
    IServiceIdentityRegistry? serviceIdentities = null,
    IRequestTypeRepository? requestTypeRepository = null,
    CollectionsTeam? collectionsTeam = null,
    CollectionsTicketRoutingService? collectionsRouting = null,
    CollectionsTicketTowerResolver? collectionsTowerResolver = null,
    ITicketAssignmentHistoryRepository? assignmentHistory = null,
    CollectionsTransferGuard? collectionsGuard = null,
    IOutboxWriter? outboxWriter = null,
    IUserTransferDepartmentRepository? transferDepartments = null)
{
    /// <summary>Customer "assigned" and colleague "assigned to you" notification events, written in the operation's own transaction.</summary>
    private readonly TicketNotificationEvents? _notificationEvents = TicketNotificationEvents.CreateOrNull(outboxWriter, auditWriter);

    /// <summary>
    /// Safe retry of a failed automatic CS Agent assignment. Authority is cross-department assignment (CS Manager; the administrator
    /// override applies as for Assign). Idempotent: an already-assigned ticket is returned untouched. A second concurrent retry loses at
    /// the row version / unique current-assignment index and is reported as a conflict, never a double assignment.
    /// </summary>
    public async Task<TicketMutationResult> RetryCsAgentAssignmentAsync(
        Guid callerEmployeeId,
        IReadOnlyCollection<string> callerRoles,
        long ticketId,
        CancellationToken cancellationToken = default)
    {
        var ticket = await ticketRepository.GetByIdAsync(ticketId, cancellationToken);
        if (ticket is null)
        {
            return TicketMutationResult.Failure(TicketMutationOutcome.NotFound);
        }

        var authorized = await AuthorizationGate.EvaluateAsync(callerRoles, () =>
            Task.FromResult(callerRoles.Any(TicketRoleSets.AssignCrossDepartment.Contains)));
        if (!authorized)
        {
            return TicketMutationResult.Failure(TicketMutationOutcome.Forbidden);
        }

        if (ticket.TicketStatus is TicketStatus.Closed or TicketStatus.Resolved)
        {
            return TicketMutationResult.Failure(TicketMutationOutcome.TicketClosed);
        }

        if (ticket.CurrentOwnerEmployeeId is not null)
        {
            return TicketMutationResult.Success(TicketQueryAppService.ToDetailDto(ticket));
        }

        var now = timeProvider.GetUtcNow().UtcDateTime;
        var correlationId = Guid.NewGuid();
        await using var transaction = await unitOfWork.BeginTransactionAsync(cancellationToken);

        var result = await autoAssignmentService.EnsureCsAgentAsync(
            ticket, preferredOwnerEmployeeId: null, AutoAssignmentTrigger.ManualRetry, now, correlationId, cancellationToken);

        await auditWriter.WriteAsync(
            callerEmployeeId, "RetryCsAgentAssignment", "Ticket", ticketId.ToString(),
            beforeValue: "Unassigned",
            afterValue: $"Outcome={result.Outcome};AssignedEmployeeId={result.AssignedEmployeeId?.ToString() ?? "none"}",
            correlationId, cancellationToken);

        try
        {
            await unitOfWork.SaveChangesAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is TicketConcurrentlyModifiedException or DuplicateWriteException)
        {
            return TicketMutationResult.Failure(TicketMutationOutcome.ConcurrencyConflict);
        }

        await transaction.CommitAsync(cancellationToken);
        return result.AssignedEmployeeId is null
            ? TicketMutationResult.Failure(TicketMutationOutcome.NoEligibleCsAgent)
            : TicketMutationResult.Success(TicketQueryAppService.ToDetailDto(ticket));
    }

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

        // An explicit denial of Tickets.Assign always blocks (the endpoint gate enforces it too; the service repeats it so no other caller can bypass it).
        // Reassignment authority comes only from this permission and the role / team rules below - never from which departments the caller can view
        // or may transfer to.
        if (await CallerPermissionOverrides.GetAsync(permissionOverrides, callerEmployeeId, Permissions.TicketsAssign, cancellationToken) == PermissionEffect.Deny)
        {
            return TicketMutationResult.Failure(TicketMutationOutcome.Forbidden);
        }

        // Collections tickets: the Collections team itself (not just its supervisors) may assign and reassign within the team. Decided server-side from the
        // ticket's department and the caller's membership; it adds nothing for tickets of any other department.
        var isCollectionsTicket = collectionsTeam is not null
            && await collectionsTeam.IsCollectionsDepartmentAsync(ticket.CurrentDepartmentId, cancellationToken);

        var authorized = await AuthorizationGate.EvaluateAsync(callerRoles, async () =>
            callerRoles.Any(TicketRoleSets.AssignCrossDepartment.Contains)
            || (callerRoles.Any(TicketRoleSets.AssignWithinOwnDepartment.Contains)
                && await userDepartmentAssignmentRepository.ExistsAsync(callerEmployeeId, ticket.CurrentDepartmentId, cancellationToken))
            || (isCollectionsTicket
                && await collectionsTeam!.CanAssignTicketsAsync(callerEmployeeId, callerRoles, ticket.CurrentDepartmentId, cancellationToken)));

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

        // Reassigning to the person who already owns the ticket changes nothing; say so instead of writing a no-op history row.
        // (After the authorization gate, so it never reveals who owns a ticket to a caller who may not touch it.)
        if (ticket.CurrentOwnerEmployeeId is { } existingOwner && existingOwner == request.AssignedEmployeeId)
        {
            return TicketMutationResult.Failure(TicketMutationOutcome.AlreadyAssignedToEmployee);
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
        // Collections: the reason is optional (it is recorded in the assignment history when given).
        if (!isCollectionsTicket
            && ticket.CurrentOwnerEmployeeId is { } currentOwner
            && currentOwner != request.AssignedEmployeeId
            && request.ReasonCode is (null or TicketTransferReason.Unspecified or TicketTransferReason.SystemRouting))
        {
            return TicketMutationResult.Failure(TicketMutationOutcome.TransferReasonRequired);
        }

        // MVP-API-Contracts.md §3.5's own validation: AssignedEmployeeId
        // must be an active member of the ticket's CurrentDepartmentId —
        // checked against the department the assignment target actually
        // needs to belong to, never trusted from the caller's own claims.
        // A Collections ticket can only go to an ACTIVE member of the Collections team (any of them - the tower owner is not required for a manual assignment).
        if (isCollectionsTicket
            ? !await collectionsTeam!.IsActiveMemberAsync(request.AssignedEmployeeId, ticket.CurrentDepartmentId, cancellationToken)
            : !await IsActiveDepartmentMemberAsync(request.AssignedEmployeeId, ticket.CurrentDepartmentId, cancellationToken))
        {
            // Not a member of the ticket's department, or a member whose account is deactivated: a deactivated employee never receives a ticket.
            return TicketMutationResult.Failure(TicketMutationOutcome.EmployeeNotInDepartment);
        }

        ticketRepository.SetRowVersion(ticket, request.RowVersion);

        var now = timeProvider.GetUtcNow().UtcDateTime;
        var previousOwnerEmployeeId = ticket.CurrentOwnerEmployeeId;

        await using var transaction = await unitOfWork.BeginTransactionAsync(cancellationToken);

        var currentAssignment = await ticketAssignmentRepository.GetCurrentAsync(ticketId, cancellationToken);
        currentAssignment?.MarkSuperseded();

        ticket.AssignTo(request.AssignedEmployeeId);

        var correlationId = Guid.NewGuid();
        // Reassignment never changes an existing Original Human Agent; it only fills the gap when the first eligible agent is assigned.
        await autoAssignmentService.RecordOriginalHumanAgentAsync(ticket, request.AssignedEmployeeId, "ManualAssign", correlationId, cancellationToken);

        await ticketAssignmentRepository.AddAsync(
            new TicketAssignment(ticketId, request.AssignedEmployeeId, ticket.CurrentDepartmentId, now, callerEmployeeId),
            cancellationToken);

        await auditWriter.WriteAsync(
            callerEmployeeId, "Assign", "Ticket", ticketId.ToString(),
            beforeValue: previousOwnerEmployeeId?.ToString(), afterValue: request.AssignedEmployeeId.ToString(),
            correlationId, cancellationToken);

        if (_notificationEvents is not null)
        {
            await _notificationEvents.QueueAssignedAsync(
                ticket, previousOwnerEmployeeId, request.AssignedEmployeeId, callerEmployeeId, correlationId, now, cancellationToken);
        }

        if (isCollectionsTicket && previousOwnerEmployeeId != request.AssignedEmployeeId)
        {
            await RecordCollectionsAssignmentAsync(
                ticket, previousOwnerEmployeeId, request.AssignedEmployeeId, TicketAssignmentKind.Manual, callerEmployeeId, now, request.Reason, cancellationToken);
        }

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
        catch (Exception ex) when (ex is TicketConcurrentlyModifiedException or DuplicateWriteException)
        {
            // Lost the race: another request changed the ticket, or already made someone its current assignee (unique current assignment).
            return TicketMutationResult.Failure(TicketMutationOutcome.ConcurrencyConflict);
        }

        await transaction.CommitAsync(cancellationToken);
        return TicketMutationResult.Success(TicketQueryAppService.ToDetailDto(ticket));
    }

    private async Task<bool> IsActiveDepartmentMemberAsync(Guid employeeId, int departmentId, CancellationToken cancellationToken) =>
        (await userDepartmentAssignmentRepository.GetByDepartmentIdAsync(departmentId, activeEmployeesOnly: true, cancellationToken))
            .Any(a => a.EmployeeId == employeeId);

    /// <summary>
    /// Transfer authority is the Call Center layer, scoped per ticket — never global: a CS Manager may transfer any ticket; a CS Agent or
    /// CS Supervisor only a ticket currently assigned to them, and a CS Supervisor also a ticket of a department they belong to.
    /// Department Head, Department Employee and every other role are forbidden. After the move, the target department's rule or Round Robin
    /// picks the assignee (never a random employee). A ticket leaving the Call Center that is classified can only go to its request type's
    /// department (except for the CS Manager's administrative transfer).
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

        // An explicit denial of Tickets.Transfer always blocks, the System Administrator included.
        var transferOverride = await CallerPermissionOverrides.GetAsync(permissionOverrides, callerEmployeeId, Permissions.TicketsTransfer, cancellationToken);
        if (transferOverride == PermissionEffect.Deny)
        {
            return TicketMutationResult.Failure(TicketMutationOutcome.Forbidden);
        }

        // Tickets.TransferAny ("Transfer Any Ticket"): an explicit grant lets a Call Center employee transfer any open ticket, whatever its
        // department, to any active department. It is separate from department membership, Tickets.Assign, user administration and the
        // System Administrator override, and it changes nothing else about the transfer (status, reasons, SLA, audit, Collections Tower/Unit).
        // An explicit denial removes the CS Manager's built-in any-ticket reach (the ordinary per-ticket rule below still applies to them).
        var transferAnyOverride = await TransferAnyTicketPolicy.GetOverrideAsync(permissionOverrides, callerEmployeeId, cancellationToken);
        var holdsTransferAnyGrant = transferAnyOverride == PermissionEffect.Grant && TransferAnyTicketPolicy.IsEligibleRole(callerRoles);
        var hasRoleTransferAny = callerRoles.Any(TicketRoleSets.TransferAnyTicket.Contains) && transferAnyOverride != PermissionEffect.Deny;

        var transferAuthorized = await AuthorizationGate.EvaluateAsync(callerRoles, async () =>
            hasRoleTransferAny
            || holdsTransferAnyGrant
            || (callerRoles.Any(TicketRoleSets.Transfer.Contains)
                && (ticket.CurrentOwnerEmployeeId == callerEmployeeId
                    || await TicketOwnershipRules.IsActingInteractionHandlerAsync(
                        handlerRepository, userDepartmentAssignmentRepository, callerEmployeeId, callerRoles, ticket, cancellationToken, ticketAssignmentRepository)
                    || (callerRoles.Contains(Roles.CsSupervisor)
                        && await userDepartmentAssignmentRepository.ExistsAsync(callerEmployeeId, ticket.CurrentDepartmentId, cancellationToken)))));

        // A Tickets.Transfer grant stands in for the Transfer role only. It never widens visibility: the grantee must still be able
        // to modify THIS ticket (their department / a ticket they handle).
        if (!transferAuthorized
            && transferOverride == PermissionEffect.Grant
            && !Roles.IsReadOnlyCaller(callerRoles)
            && await TicketVisibilityRule.CanModifyTicketAsync(
                userDepartmentAssignmentRepository, handlerRepository, callerEmployeeId, callerRoles, ticket, serviceIdentities, cancellationToken))
        {
            transferAuthorized = true;
        }

        var isAdministrativeTransfer = AuthorizationOverride.AppliesTo(callerRoles) || hasRoleTransferAny || holdsTransferAnyGrant;
        var fromCallCenter = !isAdministrativeTransfer
            && await autoAssignmentService.IsCallCenterDepartmentAsync(ticket.CurrentDepartmentId, cancellationToken);

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

        // A Call Center agent transfers by Request Type: once the ticket is classified, the destination is the department that owns its
        // request type. (A CS Manager keeps the unrestricted manual transfer.)
        if (fromCallCenter
            && ticket.RequestTypeId is { } classifiedRequestTypeId
            && requestTypeRepository is not null
            && await requestTypeRepository.GetByIdAsync(classifiedRequestTypeId, cancellationToken) is { } classifiedRequestType
            && classifiedRequestType.DepartmentId != request.TargetDepartmentId)
        {
            return TicketMutationResult.Failure(TicketMutationOutcome.TransferTargetMustMatchRequestType);
        }

        // Workflow/Automation phase 2 — same narrowing-only rule as
        // AssignAsync: the SOURCE department's settings may disable
        // transferring tickets out of it; nothing here widens the CS-Manager
        // role gate above.
        var sourceSettings = await departmentWorkflowSettingsRepository.GetByDepartmentIdAsync(
            ticket.CurrentDepartmentId, cancellationToken);
        if (!fromCallCenter && sourceSettings is { AllowTransferToOtherDepartments: false })
        {
            return TicketMutationResult.Failure(TicketMutationOutcome.DisabledByDepartmentSettings);
        }

        var targetDepartment = await departmentRepository.GetByIdAsync(request.TargetDepartmentId, cancellationToken);
        if (targetDepartment is null || !targetDepartment.IsActive)
        {
            return TicketMutationResult.Failure(TicketMutationOutcome.TargetDepartmentInactive);
        }

        // The destination must be one the caller is authorised to transfer to. Judged on the department actually requested (never on the departments
        // the caller can merely view), and before any write.
        // A Transfer Any Ticket grant means "any active department" (checked above), so the per-user destination list does not narrow it.
        if (!holdsTransferAnyGrant
            && !await TransferDestinationPolicy.IsPermittedAsync(transferDepartments, callerEmployeeId, callerRoles, request.TargetDepartmentId, cancellationToken))
        {
            return TicketMutationResult.Failure(TicketMutationOutcome.TransferDestinationNotPermitted);
        }

        // Transfer-and-assign: the explicit assignee must belong to the
        // department the ticket is MOVING TO — the same "assignee belongs to
        // the ticket's (resulting) department" rule AssignAsync enforces,
        // checked against the target rather than the source, and checked
        // before any write so a wrong choice changes nothing.
        if (request.AssignToEmployeeId is { } assignee
            && !await IsActiveDepartmentMemberAsync(assignee, request.TargetDepartmentId, cancellationToken))
        {
            return TicketMutationResult.Failure(TicketMutationOutcome.EmployeeNotInDepartment);
        }

        // Into Collections a verified Tower AND Unit are mandatory. Refused here, before any write, so the department and the assignee stay exactly as they were.
        var collectionsDecision = collectionsGuard is null
            ? CollectionsTransferDecision.NotCollections
            : await collectionsGuard.CheckAsync(
                ticket, request.TargetDepartmentId, request.CollectionsTowerId, request.CollectionsUnitRef, cancellationToken, towerOptional: true);
        if (collectionsDecision.Refusal is { } refusal)
        {
            return TicketMutationResult.Failure(refusal);
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
        // The CS Agent and the department are independent fields: the transfer moves the department and KEEPS the agent. The agent changes
        // only by an explicit reassignment (AssignToEmployeeId below, or a later Assign). A ticket with no agent gets one from the shared rotation.
        ticket.TransferToDepartment(request.TargetDepartmentId, preserveOwner: true);

        await auditWriter.WriteAsync(
            callerEmployeeId, "Transfer", "Ticket", ticketId.ToString(),
            beforeValue: $"DepartmentId={previousDepartmentId};AssignedEmployeeId={previousOwnerEmployeeId?.ToString() ?? "DepartmentQueue"}",
            afterValue: $"DepartmentId={request.TargetDepartmentId};Reason={request.Reason}"
                + (previousOwnerEmployeeId is not null ? ";CsAgentPreserved=true" : string.Empty)
                + (collectionsDecision.Unit is { } selected ? ";" + CollectionsTransferGuard.Describe(selected) : string.Empty),
            correlationId, cancellationToken);

        if (collectionsDecision.Unit is { } verifiedUnit)
        {
            await collectionsGuard!.StoreAsync(ticket, verifiedUnit, callerEmployeeId, now, cancellationToken);
        }

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
            await autoAssignmentService.RecordOriginalHumanAgentAsync(ticket, explicitAssignee, "TransferAndAssign", correlationId, cancellationToken);

            await ticketAssignmentRepository.AddAsync(
                new TicketAssignment(ticketId, explicitAssignee, request.TargetDepartmentId, now, callerEmployeeId),
                cancellationToken);

            await auditWriter.WriteAsync(
                callerEmployeeId, "Assign", "Ticket", ticketId.ToString(),
                beforeValue: previousOwnerEmployeeId?.ToString() ?? "DepartmentQueue",
                afterValue: $"{explicitAssignee};DepartmentId={request.TargetDepartmentId};ViaTransfer=true",
                correlationId, cancellationToken);

            if (_notificationEvents is not null)
            {
                await _notificationEvents.QueueAssignedAsync(
                    ticket, previousOwnerEmployeeId, explicitAssignee, callerEmployeeId, correlationId, now, cancellationToken);
            }

            // An explicit choice into Collections is a manual assignment: recorded as such, and never overwritten by tower routing.
            if (collectionsTeam is not null && await collectionsTeam.IsCollectionsDepartmentAsync(request.TargetDepartmentId, cancellationToken))
            {
                await RecordCollectionsAssignmentAsync(
                    ticket, previousOwnerEmployeeId, explicitAssignee, TicketAssignmentKind.Manual, callerEmployeeId, now, request.Reason, cancellationToken);
            }
        }
        else if (collectionsRouting is not null
            && (await collectionsRouting.RouteAsync(
                ticket, departmentEntry: true, fromCallCenter: await autoAssignmentService.IsCallCenterDepartmentAsync(previousDepartmentId, cancellationToken),
                now, correlationId, cancellationToken)).Handled)
        {
            // Into Collections: the collector responsible for the unit's tower this month took it, or (unknown tower / nobody assigned) it waits in the Collections queue.
            // Tower routing owns this decision, so neither the department rule nor the CS Agent rotation picks someone for it.
        }
        else if (await autoAssignmentService.IsCallCenterDepartmentAsync(request.TargetDepartmentId, cancellationToken)
            && ticket.ActiveOriginalHumanAgentId is not null
            && (await autoAssignmentService.ReturnToOriginalHumanAgentAsync(ticket, callerEmployeeId, now, correlationId, cancellationToken)).Outcome
                is not TicketAutoAssignmentService.OriginalAgentReturn.NoOriginalAgent)
        {
            // Back to the Call Center: the Original Human Agent takes it again (not the general queue / rotation). OriginalHumanAgentId is untouched.
            // When that agent is inactive or ineligible the carried-over owner was released, the reason audited and the ticket left UNASSIGNED in the
            // Call Center queue with automatic assignment suspended (ReturnToOriginalHumanAgentAsync). No department rule and no rotation runs here.
        }
        else if (ticket.CurrentOwnerEmployeeId is null)
        {
            // ...THEN re-evaluate the assignment automation against the new
            // department + the ticket's request type. Every non-assignable case
            // (no rule, rule targets someone outside department C, settings
            // disable assignment) leaves the ticket in the NEW department's
            // queue — audited, and never a randomly picked employee.
            var configured = await autoAssignmentService.ApplyAsync(
                ticket, now, correlationId, AutoAssignmentTrigger.DepartmentTransfer, cancellationToken);

            // No rule decided: the ONE shared CS Agent rotation picks the agent (and a failure is recorded, never silent) — see
            // ApplyRoundRobinFallbackAsync. Only reached for a ticket that had no agent to preserve.
            await autoAssignmentService.ApplyRoundRobinFallbackAsync(
                ticket, configured, AutoAssignmentTrigger.DepartmentTransfer, now, correlationId, cancellationToken);
        }

        // Ticket transfer tracking: one typed event per transfer — who moved it, to which department and (when one took
        // it, explicitly or by the target department's rule) which employee, why, and when. Same transaction and
        // correlation id as the Transfer/Assign audit entries above.
        if (workflowEventRepository is not null)
        {
            await workflowEventRepository.AddAsync(
                TicketWorkflowEvent.ForTransfer(
                    ticketId, now, callerEmployeeId, request.ReasonCode ?? TicketTransferReason.Unspecified,
                    previousDepartmentId, request.TargetDepartmentId,
                    // Who TOOK it: a CS Agent that merely stayed on the ticket is not a receiving employee.
                    ticket.CurrentOwnerEmployeeId != previousOwnerEmployeeId ? ticket.CurrentOwnerEmployeeId : null,
                    request.Reason, correlationId),
                cancellationToken);
        }

        try
        {
            await unitOfWork.SaveChangesAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is TicketConcurrentlyModifiedException or DuplicateWriteException)
        {
            // Lost the race: another request changed the ticket, or already made someone its current assignee (unique current assignment).
            return TicketMutationResult.Failure(TicketMutationOutcome.ConcurrencyConflict);
        }

        await transaction.CommitAsync(cancellationToken);
        return TicketMutationResult.Success(TicketQueryAppService.ToDetailDto(ticket));
    }

    /// <summary>Writes one Collections assignment-history entry. The tower is looked up best-effort for context only: a failure there never blocks the assignment.</summary>
    private async Task RecordCollectionsAssignmentAsync(
        Ticket ticket, Guid? previousEmployeeId, Guid newEmployeeId, TicketAssignmentKind kind, Guid? changedBy, DateTime nowUtc, string? reason,
        CancellationToken cancellationToken)
    {
        if (assignmentHistory is null)
        {
            return;
        }

        TicketTowerResolution? context = null;
        if (collectionsTowerResolver is not null)
        {
            try { context = await collectionsTowerResolver.ResolveAsync(ticket, cancellationToken); }
            catch (Exception ex) when (ex is not OperationCanceledException) { context = null; }
        }

        await assignmentHistory.AddAsync(
            new TicketAssignmentHistoryEntry(
                ticket.TicketId, ticket.CurrentDepartmentId, previousEmployeeId, newEmployeeId, kind, changedBy, nowUtc, reason,
                context?.Tower?.TowerId, context?.Tower?.TowerCode, context?.Tower?.TowerName,
                context?.UnitNumber ?? ticket.CrmBuyerUnitNumber ?? ticket.ManualUnitNumber),
            cancellationToken);
    }
}
