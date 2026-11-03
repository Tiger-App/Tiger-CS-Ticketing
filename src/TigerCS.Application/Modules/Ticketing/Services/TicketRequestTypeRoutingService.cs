using TigerCS.Application.Abstractions;
using TigerCS.Application.Modules.IdentityAndAccess.Abstractions;
using TigerCS.Application.Modules.SlaAndEscalation.Services;
using TigerCS.Application.Modules.Ticketing.Abstractions;
using TigerCS.Application.Modules.WorkflowConfiguration.Abstractions;
using TigerCS.Domain.Modules.SlaAndEscalation;
using TigerCS.Domain.Modules.Ticketing;
using TigerCS.Domain.Modules.WorkflowConfiguration;

namespace TigerCS.Application.Modules.Ticketing.Services;

/// <summary>Why a supplied request type could not be used. Every one of these leaves the ticket exactly as it was.</summary>
public enum RequestTypeResolutionFailure
{
    /// <summary>Neither an id nor a name was supplied.</summary>
    NotSupplied,

    NotFound,
    Inactive,

    /// <summary>The name matches active request types in more than one department; the id is required.</summary>
    Ambiguous,

    /// <summary>An id and a name were both supplied and name different request types; neither is trusted, so nothing is routed on a guess.</summary>
    Mismatch,

    /// <summary>The request type's responsible department is missing or inactive, so there is nowhere to route.</summary>
    DepartmentInactive,

    /// <summary>The request type has no Published workflow version, so it cannot govern a ticket.</summary>
    WorkflowNotPublished
}

/// <summary>A supplied request type, resolved and validated — or the precise reason it is not usable.</summary>
public sealed record RequestTypeResolution(
    RequestType? RequestType,
    WorkflowTemplate? PublishedWorkflow,
    RequestTypeResolutionFailure? Failure,
    string? Detail)
{
    public bool IsValid => Failure is null;
}

/// <summary>What applying a request type to a ticket did, for the caller's response and the audit trail.</summary>
public sealed record RequestTypeRoutingResult(
    int RequestTypeId,
    string RequestTypeName,
    int PreviousDepartmentId,
    int DepartmentId,
    bool Transferred,
    Guid? AssignedEmployeeId,
    string AssignmentOutcome,
    string SlaOutcome);

