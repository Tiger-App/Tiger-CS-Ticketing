using TigerCS.Application.Abstractions;
using TigerCS.Application.Authorization;
using TigerCS.Application.Modules.IdentityAndAccess.Abstractions;
using TigerCS.Application.Modules.SlaAndEscalation.Services;
using TigerCS.Application.Modules.Ticketing.Abstractions;
using TigerCS.Application.Modules.Ticketing.Dto;
using TigerCS.Application.Modules.WorkflowConfiguration.Abstractions;
using TigerCS.Domain.Modules.Ticketing;

namespace TigerCS.Application.Modules.Ticketing.Services;

/// <summary>
/// The Unclassified → Classified transition: an agent has read the inquiry
/// and records what the customer actually wants.
///
/// <para>
/// <b>The same ticket is classified in place.</b> No second ticket is
/// created, nothing is copied, and the ticket keeps its number, its
/// department, its interactions and its whole history — a customer who
/// started one conversation has one ticket, before and after.
/// </para>
///
/// <para>
/// <b>The SLA clock is never restarted here.</b> A Genesys ticket is created
/// on the default (Normal) priority with its SLA period already running from
/// creation, so classifying it adds the category (and request type) to the
/// running period: a more urgent priority tightens it under the earlier-of
/// rule, a less urgent one needs the approved-downgrade flow. Only a ticket
/// that has <i>no</i> priority and no period (created before default
/// priorities existed, or while none was configured) starts its clock here —
/// from <c>now</c>, the classification timestamp, never backdated, since no
/// target existed before.
/// </para>
///
/// <para>
/// <b>First Response is measured separately and is unaffected.</b>
/// <c>Ticket.FirstHumanResponseAtUtc</c> lives on the ticket, not on the SLA
/// period, so an agent's first reply to an unclassified Genesys inquiry is
/// recorded with its real timestamp exactly as on any other ticket — how
/// quickly the customer reached a human stays measurable from the moment the
/// inquiry arrived, whether or not anyone has classified it yet.
/// </para>
///
/// <para>
/// A ticket that was created classified is untouched by all of this: it
/// already has its category, its priority and its SLA period, and this
/// service performs <i>initial</i> classification only
/// (<see cref="TicketMutationOutcome.AlreadyClassified"/>). That refusal is a
/// Phase 1 safeguard while reclassification semantics are undefined — see
/// <see cref="TicketAlreadyClassifiedException"/> — not a rule that a
/// classification may never change.
/// </para>
/// </summary>
public sealed class TicketClassificationAppService(
    ITicketRepository ticketRepository,
    ICategoryRepository categoryRepository,
    IPriorityRepository priorityRepository,
    IRequestTypeRepository requestTypeRepository,
    IWorkflowTemplateRepository workflowTemplateRepository,
    IUserDepartmentAssignmentRepository userDepartmentAssignmentRepository,
    ITicketStatusHistoryRepository statusHistoryRepository,
    ITicketingUnitOfWork unitOfWork,
    IAuditEntryWriter auditWriter,
    SlaDueDateService slaDueDateService,
    TimeProvider timeProvider,
    ITicketInteractionHandlerRepository? handlerRepository = null,
    IServiceIdentityRegistry? serviceIdentities = null,
    IDepartmentRepository? departmentRepository = null,
    TicketAutoAssignmentService? autoAssignmentService = null,
    ITicketWorkflowEventRepository? workflowEventRepository = null,
    DefaultClassificationService? defaultClassification = null,
    TigerCS.Application.Modules.Collections.TowerAssignments.CollectionsTicketRoutingService? collectionsRouting = null)
{
    public async Task<TicketMutationResult> ClassifyAsync(
        Guid callerEmployeeId,
        IReadOnlyCollection<string> callerRoles,
        long ticketId,
        ClassifyTicketRequestDto request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var ticket = await ticketRepository.GetByIdAsync(ticketId, cancellationToken);
        if (ticket is null)
        {
            return TicketMutationResult.Failure(TicketMutationOutcome.NotFound);
        }

        // Classifying is ordinary work on a ticket you can already see and
        // work: the same department-scoped authority that governs status
        // changes, never a new privilege tier.
        var authorized = await AuthorizationGate.EvaluateAsync(callerRoles, async () =>
            !Domain.Modules.IdentityAndAccess.Roles.IsReadOnlyCaller(callerRoles)
            && (callerRoles.Any(TicketRoleSets.CrossDepartmentSupervisory.Contains)
            || callerRoles.Contains(Domain.Modules.IdentityAndAccess.Roles.CsAgent)
            || await userDepartmentAssignmentRepository.ExistsAsync(callerEmployeeId, ticket.CurrentDepartmentId, cancellationToken)));

        // CS Agent is cross-department for this action; a restricted agent may classify only a ticket they can see.
        if (!authorized
            || !await TicketVisibilityRule.PassesAgentScopeAsync(
                userDepartmentAssignmentRepository, handlerRepository, callerEmployeeId, callerRoles, ticket, serviceIdentities, cancellationToken))
        {
            return TicketMutationResult.Failure(TicketMutationOutcome.Forbidden);
        }

        if (ticket.TicketStatus == TicketStatus.Closed)
        {
            return TicketMutationResult.Failure(TicketMutationOutcome.TicketClosed);
        }

        // A priority DECREASE on a ticket that already carries a priority is
        // a downgrade and takes effect only through an approved request
        // (ISSUE-023 Option B). A Genesys ticket arrives with the DEFAULT
        // priority, so classifying it as less urgent than that default is a
        // downgrade too; only a ticket with no priority at all (one from
        // before defaults existed) is setting its first.
        if (ticket.PriorityId is { } currentPriority && request.PriorityId > currentPriority)
        {
            return TicketMutationResult.Failure(TicketMutationOutcome.DowngradeRequiresApproval);
        }

        // A ticket on the default classification (Customer Services / General) is a placeholder, not a judgement: an authorized user may
        // replace it once the real classification is known. Any other existing classification stays write-once, as before.
        var replacingDefault = ticket.IsClassified
            && defaultClassification is not null
            && await defaultClassification.IsDefaultAsync(ticket.CategoryId, ticket.RequestTypeId, cancellationToken);

        // The CS Agent the ticket is assigned to reviews and corrects its Department, Category and Request Type: the one other case in which an
        // existing classification is replaced. Nobody else (and no one on a ticket they do not own) can overwrite a valid classification.
        var assignedAgentCorrecting = ticket.IsClassified && !replacingDefault
            && ticket.CurrentOwnerEmployeeId == callerEmployeeId
            && callerRoles.Contains(Domain.Modules.IdentityAndAccess.Roles.CsAgent);
        var replacing = replacingDefault || assignedAgentCorrecting;

        // Choosing the default again is not a reclassification: nothing changes and nothing moves.
        if (ticket.IsClassified && (!replacing || (request.CategoryId == ticket.CategoryId && request.RequestTypeId is null || request.CategoryId == ticket.CategoryId && request.RequestTypeId == ticket.RequestTypeId)))
        {
            return TicketMutationResult.Failure(TicketMutationOutcome.AlreadyClassified);
        }

        var category = await categoryRepository.GetByIdAsync(request.CategoryId, cancellationToken);
        if (category is null || !category.IsActive)
        {
            return TicketMutationResult.Failure(TicketMutationOutcome.CategoryNotFound);
        }

        // The department is already settled — the inquiry resolved it before
        // the ticket existed. Classification names the request, it does not
        // re-route the ticket; a category from another department would do
        // exactly that silently. The one deliberate exception: a ticket sitting
        // in the Call Center (the first human queue) is routed to the department
        // that owns the category the Call Center user selected.
        var routeToCategoryDepartment = false;
        if (category.DepartmentId != ticket.CurrentDepartmentId)
        {
            // The Call Center routes to the category's department by design; so does reclassifying a default (General) ticket, whose
            // placeholder department says nothing about where the real request belongs. The owner is preserved either way.
            if (departmentRepository is null || autoAssignmentService is null
                || !(replacing || await IsCallCenterDepartmentAsync(ticket.CurrentDepartmentId, cancellationToken))
                || await departmentRepository.GetByIdAsync(category.DepartmentId, cancellationToken) is not { IsActive: true })
            {
                return TicketMutationResult.Failure(TicketMutationOutcome.CategoryDepartmentMismatch);
            }

            routeToCategoryDepartment = true;
        }

        var requestTypeDepartmentId = routeToCategoryDepartment ? category.DepartmentId : ticket.CurrentDepartmentId;

        if (await priorityRepository.GetByIdAsync(request.PriorityId, cancellationToken) is null)
        {
            return TicketMutationResult.Failure(TicketMutationOutcome.PriorityNotFound);
        }

        // Optional request type — same rules as ticket creation applies them,
        // so a ticket classified late is pinned exactly as one classified at
        // creation would have been.
        Domain.Modules.WorkflowConfiguration.RequestType? requestType = null;
        Domain.Modules.WorkflowConfiguration.WorkflowTemplate? workflowVersion = null;
        if (request.RequestTypeId is { } requestTypeId)
        {
            requestType = await requestTypeRepository.GetByIdAsync(requestTypeId, cancellationToken);
            if (requestType is null || !requestType.IsActive)
            {
                return TicketMutationResult.Failure(TicketMutationOutcome.NotAllowedForRequestType);
            }

            if (requestType.DepartmentId != requestTypeDepartmentId)
            {
                return TicketMutationResult.Failure(TicketMutationOutcome.NotAllowedForRequestType);
            }

            workflowVersion = await workflowTemplateRepository.GetPublishedAsync(requestType.WorkflowId, cancellationToken);
            if (workflowVersion is null)
            {
                return TicketMutationResult.Failure(TicketMutationOutcome.NotAllowedForRequestType);
            }
        }

        // A correction that names no request type must not leave the ticket without one: keep the current request type when it still belongs to
        // the (possibly new) department, otherwise use that department's General request type when it has one.
        if (replacing && requestType is null)
        {
            if (ticket.RequestTypeId is { } currentRequestTypeId
                && await requestTypeRepository.GetByIdAsync(currentRequestTypeId, cancellationToken) is { } current
                && current.DepartmentId == requestTypeDepartmentId
                && ticket.WorkflowTemplateId is { } pinnedVersionId
                && await workflowTemplateRepository.GetByIdAsync(pinnedVersionId, cancellationToken) is { } pinned)
            {
                requestType = current;
                workflowVersion = pinned;
            }
            else if (defaultClassification is not null
                && (await defaultClassification.ResolveForDepartmentAsync(requestTypeDepartmentId, cancellationToken)).Value is { } own
                && own.Department.DepartmentId == requestTypeDepartmentId)
            {
                requestType = own.RequestType;
                workflowVersion = own.PublishedWorkflow;
            }
        }

        // A replacement always ends with a valid request type: the one chosen, the current one when it still fits, or the target department's own
        // General. When none exists that is a configuration error - nothing is changed.
        if (replacing && requestType is null)
        {
            return TicketMutationResult.Failure(TicketMutationOutcome.DefaultClassificationNotConfigured);
        }

        var now = timeProvider.GetUtcNow().UtcDateTime;
        var correlationId = Guid.NewGuid();

        await using var transaction = await unitOfWork.BeginTransactionAsync(cancellationToken);

        ticketRepository.SetRowVersion(ticket, request.RowVersion);

        // Captured before anything moves, for the audit trail: a Genesys
        // ticket already runs on its default priority and SLA period.
        var priorityBefore = ticket.PriorityId;
        var slaStateBefore = ticket.SlaState;
        var categoryBefore = ticket.CategoryId;
        var requestTypeBefore = ticket.RequestTypeId;
        var hadRunningPeriod = priorityBefore is not null && slaStateBefore != SlaState.NotApplicable;

        try
        {
            if (priorityBefore is { } alreadyHas)
            {
                // The default priority stands unless the agent judged the
                // request MORE urgent (a less urgent one was refused above).
                // Setting the category must not restart the clock, so the
                // running period is kept — or tightened by an upgrade.
                if (replacing)
                {
                    ticket.ReplaceClassification(
                        category.CategoryId, requestType?.RequestTypeId, workflowVersion?.WorkflowTemplateId);
                }
                else
                {
                    ticket.Classify(category.CategoryId, alreadyHas);
                }

                // A reclassification never changes priority (so it can never re-time the running SLA); a priority change has its own flow.
                if (!replacing && request.PriorityId < alreadyHas)
                {
                    ticket.ChangePriority(request.PriorityId);
                }
            }
            else if (replacing)
            {
                ticket.ReplaceClassification(
                    category.CategoryId, requestType?.RequestTypeId, workflowVersion?.WorkflowTemplateId);
                ticket.ApplyDefaultPriority(request.PriorityId);
            }
            else
            {
                ticket.Classify(category.CategoryId, request.PriorityId);
            }

            if (!replacing && requestType is not null && workflowVersion is not null)
            {
                ticket.ClassifyRequestType(requestType.RequestTypeId);
                ticket.PinWorkflowVersion(workflowVersion.WorkflowTemplateId);
            }

            if (routeToCategoryDepartment)
            {
                // Department follows the category; TransferToDepartment clears the previous owner, then the one assignment
                // engine (rule first, rotation otherwise) picks the next eligible employee or leaves the department queue.
                var fromDepartmentId = ticket.CurrentDepartmentId;
                var fromOwnerEmployeeId = ticket.CurrentOwnerEmployeeId;
                ticket.TransferToDepartment(category.DepartmentId, preserveOwner: true);
                await auditWriter.WriteAsync(
                    callerEmployeeId, "Transfer", "Ticket", ticket.TicketId.ToString(),
                    beforeValue: $"DepartmentId={fromDepartmentId};AssignedEmployeeId={fromOwnerEmployeeId?.ToString() ?? "DepartmentQueue"}",
                    afterValue: $"DepartmentId={category.DepartmentId};Reason={(replacing ? "Reclassified to" : "Call Center selected")} category {category.CategoryId}",
                    correlationId, cancellationToken);

                // Into Collections the unit's tower decides (collector, or the Collections queue); the rule/rotation below never picks someone for it.
                var collectionsRouted = collectionsRouting is not null
                    && (await collectionsRouting.RouteAsync(
                        ticket, departmentEntry: true, fromCallCenter: await autoAssignmentService!.IsCallCenterDepartmentAsync(fromDepartmentId, cancellationToken),
                        now, correlationId, cancellationToken)).Handled;

                if (!collectionsRouted && ticket.CurrentOwnerEmployeeId is null)
                {
                    var configured = await autoAssignmentService!.ApplyAsync(
                        ticket, now, correlationId, AutoAssignmentTrigger.CategoryRouted, cancellationToken);
                    await autoAssignmentService.ApplyCategoryRoutingAsync(ticket, configured, now, correlationId, cancellationToken: cancellationToken);
                }

                if (workflowEventRepository is not null)
                {
                    await workflowEventRepository.AddAsync(
                        TicketWorkflowEvent.ForTransfer(
                            ticket.TicketId, now, callerEmployeeId, TicketTransferReason.Unspecified,
                            fromDepartmentId, category.DepartmentId, ticket.CurrentOwnerEmployeeId,
                            "Call Center category routing", correlationId),
                        cancellationToken);
                }
            }

            if (hadRunningPeriod)
            {
                // The clock is already running from ticket creation and is
                // NOT restarted. A more urgent priority replaces the period
                // under the earlier-of rule (ADR-0012); otherwise a request
                // type classified now has its SLA policy applied to the
                // running period from its original start.
                if (ticket.PriorityId != priorityBefore)
                {
                    await slaDueDateService.ReplaceCurrentPeriodForUpgradeAsync(
                        ticket, ticket.PriorityId!.Value, now, callerEmployeeId, correlationId, cancellationToken);
                }
                else if (requestType is not null && !replacing)
                {
                    // A reclassification never re-times a running SLA; only the first classification applies the request type's policy.
                    await slaDueDateService.ReapplyPolicyForRequestTypeAsync(
                        ticket, callerEmployeeId, correlationId, cancellationToken);
                }
            }
            else
            {
                // The SLA clock the ticket never had, started from this
                // moment — the first instant at which a target exists to
                // measure against. See this type's remarks for why it is not
                // backdated.
                await slaDueDateService.OpenInitialPeriodAsync(
                    ticket, now, callerEmployeeId, correlationId, cancellationToken);

                // The ticket's SlaState was NotApplicable while unclassified;
                // a real period now exists, so the dimension reports Running
                // like any other live ticket.
                ticket.StartSlaClock();

                // The SlaState dimension genuinely moved, so it is recorded on
                // the ticket's status history like every other dimension change.
                await statusHistoryRepository.AddAsync(
                    new TicketStatusHistory(
                        ticket.TicketId, TicketStatusDimension.SlaState, oldValue: (byte)SlaState.NotApplicable,
                        newValue: (byte)SlaState.Running, callerEmployeeId, actorIsSystem: false,
                        note: $"SLA clock started at classification ({now:O}).", correlationId, now),
                    cancellationToken);
            }

            await auditWriter.WriteAsync(
                callerEmployeeId, replacing ? "ReclassifyTicket" : "ClassifyTicket", "Ticket", ticket.TicketId.ToString(),
                beforeValue: $"CategoryId={categoryBefore?.ToString() ?? "(none)"};PriorityId={priorityBefore?.ToString() ?? "(none)"};RequestTypeId={requestTypeBefore?.ToString() ?? "(none)"};SlaState={slaStateBefore}"
                    + (replacingDefault ? $";Default={DefaultClassificationService.DisplayLabel}" : replacing ? ";CorrectedByAssignedCsAgent=true" : string.Empty),
                afterValue:
                    $"CategoryId={ticket.CategoryId};PriorityId={ticket.PriorityId};"
                    + $"RequestTypeId={ticket.RequestTypeId?.ToString() ?? "(none)"};SlaState={ticket.SlaState}",
                correlationId, cancellationToken);

            await unitOfWork.SaveChangesAsync(cancellationToken);
        }
        catch (TicketAlreadyClassifiedException)
        {
            return TicketMutationResult.Failure(TicketMutationOutcome.AlreadyClassified);
        }
        catch (TicketClosedException)
        {
            return TicketMutationResult.Failure(TicketMutationOutcome.TicketClosed);
        }
        catch (TicketConcurrentlyModifiedException)
        {
            return TicketMutationResult.Failure(TicketMutationOutcome.ConcurrencyConflict);
        }

        await transaction.CommitAsync(cancellationToken);

        return TicketMutationResult.Success(TicketQueryAppService.ToDetailDto(ticket));
    }

    private async Task<bool> IsCallCenterDepartmentAsync(int departmentId, CancellationToken cancellationToken) =>
        departmentRepository is not null
        && await departmentRepository.GetByIdAsync(departmentId, cancellationToken) is { } department
        && string.Equals(department.Code, TigerCS.Application.Modules.Reporting.Services.TeamPerformanceAppService.CallCenterDepartmentCode,
            StringComparison.OrdinalIgnoreCase);
}
