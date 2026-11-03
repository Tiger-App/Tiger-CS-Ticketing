using TigerCS.Application.Abstractions;
using TigerCS.Application.Authorization;
using TigerCS.Application.Modules.IdentityAndAccess.Abstractions;
using TigerCS.Application.Modules.Ticketing.Abstractions;
using TigerCS.Application.Modules.Ticketing.Dto;
using TigerCS.Application.Modules.WorkflowConfiguration.Abstractions;
using TigerCS.Domain.Modules.IdentityAndAccess;
using TigerCS.Domain.Modules.Ticketing;
using TigerCS.Domain.Modules.WorkflowConfiguration;

namespace TigerCS.Application.Modules.Ticketing.Services;

/// <summary>
/// Approvals and dependency events (Workflow/Automation phase 3): request /
/// approve / reject / cancel an approval cycle, and record the typed
/// prerequisite/maintenance events — all configuration-driven (the ticket's
/// request type must configure the approval), all audited, and all
/// independent of <c>TicketStatus</c>. The approval record is the
/// authoritative approval state; a ticket that also waits operationally uses
/// the existing structured Pending Internal, as a separate concern.
///
/// <para>
/// <b>No SLA is calculated here.</b> An approval decision only produces its
/// typed <see cref="TicketWorkflowEvent"/> (ApprovalReceived /
/// CustomerServiceApproved / ApprovalRejected) with a trustworthy timestamp;
/// phase 4 reads those events as its conditional trigger source.
/// </para>
///
/// <para>
/// <b>No automatic resume.</b> When an approval is granted, the ticket's
/// Pending state (if any) stays until an authorized user explicitly resumes
/// via the existing status change — whether approval auto-resumes the
/// ticket is an open business decision
/// (docs/Workflow-Approvals-Phase3.md).
/// </para>
///
/// <para>
/// <b>Provisional approver authorization (fail-safe, documented):</b> the
/// decision is gated by the approval's target snapshot — a role target
/// needs that role; an employee target needs that exact employee; a
/// department target needs active membership of that department AND one of
/// <see cref="DepartmentTargetDefaultApproverRoles"/> (or the requirement's
/// narrowing role). Nothing broader is granted; the exact Accounting and CS
/// approver roles remain open business decisions, and the ADR-0024 System
/// Administrator override applies through <see cref="AuthorizationGate"/>
/// exactly as it does for every other permission rule.
/// </para>
///
/// <para>
/// <b><see cref="ApprovalType.ReopenApproval"/> is a request, not a reopen.</b>
/// It is the one type requestable on a <b>Closed</b> ticket — every other
/// type stays refused there — and it exists so that a role without direct
/// Reopen can ask for one. Granting it writes the decision, the typed event
/// and the audit row and <b>touches nothing on the ticket</b>: no status
/// change, no department move, no owner change, no SLA cycle, no customer
/// email. The reopen itself remains
/// <c>TicketLifecycleAppService.ReopenAsync</c>, performed afterwards by a
/// CS user who supplies a target department and a current RowVersion, and
/// which revalidates every rule at that moment. An approved cycle is
/// therefore never a bypass and never a precondition — direct Reopen is
/// unchanged and needs no approval.
/// </para>
/// </summary>
public sealed class TicketApprovalAppService(
    ITicketRepository ticketRepository,
    ITicketApprovalRepository approvalRepository,
    ITicketWorkflowEventRepository workflowEventRepository,
    IRequestTypeApprovalRequirementRepository approvalRequirementRepository,
    IUserDepartmentAssignmentRepository userDepartmentAssignmentRepository,
    IDepartmentRepository departmentRepository,
    ITicketingUnitOfWork unitOfWork,
    IAuditEntryWriter auditWriter,
    TimeProvider timeProvider,
    ReopenEligibilityService reopenEligibilityService,
    ITicketInteractionHandlerRepository? handlerRepository = null,
    IServiceIdentityRegistry? serviceIdentities = null,
    IEmployeeRepository? employeeRepository = null,
    TicketClaimPolicy? claimPolicy = null)
{
    /// <summary>Provisional: which department-side roles may decide a department-targeted approval when the requirement names no narrowing role. Deliberately the two department-scoped roles only — never CS-layer, never broad.</summary>
    public static readonly IReadOnlyCollection<string> DepartmentTargetDefaultApproverRoles =
        [Roles.DepartmentEmployee, Roles.DepartmentHead];

    /// <summary>
    /// Cross-department roles that may REQUEST a
    /// <see cref="ApprovalType.ReopenApproval"/> without belonging to the
    /// ticket's department. Executive reach only — never CS-layer, who hold
    /// direct Reopen instead and have nothing to ask for.
    /// </summary>
    public static readonly IReadOnlyCollection<string> ReopenApprovalCrossDepartmentRequesterRoles =
        [Roles.GeneralManager, Roles.ChairmanCeo];

    /// <summary>
    /// Department-side roles that may REQUEST a
    /// <see cref="ApprovalType.ReopenApproval"/>, each scoped to membership
    /// of the ticket's <c>CurrentDepartmentId</c>.
    ///
    /// <para>
    /// <b>Ownership is deliberately not required.</b> Unlike
    /// <see cref="IsOperationalActorAsync"/>, whose owner branch is the right
    /// rule for an in-flight ticket, a Reopen Approval is raised on a
    /// <b>Closed</b> ticket — the work is finished, and which employee
    /// happened to hold it last is not what decides who may ask for it back.
    /// Department membership is.
    /// </para>
    /// </summary>
    public static readonly IReadOnlyCollection<string> ReopenApprovalDepartmentRequesterRoles =
        [Roles.DepartmentEmployee, Roles.DepartmentHead];

    /// <summary>The event types operational users may record directly. Approval events are produced by approval actions only.</summary>
    private static readonly IReadOnlyCollection<WorkflowEventType> RecordableEventTypes =
    [
        WorkflowEventType.PrerequisitesCompleted,
        WorkflowEventType.MaintenanceRequired,
        WorkflowEventType.MaintenanceNotRequired,
        WorkflowEventType.MaintenanceCompleted
    ];

    private static readonly IReadOnlyCollection<WorkflowEventType> MaintenanceStateEventTypes =
    [
        WorkflowEventType.MaintenanceRequired,
        WorkflowEventType.MaintenanceNotRequired,
        WorkflowEventType.MaintenanceCompleted
    ];

    public async Task<ApprovalMutationResult> RequestApprovalAsync(
        Guid callerEmployeeId,
        IReadOnlyCollection<string> callerRoles,
        long ticketId,
        RequestApprovalRequestDto request,
        CancellationToken cancellationToken = default)
    {
        var ticket = await ticketRepository.GetByIdAsync(ticketId, cancellationToken);
        if (ticket is null)
        {
            return ApprovalMutationResult.Failure(ApprovalMutationOutcome.TicketNotFound);
        }

        if (!Enum.TryParse<ApprovalType>(request.ApprovalType, ignoreCase: true, out var approvalType)
            || !Enum.IsDefined(approvalType))
        {
            return ApprovalMutationResult.Failure(ApprovalMutationOutcome.InvalidInput);
        }

        var isReopenApproval = approvalType is ApprovalType.ReopenApproval;
        var now = timeProvider.GetUtcNow().UtcDateTime;

        // Reopen Approval has its own requester rule — department membership
        // rather than the operational-actor rule's owner branch, because the
        // ticket is Closed and ownership is not what decides who may ask.
        var authorized = isReopenApproval
            ? await IsReopenApprovalRequesterAsync(callerEmployeeId, callerRoles, ticket, cancellationToken)
            : await IsOperationalActorAsync(callerEmployeeId, callerRoles, ticket, cancellationToken);
        if (!authorized)
        {
            return ApprovalMutationResult.Failure(ApprovalMutationOutcome.Forbidden);
        }

        // The Closed carve-out, keyed on the one type it exists for: every
        // other approval is refused on a Closed ticket exactly as before.
        if (ticket.TicketStatus == TicketStatus.Closed && !isReopenApproval)
        {
            return ApprovalMutationResult.Failure(ApprovalMutationOutcome.TicketClosed);
        }


        // Configuration-driven, never ad hoc: the ticket's request type must
        // actively require this approval type.
        var requirement = ticket.RequestTypeId is { } requestTypeId
            ? await approvalRequirementRepository.GetActiveAsync(requestTypeId, approvalType, cancellationToken)
            : null;
        if (requirement is null)
        {
            return ApprovalMutationResult.Failure(ApprovalMutationOutcome.ApprovalNotConfigured);
        }

        if (isReopenApproval)
        {
            // The carve-out is not a hole: a Reopen Approval may only be
            // raised for a reopen that could actually happen — Closed, closed
            // as Resolved, the request type still allows Reopen, and the
            // window has not passed. Same shared computation the lifecycle
            // service uses, never a second copy of the rule. Checked after the
            // requirement lookup so a request type that does not offer this
            // approval at all says exactly that, whatever state its ticket is
            // in.
            if (await reopenEligibilityService.EvaluateAsync(ticket, now, cancellationToken) is not ReopenEligibility.Eligible)
            {
                return ApprovalMutationResult.Failure(ApprovalMutationOutcome.ReopenNotEligible);
            }

            // The reopen reason is mandatory and is what the approver decides
            // on. It rides the existing RequestComment column — no new field,
            // and it is already surfaced in the approvals view, the typed
            // event and the audit row.
            if (string.IsNullOrWhiteSpace(request.Comment))
            {
                return ApprovalMutationResult.Failure(ApprovalMutationOutcome.ReasonRequired);
            }
        }

        // No duplicate simultaneously-active cycles, and no silent
        // superseding of an already-granted approval: a new cycle may only
        // open when no cycle exists yet, or the current one ended without
        // approval (Rejected/Cancelled). The filtered unique index backs the
        // Pending half against concurrent races.
        var currentCycle = await approvalRepository.GetCurrentAsync(ticketId, approvalType, cancellationToken);
        if (currentCycle is { Status: ApprovalStatus.Pending or ApprovalStatus.Approved })
        {
            return ApprovalMutationResult.Failure(ApprovalMutationOutcome.DuplicateActiveApproval);
        }

        var correlationId = Guid.NewGuid();

        await using var transaction = await unitOfWork.BeginTransactionAsync(cancellationToken);

        // A legitimate new cycle (re-request after rejection/cancellation)
        // supersedes the prior one's IsCurrent — history is never
        // overwritten.
        currentCycle?.MarkSuperseded();

        var requestComment = string.IsNullOrWhiteSpace(request.Comment) ? null : request.Comment.Trim();
        var approval = TicketApproval.Request(ticketId, requirement, callerEmployeeId, now, requestComment, correlationId);
        await approvalRepository.AddAsync(approval, cancellationToken);

        // The approval's identity PK is needed by the event/audit rows —
        // same two-phase, one-transaction pattern as ticket creation.
        await unitOfWork.SaveChangesAsync(cancellationToken);

        await workflowEventRepository.AddAsync(
            new TicketWorkflowEvent(
                ticketId, WorkflowEventType.ApprovalRequested, now, callerEmployeeId,
                approval.TicketApprovalId, requestComment, correlationId),
            cancellationToken);

        await auditWriter.WriteAsync(
            callerEmployeeId, "RequestApproval", "TicketApproval", approval.TicketApprovalId.ToString(),
            beforeValue: null,
            afterValue: $"TicketId={ticketId};Type={approvalType};Status=Pending;Target={DescribeTargetForAudit(approval)}",
            correlationId, cancellationToken);

        await unitOfWork.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return ApprovalMutationResult.Success(await ToDtoAsync(approval, callerEmployeeId, callerRoles, cancellationToken));
    }

    public async Task<ApprovalMutationResult> DecideAsync(
        Guid callerEmployeeId,
        IReadOnlyCollection<string> callerRoles,
        long ticketId,
        long ticketApprovalId,
        DecideApprovalRequestDto request,
        CancellationToken cancellationToken = default)
    {
        var approval = await approvalRepository.GetByIdAsync(ticketApprovalId, cancellationToken);
        if (approval is null || approval.TicketId != ticketId)
        {
            return ApprovalMutationResult.Failure(ApprovalMutationOutcome.ApprovalNotFound);
        }

        var isApprove = string.Equals(request.Decision, "Approve", StringComparison.OrdinalIgnoreCase);
        var isReject = string.Equals(request.Decision, "Reject", StringComparison.OrdinalIgnoreCase);
        if (!isApprove && !isReject)
        {
            return ApprovalMutationResult.Failure(ApprovalMutationOutcome.InvalidInput);
        }

        if (!await IsAuthorizedApproverAsync(callerEmployeeId, callerRoles, approval, cancellationToken))
        {
            return ApprovalMutationResult.Failure(ApprovalMutationOutcome.Forbidden);
        }

        if (approval.Status != ApprovalStatus.Pending)
        {
            return ApprovalMutationResult.Failure(ApprovalMutationOutcome.ApprovalAlreadyDecided);
        }

        if (isReject && string.IsNullOrWhiteSpace(request.Comment))
        {
            return ApprovalMutationResult.Failure(ApprovalMutationOutcome.ReasonRequired);
        }

        var now = timeProvider.GetUtcNow().UtcDateTime;

        // Granting a Reopen Approval that can no longer be executed would
        // create an Approved cycle nobody can ever act on — the window can
        // pass, the request type can be re-configured, or someone with direct
        // Reopen can have reopened and re-closed the ticket while this cycle
        // sat Pending. So the same shared eligibility computation runs again
        // at decision time, and only for Approve: Reject stays available
        // precisely so a stale cycle can still be closed out deliberately
        // rather than being stuck Pending forever.
        if (isApprove && approval.ApprovalType is ApprovalType.ReopenApproval)
        {
            var ticket = await ticketRepository.GetByIdAsync(ticketId, cancellationToken);
            if (ticket is null)
            {
                return ApprovalMutationResult.Failure(ApprovalMutationOutcome.TicketNotFound);
            }

            if (await reopenEligibilityService.EvaluateAsync(ticket, now, cancellationToken) is not ReopenEligibility.Eligible)
            {
                return ApprovalMutationResult.Failure(ApprovalMutationOutcome.ReopenNotEligible);
            }
        }

        var correlationId = Guid.NewGuid();

        await using var transaction = await unitOfWork.BeginTransactionAsync(cancellationToken);

        try
        {
            if (isApprove)
            {
                approval.Approve(callerEmployeeId, now, request.Comment);
            }
            else
            {
                approval.Reject(callerEmployeeId, now, request.Comment!);
            }
        }
        catch (ApprovalAlreadyDecidedException)
        {
            return ApprovalMutationResult.Failure(ApprovalMutationOutcome.ApprovalAlreadyDecided);
        }

        // The typed semantic event phase 4's conditional SLA triggers read:
        // AccountingApproval → ApprovalReceived (Send Receipts' 1 day runs
        // from this timestamp); CustomerServiceApproval →
        // CustomerServiceApproved (Handover's 1–4 days run from it);
        // rejection → ApprovalRejected. Nothing here computes a deadline.
        var eventType = (isApprove, approval.ApprovalType) switch
        {
            (true, ApprovalType.CustomerServiceApproval) => WorkflowEventType.CustomerServiceApproved,
            (true, _) => WorkflowEventType.ApprovalReceived,
            (false, _) => WorkflowEventType.ApprovalRejected
        };

        await workflowEventRepository.AddAsync(
            new TicketWorkflowEvent(
                ticketId, eventType, now, callerEmployeeId, approval.TicketApprovalId, request.Comment, correlationId),
            cancellationToken);

        await auditWriter.WriteAsync(
            callerEmployeeId, isApprove ? "ApproveApproval" : "RejectApproval",
            "TicketApproval", approval.TicketApprovalId.ToString(),
            beforeValue: $"TicketId={ticketId};Type={approval.ApprovalType};Status=Pending",
            afterValue: $"Status={approval.Status};Target={DescribeTargetForAudit(approval)}"
                + (request.Comment is { } comment ? $";Comment={comment}" : string.Empty),
            correlationId, cancellationToken);

        await unitOfWork.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        // Deliberately no automatic resume of a Pending ticket — see this
        // type's remarks.
        return ApprovalMutationResult.Success(await ToDtoAsync(approval, callerEmployeeId, callerRoles, cancellationToken));
    }

    public async Task<ApprovalMutationResult> CancelAsync(
        Guid callerEmployeeId,
        IReadOnlyCollection<string> callerRoles,
        long ticketId,
        long ticketApprovalId,
        CancelApprovalRequestDto request,
        CancellationToken cancellationToken = default)
    {
        var ticket = await ticketRepository.GetByIdAsync(ticketId, cancellationToken);
        if (ticket is null)
        {
            return ApprovalMutationResult.Failure(ApprovalMutationOutcome.TicketNotFound);
        }

        var approval = await approvalRepository.GetByIdAsync(ticketApprovalId, cancellationToken);
        if (approval is null || approval.TicketId != ticketId)
        {
            return ApprovalMutationResult.Failure(ApprovalMutationOutcome.ApprovalNotFound);
        }

        // Cancelling is an operational action of the requesting side, not a
        // decision — the same authority that may open a cycle may withdraw a
        // still-pending one.
        if (!await IsOperationalActorAsync(callerEmployeeId, callerRoles, ticket, cancellationToken))
        {
            return ApprovalMutationResult.Failure(ApprovalMutationOutcome.Forbidden);
        }

        if (approval.Status != ApprovalStatus.Pending)
        {
            return ApprovalMutationResult.Failure(ApprovalMutationOutcome.ApprovalAlreadyDecided);
        }

        var now = timeProvider.GetUtcNow().UtcDateTime;
        var correlationId = Guid.NewGuid();

        await using var transaction = await unitOfWork.BeginTransactionAsync(cancellationToken);

        approval.Cancel(callerEmployeeId, now, request.Comment);

        await auditWriter.WriteAsync(
            callerEmployeeId, "CancelApproval", "TicketApproval", approval.TicketApprovalId.ToString(),
            beforeValue: $"TicketId={ticketId};Type={approval.ApprovalType};Status=Pending",
            afterValue: $"Status=Cancelled" + (request.Comment is { } comment ? $";Comment={comment}" : string.Empty),
            correlationId, cancellationToken);

        await unitOfWork.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return ApprovalMutationResult.Success(await ToDtoAsync(approval, callerEmployeeId, callerRoles, cancellationToken));
    }

    /// <summary>
    /// Records one of the operational dependency events. Rules keep the
    /// event stream truthful: PrerequisitesCompleted is once-only (its FIRST
    /// timestamp is what a trigger would read, so a repeat is refused rather
    /// than silently shifting meaning); MaintenanceCompleted needs the
    /// maintenance state to actually be Required and is once-only; the two
    /// maintenance-state events may correct each other while nothing has
    /// completed, but never repeat the standing state.
    /// </summary>
    public async Task<ApprovalMutationResult> RecordWorkflowEventAsync(
        Guid callerEmployeeId,
        IReadOnlyCollection<string> callerRoles,
        long ticketId,
        RecordWorkflowEventRequestDto request,
        CancellationToken cancellationToken = default)
    {
        var ticket = await ticketRepository.GetByIdAsync(ticketId, cancellationToken);
        if (ticket is null)
        {
            return ApprovalMutationResult.Failure(ApprovalMutationOutcome.TicketNotFound);
        }

        if (!Enum.TryParse<WorkflowEventType>(request.EventType, ignoreCase: true, out var eventType)
            || !RecordableEventTypes.Contains(eventType))
        {
            return ApprovalMutationResult.Failure(ApprovalMutationOutcome.InvalidInput);
        }

        if (!await IsOperationalActorAsync(callerEmployeeId, callerRoles, ticket, cancellationToken))
        {
            return ApprovalMutationResult.Failure(ApprovalMutationOutcome.Forbidden);
        }

        if (ticket.TicketStatus == TicketStatus.Closed)
        {
            return ApprovalMutationResult.Failure(ApprovalMutationOutcome.TicketClosed);
        }

        if (eventType == WorkflowEventType.PrerequisitesCompleted
            && await workflowEventRepository.GetFirstAsync(ticketId, eventType, cancellationToken) is not null)
        {
            return ApprovalMutationResult.Failure(ApprovalMutationOutcome.EventAlreadyRecorded);
        }

        if (MaintenanceStateEventTypes.Contains(eventType))
        {
            var latestMaintenance = await workflowEventRepository.GetLatestAsync(
                ticketId, MaintenanceStateEventTypes, cancellationToken);

            if (latestMaintenance?.EventType == WorkflowEventType.MaintenanceCompleted)
            {
                return ApprovalMutationResult.Failure(ApprovalMutationOutcome.EventNotApplicable);
            }

            if (eventType == WorkflowEventType.MaintenanceCompleted
                && latestMaintenance?.EventType != WorkflowEventType.MaintenanceRequired)
            {
                return ApprovalMutationResult.Failure(ApprovalMutationOutcome.EventNotApplicable);
            }

            if (latestMaintenance?.EventType == eventType)
            {
                return ApprovalMutationResult.Failure(ApprovalMutationOutcome.EventAlreadyRecorded);
            }
        }

        var now = timeProvider.GetUtcNow().UtcDateTime;
        var correlationId = Guid.NewGuid();

        await using var transaction = await unitOfWork.BeginTransactionAsync(cancellationToken);

        await workflowEventRepository.AddAsync(
            new TicketWorkflowEvent(ticketId, eventType, now, callerEmployeeId, ticketApprovalId: null, request.Note, correlationId),
            cancellationToken);

        await auditWriter.WriteAsync(
            callerEmployeeId, "RecordWorkflowEvent", "Ticket", ticketId.ToString(),
            beforeValue: null,
            afterValue: $"EventType={eventType}" + (request.Note is { } note ? $";Note={note}" : string.Empty),
            correlationId, cancellationToken);

        await unitOfWork.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return ApprovalMutationResult.Success();
    }

    /// <summary>The Approvals / Dependencies view for Ticket Details — cycles with per-caller decision capability, requestable configured approvals, and the derived dependency states.</summary>
    public async Task<TicketQueryResultDto<TicketApprovalsViewDto>> GetApprovalsViewAsync(
        Guid callerEmployeeId,
        IReadOnlyCollection<string> callerRoles,
        long ticketId,
        CancellationToken cancellationToken = default)
    {
        var ticket = await ticketRepository.GetByIdAsync(ticketId, cancellationToken);
        if (ticket is null)
        {
            return TicketQueryResultDto<TicketApprovalsViewDto>.Failure(TicketQueryOutcome.NotFound);
        }

        var approvals = await approvalRepository.ListByTicketIdAsync(ticketId, cancellationToken);

        // Same visibility rule as the ticket itself (CS-layer/executive
        // cross-department view, or membership of the ticket's department) —
        // plus one narrow, purpose-built extension: the configured approver
        // of a pending cycle may see this view, or they could never act on
        // the approval addressed to them (an Accounting approver is not a
        // member of the Collections department that owns the ticket).
        // Nothing broader: it grants this view only, only while a cycle
        // addressed to the caller is pending.
        var access = await TicketVisibilityRule.GetTicketAccessAsync(
            userDepartmentAssignmentRepository, handlerRepository, callerEmployeeId, callerRoles, ticket, serviceIdentities, cancellationToken, claimPolicy, ticketRepository);
        var canView = access != TicketAccessLevel.None;

        // A creator who may only READ the ticket (it is in another department) is offered no approval action: every flag below is false
        // for them, so the page never offers what the API would refuse.
        var readOnly = access == TicketAccessLevel.ReadOnly;
        if (!canView)
        {
            var isPendingApprover = false;
            foreach (var pending in approvals.Where(a => a.Status == ApprovalStatus.Pending))
            {
                if (await IsAuthorizedApproverAsync(callerEmployeeId, callerRoles, pending, cancellationToken))
                {
                    isPendingApprover = true;
                    break;
                }
            }

            if (!isPendingApprover)
            {
                return TicketQueryResultDto<TicketApprovalsViewDto>.Failure(TicketQueryOutcome.Forbidden);
            }
        }
        var approvalDtos = new List<TicketApprovalDto>(approvals.Count);
        foreach (var approval in approvals)
        {
            var dto = await ToDtoAsync(approval, callerEmployeeId, callerRoles, cancellationToken);
            approvalDtos.Add(readOnly ? dto with { CallerCanDecide = false } : dto);
        }

        var canOperate = !readOnly && await IsOperationalActorAsync(callerEmployeeId, callerRoles, ticket, cancellationToken);

        // Reopen Approval's offer is computed once per view rather than per
        // requirement: it is the only type whose availability depends on the
        // ticket's reopen eligibility, and the caller not already holding
        // direct Reopen.
        var canRequestReopenApproval =
            !readOnly
            && !HoldsDirectReopen(callerRoles)
            && await IsReopenApprovalRequesterAsync(callerEmployeeId, callerRoles, ticket, cancellationToken)
            && await reopenEligibilityService.EvaluateAsync(
                ticket, timeProvider.GetUtcNow().UtcDateTime, cancellationToken) is ReopenEligibility.Eligible;

        var requestable = new List<RequestableApprovalDto>();
        if (ticket.RequestTypeId is { } requestTypeId)
        {
            foreach (var requirement in await approvalRequirementRepository.ListActiveByRequestTypeIdAsync(requestTypeId, cancellationToken))
            {
                var pending = approvals.Any(a => a.ApprovalType == requirement.ApprovalType && a.Status == ApprovalStatus.Pending);
                var approved = approvals.Any(a => a.ApprovalType == requirement.ApprovalType && a.Status == ApprovalStatus.Approved && a.IsCurrent);
                if (!pending && !approved)
                {
                    requestable.Add(new RequestableApprovalDto(
                        requirement.ApprovalType.ToString(),
                        await DescribeTargetAsync(
                            requirement.TargetKind, requirement.TargetDepartmentId, requirement.TargetRoleName,
                            requirement.TargetEmployeeId, cancellationToken),
                        requirement.BlocksWorkUntilApproved,
                        CallerCanRequest: requirement.ApprovalType is ApprovalType.ReopenApproval
                            ? canRequestReopenApproval
                            : canOperate && ticket.TicketStatus != TicketStatus.Closed));
                }
            }
        }

        var events = await workflowEventRepository.ListByTicketIdAsync(ticketId, cancellationToken);
        var latestMaintenance = events
            .Where(e => MaintenanceStateEventTypes.Contains(e.EventType))
            .OrderBy(e => e.OccurredAtUtc).ThenBy(e => e.TicketWorkflowEventId)
            .LastOrDefault();
        var maintenanceState = latestMaintenance?.EventType switch
        {
            WorkflowEventType.MaintenanceRequired => "Required",
            WorkflowEventType.MaintenanceNotRequired => "NotRequired",
            WorkflowEventType.MaintenanceCompleted => "Completed",
            _ => null
        };

        var prerequisitesCompletedAt = events
            .Where(e => e.EventType == WorkflowEventType.PrerequisitesCompleted)
            .OrderBy(e => e.OccurredAtUtc)
            .FirstOrDefault()?.OccurredAtUtc;

        // Display names for the people the transfer/handling events name, read from the employee directory itself so
        // someone who has since left the department (or been deactivated) is still named.
        var names = new Dictionary<Guid, string?>();
        if (employeeRepository is not null)
        {
            foreach (var id in events
                .Where(e => e.EventType is WorkflowEventType.TicketTransferred or WorkflowEventType.GenesysAgentConnected
                    or WorkflowEventType.GenesysCallTransferred or WorkflowEventType.Reopened or WorkflowEventType.HandoffStarted)
                .SelectMany(e => new[] { e.ActorEmployeeId, e.ToEmployeeId })
                .OfType<Guid>().Distinct())
            {
                names[id] = (await employeeRepository.GetByIdAsync(id, cancellationToken))?.DisplayName;
            }
        }

        string? NameOf(Guid? id) => id is { } known && names.TryGetValue(known, out var name) ? name : null;

        return TicketQueryResultDto<TicketApprovalsViewDto>.Success(new TicketApprovalsViewDto(
            approvalDtos,
            requestable,
            events.Select(e => new TicketWorkflowEventDto(
                e.EventType.ToString(), e.OccurredAtUtc, e.ActorEmployeeId, e.Note,
                e.TransferReasonCode is { } reasonCode ? ((TicketTransferReason)reasonCode).ToString() : null,
                e.FromDepartmentId, e.ToDepartmentId, e.ToEmployeeId,
                NameOf(e.ActorEmployeeId), NameOf(e.ToEmployeeId))).ToList(),
            maintenanceState,
            prerequisitesCompletedAt,
            CallerCanRecordEvents: canOperate && ticket.TicketStatus != TicketStatus.Closed));
    }

    /// <summary>Who may request/cancel approvals and record dependency events: the ticket's current owner, a cross-department supervisory role, or a department-scoped Department Head — exactly the existing status-change authority shape, nothing broader.</summary>
    private Task<bool> IsOperationalActorAsync(
        Guid callerEmployeeId, IReadOnlyCollection<string> callerRoles, Ticket ticket, CancellationToken cancellationToken) =>
        AuthorizationGate.EvaluateAsync(callerRoles, async () =>
        {
            if (Roles.IsReadOnlyCaller(callerRoles))
            {
                return false;
            }

            if (ticket.CurrentOwnerEmployeeId == callerEmployeeId)
            {
                return true;
            }

            if (callerRoles.Any(TicketRoleSets.CrossDepartmentSupervisory.Contains))
            {
                return true;
            }

            return callerRoles.Contains(Roles.DepartmentHead)
                && await userDepartmentAssignmentRepository.ExistsAsync(callerEmployeeId, ticket.CurrentDepartmentId, cancellationToken);
        });

    /// <summary>
    /// Who may REQUEST a <see cref="ApprovalType.ReopenApproval"/>: a
    /// Department Employee or Department Head who belongs to the ticket's
    /// current department, or a General Manager / Chairman-CEO anywhere.
    /// Reporting User is in neither set and is refused, as is every CS-layer
    /// role — they hold direct Reopen and have nothing to request.
    ///
    /// <para>
    /// Routed through <see cref="AuthorizationGate"/> like every other rule
    /// here, so ADR-0024's System Administrator override reaches the endpoint
    /// exactly as it reaches all the others. The administrator is still not
    /// <i>offered</i> the control — the approvals view suppresses it for
    /// anyone who already holds direct Reopen, which the override makes true
    /// of this role — but the endpoint is never closed to it, which is what
    /// ADR-0024 requires.
    /// </para>
    /// </summary>
    private Task<bool> IsReopenApprovalRequesterAsync(
        Guid callerEmployeeId, IReadOnlyCollection<string> callerRoles, Ticket ticket, CancellationToken cancellationToken) =>
        AuthorizationGate.EvaluateAsync(callerRoles, async () =>
            callerRoles.Any(ReopenApprovalCrossDepartmentRequesterRoles.Contains)
            || (callerRoles.Any(ReopenApprovalDepartmentRequesterRoles.Contains)
                && await userDepartmentAssignmentRepository.ExistsAsync(callerEmployeeId, ticket.CurrentDepartmentId, cancellationToken)));

    /// <summary>
    /// Whether the caller already holds direct Reopen — the CS-layer role set
    /// plus ADR-0024's override. Read from <see cref="TicketRoleSets.Reopen"/>
    /// rather than a local list, so widening or narrowing direct Reopen moves
    /// this with it: someone who can simply reopen the ticket is never offered
    /// a form to ask permission to.
    /// </summary>
    private static bool HoldsDirectReopen(IReadOnlyCollection<string> callerRoles) =>
        callerRoles.Any(TicketRoleSets.Reopen.Contains) || AuthorizationOverride.AppliesTo(callerRoles);

    /// <summary>The target-snapshot gate — see this type's remarks on the provisional, fail-safe rules per target kind.</summary>
    private Task<bool> IsAuthorizedApproverAsync(
        Guid callerEmployeeId, IReadOnlyCollection<string> callerRoles, TicketApproval approval, CancellationToken cancellationToken) =>
        AuthorizationGate.EvaluateAsync(callerRoles, async () => !Roles.IsReadOnlyCaller(callerRoles) && approval.TargetKind switch
        {
            ApprovalTargetKind.Employee => approval.TargetEmployeeId == callerEmployeeId,

            ApprovalTargetKind.Role => approval.TargetRoleName is { } roleName && callerRoles.Contains(roleName),

            ApprovalTargetKind.Department when approval.TargetDepartmentId is { } departmentId =>
                await userDepartmentAssignmentRepository.ExistsAsync(callerEmployeeId, departmentId, cancellationToken)
                && (approval.TargetRoleName is { } narrowingRole
                    ? callerRoles.Contains(narrowingRole)
                    : callerRoles.Any(DepartmentTargetDefaultApproverRoles.Contains)),

            _ => false
        });

    private async Task<TicketApprovalDto> ToDtoAsync(
        TicketApproval approval, Guid callerEmployeeId, IReadOnlyCollection<string> callerRoles, CancellationToken cancellationToken) =>
        new(
            approval.TicketApprovalId,
            approval.ApprovalType.ToString(),
            approval.Status.ToString(),
            await DescribeTargetAsync(
                approval.TargetKind, approval.TargetDepartmentId, approval.TargetRoleName, approval.TargetEmployeeId, cancellationToken),
            approval.RequestedByEmployeeId,
            approval.RequestedAtUtc,
            approval.RequestComment,
            approval.DecidedByEmployeeId,
            approval.DecisionAtUtc,
            approval.DecisionComment,
            approval.IsCurrent,
            CallerCanDecide: approval.Status == ApprovalStatus.Pending
                && await IsAuthorizedApproverAsync(callerEmployeeId, callerRoles, approval, cancellationToken));

    /// <summary>Human-readable target ("Accounting department", "CS Supervisor role") — the UI never shows raw technical ids.</summary>
    private async Task<string> DescribeTargetAsync(
        ApprovalTargetKind kind, int? departmentId, string? roleName, Guid? employeeId, CancellationToken cancellationToken)
    {
        switch (kind)
        {
            case ApprovalTargetKind.Department when departmentId is { } id:
                var department = await departmentRepository.GetByIdAsync(id, cancellationToken);
                var name = department?.Name ?? "department";
                return roleName is null ? $"{name} department" : $"{name} department ({roleName})";
            case ApprovalTargetKind.Role:
                return $"{roleName} role";
            case ApprovalTargetKind.Employee:
                return "Configured approver";
            default:
                return "Configured approver";
        }
    }

    private static string DescribeTargetForAudit(TicketApproval approval) => approval.TargetKind switch
    {
        ApprovalTargetKind.Department => $"Department:{approval.TargetDepartmentId}"
            + (approval.TargetRoleName is { } role ? $"/{role}" : string.Empty),
        ApprovalTargetKind.Role => $"Role:{approval.TargetRoleName}",
        ApprovalTargetKind.Employee => $"Employee:{approval.TargetEmployeeId}",
        _ => approval.TargetKind.ToString()
    };
}