/// <summary>
/// Classifies a request type onto an existing ticket and applies everything
/// the request type configures, in the caller's transaction: its workflow
/// version (pinned), its responsible department (through the same transfer
/// semantics as <c>TicketAssignmentAppService.TransferAsync</c>), the
/// configured automatic assignment, and its SLA policy.
///
/// <para>
/// <b>Used for Genesys tickets, which are created before the request type
/// is known.</b> The ticket is created in the department the interaction
/// arrived in; <c>OriginatingDepartmentId</c> is therefore never rewritten —
/// only <c>CurrentDepartmentId</c> moves, via <see cref="Ticket.TransferToDepartment"/>
/// (which also clears the owner, so the receiving department's rule or queue
/// decides). The routing is a system action driven by configuration, so it is
/// not gated on the CS-Manager-only manual transfer role and does not
/// consult the source department's "allow transfers out" setting; the
/// configured request-type department is the authority.
/// </para>
///
/// <para>
/// <b>Assignment stays automatic and fail-safe.</b> <see cref="TicketAutoAssignmentService"/>
/// picks the configured assignee; no rule, a stale assignee or a department
/// that disables assignment leave the ticket in the responsible
/// department's queue. A ticket that already has an owner and does not move
/// departments keeps its owner.
/// </para>
///
/// <para>
/// <b>The SLA is adjusted, never restarted.</b> See
/// <see cref="SlaDueDateService.ReapplyPolicyForRequestTypeAsync"/>. Routing
/// and assignment never touch <c>FirstHumanResponseAtUtc</c>.
/// </para>
/// </summary>
public sealed class TicketRequestTypeRoutingService(
    IRequestTypeRepository requestTypeRepository,
    ICategoryRepository categoryRepository,
    IWorkflowTemplateRepository workflowTemplateRepository,
    IDepartmentRepository departmentRepository,
    TicketAutoAssignmentService autoAssignmentService,
    SlaDueDateService slaDueDateService,
    ITicketStatusHistoryRepository statusHistoryRepository,
    IAuditEntryWriter auditWriter,
    ITicketWorkflowEventRepository workflowEventRepository)
{
    /// <summary>
    /// The Category that pairs with a request type under the only rule the data model has: an active category of the request type's
    /// department with the same name (how the UAT import and the New Ticket picker pair them). Exactly one match or null — never invented,
    /// never guessed between several.
    /// </summary>
    public async Task<int?> ResolveCategoryIdAsync(RequestType requestType, CancellationToken cancellationToken = default)
    {
        var categories = await categoryRepository.ListAsync(activeOnly: true, requestType.DepartmentId, cancellationToken);
        var matches = categories
            .Where(c => string.Equals(c.Name?.Trim(), requestType.Name.Trim(), StringComparison.OrdinalIgnoreCase))
            .ToList();
        return matches.Count == 1 ? matches[0].CategoryId : null;
    }

    public async Task<int?> ResolveCategoryForRequestTypeIdAsync(int requestTypeId, CancellationToken cancellationToken = default) =>
        await requestTypeRepository.GetByIdAsync(requestTypeId, cancellationToken) is { } rt
            ? await ResolveCategoryIdAsync(rt, cancellationToken)
            : null;

    /// <summary>Resolves a request type by id (preferred) or by exact name, and validates that it can govern a ticket.</summary>
    public async Task<RequestTypeResolution> ResolveAsync(
        int? requestTypeId, string? requestTypeName, CancellationToken cancellationToken = default)
    {
        RequestType? requestType;
        if (requestTypeId is { } id)
        {
            requestType = await requestTypeRepository.GetByIdAsync(id, cancellationToken);
            if (requestType is null)
            {
                return Fail(RequestTypeResolutionFailure.NotFound, $"Request type {id} does not exist.");
            }

            // Id wins, but a name sent alongside it must agree: a stale or mistyped pair would otherwise route to the wrong department.
            if (!string.IsNullOrWhiteSpace(requestTypeName)
                && !string.Equals(requestType.Name.Trim(), requestTypeName.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                return Fail(RequestTypeResolutionFailure.Mismatch,
                    $"Request type {id} is '{requestType.Name}', not '{requestTypeName.Trim()}'. Send the id alone, or an id and name that agree.");
            }
        }
        else if (!string.IsNullOrWhiteSpace(requestTypeName))
        {
            var name = requestTypeName.Trim();
            var all = await requestTypeRepository.ListAsync(departmentId: null, includeInactive: true, cancellationToken);
            var matches = all.Where(r => string.Equals(r.Name, name, StringComparison.OrdinalIgnoreCase)).ToList();
            var active = matches.Where(r => r.IsActive).ToList();

            if (active.Count > 1)
            {
                return Fail(RequestTypeResolutionFailure.Ambiguous,
                    $"Request type name '{name}' exists in more than one department; send requestTypeId instead.");
            }

            requestType = active.FirstOrDefault() ?? matches.FirstOrDefault();
            if (requestType is null)
            {
                return Fail(RequestTypeResolutionFailure.NotFound, $"No request type is named '{name}'.");
            }
        }
        else
        {
            return Fail(RequestTypeResolutionFailure.NotSupplied, "No request type was supplied.");
        }

        if (!requestType.IsActive)
        {
            return Fail(RequestTypeResolutionFailure.Inactive, $"Request type '{requestType.Name}' ({requestType.RequestTypeId}) is inactive.");
        }

        var department = await departmentRepository.GetByIdAsync(requestType.DepartmentId, cancellationToken);
        if (department is null || !department.IsActive)
        {
            return Fail(RequestTypeResolutionFailure.DepartmentInactive,
                $"The responsible department ({requestType.DepartmentId}) of request type '{requestType.Name}' is missing or inactive.");
        }

        var workflow = await workflowTemplateRepository.GetPublishedAsync(requestType.WorkflowId, cancellationToken);
        if (workflow is null)
        {
            return Fail(RequestTypeResolutionFailure.WorkflowNotPublished,
                $"Request type '{requestType.Name}' has no published workflow version.");
        }

        return new RequestTypeResolution(requestType, workflow, null, null);
    }

    /// <summary>
    /// Applies a <b>valid</b> resolution to a ticket that has no request type
    /// yet. Throws <see cref="TicketRequestTypeAlreadySetException"/> (domain
    /// guard) when one is set — the caller decides idempotency before calling.
    ///
    /// <para>
    /// <c>reclassify: true</c> is the one exception: it replaces the request
    /// type (and pinned workflow version) of an already-classified ticket. The
    /// caller owns the decision that this is allowed; everything else —
    /// transfer semantics, automatic assignment, SLA re-policy without a
    /// clock restart — is the same code path, and the previous classification
    /// is kept as an append-only <see cref="WorkflowEventType.RequestTypeReclassified"/>
    /// event plus a <c>ReclassifyRequestType</c> audit entry.
    /// </para>
    /// </summary>
    /// <remarks>
    /// <c>defaultPriority</c> is for a ticket that predates default priorities
    /// (no priority, no SLA period): it gets the default so it can be measured
    /// at all, and its clock starts now — nothing is backdated. It is ignored
    /// for a ticket that already has a priority.
    /// </remarks>
    public async Task<RequestTypeRoutingResult> ApplyAsync(
        Ticket ticket,
        RequestTypeResolution resolution,
        Guid actorEmployeeId,
        string source,
        Priority? defaultPriority,
        DateTime nowUtc,
        Guid correlationId,
        CancellationToken cancellationToken = default,
        bool reclassify = false)
    {
        ArgumentNullException.ThrowIfNull(ticket);
        if (!resolution.IsValid || resolution.RequestType is null || resolution.PublishedWorkflow is null)
        {
            throw new ArgumentException("A valid request-type resolution is required.", nameof(resolution));
        }

        var requestType = resolution.RequestType;
        var previousDepartmentId = ticket.CurrentDepartmentId;
        var previousOwner = ticket.CurrentOwnerEmployeeId;
        var previousRequestTypeId = ticket.RequestTypeId;

        if (reclassify)
        {
            ticket.ReclassifyRequestType(requestType.RequestTypeId, resolution.PublishedWorkflow.WorkflowTemplateId);
        }
        else
        {
            ticket.ClassifyRequestType(requestType.RequestTypeId);
            ticket.PinWorkflowVersion(resolution.PublishedWorkflow.WorkflowTemplateId);
        }

        // The paired Category is saved with the request type so Ticket Details shows both. No pairing exists -> Category stays empty; a category a person already chose is never overwritten (a reclassification is only allowed by the caller when the category is the system's own).
        var categoryId = await ResolveCategoryIdAsync(requestType, cancellationToken);
        if (categoryId is { } pairedCategoryId && (reclassify || ticket.CategoryId is null))
        {
            ticket.ApplyCategoryFromRequestType(pairedCategoryId);
        }

        // Department FIRST, exactly as TransferAsync does: the responsible
        // department is the primary assignment, OriginatingDepartmentId is
        // never touched, and the transfer clears the previous owner.
        // Call Center first: a ticket sitting in the Call Center is waiting for a person. Classifying it records the request type (the
        // Call Center agent then transfers it to that department); the system never moves it past the Call Center on its own.
        var inCallCenter = await autoAssignmentService.IsCallCenterDepartmentAsync(ticket.CurrentDepartmentId, cancellationToken);
        var transferred = !inCallCenter && requestType.DepartmentId != ticket.CurrentDepartmentId;
        if (transferred)
        {
            ticket.TransferToDepartment(requestType.DepartmentId, preserveOwner: true);

            await auditWriter.WriteAsync(
                actorEmployeeId, "Transfer", "Ticket", ticket.TicketId.ToString(),
                beforeValue: $"DepartmentId={previousDepartmentId};AssignedEmployeeId={previousOwner?.ToString() ?? "DepartmentQueue"}",
                afterValue: $"DepartmentId={requestType.DepartmentId};Reason=Request type '{requestType.Name}' ({requestType.RequestTypeId}) routing ({source});OriginatingDepartmentId={ticket.OriginatingDepartmentId} (unchanged)",
                correlationId, cancellationToken);
        }

        // Existing automatic assignment: department + request type -> rule ->
        // employee, else the department queue. A ticket a person already owns
        // (and that did not move) keeps its owner.
        var assignmentOutcome = "OwnerKept";
        Guid? assignedEmployeeId = ticket.CurrentOwnerEmployeeId;
        if (ticket.CurrentOwnerEmployeeId is null)
        {
            var trigger = transferred ? AutoAssignmentTrigger.DepartmentTransfer : AutoAssignmentTrigger.RequestTypeClassified;
            var assignment = inCallCenter
                ? await autoAssignmentService.AssignCallCenterIntakeAsync(ticket, nowUtc, correlationId, cancellationToken)
                : await autoAssignmentService.ApplyAsync(ticket, nowUtc, correlationId, trigger, cancellationToken);
            // The department rule had no say: the shared CS Agent rotation guarantees an agent (failure recorded when nobody is eligible).
            if (!inCallCenter)
            {
                assignment = await autoAssignmentService.ApplyRoundRobinFallbackAsync(ticket, assignment, trigger, nowUtc, correlationId, cancellationToken);
            }

            assignmentOutcome = assignment.Outcome.ToString();
            assignedEmployeeId = assignment.AssignedEmployeeId;
        }

        // Ticket transfer tracking: the system moving a ticket to its request type's department is recorded as a
        // transfer too — but as SystemRouting with no acting employee, so it is visible in the history and the count
        // while never reading as an agent escalating.
        if (transferred)
        {
            await workflowEventRepository.AddAsync(
                TicketWorkflowEvent.ForTransfer(
                    ticket.TicketId, nowUtc, actorEmployeeId: null, TicketTransferReason.SystemRouting,
                    previousDepartmentId, ticket.CurrentDepartmentId, ticket.CurrentOwnerEmployeeId,
                    $"Request type '{requestType.Name}' routing ({source})", correlationId),
                cancellationToken);
        }

        var slaOutcome = await ApplySlaAsync(ticket, defaultPriority, actorEmployeeId, nowUtc, correlationId, cancellationToken);

        if (reclassify)
        {
            await workflowEventRepository.AddAsync(
                new TicketWorkflowEvent(
                    ticket.TicketId, WorkflowEventType.RequestTypeReclassified, nowUtc, actorEmployeeId: null,
                    ticketApprovalId: null,
                    note: $"Request type changed from {previousRequestTypeId} to '{requestType.Name}' ({requestType.RequestTypeId}) — {source}.",
                    correlationId),
                cancellationToken);
        }

        await auditWriter.WriteAsync(
            actorEmployeeId, reclassify ? "ReclassifyRequestType" : "ClassifyRequestType", "Ticket", ticket.TicketId.ToString(),
            beforeValue: $"RequestTypeId={previousRequestTypeId?.ToString() ?? "(none)"};DepartmentId={previousDepartmentId};AssignedEmployeeId={previousOwner?.ToString() ?? "DepartmentQueue"}",
            afterValue:
                $"RequestTypeId={requestType.RequestTypeId};RequestType={requestType.Name};CategoryId={categoryId?.ToString() ?? "(none paired)"};DepartmentId={ticket.CurrentDepartmentId};"
                + $"OriginatingDepartmentId={ticket.OriginatingDepartmentId};Transferred={transferred};"
                + $"Assignment={assignmentOutcome};AssignedEmployeeId={assignedEmployeeId?.ToString() ?? "DepartmentQueue"};"
                + $"Sla={slaOutcome};Source={source}",
            correlationId, cancellationToken);

        return new RequestTypeRoutingResult(
            requestType.RequestTypeId, requestType.Name, previousDepartmentId, ticket.CurrentDepartmentId,
            transferred, assignedEmployeeId, assignmentOutcome, slaOutcome);
    }

    private async Task<string> ApplySlaAsync(
        Ticket ticket, Priority? defaultPriority, Guid actorEmployeeId, DateTime nowUtc, Guid correlationId,
        CancellationToken cancellationToken)
    {
        if (ticket.PriorityId is null)
        {
            if (defaultPriority is null)
            {
                return "NoPriority";
            }

            // A ticket from before default priorities: give it the default
            // and start its clock now (never backdated), request-type policy
            // included because the request type is already set.
            ticket.ApplyDefaultPriority(defaultPriority.PriorityId);
            await slaDueDateService.OpenInitialPeriodAsync(ticket, nowUtc, actorEmployeeId, correlationId, cancellationToken);

            var previousState = ticket.SlaState;
            ticket.StartSlaClock();
            if (previousState != ticket.SlaState)
            {
                await statusHistoryRepository.AddAsync(
                    new TicketStatusHistory(
                        ticket.TicketId, TicketStatusDimension.SlaState, (byte)previousState, (byte)ticket.SlaState,
                        actorEmployeeId, actorIsSystem: true,
                        note: $"SLA clock started at request-type classification ({nowUtc:O}); the ticket had no priority before.",
                        correlationId, nowUtc),
                    cancellationToken);
            }

            return "StartedAtClassification";
        }

        return (await slaDueDateService.ReapplyPolicyForRequestTypeAsync(ticket, actorEmployeeId, correlationId, cancellationToken)).ToString();
    }

    private static RequestTypeResolution Fail(RequestTypeResolutionFailure failure, string detail) =>
        new(null, null, failure, detail);
}
