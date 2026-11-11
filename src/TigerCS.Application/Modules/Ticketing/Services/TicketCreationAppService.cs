using TigerCS.Application.Authorization;
using TigerCS.Application.Abstractions;
using TigerCS.Application.Modules.CustomerVerification.Abstractions;
using TigerCS.Application.Modules.IdentityAndAccess.Abstractions;
using TigerCS.Application.Modules.Ticketing.Abstractions;
using TigerCS.Application.Modules.Notifications;
using TigerCS.Application.Modules.Notifications.Services;
using TigerCS.Application.Modules.Reporting.Services;
using TigerCS.Application.Modules.SlaAndEscalation.Services;
using TigerCS.Application.Modules.Ticketing.Dto;
using TigerCS.Application.Modules.WorkflowConfiguration.Abstractions;
using TigerCS.Domain.Infrastructure;
using TigerCS.Domain.Modules.CustomerVerification;
using TigerCS.Domain.Modules.SlaAndEscalation;
using TigerCS.Domain.Modules.Ticketing;
using TigerCS.Domain.Modules.WorkflowConfiguration;

namespace TigerCS.Application.Modules.Ticketing.Services;

/// <summary>
/// Business-rule change: a single ticket-creation path for every IntakeRecord
/// — unit-related or not. Customer information from CRM, PACT, or Tasleeh is
/// attached when the agent already resolved a match via
/// <see cref="CustomerLookupAppService"/> (<see cref="CreateTicketRequestDto.UnitReferenceId"/>/
/// <see cref="CreateTicketRequestDto.ContactReferenceId"/>); lack of a match
/// never prevents ticket creation. The only thing every ticket requires is a
/// valid, active Ticket Category. This method inspects the request and the
/// IntakeRecord itself and creates the appropriate <see cref="Ticket"/>
/// internally — <see cref="Ticket.CreateVerified"/> when a resolved
/// unit/contact pair is supplied, <see cref="Ticket.CreateUnverified"/>
/// otherwise — rather than exposing a separate endpoint per case.
///
/// <para>
/// <b>Two SaveChanges calls, in one real transaction.</b> Both
/// <see cref="Ticket"/> and <see cref="IntakeRecord"/> use database-generated
/// identity PKs (bigint), so a <see cref="Ticket"/>'s real <c>TicketId</c> is
/// not known until its own insert commits — but linking the IntakeRecord and
/// writing the requester snapshot/status-history/audit rows all need that
/// real ID. The ticket is therefore inserted alone first; everything else
/// commits in a second call — both wrapped in one
/// <see cref="ITicketingUnitOfWork.BeginTransactionAsync"/> scope. A
/// <c>TicketNumber</c> unique-index collision on the first call
/// (<see cref="DuplicateWriteException"/>) is translated to
/// <see cref="TicketCreationOutcome.TicketNumberCollision"/> — a clean,
/// retryable <c>409</c> rather than an unhandled exception; nothing has been
/// touched yet at that point, so retrying the whole request is always
/// correct.
/// </para>
/// </summary>
public sealed class TicketCreationAppService(
    IIntakeRecordRepository intakeRecordRepository,
    IUnitReferenceRepository unitReferenceRepository,
    IContactReferenceRepository contactReferenceRepository,
    ICategoryRepository categoryRepository,
    IPriorityRepository priorityRepository,
    IDepartmentRepository departmentRepository,
    ITicketRepository ticketRepository,
    ITicketRequesterSnapshotRepository snapshotRepository,
    ITicketStatusHistoryRepository statusHistoryRepository,
    ITicketingUnitOfWork unitOfWork,
    IAuditEntryWriter auditWriter,
    IOutboxWriter outboxWriter,
    SlaDueDateService slaDueDateService,
    TimeProvider timeProvider,
    IRequestTypeRepository requestTypeRepository,
    ITicketInteractionRepository interactionRepository,
    TicketAutoAssignmentService autoAssignmentService,
    IWorkflowTemplateRepository workflowTemplateRepository,
    IServiceIdentityRegistry? serviceIdentities = null,
    ITicketWorkflowEventRepository? workflowEventRepository = null,
    DefaultClassificationService? defaultClassification = null,
    TigerCS.Application.Modules.Collections.TowerAssignments.CollectionsTicketRoutingService? collectionsRouting = null)
{
    /// <summary>Creates a ticket from an intake record. A CS Agent creating it by hand becomes its owner when eligible (see <paramref name="callerRoles"/>).</summary>
    /// <param name="callerEmployeeId">The creating employee.</param>
    /// <param name="request">The ticket to create.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <param name="answeringAgent">Set only by the Genesys ingestion for a call a human agent already answered; see <see cref="TicketAutoAssignmentService.ApplyAnsweringAgentAsync"/>.</param>
    /// <param name="callerRoles">
    /// The caller's roles, when the caller is a person acting in the UI/API. Needed only for self-assignment: a CS Agent who creates a
    /// ticket by hand owns it unless they chose the Department Queue (see <c>TicketAutoAssignmentService.TrySelfAssignAsync</c>). Left
    /// out, nothing is self-assigned — which is what the Genesys ingestion (an integration, not a person) does.
    /// </param>
    public async Task<TicketCreationResult> CreateAsync(
        Guid callerEmployeeId, CreateTicketRequestDto request, CancellationToken cancellationToken = default,
        IReadOnlyCollection<string>? callerRoles = null, AnsweringAgent? answeringAgent = null)
    {
        var intakeRecord = await intakeRecordRepository.GetByIdAsync(request.IntakeRecordId, cancellationToken);
        if (intakeRecord is null)
        {
            return TicketCreationResult.Failure(TicketCreationOutcome.IntakeRecordNotFound);
        }

        if (intakeRecord.LinkedTicketId is not null)
        {
            return TicketCreationResult.Failure(TicketCreationOutcome.IntakeRecordAlreadyLinked);
        }

        if ((request.UnitReferenceId is null) != (request.ContactReferenceId is null))
        {
            return TicketCreationResult.Failure(TicketCreationOutcome.UnitOrContactReferenceMismatch);
        }

        UnitReference? unitReference = null;
        ContactReference? contactReference = null;
        if (request.UnitReferenceId is { } unitReferenceId && request.ContactReferenceId is { } contactReferenceId)
        {
            unitReference = await unitReferenceRepository.GetByIdAsync(unitReferenceId, cancellationToken);
            if (unitReference is null)
            {
                return TicketCreationResult.Failure(TicketCreationOutcome.UnitReferenceNotFound);
            }

            contactReference = await contactReferenceRepository.GetByIdAsync(contactReferenceId, cancellationToken);
            if (contactReference is null)
            {
                return TicketCreationResult.Failure(TicketCreationOutcome.ContactReferenceNotFound);
            }
        }

        // Business-rule change: the real CRM Buyer Lookup match (GET
        // /api/crm/buyers — phone search only). All four CRM ids travel
        // together or not at all, same pairing discipline as
        // UnitReferenceId/ContactReferenceId above.
        var crmBuyerIds = new[]
        {
            request.CrmBuyerCustomerId, request.CrmBuyerLeadId, request.CrmBuyerUnitId, request.CrmBuyerProjectId
        };
        var hasCrmBuyerMatch = crmBuyerIds.All(id => id is not null);
        if (!hasCrmBuyerMatch && crmBuyerIds.Any(id => id is not null))
        {
            return TicketCreationResult.Failure(TicketCreationOutcome.CrmBuyerReferenceMismatch);
        }

        // Unit 0 is a CRM/PACT placeholder, never a real unit: refuse it here even though the lookups already
        // drop it, because this endpoint is also reachable without the wizard.
        if (hasCrmBuyerMatch && request.CrmBuyerUnitId <= 0)
        {
            return TicketCreationResult.Failure(TicketCreationOutcome.CrmBuyerReferenceMismatch);
        }

        var hasManualProjectUnit = !string.IsNullOrWhiteSpace(request.ManualProjectName)
            || !string.IsNullOrWhiteSpace(request.ManualUnitNumber);
        if (hasCrmBuyerMatch && hasManualProjectUnit)
        {
            return TicketCreationResult.Failure(TicketCreationOutcome.CrmBuyerAndManualProjectUnitBothSupplied);
        }

        // External-lookup verification (PACT/Tasleeh): the source name and
        // its own customer/unit identifiers travel together — identifiers
        // without a source are meaningless for audit/reconciliation and are
        // rejected rather than stored orphaned. Mutually exclusive with a
        // CRM Buyer match (a ticket records one verified identity), but the
        // manual Project/Unit snapshot deliberately accompanies it — that
        // pair is the human-readable snapshot for a unit with no local
        // reference.
        var hasExternalVerification = !string.IsNullOrWhiteSpace(request.CustomerVerificationSource);
        if (!hasExternalVerification
            && (!string.IsNullOrWhiteSpace(request.ExternalCustomerId) || !string.IsNullOrWhiteSpace(request.ExternalUnitId)))
        {
            return TicketCreationResult.Failure(TicketCreationOutcome.ExternalVerificationSourceMissing);
        }

        if (hasCrmBuyerMatch && hasExternalVerification)
        {
            return TicketCreationResult.Failure(TicketCreationOutcome.CrmBuyerAndExternalVerificationBothSupplied);
        }

        // The New Ticket wizard's own "Project and Unit Number are required
        // when no CRM Buyer match was selected" business rule is enforced in
        // NewTicketModel.OnPostCreateAsync (TigerCS.Web), not here: Ticket
        // Category remains the only universal requirement POST /api/tickets
        // itself enforces for every caller (business-rule change predating
        // CRM Buyer Lookup — see this method's own remarks). ManualProjectName/
        // ManualUnitNumber are accepted and stored as optional pass-through
        // fields whenever a caller does supply them.

        // Human work: a manual ticket, a Genesys inquiry that needs a person, or a call a person already answered. (See the Call Center comment below.)
        var needsHuman = request.GenesysContext is null || request.GenesysContext.RequiresHumanFollowUp || answeringAgent is not null;

        // Default classification: a ticket with no classification that can be trusted - none at all, a partial or ambiguous one, or one
        // that names a category / request type that is not usable - is created General / General within its own department (Customer Services'
        // only when the department is unknown). A valid classification is never replaced, and Collections is out of scope. A valid department
        // WITHOUT its own General is a configuration error: a human-handled ticket is then refused with an explicit, audited outcome (never
        // created half classified, never moved to another department); an AI-handled one is created and the error is reported.
        var decision = await TryResolveFallbackAsync(request, intakeRecord.DepartmentId, cancellationToken);
        var fallback = decision.Choice;
        var configurationError = decision.ConfigurationError;
        if (configurationError is not null && needsHuman)
        {
            return await RefuseNotConfiguredAsync(callerEmployeeId, intakeRecord.IntakeRecordId, configurationError, cancellationToken);
        }

        RoutingResolution routing;
        RequestType? requestType = null;
        WorkflowTemplate? workflowVersion = null;
        if (fallback is not null)
        {
            var defaultPriority = await ResolveFallbackPriorityAsync(request.PriorityId, fallback.Classification, cancellationToken);
            if (defaultPriority is null)
            {
                return TicketCreationResult.Failure(TicketCreationOutcome.PriorityNotFound);
            }

            routing = RoutingResolution.Succeeded(fallback.Classification.Category, defaultPriority, fallback.Classification.Department);
            requestType = fallback.Classification.RequestType;
            workflowVersion = fallback.Classification.PublishedWorkflow;
        }
        else
        {
            routing = await ResolveRoutingAsync(
                request.CategoryId, request.DepartmentId, request.PriorityId, intakeRecord.DepartmentId,
                allowUnclassifiedPriority: request.GenesysContext is not null, cancellationToken);
            if (routing.Failure is { } routingFailure)
            {
                return TicketCreationResult.Failure(routingFailure);
            }
        }

        // Null exactly when the ticket is being created Unclassified - see
        // ResolveRoutingAsync.
        var category = routing.Category;
        var priority = routing.Priority;
        var department = routing.Department!;

        // Workflow/Automation phase 2 - optional request-type classification.
        // When supplied it must be an active request type of the department
        // the ticket routes to (request types are never offered across
        // departments); when absent, everything below behaves exactly as
        // before this phase.
        if (fallback is null && request.RequestTypeId is { } requestTypeId)
        {
            requestType = await requestTypeRepository.GetByIdAsync(requestTypeId, cancellationToken);
            if (requestType is null || !requestType.IsActive)
            {
                return TicketCreationResult.Failure(TicketCreationOutcome.RequestTypeNotFound);
            }

            // Compared against the RESOLVED department rather than the
            // category's, so the check reads the same on the Unclassified
            // path (where there is no category, and the department was
            // supplied directly). For a classified ticket the two are the
            // same value by construction.
            if (requestType.DepartmentId != department.DepartmentId)
            {
                return TicketCreationResult.Failure(TicketCreationOutcome.RequestTypeDepartmentMismatch);
            }
        }

        // Administration / Workflow Designer phase - resolve the request
        // type's currently Published workflow version NOW, so the ticket is
        // pinned to exactly the definition in force at creation. A request
        // type whose workflow has no published version cannot govern a new
        // ticket; that is a configuration error surfaced to the caller, never
        // a silently unpinned ticket.
        if (fallback is null && requestType is not null)
        {
            workflowVersion = await workflowTemplateRepository.GetPublishedAsync(requestType.WorkflowId, cancellationToken);
            if (workflowVersion is null)
            {
                return TicketCreationResult.Failure(TicketCreationOutcome.RequestTypeWorkflowNotPublished);
            }
        }

        // A valid category with no request type keeps its category and is completed with that department's own General request type. Without
        // one, a human-handled ticket is refused (never left without a valid request type); an AI-handled one is created and the gap reported.
        string? requestTypeCompletion = null;
        if (fallback is null && requestType is null && category is not null && defaultClassification is not null
            && request.SkipDefaultClassification != true && request.RequestTypeId is null)
        {
            var own = await defaultClassification.ResolveForDepartmentAsync(department.DepartmentId, cancellationToken);
            if (own.Value is { } ownGeneral)
            {
                requestType = ownGeneral.RequestType;
                workflowVersion = ownGeneral.PublishedWorkflow;
                requestTypeCompletion = $"RequestTypeId={requestType.RequestTypeId} (department General)";
            }
            else if (own.DepartmentKnown)
            {
                if (needsHuman)
                {
                    return await RefuseNotConfiguredAsync(callerEmployeeId, intakeRecord.IntakeRecordId, own.Detail!, cancellationToken);
                }

                configurationError = own.Detail;
            }
        }

        if (request.GenesysContext is { } genesysContext && string.IsNullOrWhiteSpace(genesysContext.ConversationId))
        {
            return TicketCreationResult.Failure(TicketCreationOutcome.GenesysConversationIdRequired);
        }

        var now = timeProvider.GetUtcNow().UtcDateTime;

        // Call Center is the FIRST human-handling department for every channel. A ticket that needs a person enters it whatever its
        // category's department; a Genesys inquiry the AI is still handling (no human follow-up requested) does not — it only reaches
        // the Call Center when a person is actually asked for (GenesysAgentHandoffAppService), so an AI-resolved ticket is never
        // routed there needlessly. No active Call Center department configured -> the previous behaviour, reported as a blocker.
        // A call a person already ANSWERED is human work by definition (answeringAgent is built server-side by the Genesys ingestion, never from the client).
        var callCenter = needsHuman ? await autoAssignmentService.ResolveCallCenterAsync(cancellationToken) : null;

        await using var transaction = await unitOfWork.BeginTransactionAsync(cancellationToken);

        Ticket ticket;
        try
        {
            var ticketNumber = await GenerateTicketNumberAsync(department, now, cancellationToken);
            ticket = category is null
                // Unclassified: nobody has read the request yet, so no
                // category — and therefore no SLA clock — is invented. The
                // customer-identification variants below all presuppose a
                // classified ticket; an unclassified one carries whatever the
                // channel collected as the manual project/unit snapshot, the
                // same as CreateUnverified.
                ? Ticket.CreateUnclassified(
                    ticketNumber, department.DepartmentId, request.RequestSummary, now,
                    request.ManualProjectName, request.ManualUnitNumber, defaultPriorityId: priority?.PriorityId)
                : (unitReference, contactReference, hasCrmBuyerMatch, hasExternalVerification) switch
            {
                (not null, not null, _, _) => Ticket.CreateVerified(
                    ticketNumber, department.DepartmentId, unitReference!.UnitReferenceId, contactReference!.ContactReferenceId,
                    category.CategoryId, priority!.PriorityId, request.RequestSummary, now),
                (_, _, true, _) => Ticket.CreateVerifiedFromCrmBuyer(
                    ticketNumber, department.DepartmentId,
                    request.CrmBuyerCustomerId!.Value, request.CrmBuyerLeadId!.Value, request.CrmBuyerUnitId!.Value, request.CrmBuyerProjectId!.Value,
                    request.CrmBuyerCustomerName, request.CrmBuyerProjectName, request.CrmBuyerUnitNumber,
                    category.CategoryId, priority!.PriorityId, request.RequestSummary, now),
                (_, _, _, true) => Ticket.CreateFromExternalLookup(
                    ticketNumber, department.DepartmentId,
                    request.CustomerVerificationSource!, request.ExternalCustomerId, request.ExternalUnitId,
                    request.ManualProjectName, request.ManualUnitNumber,
                    category.CategoryId, priority!.PriorityId, request.RequestSummary, now,
                    // The verified customer's own name/email, as the source
                    // returned them — snapshotted like the CRM Buyer name
                    // beside it, so the Customers directory has a real name
                    // to show instead of "Pact customer".
                    request.ExternalCustomerName, request.ExternalCustomerEmail),
                _ => Ticket.CreateUnverified(
                    ticketNumber, department.DepartmentId, category.CategoryId, priority!.PriorityId, request.RequestSummary, now,
                    request.ManualProjectName, request.ManualUnitNumber)
            };

            // Call Center tickets are worked from the start. Keyed off the department the ticket originates in (the same
            // department it is created in, so OriginatingDepartmentId is untouched); every other department keeps Open.
            if (string.Equals(department.Code, TeamPerformanceAppService.CallCenterDepartmentCode, StringComparison.OrdinalIgnoreCase))
            {
                ticket.StartInProgressAtCreation();
            }

            if (requestType is not null && workflowVersion is not null)
            {
                ticket.ClassifyRequestType(requestType.RequestTypeId);
                ticket.PinWorkflowVersion(workflowVersion.WorkflowTemplateId);
            }

            ticket.RecordCreator(callerEmployeeId);

            // Manual creation by an authenticated person: they are the Original Human Agent from the first moment. A ticket created by Genesys AI or
            // any integration (no caller roles = the ingestion; or a configured service identity) starts with none - the first eligible agent to
            // accept or be assigned it becomes the original (TicketAutoAssignmentService.RecordOriginalHumanAgentAsync).
            if (callerRoles is not null
                && callerEmployeeId != Guid.Empty
                && serviceIdentities?.IsServiceIdentity(callerEmployeeId) != true)
            {
                ticket.RecordOriginalHumanAgent(callerEmployeeId);
            }

            await ticketRepository.AddAsync(ticket, cancellationToken);
            await unitOfWork.SaveChangesAsync(cancellationToken);
        }
        catch (DuplicateWriteException)
        {
            // Nothing else has been touched yet — the IntakeRecord is still
            // unlinked. Safe to retry the whole request unchanged.
            return TicketCreationResult.Failure(TicketCreationOutcome.TicketNumberCollision);
        }

        // hasSelectedUnit is the real, source-agnostic rule for
        // IntakeRecord.IsUnitRelated: a resolved local Unit/Contact
        // reference — or, business-rule change, a real CRM Buyer Lookup
        // match — was linked; not ticket.VerificationStatus, which is a
        // separate, CRM-named concept (see IntakeRecord.LinkToTicket's
        // remarks).
        intakeRecord.LinkToTicket(ticket.TicketId, ticket.VerificationStatus, unitReference is not null || hasCrmBuyerMatch);

        if (unitReference is not null && contactReference is not null)
        {
            await snapshotRepository.AddAsync(
                new TicketRequesterSnapshot(
                    ticket.TicketId,
                    unitReference.UnitNumber,
                    unitReference.PropertyName,
                    unitReference.TowerName,
                    unitReference.UnitType,
                    contactReference.DisplayName,
                    contactReference.ContactChannel,
                    now),
                cancellationToken);
        }

        await SeedStatusHistoryAsync(ticket, callerEmployeeId, now, cancellationToken);

        var correlationId = Guid.NewGuid();

        // Workflow/Automation phase 2 — record the ORIGINATING interaction
        // the ticket was created from (a ticket accumulates further
        // interaction rows over its lifetime; this first one carries
        // IsOriginatingInteraction). Channel and customer phone come from
        // the intake record (the phone stays the verification identity
        // input); a supplied Genesys context marks the row Genesys-sourced
        // and is stored verbatim for traceability — Ticketing never
        // re-derives routing from it. Face-to-Face and every other
        // locally-created interaction records a Ticketing-sourced row with
        // all Genesys fields null.
        var originatingInteraction = request.GenesysContext is { } genesys
            ? TicketInteraction.CreateFromGenesys(
                ticket.TicketId, intakeRecord.ChannelId, intakeRecord.PhoneNumber,
                genesys.ConversationId, genesys.CalledNumber, genesys.QueueId, genesys.QueueName,
                genesys.AgentId, genesys.AgentName, genesys.InteractionStartedAtUtc, genesys.Direction, now,
                isOriginatingInteraction: true,
                customerName: genesys.CustomerName, customerEmail: genesys.CustomerEmail,
                genesysServiceRequestId: genesys.ServiceRequestId, requiresHumanFollowUp: genesys.RequiresHumanFollowUp)
            : TicketInteraction.CreateLocal(
                ticket.TicketId, intakeRecord.ChannelId, intakeRecord.PhoneNumber, now, isOriginatingInteraction: true);
        // A Website/Chatbot conversation starts its "customer inactive" clock now: the customer opening it is the first interaction.
        // Call Center tickets are outside that rule and never tracked.
        if (originatingInteraction.IsWebsiteOrChatbotConversation
            && callCenter is null
            && !string.Equals(department.Code, TeamPerformanceAppService.CallCenterDepartmentCode, StringComparison.OrdinalIgnoreCase))
        {
            originatingInteraction.StartTrackingCustomerInteraction(now, callerEmployeeId);
        }

        await interactionRepository.AddAsync(originatingInteraction, cancellationToken);

        if (callCenter is not null)
        {
            // Call Center first: the ticket enters the Call Center (OriginatingDepartmentId is untouched) and ONE eligible Call
            // Center agent is picked by Round Robin. Request-type rules and creator self-assignment do not apply here — the agent
            // either resolves/closes it or transfers it, and the destination department's own assignment runs on that Transfer.
            if (callCenter.DepartmentId != ticket.CurrentDepartmentId)
            {
                var fromDepartmentId = ticket.CurrentDepartmentId;
                ticket.TransferToDepartment(callCenter.DepartmentId);
                await auditWriter.WriteAsync(
                    actorEmployeeId: null, "Transfer", "Ticket", ticket.TicketId.ToString(),
                    beforeValue: $"DepartmentId={fromDepartmentId};AssignedEmployeeId=DepartmentQueue",
                    afterValue: $"DepartmentId={callCenter.DepartmentId};Reason=New ticket requires a person — routed to Call Center first;OriginatingDepartmentId={ticket.OriginatingDepartmentId} (unchanged)",
                    correlationId, cancellationToken);
                if (workflowEventRepository is not null)
                {
                    await workflowEventRepository.AddAsync(
                        TicketWorkflowEvent.ForTransfer(
                            ticket.TicketId, now, actorEmployeeId: null, TicketTransferReason.SystemRouting,
                            fromDepartmentId, callCenter.DepartmentId, toEmployeeId: null,
                            "New ticket — Call Center first", correlationId),
                        cancellationToken);
                }
            }

            // Mandatory: neither AssignToDepartmentQueue nor creator self-assignment can bypass it. With no eligible agent the ticket stays
            // in the Call Center queue, audited as Unassigned.
            // The one exception: a call a mapped agent already ANSWERED goes to that agent directly (or waits in the queue, flagged, when they
            // cannot take it) - never to the next agent in the rotation.
            if (answeringAgent is not null)
            {
                await autoAssignmentService.ApplyAnsweringAgentAsync(
                    ticket, answeringAgent, "Inquiry", now, correlationId, callerEmployeeId,
                    routeToCallCenterIfAgentUnavailable: true, cancellationToken);
            }
            else if (request.GenesysContext is null
                && callerRoles is not null
                && ticket.OriginalHumanAgentId == callerEmployeeId
                && await autoAssignmentService.TryAssignManualCreatorInCallCenterAsync(ticket, callerEmployeeId, now, correlationId, cancellationToken))
            {
                // A Human Agent who creates the ticket by hand owns it: the Call Center rotation is not consulted (it would hand a ticket Kawthar
                // created to the next agent). An ineligible creator (not a Call Center CS Agent) falls through to the rotation below.
            }
            else
            {
                await autoAssignmentService.AssignCallCenterIntakeAsync(ticket, now, correlationId, cancellationToken);
            }
        }
        else if (needsHuman
            && collectionsRouting is not null
            && (await collectionsRouting.RouteAsync(ticket, departmentEntry: true, fromCallCenter: false, now, correlationId, cancellationToken)).Handled)
        {
            // Created directly in Collections: the unit's tower decides (collector, or the Collections queue) - no rule and no CS Agent rotation.
        }
        else
        {
            // No Call Center first (an AI-handled Genesys inquiry, or no Call Center department is configured): the previous
            // behaviour — Department + Request Type resolve the configured rule; no rule leaves the department queue.
            // The fallback never assigns to a department employee: General has no assignment rule, and a rule configured for it later must
            // not turn the safety net into automatic department routing. The CS Agent rotation below is what gives it an owner.
            var automatic = fallback is not null
                ? new AutoAssignmentResult(AutoAssignmentOutcome.NoRuleConfigured)
                : await autoAssignmentService.ApplyAsync(
                    ticket, now, correlationId, AutoAssignmentTrigger.TicketCreated, cancellationToken);

            if (request.GenesysContext is null
                && request.AssignToDepartmentQueue != true
                && callerRoles is not null
                && ticket.CurrentOwnerEmployeeId is null
                && (automatic.Outcome is AutoAssignmentOutcome.NoRequestType or AutoAssignmentOutcome.NoRuleConfigured
                    or AutoAssignmentOutcome.ConfiguredAssigneeNotInDepartment)
                && await autoAssignmentService.IsCallCenterRoutingAsync(callerEmployeeId, ticket.CurrentDepartmentId, cancellationToken))
            {
                automatic = await autoAssignmentService.ApplyCategoryRoutingAsync(ticket, automatic, now, correlationId, cancellationToken: cancellationToken);
            }

            if (request.GenesysContext is null
                && request.AssignToDepartmentQueue != true
                && callerRoles is not null
                && TicketVisibilityRule.IsRestrictedCsAgent(callerRoles, callerEmployeeId, serviceIdentities))
            {
                await autoAssignmentService.TrySelfAssignAsync(
                    ticket, callerEmployeeId, automatic.Outcome, now, correlationId, cancellationToken);
            }

            // Every human-handled ticket gets a CS Agent whatever its department: the shared rotation fills what the rule left open.
            // (An AI-handled Genesys inquiry — needsHuman=false — is protected until a person is actually asked for; the handoff assigns it.
            // An AI conversation that is closed by the AI never needs an agent.)
            if (needsHuman)
            {
                await autoAssignmentService.ApplyRoundRobinFallbackAsync(
                    ticket, automatic, AutoAssignmentTrigger.TicketCreated, now, correlationId, cancellationToken);
            }
        }

        // Backlog S-08's corrected acceptance criterion: ticket creation
        // opens the ticket's initial TicketSlaInstances row with computed due
        // dates — always, immediately. Business-rule change: nothing about
        // customer lookup ever pauses this clock any more (see
        // Ticket.CreateUnverified's remarks); `now` is both the ticket's
        // CreatedAtUtc and the SLA clock-start event (ISSUE-001 Option C,
        // SLA-Architecture.md §1/§2).
        //
        // The SLA policy is selected by PriorityId (SlaDueDateService.ComputeDueDates
        // -> SlaPolicies keyed on priority), so the period opens whenever the
        // ticket has one — a classified ticket always does, and an
        // Unclassified Genesys ticket does too since it starts on the
        // configured default (Normal) priority. The clock start is the same
        // `now` either way: creation. Only an Unclassified ticket with NO
        // priority (the default is switched off or unresolved) has no policy
        // to select and no commitment to measure; TicketClassificationAppService
        // opens its period at the moment a real classification exists.
        if (ticket.PriorityId is not null)
        {
            await slaDueDateService.OpenInitialPeriodAsync(ticket, now, callerEmployeeId, correlationId, cancellationToken);
        }

        await auditWriter.WriteAsync(
            callerEmployeeId, "Create", "Ticket", ticket.TicketId.ToString(),
            beforeValue: null,
            afterValue: $"TicketNumber={ticket.TicketNumber};DepartmentId={ticket.CurrentDepartmentId};VerificationStatus={ticket.VerificationStatus}",
            correlationId, cancellationToken);

        if (configurationError is not null)
        {
            await auditWriter.WriteAsync(
                callerEmployeeId, DefaultClassificationAuditActions.ConfigurationError, "Ticket", ticket.TicketId.ToString(),
                beforeValue: null,
                afterValue: $"Handling=AI;DepartmentId={department.DepartmentId};Error={configurationError};Action=Run EnsureDefaultGeneralClassification.sql",
                correlationId, cancellationToken);
        }

        if (requestTypeCompletion is not null)
        {
            await auditWriter.WriteAsync(
                callerEmployeeId, "ApplyDefaultClassification", "Ticket", ticket.TicketId.ToString(),
                beforeValue: null,
                afterValue: $"Reason=a category was chosen without a request type;CategoryId={category!.CategoryId} (kept);RequestType={requestTypeCompletion}",
                correlationId, cancellationToken);
        }

        if (fallback is not null)
        {
            await auditWriter.WriteAsync(
                callerEmployeeId, "ApplyDefaultClassification", "Ticket", ticket.TicketId.ToString(),
                beforeValue: null,
                afterValue:
                    $"Default={DefaultClassificationService.DisplayLabel};CategoryId={fallback.Classification.Category.CategoryId};"
                    + $"RequestTypeId={fallback.Classification.RequestType.RequestTypeId};Reason={fallback.Reason};DepartmentId={department.DepartmentId}",
                correlationId, cancellationToken);
        }

        await EnqueueTicketCreatedAsync(ticket, callerEmployeeId, correlationId, now, cancellationToken);

        await unitOfWork.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return TicketCreationResult.Success(ToDto(ticket));
    }

    /// <summary>
    /// Writes MVP-API-Contracts.md §3.1's <c>TicketCreated</c> Outbox event —
    /// the one that "drives the automated acknowledgement notification"
    /// (FR-NOT-01) — into the same transaction as the ticket itself.
    ///
    /// <para>
    /// <b>Nothing is sent from here.</b> NFR-REL-01 is explicit that nothing
    /// is dispatched from application code inside a request handler without
    /// first being durably recorded in the same transaction as the state
    /// change. This method records; ADR-0015's recurring dispatcher delivers,
    /// after the commit. A caller that rolls back — a ticket-number
    /// collision — takes this row down with it, so a ticket that does not
    /// exist can never acknowledge anything.
    /// </para>
    ///
    /// <para>
    /// <b>Written for an unverified ticket too, deliberately.</b> FR-NOT-01's
    /// acceptance criterion is "email attempted for every ticket", and an
    /// unverified ticket has no requester snapshot to resolve a recipient
    /// from. Skipping the event would make that ticket invisible —
    /// indistinguishable from one that was acknowledged — whereas enqueuing
    /// it produces a dead-lettered, audited, countable record that no
    /// acknowledgement could be sent and why.
    /// </para>
    ///
    /// <para>
    /// A <c>null</c> return means the key was already reserved — the same
    /// logical event is already enqueued, so no second message is written.
    /// That is the normal outcome of a retried request, not an error, and no
    /// audit entry is written for it because nothing was queued.
    /// </para>
    /// </summary>
    private Task EnqueueTicketCreatedAsync(
        Ticket ticket, Guid callerEmployeeId, Guid correlationId, DateTime nowUtc, CancellationToken cancellationToken) =>
        new TicketNotificationEvents(outboxWriter, auditWriter)
            .QueueTicketCreatedAsync(ticket, callerEmployeeId, correlationId, nowUtc, cancellationToken);

    private sealed record FallbackChoice(DefaultClassification Classification, string Reason);

    /// <summary>The fallback to apply, or - for a valid department without its own General - the configuration error to report.</summary>
    private sealed record FallbackDecision(FallbackChoice? Choice, string? ConfigurationError);

    /// <summary>
    /// Decides whether a new ticket takes the default classification. It does when nothing trustworthy classifies it: no category, a category
    /// or request type that is unknown / inactive / unusable, or a category that contradicts the department the intake named. A valid
    /// category (with a valid request type when one is given) is respected exactly as before. No choice is also the answer when the fallback is
    /// not configured at all, the Genesys ingestion asked to skip it, or the ticket belongs to Collections. A valid department with no General of
    /// its own yields a configuration error instead - never Customer Services' General as a stand-in.
    /// </summary>
    private async Task<FallbackDecision> TryResolveFallbackAsync(
        CreateTicketRequestDto request, int? intakeDepartmentId, CancellationToken cancellationToken)
    {
        var none = new FallbackDecision(null, null);
        if (defaultClassification is null || request.SkipDefaultClassification == true)
        {
            return none;
        }

        string? reason = null;
        if (request.CategoryId is not { } categoryId)
        {
            reason = request.RequestTypeId is null
                ? "no category was supplied"
                : "a request type was supplied without a category (partial classification)";
        }
        else
        {
            var category = await categoryRepository.GetByIdAsync(categoryId, cancellationToken);
            if (category is null || !category.IsActive)
            {
                reason = $"category {categoryId} is unknown or inactive";
            }
            else
            {
                var categoryDepartment = await departmentRepository.GetByIdAsync(category.DepartmentId, cancellationToken);
                if (DefaultClassificationService.IsExcluded(categoryDepartment))
                {
                    return none;
                }

                if (categoryDepartment is null || !categoryDepartment.IsActive)
                {
                    reason = $"category {categoryId} routes to a missing or inactive department";
                }
                else if (intakeDepartmentId is { } intakeDepartment && intakeDepartment != category.DepartmentId)
                {
                    reason = $"category {categoryId} contradicts the department the intake named";
                }
                else if (request.RequestTypeId is { } requestTypeId)
                {
                    var rt = await requestTypeRepository.GetByIdAsync(requestTypeId, cancellationToken);
                    if (rt is null || !rt.IsActive)
                    {
                        reason = $"request type {requestTypeId} is unknown or inactive";
                    }
                    else if (rt.DepartmentId != category.DepartmentId)
                    {
                        reason = $"request type {requestTypeId} does not belong to the category's department (ambiguous classification)";
                    }
                    else if (await workflowTemplateRepository.GetPublishedAsync(rt.WorkflowId, cancellationToken) is null)
                    {
                        reason = $"request type {requestTypeId} has no published workflow";
                    }
                }
            }
        }

        if (reason is null)
        {
            return none;
        }

        // General / General within the department the caller (or AI) named - the request's, else the intake's. Collections is never classified here.
        var hint = request.DepartmentId ?? intakeDepartmentId;
        if (hint is { } hintId && DefaultClassificationService.IsExcluded(await departmentRepository.GetByIdAsync(hintId, cancellationToken)))
        {
            return none;
        }

        var resolution = await defaultClassification.ResolveForDepartmentAsync(hint, cancellationToken);
        if (resolution.Value is { } value)
        {
            return new FallbackDecision(new FallbackChoice(value, reason), null);
        }

        return resolution.DepartmentKnown ? new FallbackDecision(null, resolution.Detail) : none;
    }

    /// <summary>
    /// The controlled configuration failure for a human-handled ticket: nothing is created, the error is audited with what to run, and the caller
    /// gets an explicit outcome (HTTP 409) - so a ticket is never persisted half classified or moved to another department, and the same request
    /// succeeds as soon as the department's General is configured.
    /// </summary>
    private async Task<TicketCreationResult> RefuseNotConfiguredAsync(
        Guid callerEmployeeId, long intakeRecordId, string error, CancellationToken cancellationToken)
    {
        await auditWriter.WriteAsync(
            callerEmployeeId, DefaultClassificationAuditActions.ConfigurationError, "IntakeRecord", intakeRecordId.ToString(),
            beforeValue: null,
            afterValue: $"Handling=Human;Error={error};Action=Run EnsureDefaultGeneralClassification.sql;TicketCreated=false",
            Guid.NewGuid(), cancellationToken);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return TicketCreationResult.Failure(TicketCreationOutcome.DefaultClassificationNotConfigured);
    }

    /// <summary>
    /// The priority a fallback ticket starts with: the one the caller supplied when it exists (a Genesys default or an agent's choice),
    /// otherwise the General request type's configured default. Null when neither resolves.
    /// </summary>
    private async Task<Priority?> ResolveFallbackPriorityAsync(byte? suppliedPriorityId, DefaultClassification classification, CancellationToken cancellationToken)
    {
        if (suppliedPriorityId is { } supplied && await priorityRepository.GetByIdAsync(supplied, cancellationToken) is { } suppliedPriority)
        {
            return suppliedPriority;
        }

        return await priorityRepository.GetByIdAsync(classification.DefaultPriorityId, cancellationToken);
    }

    /// <summary>
    /// Resolves and validates Category → Department routing (FR-CLS-01/
    /// FR-RTE-01) and Priority together, since ticket creation needs both.
    /// Rejects a Category that is missing/inactive, a Priority that is
    /// missing, a Category whose routed Department is itself missing/
    /// inactive (item 9 of the senior review, so a ticket can never be
    /// silently routed to a department nobody is staffing), and — the
    /// Category-directory follow-up — a Category that routes to a different
    /// Department than the one the IntakeRecord itself named. The UI's
    /// Category dropdown is always scoped to the IntakeRecord's Department
    /// (or offers every Department's Categories when none was given), so
    /// this last case only fires against a request built outside that UI.
    /// </summary>
    private async Task<RoutingResolution> ResolveRoutingAsync(
        int? categoryId, int? requestedDepartmentId, byte? priorityId, int? intakeDepartmentId,
        bool allowUnclassifiedPriority, CancellationToken cancellationToken)
    {
        // The Unclassified path: no category has been chosen because nobody
        // has read the request yet, so the department is supplied directly
        // and is the ONLY thing that places the ticket. Nothing is inferred —
        // no category stands in, and no priority either: an unjudged ticket
        // carries none rather than a default that would rank it against
        // tickets a human actually triaged. The one exception is a Genesys
        // ticket (allowUnclassifiedPriority), which carries the configured
        // DEFAULT priority so its SLA runs from creation: the priority is
        // validated here, but it remains a default — category and request
        // type are still unjudged.
        Priority? defaultPriority = null;
        if (categoryId is null)
        {
            if (priorityId is { } suppliedPriorityId)
            {
                if (!allowUnclassifiedPriority)
                {
                    return RoutingResolution.Failed(TicketCreationOutcome.PriorityRequiresCategory);
                }

                defaultPriority = await priorityRepository.GetByIdAsync(suppliedPriorityId, cancellationToken);
                if (defaultPriority is null)
                {
                    return RoutingResolution.Failed(TicketCreationOutcome.PriorityNotFound);
                }
            }

            if (requestedDepartmentId is not { } unclassifiedDepartmentId)
            {
                return RoutingResolution.Failed(TicketCreationOutcome.DepartmentOrCategoryRequired);
            }

            var unclassifiedDepartment = await departmentRepository.GetByIdAsync(unclassifiedDepartmentId, cancellationToken);
            if (unclassifiedDepartment is null)
            {
                return RoutingResolution.Failed(TicketCreationOutcome.DepartmentNotFound);
            }

            if (!unclassifiedDepartment.IsActive)
            {
                return RoutingResolution.Failed(TicketCreationOutcome.DepartmentInactive);
            }

            if (intakeDepartmentId is { } intakeDepartment && intakeDepartment != unclassifiedDepartmentId)
            {
                return RoutingResolution.Failed(TicketCreationOutcome.CategoryDepartmentMismatch);
            }

            return RoutingResolution.Succeeded(category: null, defaultPriority, unclassifiedDepartment);
        }

        // A classified ticket must name a real priority — the classified
        // factories and the SLA period both require one.
        if (priorityId is not { } classifiedPriorityId)
        {
            return RoutingResolution.Failed(TicketCreationOutcome.PriorityNotFound);
        }

        var priority = await priorityRepository.GetByIdAsync(classifiedPriorityId, cancellationToken);
        if (priority is null)
        {
            return RoutingResolution.Failed(TicketCreationOutcome.PriorityNotFound);
        }

        var category = await categoryRepository.GetByIdAsync(categoryId.Value, cancellationToken);
        if (category is null || !category.IsActive)
        {
            return RoutingResolution.Failed(TicketCreationOutcome.CategoryNotFound);
        }

        if (intakeDepartmentId is { } departmentIdOnIntake && category.DepartmentId != departmentIdOnIntake)
        {
            return RoutingResolution.Failed(TicketCreationOutcome.CategoryDepartmentMismatch);
        }

        var department = await departmentRepository.GetByIdAsync(category.DepartmentId, cancellationToken);
        if (department is null || !department.IsActive)
        {
            return RoutingResolution.Failed(TicketCreationOutcome.DepartmentInactive);
        }

        return RoutingResolution.Succeeded(category, priority, department);
    }

    private sealed record RoutingResolution(
        Domain.Modules.ClassificationAndRouting.Category? Category,
        Priority? Priority,
        Domain.Modules.IdentityAndAccess.Department? Department,
        TicketCreationOutcome? Failure)
    {
        /// <summary><paramref name="category"/> is null exactly on the Unclassified path, where the department alone places the ticket; <paramref name="priority"/> is then null too unless a Genesys default priority was supplied.</summary>
        public static RoutingResolution Succeeded(
            Domain.Modules.ClassificationAndRouting.Category? category, Priority? priority, Domain.Modules.IdentityAndAccess.Department department) =>
            new(category, priority, department, null);

        public static RoutingResolution Failed(TicketCreationOutcome outcome) => new(null, null, null, outcome);
    }

    /// <summary>
    /// Seeds one TicketStatusHistory row per dimension that actually has an
    /// initial value at creation (MVP-API-Contracts.md §3.1's audit note).
    /// ResolutionOutcome is deliberately excluded — it is null until the
    /// ticket is later resolved, and TicketStatusHistory.NewValue is NOT NULL
    /// (MVP-Data-Dictionary.md §2.13), so there is no value to seed a row
    /// with yet.
    /// </summary>
    private async Task SeedStatusHistoryAsync(
        Ticket ticket, Guid actorEmployeeId, DateTime nowUtc, CancellationToken cancellationToken)
    {
        var correlationId = Guid.NewGuid();
        (TicketStatusDimension Dimension, byte NewValue)[] seeds =
        [
            (TicketStatusDimension.TicketStatus, (byte)ticket.TicketStatus),
            (TicketStatusDimension.VerificationStatus, (byte)ticket.VerificationStatus),
            (TicketStatusDimension.EscalationLevel, (byte)ticket.EscalationLevel),
            (TicketStatusDimension.SlaState, (byte)ticket.SlaState)
        ];

        foreach (var (dimension, newValue) in seeds)
        {
            await statusHistoryRepository.AddAsync(
                new TicketStatusHistory(
                    ticket.TicketId, dimension, oldValue: null, newValue,
                    actorEmployeeId, actorIsSystem: false, note: null, correlationId, nowUtc),
                cancellationToken);
        }
    }

    private async Task<string> GenerateTicketNumberAsync(
        Domain.Modules.IdentityAndAccess.Department department, DateTime nowUtc, CancellationToken cancellationToken)
    {
        var prefix = $"TG-{department.Code}-{nowUtc:yyyyMMdd}-";
        var existingCount = await ticketRepository.CountByTicketNumberPrefixAsync(prefix, cancellationToken);
        return $"{prefix}{existingCount + 1:D4}";
    }

    private static TicketResponseDto ToDto(Ticket ticket) => TicketProjection.ToResponseDto(ticket);
}
