using TigerCS.Application.Abstractions;
using TigerCS.Application.Modules.CustomerVerification.Abstractions;
using TigerCS.Application.Modules.GenesysIntegration.Abstractions;
using TigerCS.Application.Modules.GenesysIntegration.Dto;
using TigerCS.Application.Modules.IdentityAndAccess.Abstractions;
using TigerCS.Application.Modules.Ticketing.Abstractions;
using TigerCS.Application.Modules.Ticketing.Dto;
using TigerCS.Application.Modules.Ticketing.Services;
using TigerCS.Domain.Modules.IdentityAndAccess;
using TigerCS.Domain.Modules.SlaAndEscalation;
using TigerCS.Domain.Modules.Ticketing;

namespace TigerCS.Application.Modules.GenesysIntegration.Services;

/// <summary>
/// Genesys integration phase 1 — the <b>one</b> ingestion flow every Genesys
/// channel converges on. Phone, website chat, WhatsApp and social media do
/// not get separate ticket-creation implementations: each arrives as the
/// same normalized <see cref="GenesysInquiryDto"/> and runs the same
/// sequence — idempotency check, channel resolution, department resolution,
/// customer lookup, ticket creation, conversation link.
///
/// <para>
/// <b>One service request, exactly one ticket.</b>
/// The identity of an inquiry is the pair (<see cref="GenesysInquiryDto.ConversationId"/>,
/// <see cref="GenesysInquiryDto.ServiceRequestId"/>). The first accepted event for a pair
/// creates a ticket; every retry, redelivery, or duplicate event returns <i>that same ticket</i>
/// (<see cref="GenesysIngestionOutcome.AlreadyIngested"/>) and creates
/// nothing. A conversation can therefore carry several distinct service requests (documents,
/// then apartment leasing), each a separate ticket with its own lifecycle. A caller that sends no
/// service request id keeps the original contract — the conversation is one ticket and every repeat
/// returns it. The guarantee is not only this service's read-before-write: the pair carries a unique
/// index on <c>TicketInteractions</c>, so two concurrent deliveries that both pass the read still produce
/// one ticket — the loser's unique-constraint violation is caught below and answered with the winner's ticket.
/// </para>
///
/// <para>
/// <b>A ringing phone never gets here.</b> Reaching this service means a real
/// inquiry exists: an agent picked up the call, or a digital conversation
/// started. TigerCS is not an event receiver for call progress, so there is no
/// Ringing/Answered vocabulary for Genesys to send — the phone flow simply
/// begins at pickup, with the customer lookup and then this.
/// </para>
///
/// <para>
/// <b>Nothing is re-implemented.</b> The intake record comes from
/// <see cref="IntakeRecordAppService"/>, the ticket from
/// <see cref="TicketCreationAppService"/> (ticket number, status history, SLA
/// clock, auto-assignment, outbox, audit and the originating interaction all
/// unchanged), and the customer identification from
/// <see cref="CustomerSearchAppService"/> — the same lookup the New Ticket
/// wizard uses. Genesys never reaches the CRM/PACT/Tasleeh gateways, or any
/// customer database, directly.
/// </para>
///
/// <para>
/// <b>Lookup never gates the inquiry.</b> A customer that cannot be found,
/// or a lookup source that is down, changes nothing: the ticket is created
/// as an unverified customer inquiry with whatever the channel collected
/// (name, email, tower/unit typed into the website form). A CRM/PACT outage
/// must never make a Genesys inquiry disappear, so the lookup is wrapped and
/// its failure recorded rather than propagated. Nor is a customer match ever
/// auto-selected — that stays the agent's explicit act, exactly as
/// everywhere else in this system.
/// </para>
///
/// <para>
/// <b>The ticket is created Unclassified, on the default priority.</b> The
/// department (the website form's Leasing / Customer Service / Maintenance
/// choice, or the queue mapping) is a department and nothing else: it is not
/// a Category and not a Request Type. The ticket is created with
/// <c>CategoryId = null</c> rather than filed under a placeholder, but it
/// starts on the configured default priority (<c>Genesys:DefaultTicketPriority</c>,
/// "Normal" — the Medium tier — resolved from the Priorities table, never a
/// hard-coded id) so its SLA is measured from the first moment: the period
/// opens at ticket creation with that priority's policy, the same clock-start
/// rule as every other ticket. A staff member can change the priority through
/// the existing Ticket Details flow; re-ingesting the conversation never
/// does.
/// </para>
///
/// <para>
/// <b>The request type, when the bot has one.</b> A request type on the
/// inquiry is validated and applied by
/// <see cref="GenesysRequestTypeClassificationAppService"/> right after the
/// ticket exists: its department becomes the responsible one (through the
/// transfer semantics, <c>OriginatingDepartmentId</c> untouched), the
/// configured automatic assignment runs, and its SLA policy is applied to
/// the running period without restarting the clock. A missing or unusable
/// request type is not an error: the ticket is created with the default
/// priority, stays Unclassified and is left to the existing manual classification
/// workflow (it is not a human handoff). An explicitly
/// <i>invalid</i> request type (unknown, inactive, ambiguous, cannot route) is
/// refused with 422 before anything is written.
/// </para>
/// </summary>
public sealed class GenesysInquiryIngestionAppService(
    GenesysOptions options,
    IGenesysConversationRepository conversationRepository,
    IGenesysQueueMappingRepository queueMappingRepository,
    IDepartmentRepository departmentRepository,
    IChannelRepository channelRepository,
    ITicketRepository ticketRepository,
    IntakeRecordAppService intakeRecordAppService,
    TicketCreationAppService ticketCreationAppService,
    CustomerSearchAppService customerSearchAppService,
    IAuditEntryWriter auditWriter,
    ITicketingUnitOfWork unitOfWork,
    GenesysAgentResolutionAppService agentResolution,
    GenesysDefaultPriorityResolver defaultPriorityResolver,
    GenesysRequestTypeClassificationAppService classificationAppService,
    GenesysAgentHandlingHistory handlingHistory,
    GenesysInteractionHandlerRecorder? handlerRecorder = null,
    DefaultClassificationService? defaultClassification = null)
{
    public async Task<GenesysIngestionResult> IngestAsync(
        Guid callerEmployeeId, GenesysInquiryDto inquiry, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(inquiry);

        if (!options.Enabled)
        {
            return GenesysIngestionResult.Failure(GenesysIngestionOutcome.IntegrationDisabled);
        }

        if (string.IsNullOrWhiteSpace(inquiry.ConversationId))
        {
            return GenesysIngestionResult.Failure(GenesysIngestionOutcome.ConversationIdRequired);
        }

        var conversationId = inquiry.ConversationId.Trim();
        var serviceRequestId = NullIfBlank(inquiry.ServiceRequestId);

        // Idempotency, first — before an intake record or anything else is
        // written, so a retry never leaves debris behind either. The identity of
        // a ticket is the (conversation, service request) pair; a caller that
        // sends no service request id keeps the original rule — the conversation
        // IS the ticket, whichever service request created it.
        if (await FindExistingAsync(conversationId, serviceRequestId, cancellationToken) is { } existing)
        {
            return await AlreadyIngestedAsync(existing, cancellationToken);
        }

        var channel = await channelRepository.GetByCodeAsync(GenesysChannelResolver.CodeFor(inquiry.Channel), cancellationToken);
        if (channel is null || !channel.IsActive)
        {
            return GenesysIngestionResult.Failure(
                GenesysIngestionOutcome.ChannelNotConfigured,
                $"No active channel is configured under code '{GenesysChannelResolver.CodeFor(inquiry.Channel)}'.");
        }

        var (department, departmentSource) = await ResolveDepartmentAsync(inquiry, cancellationToken);

        // An EXPLICITLY supplied request type is validated before anything is written. Absent is fine (the ticket is created on the
        // default priority and stays Unclassified for manual classification).
        RequestTypeResolution? requestTypeCheck = inquiry.RequestType is { IsSupplied: true } supplied
            ? await classificationAppService.ValidateAsync(supplied, cancellationToken)
            : null;

        // Default classification (Customer Services / General): an inquiry whose request type is missing, invalid, ambiguous or cannot complete a
        // category + request type pair is classified by the approved fallback instead of staying "Unclassified" - and instead of guessing. A valid
        // request type keeps its normal route (classified right after creation). Collections is out of scope; with the fallback not configured the
        // previous behaviour applies unchanged.
        DefaultClassification? fallback = null;
        string? fallbackReason = null;
        string? configurationError = null;
        if (defaultClassification is not null && !DefaultClassificationService.IsExcluded(department))
        {
            fallbackReason = inquiry.RequestType is not { IsSupplied: true }
                ? "no request type was supplied."
                : await classificationAppService.GetCategoryPairingGapAsync(requestTypeCheck!, cancellationToken);
            if (fallbackReason is not null)
            {
                // General / General within the department the AI named. Customer Services' only when there is no valid department. A valid
                // department WITHOUT its own General is a configuration error - never a cross-department stand-in, never a move.
                var resolved = await defaultClassification.ResolveForDepartmentAsync(department?.DepartmentId, cancellationToken);
                if (resolved.Value is { } available)
                {
                    fallback = available;
                    if (department is null)
                    {
                        (department, departmentSource) = (available.Department, GenesysDepartmentSource.DefaultClassification);
                    }
                }
                else if (resolved.DepartmentKnown)
                {
                    configurationError = resolved.Detail;
                }
                else
                {
                    fallbackReason = null;   // the rule is not configured at all: previous behaviour
                }
            }
        }

        if (department is null)
        {
            // Nothing named a department. Routing still never falls back to a guess, only to what configuration says, in this order:
            // the department the (valid) request type is configured to be handled by, then the configured classification queue.
            // With neither, the inquiry is refused exactly as before.
            if (requestTypeCheck is { IsValid: true, RequestType: { } routedType }
                && await departmentRepository.GetByIdAsync(routedType.DepartmentId, cancellationToken) is { IsActive: true } typeDepartment)
            {
                (department, departmentSource) = (typeDepartment, GenesysDepartmentSource.RequestTypeDepartment);
            }
            else if (await ResolveClassificationDepartmentAsync(cancellationToken) is { } classificationQueue)
            {
                (department, departmentSource) = (classificationQueue, GenesysDepartmentSource.ClassificationQueue);
            }
            else
            {
                return GenesysIngestionResult.Failure(
                    GenesysIngestionOutcome.DepartmentNotResolved,
                    DescribeUnresolvedDepartment(inquiry));
            }
        }

        // Present-but-unusable is the caller's mistake and is refused with nothing created, so a retry with a corrected value works and
        // no half-classified ticket is left behind — UNLESS a classification queue is configured: then the customer's request must not be
        // lost to a routing-configuration gap (an intent nobody mapped, a request type whose department was retired). The ticket is created
        // Unclassified in that queue, where a person classifies it, instead of being routed on a guess. An explicit department the caller
        // named is respected; only a department that came from a queue mapping or a fallback is replaced.
        string? requestTypeRejected = null;
        if (fallback is null && requestTypeCheck is { IsValid: false } rejected)
        {
            var classificationQueue = await ResolveClassificationDepartmentAsync(cancellationToken);
            if (classificationQueue is null)
            {
                return GenesysIngestionResult.Failure(GenesysIngestionOutcome.RequestTypeInvalid, rejected.Detail);
            }

            requestTypeRejected = rejected.Detail ?? "the supplied request type cannot be used.";
            if (departmentSource is not (GenesysDepartmentSource.ExplicitDepartmentId or GenesysDepartmentSource.ExplicitDepartmentCode))
            {
                (department, departmentSource) = (classificationQueue, GenesysDepartmentSource.ClassificationQueue);
            }
        }

        // The caller's number as TigerCS writes it. Genesys reports a voice
        // caller as a telephony address (Call.Ani = "tel:+971…"), which no
        // lookup source understands and the customer directory's phone
        // grouping cannot canonicalize — so it is reduced to "+971…" once,
        // here, and that one value is what is searched and stored. A
        // withheld caller id comes out as no number at all.
        var customerPhone = CustomerPhoneNumber.FromTelephonyAddress(inquiry.CustomerPhone);

        // Customer lookup through the EXISTING flow — enrichment only, and
        // never able to stop the inquiry (see this type's remarks).
        var lookup = await LookUpCustomerAsync(customerPhone, cancellationToken);

        // Agent identity mapping — server-side, from the Genesys User ID
        // (the existing agentId field) alone. This event may legitimately
        // name no agent (a queued chat, a system-created conversation), and
        // like the customer lookup it never gates the inquiry: an unmapped
        // agent is recorded as such on the audit entry, the verbatim Genesys
        // agent context is still stored, and ownership stays null. The
        // strict, refusing path for an agent acting is the agent-context
        // endpoint (GenesysAgentContextAppService).
        var agent = await ResolveAgentAsync(inquiry.AgentId, cancellationToken);

        // Was this accepted by a person? Judged from the actual event (an explicit flag, or a voice call created at pickup by a mapped agent),
        // never from the channel alone: a Phone ticket with no such evidence stays AI-handled and is not moved.
        var humanSignal = GenesysHumanHandlingEvidence.ForInquiry(inquiry, agent is { IsResolved: true });

        // Controlled configuration failure: a human-handled inquiry whose (valid) department has no usable General is refused with an explicit,
        // audited outcome - nothing is created, nothing is moved, and the same delivery succeeds once EnsureDefaultGeneralClassification.sql ran.
        if (configurationError is not null && humanSignal != GenesysHumanSignal.None)
        {
            await auditWriter.WriteAsync(
                callerEmployeeId, DefaultClassificationAuditActions.ConfigurationError, GenesysAuditActions.ConversationEntityType, conversationId,
                beforeValue: null,
                afterValue: $"Handling=Human;Channel={inquiry.Channel};DepartmentId={department!.DepartmentId};DepartmentCode={department.Code};Error={configurationError};"
                    + "Action=Run EnsureDefaultGeneralClassification.sql;TicketCreated=false",
                correlationId: Guid.NewGuid(), cancellationToken);
            await unitOfWork.SaveChangesAsync(cancellationToken);
            return GenesysIngestionResult.Failure(GenesysIngestionOutcome.DefaultClassificationNotConfigured, configurationError);
        }

        // The configured default priority (Normal). Unresolvable configuration
        // never costs the inquiry: it falls back to the previous behavior (no
        // priority, no SLA until classified) and says so on the audit entry.
        var defaultPriority = await defaultPriorityResolver.ResolveAsync(cancellationToken);

        var intake = await intakeRecordAppService.CreateAsync(
            callerEmployeeId,
            new CreateIntakeRecordRequestDto(
                ChannelId: channel.ChannelId.ToString(),
                // Whatever the channel actually supplied — a withheld caller
                // id or a social DM legitimately carries none, and losing the
                // inquiry over a missing number is exactly what must not
                // happen (see the enforceChannelPhoneRequirement argument).
                PhoneNumber: customerPhone ?? string.Empty,
                DepartmentId: department.DepartmentId,
                IsUnitRelated: false,
                RawUnitNumberEntered: inquiry.UnitNumber,
                PriorityHint: null),
            cancellationToken,
            enforceChannelPhoneRequirement: false);

        if (intake.Outcome != IntakeRecordOutcome.Success || intake.Response is null)
        {
            // Everything the intake can reject on — the channel and the
            // department — was already resolved and validated above, so this
            // is a genuinely unexpected state (a row deactivated between the
            // two calls). Reported as the specific configuration problem it
            // is, never flattened into a misleading one.
            return GenesysIngestionResult.Failure(
                intake.Outcome == IntakeRecordOutcome.DepartmentNotFound
                    ? GenesysIngestionOutcome.DepartmentNotResolved
                    : GenesysIngestionOutcome.ChannelNotConfigured,
                $"The intake record could not be created ({intake.Outcome}).");
        }

        var request = new CreateTicketRequestDto(
            IntakeRecordId: intake.Response.IntakeRecordId,
            UnitReferenceId: null,
            ContactReferenceId: null,
            // Unclassified, on purpose: nobody has read the request yet, so
            // there is no category to give. The department — which IS known —
            // places the ticket, and nothing else about the request is
            // guessed. The priority is the configured DEFAULT (Normal), not a
            // judgement: it is what lets the SLA run from creation, and an
            // authorized person changes it through the existing priority flow.
            CategoryId: null,
            PriorityId: defaultPriority?.PriorityId,
            RequestSummary: BuildRequestSummary(inquiry),
            // The website form's tower/unit, when the customer typed one —
            // the same manual snapshot an agent would enter by hand. Never a
            // verified match, and never used to run a second lookup.
            ManualProjectName: NullIfBlank(inquiry.TowerName),
            ManualUnitNumber: NullIfBlank(inquiry.UnitNumber),
            DepartmentId: department.DepartmentId,
            // The fallback is applied by ticket creation itself; a valid request type is classified right after creation instead.
            SkipDefaultClassification: fallback is null,
            // Deliberately no RequestTypeId: nothing about a department, a
            // queue or a channel implies a request type.
            GenesysContext: new GenesysInteractionContextDto(
                ConversationId: conversationId,
                CalledNumber: inquiry.CalledNumber,
                QueueId: inquiry.QueueId,
                QueueName: inquiry.QueueName,
                AgentId: inquiry.AgentId,
                AgentName: inquiry.AgentName,
                InteractionStartedAtUtc: inquiry.StartedAtUtc,
                Direction: inquiry.Direction,
                CustomerName: inquiry.CustomerName ?? lookup.CustomerName,
                CustomerEmail: inquiry.CustomerEmail,
                ServiceRequestId: serviceRequestId,
                RequiresHumanFollowUp: humanSignal != GenesysHumanSignal.None));

        TicketCreationResult creation;
        try
        {
            // A call picked up by a human before the ticket exists names the agent who ANSWERED it: that agent owns the ticket (directly, in
            // the Call Center) instead of Round Robin. Only a PHONE inquiry is, by contract, created at pickup. On every digital channel
            // (chat, WhatsApp, Instagram, Facebook, social) a top-level agentId is only who is offered/assigned/queued - never proof that the
            // agent accepted - so it is NOT an answer, even beside humanFollowUpRequired: such a ticket goes to the Call Center and Round Robin,
            // and the agent only takes it when a later update reports them CONNECTED (routing.agentId), which replaces that automatic pick.
            AnsweringAgent? answering = null;
            if (options.AssignOnAnswer
                && NullIfBlank(inquiry.AgentId) is { } answeringAgentId
                && inquiry.Channel == GenesysChannel.Phone
                && agent is not null)
            {
                answering = GenesysAnsweringAgentAssignmentService.ToAnsweringAgent(answeringAgentId, agent);
            }

            creation = await ticketCreationAppService.CreateAsync(
                callerEmployeeId, request, cancellationToken, callerRoles: null, answeringAgent: answering);
        }
        catch (DuplicateWriteException)
        {
            // A concurrent delivery of the SAME conversation won the race and
            // its interaction row already claimed the conversation id (unique
            // index). That is the guarantee working, not a failure: answer
            // with the ticket that won.
            var winner = await FindExistingAsync(conversationId, serviceRequestId, cancellationToken);
            return winner is null
                ? GenesysIngestionResult.CreationFailed(TicketCreationOutcome.TicketNumberCollision)
                : await AlreadyIngestedAsync(winner, cancellationToken);
        }

        if (creation.Outcome != TicketCreationOutcome.Success || creation.Response is null)
        {
            return GenesysIngestionResult.CreationFailed(creation.Outcome);
        }

        // Interaction ownership: the resolved Ticketing user is recorded on
        // the originating interaction the creation just wrote (it is the
        // one row carrying this conversation id). That records who HANDLED
        // the call. Who OWNS the ticket was decided inside the creation: a
        // call a mapped, eligible Call Center agent answered is assigned
        // directly to that agent (see answering above); nothing here changes
        // ownership afterwards.
        if (agent is { IsResolved: true }
            && await conversationRepository.GetByConversationAndTicketAsync(conversationId, creation.Response.TicketId, cancellationToken) is { } originating)
        {
            if (originating.RecordHandlingAgentIfAbsent(agent.Agent!.GenesysUserId, agent.Agent.UserId))
            {
                // The first handler is also preserved in the ticket's event timeline, beside any later call transfers.
                await handlingHistory.RecordFirstHandlerAsync(creation.Response.TicketId, conversationId, agent.Agent, cancellationToken);
            }

            // Also the per-agent history ticket visibility rests on (TicketInteractionHandlers). Staged here and saved with the audit entry below.
            if (handlerRecorder is not null)
            {
                await handlerRecorder.StageAsync(
                    originating, agent.Agent, InteractionHandlerSource.Inquiry, inquiry.StartedAtUtc, cancellationToken);
            }
        }

        // The integration-level audit entry, written after the ticket's own
        // transaction has committed. The ticket's essential "Create" audit is
        // written INSIDE that transaction by ticket creation itself and is
        // atomic with it; this one additionally records the Genesys context
        // the ticket came from (conversation, channel, event, queue, lookup
        // outcome), which only exists once the ticket does.
        await auditWriter.WriteAsync(
            callerEmployeeId,
            GenesysAuditActions.InquiryIngested,
            GenesysAuditActions.ConversationEntityType,
            conversationId,
            beforeValue: null,
            afterValue:
                $"TicketId={creation.Response.TicketId};TicketNumber={creation.Response.TicketNumber};"
                + $"Channel={inquiry.Channel};DepartmentId={department.DepartmentId};DepartmentCode={department.Code};"
                // HOW the department was decided, with the raw routing inputs
                // the caller sent — so a ticket that landed in an unexpected
                // department can be traced to the exact input (an explicit
                // code or id in the Genesys flow, or a queue mapping) without
                // guessing. Finance, or any other department, is never a
                // fallback: one of these three inputs named it.
                + $"DepartmentSource={departmentSource};"
                + $"ReceivedDepartmentId={(inquiry.DepartmentId?.ToString() ?? "(none)")};"
                + $"ReceivedDepartmentCode={(string.IsNullOrWhiteSpace(inquiry.DepartmentCode) ? "(none)" : inquiry.DepartmentCode.Trim())};"
                + $"QueueId={inquiry.QueueId ?? "(none)"};QueueName={inquiry.QueueName ?? "(none)"};"
                + $"CustomerLookup={lookup.Status};Classification={(fallback is null ? "Unclassified" : "Default")};"
                + $"DefaultClassification={(fallback is null ? "(not applied)" : $"{DefaultClassificationService.DisplayLabel} because {fallbackReason}")};"
                + $"DefaultClassificationConfigurationError={configurationError ?? "(none)"};"
                + $"DefaultPriority={(defaultPriority is null ? "(none: not configured or not found)" : $"{defaultPriority.Name}({defaultPriority.PriorityId})")};"
                + $"AgentMapping={DescribeAgentMapping(inquiry.AgentId, agent)};"
                + $"ServiceRequestId={serviceRequestId ?? "(none)"};HumanFollowUpRequired={inquiry.HumanFollowUpRequired == true};HumanSignal={humanSignal};"
                + $"RequestTypeRejected={requestTypeRejected ?? "(none)"}",
            correlationId: Guid.NewGuid(),
            cancellationToken);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        if (configurationError is not null)
        {
            // AI-handled inquiry in a department without its own General: created (the customer is not lost), left unclassified and NOT moved,
            // and the configuration error is recorded against the ticket. It can never be closed by the AI or handed to a person until fixed.
            await auditWriter.WriteAsync(
                callerEmployeeId, DefaultClassificationAuditActions.ConfigurationError, "Ticket", creation.Response.TicketId.ToString(),
                beforeValue: null,
                afterValue: $"Handling=AI;DepartmentId={department.DepartmentId};DepartmentCode={department.Code};Error={configurationError};Action=Run EnsureDefaultGeneralClassification.sql",
                correlationId: Guid.NewGuid(), cancellationToken);
            await unitOfWork.SaveChangesAsync(cancellationToken);
        }

        var classification = fallback is not null
            ? new GenesysClassificationResult(
                GenesysClassificationOutcome.Classified, fallback.RequestType.RequestTypeId, fallback.RequestType.Name, fallback.Department.DepartmentId,
                Detail: $"Default classification {DefaultClassificationService.DisplayLabel} applied because {fallbackReason}")
            : requestTypeRejected is not null
                ? GenesysRequestTypeClassificationAppService.AwaitClassification(
                    $"{requestTypeRejected} The ticket is in the classification queue for a person to classify.")
                : await ClassifyOrQueueAsync(callerEmployeeId, creation.Response.TicketId, conversationId, inquiry, cancellationToken);

        // Safety net: a validated request type that could not be applied after all (it changed between validation and write, or a concurrent
        // update won) must not leave the new ticket Unclassified. Only a ticket with no valid classification is touched.
        if (classification.Outcome == GenesysClassificationOutcome.AwaitingClassification
            && defaultClassification is not null
            && await defaultClassification.EnsureClassifiedAsync(
                creation.Response.TicketId, classification.Detail ?? "the request type could not be applied.", callerEmployeeId,
                save: true, cancellationToken) == DefaultClassificationApplyOutcome.Applied)
        {
            classification = classification with
            {
                Outcome = GenesysClassificationOutcome.Classified,
                RequestTypeName = DefaultClassificationService.RequestTypeName,
                Detail = $"Default classification {DefaultClassificationService.DisplayLabel} applied because {classification.Detail}"
            };
        }

        // Classification can move the ticket (department, owner), so the
        // response is read back rather than echoing the creation snapshot.
        var ticketNow = classification.Outcome == GenesysClassificationOutcome.Classified
            ? await ticketRepository.GetByIdAsync(creation.Response.TicketId, cancellationToken)
            : null;

        return GenesysIngestionResult.Created(
            ticketNow is null ? creation.Response : TicketProjection.ToResponseDto(ticketNow), classification);
    }

    /// <summary>
    /// A request type the bot supplied (already validated before the ticket was
    /// written) is applied now; a missing one leaves the ticket
    /// <b>Unclassified</b> for the existing manual classification workflow —
    /// <b>no human handoff is raised for it</b>: whether the customer needs a
    /// person is Genesys' explicit signal to give (the handoff contract), never
    /// something inferred from a missing field. If a validated type still cannot
    /// be applied (it changed in the instant between validation and write, or a
    /// concurrent update won) the ticket, which now exists, stays Unclassified
    /// rather than being lost.
    /// </summary>
    private async Task<GenesysClassificationResult> ClassifyOrQueueAsync(
        Guid callerEmployeeId, long ticketId, string conversationId, GenesysInquiryDto inquiry, CancellationToken cancellationToken)
    {
        if (inquiry.RequestType is not { IsSupplied: true } requestType)
        {
            return GenesysRequestTypeClassificationAppService.AwaitClassification("no request type was supplied.");
        }

        var classified = await classificationAppService.ClassifyAsync(
            callerEmployeeId, ticketId, conversationId, requestType, "GenesysIngestion", cancellationToken);

        return classified.Outcome is GenesysClassificationOutcome.Classified or GenesysClassificationOutcome.AlreadyClassified
            ? classified
            : GenesysRequestTypeClassificationAppService.AwaitClassification(
                classified.Detail ?? $"the supplied request type could not be applied ({classified.Outcome}).");
    }

    /// <summary>
    /// The interaction an inquiry's idempotency identity already maps to. With a service request id it is the exact (conversation, service
    /// request) pair; without one it is the conversation's default interaction, so an integration that never sends a service request id keeps
    /// "one conversation, one ticket".
    /// </summary>
    private Task<TicketInteraction?> FindExistingAsync(string conversationId, string? serviceRequestId, CancellationToken cancellationToken) =>
        serviceRequestId is null
            ? conversationRepository.GetByConversationIdAsync(conversationId, cancellationToken)
            : conversationRepository.GetByConversationAndServiceRequestAsync(conversationId, serviceRequestId, cancellationToken);

    /// <summary>
    /// The configured safe classification queue (<c>Genesys:ClassificationDepartmentCode</c>), or null when none is configured or the
    /// department is missing/inactive — in which case nothing is routed on a guess. Never a department id baked into code.
    /// </summary>
    private async Task<Department?> ResolveClassificationDepartmentAsync(CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(options.ClassificationDepartmentCode))
        {
            return null;
        }

        var code = options.ClassificationDepartmentCode.Trim();
        var all = await departmentRepository.ListAsync(activeOnly: true, cancellationToken);
        return all.FirstOrDefault(d => string.Equals(d.Code, code, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// The idempotent answer: the ticket this conversation already produced.
    /// Reads the ticket back so a retry receives the same body the original
    /// call did, rather than a bare acknowledgement the caller cannot act on.
    /// </summary>
    private async Task<GenesysIngestionResult> AlreadyIngestedAsync(
        TicketInteraction interaction, CancellationToken cancellationToken)
    {
        var ticket = await ticketRepository.GetByIdAsync(interaction.TicketId, cancellationToken);
        return ticket is null
            ? GenesysIngestionResult.Failure(
                GenesysIngestionOutcome.AlreadyIngested,
                $"Conversation already linked to ticket {interaction.TicketId}.")
            : GenesysIngestionResult.AlreadyIngested(TicketProjection.ToResponseDto(ticket));
    }

    /// <summary>
    /// Department resolution, in the approved priority order:
    /// <list type="number">
    ///   <item><description>the customer's explicit selection (the website chat's Leasing / Customer Service / Maintenance), by id or by department code;</description></item>
    ///   <item><description>otherwise the configured Genesys Queue → Department mapping.</description></item>
    /// </list>
    /// No department name is compared against a literal anywhere, and an
    /// unmapped queue resolves to nothing rather than to a fallback
    /// department — an inquiry routed somewhere nobody configured is a
    /// configuration gap that must be visible.
    /// </summary>
    private async Task<(Department? Department, GenesysDepartmentSource Source)> ResolveDepartmentAsync(
        GenesysInquiryDto inquiry, CancellationToken cancellationToken)
    {
        if (inquiry.DepartmentId is { } departmentId)
        {
            var selected = await departmentRepository.GetByIdAsync(departmentId, cancellationToken);
            return (selected is { IsActive: true } ? selected : null, GenesysDepartmentSource.ExplicitDepartmentId);
        }

        if (!string.IsNullOrWhiteSpace(inquiry.DepartmentCode))
        {
            var code = inquiry.DepartmentCode.Trim();
            var all = await departmentRepository.ListAsync(activeOnly: true, cancellationToken);
            return (
                all.FirstOrDefault(d => string.Equals(d.Code, code, StringComparison.OrdinalIgnoreCase)),
                GenesysDepartmentSource.ExplicitDepartmentCode);
        }

        if (string.IsNullOrWhiteSpace(inquiry.QueueId))
        {
            return (null, GenesysDepartmentSource.None);
        }

        var mapping = await queueMappingRepository.GetActiveByQueueIdAsync(inquiry.QueueId.Trim(), cancellationToken);
        if (mapping is null)
        {
            return (null, GenesysDepartmentSource.None);
        }

        var mapped = await departmentRepository.GetByIdAsync(mapping.DepartmentId, cancellationToken);
        return (mapped is { IsActive: true } ? mapped : null, GenesysDepartmentSource.QueueMapping);
    }

    /// <summary>
    /// Why <see cref="ResolveDepartmentAsync"/> found nothing, naming the
    /// input that actually decided it — an explicit department that does not
    /// resolve is reported as exactly that, never as "no department named".
    /// Mirrors the resolution order: an explicit selection is not second-
    /// guessed with the queue mapping.
    /// </summary>
    private static string DescribeUnresolvedDepartment(GenesysInquiryDto inquiry)
    {
        if (inquiry.DepartmentId is { } departmentId)
        {
            return $"Department {departmentId} does not exist or is inactive.";
        }

        if (!string.IsNullOrWhiteSpace(inquiry.DepartmentCode))
        {
            return $"No active department has the code '{inquiry.DepartmentCode.Trim()}'.";
        }

        return string.IsNullOrWhiteSpace(inquiry.QueueId)
            ? "The inquiry named no department and carried no queue id."
            : $"Genesys queue '{inquiry.QueueId.Trim()}' has no active department mapping.";
    }

    /// <summary>
    /// Runs the existing customer lookup for the inquiry's mobile number and
    /// reduces it to what ingestion actually needs: whether a customer was
    /// identified, and a display name to enrich the interaction with. Every
    /// failure mode — no phone, nothing found, a source down, an unexpected
    /// fault — comes back as a status, never as an exception: the inquiry
    /// must survive a CRM/PACT outage.
    /// </summary>
    private async Task<CustomerLookupSummary> LookUpCustomerAsync(string? phoneNumber, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(phoneNumber))
        {
            return new CustomerLookupSummary("NoPhoneSupplied", null);
        }

        try
        {
            var result = await customerSearchAppService.SearchByPhoneAsync(phoneNumber, cancellationToken);

            // A phone number is not an identity: when it matches more than one customer (a shared or reassigned number), no display name is
            // taken from the lookup at all — naming the interaction after whichever record came first would attach one customer's name to
            // another's conversation. Exactly one distinct customer is required to enrich the name.
            var candidates = result.CrmBuyers
                .Select(b => b.Customer.FullNameEnglish ?? b.Customer.FullNameArabic)
                .Concat(result.ExternalSources
                    .SelectMany(source => source.Customers)
                    .Select(c => (string?)c.DisplayName))
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .DistinctBy(name => name!.Trim(), StringComparer.OrdinalIgnoreCase)
                .ToList();

            var found = result.CrmStatus == "Found"
                || result.ExternalSources.Any(s => s.Status == "Found");

            if (candidates.Count > 1)
            {
                return new CustomerLookupSummary($"Found(Ambiguous:{candidates.Count} customers share this number)", null);
            }

            return new CustomerLookupSummary(
                found ? "Found" : $"NotFound(Crm={result.CrmStatus})",
                candidates.FirstOrDefault());
        }
        catch (Exception ex)
        {
            // Deliberately broad: an unexpected fault in an enrichment step
            // must never cost us the inquiry. What went wrong is recorded on
            // the audit entry; the ticket is created regardless.
            return new CustomerLookupSummary($"LookupFailed({ex.GetType().Name})", null);
        }
    }

    /// <summary>
    /// The ticket's request summary. Whatever the channel collected wins;
    /// otherwise a factual, channel-named placeholder — never an invented
    /// description of what the customer wanted, which only the conversation
    /// itself can say.
    /// </summary>
    private static string BuildRequestSummary(GenesysInquiryDto inquiry)
    {
        if (!string.IsNullOrWhiteSpace(inquiry.Subject))
        {
            return inquiry.Subject.Trim();
        }

        return inquiry.Channel switch
        {
            GenesysChannel.Phone => "Phone call received via Genesys",
            GenesysChannel.WebsiteChat => "Website chat started via Genesys",
            GenesysChannel.WhatsApp => "WhatsApp conversation started via Genesys",
            GenesysChannel.SocialMedia => "Social media conversation started via Genesys",
            GenesysChannel.Instagram => "Instagram conversation started via Genesys",
            GenesysChannel.Facebook => "Facebook Messenger conversation started via Genesys",
            _ => "Customer inquiry received via Genesys"
        };
    }

    /// <summary>
    /// Resolves the event's agent, when it names one, to a Ticketing user.
    /// Absent means "no agent yet" — not a failure. Every other outcome is
    /// returned as-is so the audit entry can say exactly what happened
    /// (NotMapped is reported as NotMapped, never flattened into "unknown").
    /// </summary>
    private async Task<GenesysAgentResolutionResult?> ResolveAgentAsync(string? genesysAgentId, CancellationToken cancellationToken) =>
        string.IsNullOrWhiteSpace(genesysAgentId)
            ? null
            : await agentResolution.ResolveByGenesysUserIdAsync(genesysAgentId, cancellationToken);

    private static string DescribeAgentMapping(string? genesysAgentId, GenesysAgentResolutionResult? agent) =>
        agent is null
            ? "(no agent)"
            : agent.IsResolved
                ? $"Resolved(HandledByUserId={agent.Agent!.UserId})"
                : $"{agent.Outcome}(GenesysUserId={genesysAgentId?.Trim()})";

    private static string? NullIfBlank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private sealed record CustomerLookupSummary(string Status, string? CustomerName);
}

/// <summary>The audit vocabulary of the Genesys integration, named once so no call site spells one differently.</summary>
public static class GenesysAuditActions
{
    public const string ConversationEntityType = "GenesysConversation";
    public const string InquiryIngested = "GenesysInquiryIngested";
    public const string ConversationEnded = "GenesysConversationEnded";

    /// <summary>A ticket was marked as human work: no chatbot clock, AI completion or document delivery may close it any more.</summary>
    public const string HumanFollowUpRequired = "GenesysHumanFollowUpRequired";

    /// <summary>Messages of a live conversation were stored before it ended (the incremental transcript).</summary>
    public const string TranscriptAppended = "GenesysTranscriptAppended";

    /// <summary>The conversation moved to another queue or agent (a transfer, an agent connecting) — before/after carry the full routing, so the audit trail holds every queue and agent it passed through.</summary>
    public const string RoutingChanged = "GenesysRoutingChanged";

    /// <summary>The customer confirmed during the interaction that the issue is resolved. Record-only: the ticket status in before/after is the proof nothing moved.</summary>
    public const string CustomerConfirmedResolved = "GenesysCustomerConfirmedResolved";

    /// <summary>The chatbot asked the customer something and TigerCS began waiting for the reply (inactivity timer started).</summary>
    public const string CustomerReplyTimerStarted = "GenesysCustomerReplyTimerStarted";

    /// <summary>The customer replied, or human follow-up was requested: the inactivity timer was cancelled.</summary>
    public const string CustomerReplyTimerCancelled = "GenesysCustomerReplyTimerCancelled";

    /// <summary>A Genesys agent was resolved to a Ticketing user and recorded as the handler of an interaction.</summary>
    public const string InteractionHandlerRecorded = "GenesysInteractionHandlerRecorded";

    /// <summary>A further Genesys agent was recorded as having handled an interaction (a transfer, or a second agent joining) — the per-agent history ticket visibility rests on.</summary>
    public const string InteractionHandlerAdded = "GenesysInteractionHandlerAdded";

    /// <summary>
    /// TigerCS raised pending human work on its own initiative, because an
    /// AI/virtual-agent conversation ended with no human ever taking it.
    /// Audited under its own action so "the integration asked for a human"
    /// and "we noticed nobody had" are distinguishable in the audit trail.
    /// </summary>
    public const string HandoffAutoRaisedOnConversationEnd = "GenesysHandoffAutoRaisedOnConversationEnd";

    /// <summary>Secure Screen Pop audit entries are keyed by the agent's Genesys User ID — never by the token.</summary>
    public const string ScreenPopEntityType = "GenesysScreenPop";

    /// <summary>A Screen Pop launch URL was issued for a mapped agent.</summary>
    public const string ScreenPopIssued = "GenesysScreenPopIssued";

    /// <summary>A Screen Pop launch was redeemed and the mapped user signed in.</summary>
    public const string ScreenPopRedeemed = "GenesysScreenPopRedeemed";

    /// <summary>A valid, unexpired launch was consumed but refused, because the agent's mapping or account no longer allowed sign-in.</summary>
    public const string ScreenPopRefused = "GenesysScreenPopRefused";
}

/// <summary>
/// Which routing input decided a Genesys ticket's department. Recorded on
/// the ingestion audit entry so an unexpected department (a web chat that
/// turned up in Finance, say) is traceable to the input that named it: the
/// code resolves in exactly this order and never falls back to a default
/// department, so the answer is always one of these three.
/// </summary>
public enum GenesysDepartmentSource
{
    None = 0,
    ExplicitDepartmentId = 1,
    ExplicitDepartmentCode = 2,
    QueueMapping = 3,

    /// <summary>No explicit department and no queue mapping, but the supplied (valid) request type is configured to be handled by this department.</summary>
    RequestTypeDepartment = 4,

    /// <summary>Nothing could be routed from configuration; the ticket went to the configured classification queue (<c>Genesys:ClassificationDepartmentCode</c>) for a person to classify.</summary>
    ClassificationQueue = 5,

    /// <summary>The inquiry carried no usable classification, so the approved default (Customer Services / General) placed the ticket.</summary>
    DefaultClassification = 6
}
