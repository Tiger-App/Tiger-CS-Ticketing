using TigerCS.Application.Authorization;
using TigerCS.Application.Modules.IdentityAndAccess.Abstractions;
using TigerCS.Application.Modules.Notifications;
using TigerCS.Application.Modules.Ticketing.Abstractions;
using TigerCS.Application.Modules.Ticketing.Dto;
using TigerCS.Application.Modules.WorkflowConfiguration.Abstractions;
using TigerCS.Domain.Modules.Ticketing;

namespace TigerCS.Application.Modules.Ticketing.Services;

/// <summary>
/// Ticket queue/list and detail (MVP-API-Contracts.md §3.2/§3.3, this
/// increment's item 1). Department visibility is resolved server-side from
/// the caller's roles/department membership — never from a client-supplied
/// department filter alone — per Security-Architecture.md §3 and
/// Solution-Analysis.md §4.1's View column.
/// </summary>
public sealed class TicketQueryAppService(
    ITicketRepository ticketRepository,
    IUserDepartmentAssignmentRepository userDepartmentAssignmentRepository,
    ITicketResolutionRepository ticketResolutionRepository,
    ITicketStatusHistoryRepository statusHistoryRepository,
    ReopenPolicy reopenPolicy,
    TimeProvider timeProvider,
    IRequestTypeRepository? requestTypeRepository = null,
    IWorkflowTemplateRepository? workflowTemplateRepository = null,
    IWorkflowRepository? workflowRepository = null,
    ITicketInteractionRepository? interactionRepository = null,
    IChannelRepository? channelRepository = null,
    ITicketAgentHandoffRepository? agentHandoffRepository = null,
    CustomerNotificationModePolicy? notificationMode = null,
    ITicketInteractionHandlerRepository? handlerRepository = null,
    IServiceIdentityRegistry? serviceIdentities = null,
    ITicketWorkflowEventRepository? workflowEventRepository = null,
    IEmployeeRepository? employeeRepository = null)
{
    /// <summary>Whether the caller may use the supervisor escalation-review filter (<c>escalated=true</c>).</summary>
    public static bool CanReviewEscalations(IReadOnlyCollection<string> callerRoles) =>
        AuthorizationGate.Evaluate(callerRoles, () => callerRoles.Any(TicketRoleSets.ReviewEscalations.Contains));

    public async Task<TicketListResultDto> GetQueueAsync(
        Guid callerEmployeeId,
        IReadOnlyCollection<string> callerRoles,
        TicketListRequestDto request,
        CancellationToken cancellationToken = default)
    {
        var page0 = request.Page < 1 ? 1 : request.Page;
        var pageSize0 = request.PageSize is < 1 or > 200 ? 50 : request.PageSize;
        if (request.Escalated == true && !CanReviewEscalations(callerRoles))
        {
            // Defence in depth: the API answers 403 before getting here. A caller without the review role never
            // receives an escalation list, and never silently falls back to the unfiltered queue.
            return new TicketListResultDto([], 0, page0, pageSize0);
        }

        var visibleDepartmentIds = await ResolveTicketViewDepartmentIdsAsync(callerEmployeeId, callerRoles, cancellationToken);
        var agentScope = ResolveTicketListAgentScope(callerEmployeeId, callerRoles);

        var page = request.Page < 1 ? 1 : request.Page;
        var pageSize = request.PageSize is < 1 or > 200 ? 50 : request.PageSize;

        // Dashboard drill-down (Dashboard Phase 1): the Pending Approval
        // filter needs the caller's approver scope — resolved here from
        // their own roles and memberships, never from the request.
        ApprovalApproverScope? approverScope = null;
        if (request.PendingApproval == true)
        {
            var memberships = await userDepartmentAssignmentRepository.GetByEmployeeIdAsync(callerEmployeeId, cancellationToken);
            approverScope = ApprovalApproverScope.Resolve(
                callerEmployeeId, callerRoles, memberships.Select(m => m.DepartmentId),
                TicketApprovalAppService.DepartmentTargetDefaultApproverRoles);
        }

        var query = new TicketQuery(
            visibleDepartmentIds,
            request.DepartmentId,
            request.CategoryId,
            request.PriorityId,
            ParseEnum<TicketStatus>(request.TicketStatus),
            ParseEnum<CrmVerificationStatus>(request.VerificationStatus),
            request.OwnerEmployeeId,
            request.Search,
            string.Equals(request.SortBy, "priority", StringComparison.OrdinalIgnoreCase) ? TicketSortBy.Priority : TicketSortBy.CreatedAtUtc,
            !string.Equals(request.SortDir, "asc", StringComparison.OrdinalIgnoreCase),
            page,
            pageSize,
            ChannelId: request.ChannelId,
            RequestTypeId: request.RequestTypeId,
            ActiveOnly: request.ActiveOnly == true,
            InDepartmentQueue: request.InDepartmentQueue == true,
            SlaBreached: request.SlaBreached == true,
            DueToday: request.DueToday == true,
            BacklogAge: ParseEnum<BacklogAgeBucket>(request.BacklogAge),
            PendingApprovalFor: approverScope,
            // Calendar days are UTC days — the dashboard's existing "today" convention.
            CreatedFromUtc: request.CreatedFrom?.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc),
            CreatedToUtc: request.CreatedTo?.AddDays(1).ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc),
            NowUtc: timeProvider.GetUtcNow().UtcDateTime,
            Unclassified: request.Unclassified == true,
            ClosureCategory: ParseEnum<TicketClosureCategory>(request.ClosureCategory),
            ClosedFromUtc: request.ClosedFrom?.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc),
            ClosedToUtc: request.ClosedTo?.AddDays(1).ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc),
            ReviewsAreTest: notificationMode?.IsTest == true,
            Escalated: request.Escalated == true,
            AgentScopeEmployeeId: agentScope,
            CreatedByEmployeeId: request.CreatedByMe == true ? callerEmployeeId : null,
            CreatedOnlyScope: IsGenesysIntegration(callerEmployeeId, callerRoles));

        var result = await ticketRepository.SearchAsync(query, cancellationToken);

        return new TicketListResultDto(result.Items.Select(ToSummaryDto).ToList(), result.TotalCount, page, pageSize);
    }

    public async Task<TicketQueryResultDto<TicketDetailDto>> GetDetailAsync(
        Guid callerEmployeeId,
        IReadOnlyCollection<string> callerRoles,
        long ticketId,
        CancellationToken cancellationToken = default)
    {
        var ticket = await ticketRepository.GetByIdAsync(ticketId, cancellationToken);
        if (ticket is null)
        {
            return TicketQueryResultDto<TicketDetailDto>.Failure(TicketQueryOutcome.NotFound);
        }

        var accessLevel = await GetTicketAccessAsync(callerEmployeeId, callerRoles, ticket, cancellationToken);
        if (accessLevel == TicketAccessLevel.None)
        {
            return TicketQueryResultDto<TicketDetailDto>.Failure(TicketQueryOutcome.Forbidden);
        }

        // Reopen display eligibility (FR-RES-04/ISSUE-011) rides on the
        // detail read so the UI never re-derives the rule: the same
        // ReopenPolicy that gates TicketLifecycleAppService.ReopenAsync
        // computes the flag here. Lifecycle only — roles and the caller's
        // access to the ticket are enforced at the Reopen endpoint, never
        // predicted on a read.
        var currentResolution = ticket.TicketStatus is TicketStatus.Resolved or TicketStatus.Closed
            ? await ticketResolutionRepository.GetCurrentAsync(ticketId, cancellationToken)
            : null;

        // The window runs from closure, so the closure moment comes from the
        // lifecycle history Close wrote — the same source ReopenAsync reads,
        // so the button and the action can never disagree about the deadline.
        var closedAt = ticket.TicketStatus is TicketStatus.Closed
            ? (await statusHistoryRepository.GetLatestTransitionIntoAsync(
                ticketId, TicketStatusDimension.TicketStatus, (byte)TicketStatus.Closed, cancellationToken))?.OccurredAtUtc
            : null;

        var detail = ToDetailDto(ticket) with
        {
            IsReadOnlyAccess = accessLevel == TicketAccessLevel.ReadOnly,
            ResolvedAtUtc = currentResolution?.ResolvedAtUtc,
            ClosedAtUtc = closedAt,
            IsReopenEligible = reopenPolicy.IsReopenEligible(
                ticket.TicketStatus, ticket.ResolutionOutcome, closedAt, timeProvider.GetUtcNow().UtcDateTime,
                currentResolution?.ClosedForCustomerInactivity == true)
        };

        // Derived, never stored: the ticket's handoff state IS the state of
        // its one outstanding TicketAgentHandoff, read at the moment of the
        // request. AgentHandoffStatus.NotRequired is the enum's own name for
        // "no row exists", which is why the answer is the same shape either
        // way.
        if (agentHandoffRepository is not null)
        {
            var openHandoff = (await agentHandoffRepository.ListByTicketIdAsync(ticketId, cancellationToken))
                .FirstOrDefault(h => h.IsOpen);

            detail = detail with
            {
                HandoffState = (openHandoff?.Status ?? AgentHandoffStatus.NotRequired).ToString(),
                HandoffRequestedAtUtc = openHandoff?.RequestedAtUtc
            };
        }

        // Workflow identity (Administration / Workflow Designer phase): the
        // request type's name and the PINNED version's workflow name and
        // number — read-only display facts, resolved by name so no page ever
        // shows a raw configuration id.
        if (ticket.RequestTypeId is { } requestTypeId && requestTypeRepository is not null)
        {
            var requestType = await requestTypeRepository.GetByIdAsync(requestTypeId, cancellationToken);
            detail = detail with { RequestTypeName = requestType?.Name };
        }

        if (ticket.WorkflowTemplateId is { } versionId && workflowTemplateRepository is not null)
        {
            var version = await workflowTemplateRepository.GetByIdAsync(versionId, cancellationToken);
            if (version is not null)
            {
                var workflow = workflowRepository is null ? null : await workflowRepository.GetByIdAsync(version.WorkflowId, cancellationToken);
                detail = detail with
                {
                    WorkflowName = workflow?.Name ?? version.Name,
                    WorkflowVersionNumber = version.VersionNumber
                };
            }
        }

        // Ticket transfer tracking: the resolver comes from the current resolution row, and the transfer/escalation
        // counts from the typed TicketTransferred events — nothing is stored on the ticket, so nothing can drift.
        if (currentResolution is not null)
        {
            detail = detail with { ResolvedByEmployeeId = currentResolution.ResolvingEmployeeId };
        }

        if (workflowEventRepository is not null)
        {
            var transfers = (await workflowEventRepository.ListByTicketIdAsync(ticketId, cancellationToken))
                .Where(e => e.EventType == WorkflowEventType.TicketTransferred)
                .ToList();
            detail = detail with
            {
                TransferCount = transfers.Count(e => e.TransferReasonCode != (byte)TicketTransferReason.SystemRouting),
                EscalationCount = transfers.Count(e => e.IsEscalation)
            };
        }

        // Originating channel (Channel Management phase): the channel the
        // ticket ENTERED the system on, read from its originating
        // interaction and resolved by name — including a channel that has
        // since been deactivated, so history keeps showing it. Later
        // interactions on other channels never change it.
        if (interactionRepository is not null)
        {
            var originating = await interactionRepository.GetOriginatingAsync(ticketId, cancellationToken);
            if (originating is not null)
            {
                var channel = channelRepository is null ? null : await channelRepository.GetByIdAsync(originating.ChannelId, cancellationToken);
                detail = detail with
                {
                    OriginatingChannelId = originating.ChannelId,
                    OriginatingChannelName = channel?.Name,
                    // The first agent who actually handled the Genesys interaction. Apply-if-absent on the interaction, so
                    // later transfers of the call or the ticket never move it; null when no mapped agent was ever recorded.
                    FirstHandledByEmployeeId = originating.HandledByUserId
                };
            }
        }

        // Names come from the employee directory itself, not from department membership lists, so a first handler or
        // resolver who has since left the department (or been deactivated) is still named.
        if (employeeRepository is not null)
        {
            detail = detail with
            {
                FirstHandledByName = detail.FirstHandledByEmployeeId is { } handler
                    ? (await employeeRepository.GetByIdAsync(handler, cancellationToken))?.DisplayName : null,
                ResolvedByName = detail.ResolvedByEmployeeId is { } resolver
                    ? (await employeeRepository.GetByIdAsync(resolver, cancellationToken))?.DisplayName : null
            };
        }

        return TicketQueryResultDto<TicketDetailDto>.Success(detail);
    }

    /// <summary>
    /// Null means "no restriction" (cross-department view role, or the ADR-0024 override) — never client-supplied, always resolved from the
    /// caller's own roles/department membership.
    ///
    /// <para>
    /// A restricted CS Agent (<see cref="TicketVisibilityRule.IsRestrictedCsAgent(IReadOnlyCollection{string})"/>) is <b>never</b> null: CS Agent grants no department
    /// access, so this is empty (or only the memberships a second, department role grants). That makes every caller that has not yet been
    /// taught the agent's per-ticket rule fail closed — it shows the agent nothing rather than everything. Callers that do apply the rule
    /// pass <see cref="ResolveAgentScope"/> alongside this set.
    /// </para>
    /// </summary>
    internal async Task<IReadOnlyCollection<int>?> ResolveVisibleDepartmentIdsAsync(
        Guid callerEmployeeId, IReadOnlyCollection<string> callerRoles, CancellationToken cancellationToken)
    {
        if (IsRestrictedAgent(callerEmployeeId, callerRoles))
        {
            if (!TicketVisibilityRule.HoldsDepartmentRole(callerRoles))
            {
                return Array.Empty<int>();
            }

            var departmentRoleAssignments = await userDepartmentAssignmentRepository.GetByEmployeeIdAsync(callerEmployeeId, cancellationToken);
            return departmentRoleAssignments.Select(a => a.DepartmentId).ToList();
        }

        if (AuthorizationGate.Evaluate(callerRoles, () => callerRoles.Any(TicketRoleSets.CrossDepartmentView.Contains)))
        {
            return null;
        }

        var assignments = await userDepartmentAssignmentRepository.GetByEmployeeIdAsync(callerEmployeeId, cancellationToken);
        return assignments.Select(a => a.DepartmentId).ToList();
    }

    /// <summary>
    /// The ticket's lifecycle history (ADR-0018's append-only
    /// <c>TicketStatusHistory</c>), behind the same department-visibility
    /// check as the detail read — history is as sensitive as the ticket it
    /// describes.
    ///
    /// <para>
    /// Added with the approved Reopen rule, which requires the reopen reason
    /// to be visible in Ticket Details: the reason was already being written
    /// to this table and had no read path, so exposing the table was the fix
    /// rather than a parallel activity store. Every other dimension change
    /// comes with it, which is why the Activity feed can finally show status,
    /// verification and SLA transitions at all.
    /// </para>
    /// </summary>
    public async Task<TicketQueryResultDto<TicketLifecycleHistoryDto>> GetLifecycleHistoryAsync(
        Guid callerEmployeeId,
        IReadOnlyCollection<string> callerRoles,
        long ticketId,
        CancellationToken cancellationToken = default)
    {
        var ticket = await ticketRepository.GetByIdAsync(ticketId, cancellationToken);
        if (ticket is null)
        {
            return TicketQueryResultDto<TicketLifecycleHistoryDto>.Failure(TicketQueryOutcome.NotFound);
        }

        if (!await CanViewTicketAsync(callerEmployeeId, callerRoles, ticket, cancellationToken))
        {
            return TicketQueryResultDto<TicketLifecycleHistoryDto>.Failure(TicketQueryOutcome.Forbidden);
        }

        var entries = await statusHistoryRepository.ListByTicketIdAsync(ticketId, cancellationToken);

        return TicketQueryResultDto<TicketLifecycleHistoryDto>.Success(
            new TicketLifecycleHistoryDto([.. entries.Select(ToHistoryEntryDto)]));
    }

    /// <summary>Renders the stored bytes as their dimension's enum names, so no client re-implements ADR-0008's mappings.</summary>
    private static TicketStatusHistoryEntryDto ToHistoryEntryDto(TicketStatusHistory entry) => new(
        entry.Dimension.ToString(),
        DescribeValue(entry.Dimension, entry.OldValue),
        DescribeValue(entry.Dimension, entry.NewValue) ?? entry.NewValue.ToString(),
        entry.ActorEmployeeId,
        entry.ActorIsSystem,
        entry.Note,
        entry.CorrelationId,
        entry.OccurredAtUtc);

    private static string? DescribeValue(TicketStatusDimension dimension, byte? value) => value switch
    {
        null => null,
        { } raw => dimension switch
        {
            TicketStatusDimension.TicketStatus => NameOf<TicketStatus>(raw),
            TicketStatusDimension.VerificationStatus => NameOf<CrmVerificationStatus>(raw),
            TicketStatusDimension.EscalationLevel => NameOf<EscalationLevel>(raw),
            TicketStatusDimension.SlaState => NameOf<SlaState>(raw),
            TicketStatusDimension.ResolutionOutcome => NameOf<ResolutionOutcome>(raw),
            _ => raw.ToString()
        }
    };

    /// <summary>Falls back to the raw byte rather than throwing: a value written by a newer version must still render in an older reader.</summary>
    private static string NameOf<TEnum>(byte value) where TEnum : struct, Enum =>
        Enum.IsDefined(typeof(TEnum), value) ? ((TEnum)Enum.ToObject(typeof(TEnum), value)).ToString() : value.ToString();

    /// <summary>
    /// The employee whose assigned/handled tickets a restricted CS Agent may see, or null when the caller is not one (their visibility is
    /// then entirely <see cref="ResolveVisibleDepartmentIdsAsync"/>). Always the caller themselves — never client-supplied.
    /// </summary>
    public Guid? ResolveAgentScope(Guid callerEmployeeId, IReadOnlyCollection<string> callerRoles) =>
        IsRestrictedAgent(callerEmployeeId, callerRoles) ? callerEmployeeId : null;

    /// <summary>
    /// <see cref="ResolveVisibleDepartmentIdsAsync"/> for the surfaces that <b>show tickets</b> (queue, search, dashboard): additionally scopes the
    /// Genesys integration account to its Genesys-created tickets (empty department set; pair with <see cref="ResolveTicketViewAgentScope"/>).
    /// The customer directory/history lookups keep the plain resolver — the integration account resolves customers across all of their tickets
    /// there, which is a lookup, not a ticket view.
    /// </summary>
    internal async Task<IReadOnlyCollection<int>?> ResolveTicketViewDepartmentIdsAsync(
        Guid callerEmployeeId, IReadOnlyCollection<string> callerRoles, CancellationToken cancellationToken) =>
        IsGenesysIntegration(callerEmployeeId, callerRoles)
            ? Array.Empty<int>()
            : await ResolveVisibleDepartmentIdsAsync(callerEmployeeId, callerRoles, cancellationToken);

    /// <summary>The ticket-view counterpart of <see cref="ResolveAgentScope"/>: the caller themselves for a restricted CS Agent and for the Genesys integration account.</summary>
    public Guid? ResolveTicketViewAgentScope(Guid callerEmployeeId, IReadOnlyCollection<string> callerRoles) =>
        IsGenesysIntegration(callerEmployeeId, callerRoles) ? callerEmployeeId : ResolveAgentScope(callerEmployeeId, callerRoles);

    /// <summary>
    /// The scope the ticket LIST (Queue / My Tickets / Closed) uses: <see cref="ResolveTicketViewAgentScope"/>, plus - for a
    /// department-scoped caller (Department Employee / Head) - the caller themselves, so every ticket currently assigned to them is
    /// listed whatever department it now sits in (My Tickets after a Call Center transfer). Ownership only adds the caller's own
    /// tickets; their department scope is unchanged. The dashboard keeps <see cref="ResolveTicketViewAgentScope"/>.
    /// </summary>
    public Guid? ResolveTicketListAgentScope(Guid callerEmployeeId, IReadOnlyCollection<string> callerRoles) =>
        ResolveTicketViewAgentScope(callerEmployeeId, callerRoles)
            ?? (OwnsTicketsOutsideDepartmentScope(callerRoles) ? callerEmployeeId : null);

    private static bool OwnsTicketsOutsideDepartmentScope(IReadOnlyCollection<string> callerRoles) =>
        TicketVisibilityRule.HoldsDepartmentRole(callerRoles)
        && !TigerCS.Domain.Modules.IdentityAndAccess.Roles.IsReadOnlyCaller(callerRoles)
        && !callerRoles.Any(TicketRoleSets.CrossDepartmentView.Contains);

    /// <summary>True when the caller is the Genesys integration account: it sees the Genesys-created tickets of every department (see <see cref="TicketVisibilityRule.IsGenesysIntegration"/>).</summary>
    public bool IsGenesysIntegration(Guid callerEmployeeId, IReadOnlyCollection<string> callerRoles) =>
        TicketVisibilityRule.IsGenesysIntegration(callerRoles, callerEmployeeId, serviceIdentities);

    /// <summary>True when the caller is a human CS Agent whose ticket visibility is the narrow per-ticket rule (never a service identity, never Admin/Supervisor/Manager).</summary>
    public bool IsRestrictedAgent(Guid callerEmployeeId, IReadOnlyCollection<string> callerRoles) =>
        TicketVisibilityRule.IsRestrictedCsAgent(callerRoles, callerEmployeeId, serviceIdentities);

    /// <summary>How much of the ticket the caller may use: Full, ReadOnly (the creator outside the department) or None.</summary>
    internal Task<TicketAccessLevel> GetTicketAccessAsync(
        Guid callerEmployeeId, IReadOnlyCollection<string> callerRoles, Ticket ticket, CancellationToken cancellationToken) =>
        TicketVisibilityRule.GetTicketAccessAsync(
            userDepartmentAssignmentRepository, handlerRepository, callerEmployeeId, callerRoles, ticket, serviceIdentities, cancellationToken);

    /// <summary>May the caller MODIFY this ticket (add a note, claim, close…): Full access only — a creator's read-only access does not qualify.</summary>
    internal Task<bool> CanModifyTicketAsync(
        Guid callerEmployeeId, IReadOnlyCollection<string> callerRoles, Ticket ticket, CancellationToken cancellationToken) =>
        TicketVisibilityRule.CanModifyTicketAsync(
            userDepartmentAssignmentRepository, handlerRepository, callerEmployeeId, callerRoles, ticket, serviceIdentities, cancellationToken);

    /// <summary>May the caller see this ticket — by department, because it is assigned to them or they handled one of its Genesys interactions, or (read-only) because they created it.</summary>
    internal Task<bool> CanViewTicketAsync(
        Guid callerEmployeeId, IReadOnlyCollection<string> callerRoles, Ticket ticket, CancellationToken cancellationToken) =>
        TicketVisibilityRule.CanViewTicketAsync(
            userDepartmentAssignmentRepository, handlerRepository, callerEmployeeId, callerRoles, ticket, serviceIdentities, cancellationToken);

    /// <summary>
    /// The server-side gate for an id-addressed request (a direct URL or API call): is this ticket visible to the caller? Used by the API's
    /// ticket-access filter to refuse a restricted CS Agent before any action runs, so no endpoint that takes a ticket id has to remember to.
    /// </summary>
    public async Task<TicketAccess> CheckAccessAsync(
        Guid callerEmployeeId, IReadOnlyCollection<string> callerRoles, long ticketId, CancellationToken cancellationToken = default)
    {
        var ticket = await ticketRepository.GetByIdAsync(ticketId, cancellationToken);
        if (ticket is null)
        {
            return TicketAccess.NotFound;
        }

        return await GetTicketAccessAsync(callerEmployeeId, callerRoles, ticket, cancellationToken) switch
        {
            TicketAccessLevel.Full => TicketAccess.Allowed,
            TicketAccessLevel.ReadOnly => TicketAccess.ReadOnly,
            _ => TicketAccess.Denied
        };
    }

    /// <summary>Ticket-level visibility for a caller holding only a ticket id: loads the ticket and applies <see cref="CanViewTicketAsync(Guid, IReadOnlyCollection{string}, Ticket, CancellationToken)"/>. A ticket that does not exist is reported as not visible.</summary>
    internal async Task<bool> CanViewTicketAsync(
        Guid callerEmployeeId, IReadOnlyCollection<string> callerRoles, long ticketId, CancellationToken cancellationToken)
    {
        var ticket = await ticketRepository.GetByIdAsync(ticketId, cancellationToken);
        return ticket is not null && await CanViewTicketAsync(callerEmployeeId, callerRoles, ticket, cancellationToken);
    }

    /// <summary>Modify-level check for a caller holding only a ticket id (a work item's ticket): false for a missing ticket and for read-only access.</summary>
    internal async Task<bool> CanModifyTicketAsync(
        Guid callerEmployeeId, IReadOnlyCollection<string> callerRoles, long ticketId, CancellationToken cancellationToken)
    {
        var ticket = await ticketRepository.GetByIdAsync(ticketId, cancellationToken);
        return ticket is not null && await CanModifyTicketAsync(callerEmployeeId, callerRoles, ticket, cancellationToken);
    }

    internal Task<bool> CanViewDepartmentAsync(
        Guid callerEmployeeId, IReadOnlyCollection<string> callerRoles, int departmentId, CancellationToken cancellationToken) =>
        TicketVisibilityRule.CanViewDepartmentAsync(
            userDepartmentAssignmentRepository, callerEmployeeId, callerRoles, departmentId, serviceIdentities, cancellationToken);

    private static TEnum? ParseEnum<TEnum>(string? value) where TEnum : struct, Enum =>
        value is not null && Enum.TryParse<TEnum>(value, ignoreCase: true, out var parsed) ? parsed : null;

    private static TicketSummaryDto ToSummaryDto(Ticket ticket) => new(
        ticket.TicketId,
        ticket.TicketNumber,
        ticket.CurrentDepartmentId,
        ticket.CurrentOwnerEmployeeId,
        ticket.CategoryId,
        ticket.PriorityId,
        ticket.TicketStatus.ToString(),
        ticket.VerificationStatus.ToString(),
        ticket.RequestSummary,
        ticket.CreatedAtUtc);

    internal static TicketDetailDto ToDetailDto(Ticket ticket) => new(
        ticket.TicketId,
        ticket.TicketNumber,
        ticket.OriginatingDepartmentId,
        ticket.CurrentDepartmentId,
        ticket.CurrentOwnerEmployeeId,
        ticket.UnitReferenceId,
        ticket.ContactReferenceId,
        ticket.CategoryId,
        ticket.PriorityId,
        ticket.TicketStatus.ToString(),
        ticket.VerificationStatus.ToString(),
        ticket.EscalationLevel.ToString(),
        ticket.SlaState.ToString(),
        ticket.ResolutionOutcome,
        ticket.DuplicateOfTicketId,
        ticket.RequestSummary,
        ticket.ReopenCount,
        ticket.CreatedAtUtc,
        Convert.ToBase64String(ticket.RowVersion),
        ticket.CrmBuyerCustomerId,
        ticket.CrmBuyerLeadId,
        ticket.CrmBuyerUnitId,
        ticket.CrmBuyerProjectId,
        ticket.CrmBuyerCustomerName,
        ticket.CrmBuyerProjectName,
        ticket.CrmBuyerUnitNumber,
        ticket.ManualProjectName,
        ticket.ManualUnitNumber,
        ticket.CustomerVerificationSource,
        ticket.ExternalCustomerId,
        ticket.ExternalUnitId,
        RequestTypeId: ticket.RequestTypeId,
        WorkflowTemplateId: ticket.WorkflowTemplateId,
        IsClassified: ticket.IsClassified,
        CreatedByEmployeeId: ticket.CreatedByEmployeeId);
}

/// <summary>The answer to <see cref="TicketQueryAppService.CheckAccessAsync"/>.</summary>
public enum TicketAccess
{
    /// <summary>The caller may see and use the ticket.</summary>
    Allowed,

    /// <summary>The caller may open and read the ticket but not change it (the creator, outside its department).</summary>
    ReadOnly,

    /// <summary>No such ticket — the endpoint answers it as it always has.</summary>
    NotFound,

    /// <summary>The ticket exists and the caller may not see it.</summary>
    Denied
}
