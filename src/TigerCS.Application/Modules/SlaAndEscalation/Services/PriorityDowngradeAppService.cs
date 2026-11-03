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
/// The priority-downgrade approval workflow (ISSUE-023 Option B;
/// MVP-API-Contracts.md section 5.6; MVP-ERD.md section 2.27).
///
/// <para>
/// <b>A downgrade never takes effect on request.</b> Requesting creates a
/// Pending <see cref="PriorityDowngradeRequest"/> and nothing else: the
/// ticket's priority and its current SLA period are untouched while it waits.
/// </para>
///
/// <para>
/// <b>Who decides.</b> A Department Head holding membership of the ticket's
/// <i>current</i> department, or a CS Manager / General Manager (the "Dept
/// Head and above" tier, cross-department), or a System Administrator via
/// ADR-0024's central override. The decider is always the authenticated
/// caller, never a request field. <b>The requester can never decide their own
/// request</b> (even under the override): the contract is silent on
/// self-approval, so the safe separation-of-duties answer is applied and
/// listed as an open decision. Chairman/CEO is not in the set: the contract's
/// "above" is read as the existing CS Manager / GM tiers.
/// </para>
///
/// <para>
/// <b>Approval is one transaction.</b> Re-validate (Pending, not expired,
/// ticket not final, priority unchanged since the request), then in a single
/// unit of work: the request becomes Approved, the ticket's priority changes,
/// the current SLA period ends and a Downgrade period opens, and the audit
/// rows are staged. Anything fallible (the new policy lookup) runs before any
/// state changes; a failed save rolls everything back.
/// </para>
/// </summary>
public sealed class PriorityDowngradeAppService(
    ITicketRepository ticketRepository,
    IPriorityRepository priorityRepository,
    IPriorityDowngradeRequestRepository requestRepository,
    IUserDepartmentAssignmentRepository userDepartmentAssignmentRepository,
    SlaDueDateService slaDueDateService,
    ITicketingUnitOfWork unitOfWork,
    IAuditEntryWriter auditWriter,
    PriorityDowngradeOptions options,
    TimeProvider timeProvider,
    SlaPauseService? slaPauseService = null,
    ITicketInteractionHandlerRepository? handlerRepository = null,
    IServiceIdentityRegistry? serviceIdentities = null,
    TicketClaimPolicy? claimPolicy = null)
{
    /// <summary>Roles that may REQUEST a downgrade ("Agent and above"): the ticket's owner always, plus these (department-side roles additionally need membership of the ticket's department).</summary>
    public static readonly IReadOnlyCollection<string> CrossDepartmentRequesterRoles =
        [Roles.CsAgent, Roles.CsSupervisor, Roles.CsManager, Roles.GeneralManager];

    public static readonly IReadOnlyCollection<string> DepartmentRequesterRoles =
        [Roles.DepartmentEmployee, Roles.DepartmentHead];

    /// <summary>Roles that may DECIDE with no department membership check (the "above" tier).</summary>
    public static readonly IReadOnlyCollection<string> CrossDepartmentDeciderRoles = [Roles.CsManager, Roles.GeneralManager];

    // -----------------------------------------------------------------
    // 5.6.1 Request
    // -----------------------------------------------------------------

    public async Task<DowngradeResult<PriorityDowngradeRequestResponseDto>> RequestAsync(
        Guid callerEmployeeId,
        IReadOnlyCollection<string> callerRoles,
        long ticketId,
        CreateDowngradeRequestRequestDto request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var ticket = await ticketRepository.GetByIdAsync(ticketId, cancellationToken);
        if (ticket is null)
        {
            return Fail<PriorityDowngradeRequestResponseDto>(DowngradeOutcome.NotFound);
        }

        var authorized = await AuthorizationGate.EvaluateAsync(callerRoles, async () =>
            ticket.CurrentOwnerEmployeeId == callerEmployeeId
            || callerRoles.Any(CrossDepartmentRequesterRoles.Contains)
            || (callerRoles.Any(DepartmentRequesterRoles.Contains)
                && await userDepartmentAssignmentRepository.ExistsAsync(callerEmployeeId, ticket.CurrentDepartmentId, cancellationToken)));
        if (!authorized
            || !await TicketVisibilityRule.PassesAgentScopeAsync(
                userDepartmentAssignmentRepository, handlerRepository, callerEmployeeId, callerRoles, ticket, serviceIdentities, cancellationToken))
        {
            return Fail<PriorityDowngradeRequestResponseDto>(DowngradeOutcome.Forbidden);
        }

        if (string.IsNullOrWhiteSpace(request.Reason)
            || await priorityRepository.GetByIdAsync(request.NewPriorityId, cancellationToken) is null)
        {
            return Fail<PriorityDowngradeRequestResponseDto>(DowngradeOutcome.InvalidRequest);
        }

        if (IsPriorityFinal(ticket))
        {
            return Fail<PriorityDowngradeRequestResponseDto>(DowngradeOutcome.TicketFinal);
        }

        if (ticket.PriorityId is not { } currentPriorityId)
        {
            return Fail<PriorityDowngradeRequestResponseDto>(DowngradeOutcome.TicketNotClassified);
        }

        if (request.NewPriorityId <= currentPriorityId)
        {
            return Fail<PriorityDowngradeRequestResponseDto>(DowngradeOutcome.NotADowngrade);
        }

        var now = timeProvider.GetUtcNow().UtcDateTime;
        var correlationId = Guid.NewGuid();

        await using var transaction = await unitOfWork.BeginTransactionAsync(cancellationToken);

        var existing = await requestRepository.GetPendingForTicketAsync(ticketId, cancellationToken);
        if (existing is not null)
        {
            if (!existing.IsExpiredAt(now))
            {
                return new DowngradeResult<PriorityDowngradeRequestResponseDto>(
                    DowngradeOutcome.AlreadyPending, Existing: ToDto(existing, now));
            }

            // A lapsed request must not block a fresh one forever; persist
            // its expiry (system action) and carry on.
            // Saved on its own first: the one-pending-per-ticket filtered
            // unique index must see the old row leave Pending before the new
            // one is inserted.
            await ExpireAsync(existing, now, correlationId, cancellationToken);
            await unitOfWork.SaveChangesAsync(cancellationToken);
        }

        var created = PriorityDowngradeRequest.Create(
            ticketId, currentPriorityId, request.NewPriorityId, request.Reason,
            callerEmployeeId, now, now + options.Lifetime);

        await requestRepository.AddAsync(created, cancellationToken);

        try
        {
            await unitOfWork.SaveChangesAsync(cancellationToken);

            await auditWriter.WriteAsync(
                callerEmployeeId, "RequestPriorityDowngrade", nameof(PriorityDowngradeRequest),
                created.PriorityDowngradeRequestId.ToString(),
                beforeValue: $"TicketId={ticketId};PriorityId={currentPriorityId}",
                afterValue:
                    $"Status=Pending;TicketId={ticketId};PriorityId={currentPriorityId};RequestedPriorityId={created.RequestedPriorityId};"
                    + $"RequestedBy={callerEmployeeId};ExpiresAtUtc={created.ExpiresAtUtc:O};Reason={created.Reason}",
                correlationId, cancellationToken);

            await unitOfWork.SaveChangesAsync(cancellationToken);
        }
        catch (DuplicateWriteException)
        {
            // A concurrent request won the one-pending-per-ticket index.
            await transaction.RollbackAsync(cancellationToken);
            var winner = await requestRepository.GetPendingForTicketAsync(ticketId, cancellationToken);
            return winner is null
                ? Fail<PriorityDowngradeRequestResponseDto>(DowngradeOutcome.ConcurrencyConflict)
                : new DowngradeResult<PriorityDowngradeRequestResponseDto>(
                    DowngradeOutcome.AlreadyPending, Existing: ToDto(winner, now));
        }
        catch (TicketConcurrentlyModifiedException)
        {
            return Fail<PriorityDowngradeRequestResponseDto>(DowngradeOutcome.ConcurrencyConflict);
        }

        await transaction.CommitAsync(cancellationToken);

        return DowngradeResult<PriorityDowngradeRequestResponseDto>.Success(ToDto(created, now));
    }

    // -----------------------------------------------------------------
    // 5.6.2 / 5.6.3 Reads
    // -----------------------------------------------------------------

    public async Task<DowngradeResult<IReadOnlyList<PriorityDowngradeRequestResponseDto>>> ListForTicketAsync(
        Guid callerEmployeeId, IReadOnlyCollection<string> callerRoles, long ticketId, CancellationToken cancellationToken = default)
    {
        var ticket = await ticketRepository.GetByIdAsync(ticketId, cancellationToken);
        if (ticket is null)
        {
            return Fail<IReadOnlyList<PriorityDowngradeRequestResponseDto>>(DowngradeOutcome.NotFound);
        }

        if (!await TicketVisibilityRule.CanViewTicketAsync(
                userDepartmentAssignmentRepository, handlerRepository, callerEmployeeId, callerRoles, ticket, serviceIdentities, cancellationToken, claimPolicy, ticketRepository))
        {
            return Fail<IReadOnlyList<PriorityDowngradeRequestResponseDto>>(DowngradeOutcome.Forbidden);
        }

        var now = timeProvider.GetUtcNow().UtcDateTime;
        var rows = await requestRepository.ListByTicketIdAsync(ticketId, cancellationToken);

        return DowngradeResult<IReadOnlyList<PriorityDowngradeRequestResponseDto>>.Success(
            [.. rows.OrderByDescending(r => r.RequestedAtUtc).Select(r => ToDto(r, now))]);
    }

    public async Task<DowngradeResult<PendingDowngradePageDto>> ListPendingAsync(
        Guid callerEmployeeId,
        IReadOnlyCollection<string> callerRoles,
        int? departmentId,
        int page,
        int pageSize,
        CancellationToken cancellationToken = default)
    {
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, 100);

        IReadOnlyCollection<int>? scope;
        if (AuthorizationOverride.AppliesTo(callerRoles) || callerRoles.Any(CrossDepartmentDeciderRoles.Contains))
        {
            scope = departmentId is { } d ? [d] : null;
        }
        else if (callerRoles.Contains(Roles.DepartmentHead))
        {
            var own = (await userDepartmentAssignmentRepository.GetByEmployeeIdAsync(callerEmployeeId, cancellationToken))
                .Select(a => a.DepartmentId).ToHashSet();
            if (departmentId is { } requested && !own.Contains(requested))
            {
                return Fail<PendingDowngradePageDto>(DowngradeOutcome.Forbidden);
            }

            scope = departmentId is { } one ? [one] : [.. own];
        }
        else
        {
            return Fail<PendingDowngradePageDto>(DowngradeOutcome.Forbidden);
        }

        var now = timeProvider.GetUtcNow().UtcDateTime;
        var (items, total) = await requestRepository.ListPendingAsync(
            scope, now, (page - 1) * pageSize, pageSize, cancellationToken);

        return DowngradeResult<PendingDowngradePageDto>.Success(new PendingDowngradePageDto(
            [.. items.Select(i => new PendingDowngradeRequestDto(
                i.Request.PriorityDowngradeRequestId, i.Request.TicketId, i.TicketNumber, i.TicketDepartmentId,
                i.Request.CurrentPriorityId, i.Request.RequestedPriorityId, i.Request.Reason,
                i.Request.RequestedByEmployeeId, i.Request.RequestedAtUtc, i.Request.ExpiresAtUtc, i.Request.RowVersion))],
            page, pageSize, total));
    }

    // -----------------------------------------------------------------
    // 5.6.4 Approve
    // -----------------------------------------------------------------

    public async Task<DowngradeResult<DowngradeDecisionResponseDto>> ApproveAsync(
        Guid callerEmployeeId,
        IReadOnlyCollection<string> callerRoles,
        long requestId,
        ApproveDowngradeRequestRequestDto? body,
        CancellationToken cancellationToken = default)
    {
        var loaded = await LoadForDecisionAsync(callerEmployeeId, callerRoles, requestId, cancellationToken);
        if (loaded.Failure is { } failure)
        {
            return Fail<DowngradeDecisionResponseDto>(failure);
        }

        var (downgrade, ticket) = (loaded.Request!, loaded.Ticket!);
        var now = timeProvider.GetUtcNow().UtcDateTime;
        var correlationId = Guid.NewGuid();

        if (IsPriorityFinal(ticket))
        {
            return Fail<DowngradeDecisionResponseDto>(DowngradeOutcome.TicketFinal);
        }

        if (ticket.PriorityId != downgrade.CurrentPriorityId)
        {
            return Fail<DowngradeDecisionResponseDto>(DowngradeOutcome.StalePriority);
        }

        TicketSlaInstance? period;
        await using (var transaction = await unitOfWork.BeginTransactionAsync(cancellationToken))
        {
            if (body?.RowVersion is { Length: > 0 } rowVersion)
            {
                requestRepository.SetRowVersion(downgrade, rowVersion);
            }

            try
            {
                // A pause cannot survive onto the replaced period. Closing it does not extend the old deadline (the new
                // Resolution clock starts at approval); if the ticket is still Pending Customer the pause is re-opened on the
                // successor period below.
                if (slaPauseService is not null)
                {
                    await slaPauseService.CloseOpenPauseAsync(ticket, now, callerEmployeeId, correlationId, cancellationToken);
                }

                // Fallible work first (policy lookup happens inside), so a
                // failure leaves every entity untouched.
                period = await slaDueDateService.ReplaceCurrentPeriodForApprovedDowngradeAsync(
                    ticket, downgrade.RequestedPriorityId, now, callerEmployeeId, correlationId, cancellationToken);

                ticket.ChangePriority(downgrade.RequestedPriorityId, downgradeApproved: true);
                downgrade.Approve(callerEmployeeId, now);

                await auditWriter.WriteAsync(
                    callerEmployeeId, "ApprovePriorityDowngrade", nameof(PriorityDowngradeRequest),
                    downgrade.PriorityDowngradeRequestId.ToString(),
                    beforeValue:
                        $"Status=Pending;TicketId={ticket.TicketId};PriorityId={downgrade.CurrentPriorityId};RequestedBy={downgrade.RequestedByEmployeeId}",
                    afterValue:
                        $"Status=Approved;TicketId={ticket.TicketId};PriorityId={downgrade.RequestedPriorityId};"
                        + $"DecidedBy={callerEmployeeId};DecidedAtUtc={now:O}",
                    correlationId, cancellationToken);

                await unitOfWork.SaveChangesAsync(cancellationToken);

                // The successor period is now visible to queries: re-establish the pause if the ticket is Pending Customer.
                if (slaPauseService is not null && period is not null)
                {
                    await slaPauseService.SyncAsync(ticket, now, callerEmployeeId, correlationId, cancellationToken);
                    await unitOfWork.SaveChangesAsync(cancellationToken);
                }
            }
            catch (Exception ex) when (ex is TicketConcurrentlyModifiedException or DuplicateWriteException)
            {
                return Fail<DowngradeDecisionResponseDto>(DowngradeOutcome.ConcurrencyConflict);
            }
            catch (PriorityDowngradeRequestNotPendingException)
            {
                return Fail<DowngradeDecisionResponseDto>(DowngradeOutcome.NotPending);
            }

            await transaction.CommitAsync(cancellationToken);
        }

        return DowngradeResult<DowngradeDecisionResponseDto>.Success(new DowngradeDecisionResponseDto(
            ToDto(downgrade, now),
            period is null ? null : ToPeriodDto(period)));
    }

    // -----------------------------------------------------------------
    // 5.6.5 Reject
    // -----------------------------------------------------------------

    public async Task<DowngradeResult<PriorityDowngradeRequestResponseDto>> RejectAsync(
        Guid callerEmployeeId,
        IReadOnlyCollection<string> callerRoles,
        long requestId,
        RejectDowngradeRequestRequestDto request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (string.IsNullOrWhiteSpace(request.DecisionNote))
        {
            // Authorization still runs first so a non-decider learns nothing.
            var probe = await LoadForDecisionAsync(callerEmployeeId, callerRoles, requestId, cancellationToken);
            return Fail<PriorityDowngradeRequestResponseDto>(probe.Failure ?? DowngradeOutcome.InvalidRequest);
        }

        var loaded = await LoadForDecisionAsync(callerEmployeeId, callerRoles, requestId, cancellationToken);
        if (loaded.Failure is { } failure)
        {
            return Fail<PriorityDowngradeRequestResponseDto>(failure);
        }

        var downgrade = loaded.Request!;
        var now = timeProvider.GetUtcNow().UtcDateTime;
        var correlationId = Guid.NewGuid();

        await using var transaction = await unitOfWork.BeginTransactionAsync(cancellationToken);

        if (request.RowVersion is { Length: > 0 } rowVersion)
        {
            requestRepository.SetRowVersion(downgrade, rowVersion);
        }

        try
        {
            downgrade.Reject(callerEmployeeId, now, request.DecisionNote);

            await auditWriter.WriteAsync(
                callerEmployeeId, "RejectPriorityDowngrade", nameof(PriorityDowngradeRequest),
                downgrade.PriorityDowngradeRequestId.ToString(),
                beforeValue: $"Status=Pending;TicketId={downgrade.TicketId};PriorityId={downgrade.CurrentPriorityId}",
                afterValue:
                    $"Status=Rejected;TicketId={downgrade.TicketId};PriorityId={downgrade.CurrentPriorityId};"
                    + $"DecidedBy={callerEmployeeId};DecidedAtUtc={now:O};DecisionNote={downgrade.DecisionNote}",
                correlationId, cancellationToken);

            await unitOfWork.SaveChangesAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is TicketConcurrentlyModifiedException or DuplicateWriteException)
        {
            return Fail<PriorityDowngradeRequestResponseDto>(DowngradeOutcome.ConcurrencyConflict);
        }
        catch (PriorityDowngradeRequestNotPendingException)
        {
            return Fail<PriorityDowngradeRequestResponseDto>(DowngradeOutcome.NotPending);
        }

        await transaction.CommitAsync(cancellationToken);

        return DowngradeResult<PriorityDowngradeRequestResponseDto>.Success(ToDto(downgrade, now));
    }

    // -----------------------------------------------------------------
    // Shared
    // -----------------------------------------------------------------

    private sealed record Loaded(PriorityDowngradeRequest? Request, Ticket? Ticket, DowngradeOutcome? Failure);

    /// <summary>
    /// The checks approve and reject share, in the order that leaks least:
    /// existence, then authorization against the ticket's CURRENT department
    /// (never any request field), then self-decision, then Pending/expiry.
    /// An expired Pending request has its expiry persisted here before 410.
    /// </summary>
    private async Task<Loaded> LoadForDecisionAsync(
        Guid callerEmployeeId, IReadOnlyCollection<string> callerRoles, long requestId, CancellationToken cancellationToken)
    {
        var downgrade = await requestRepository.GetByIdAsync(requestId, cancellationToken);
        if (downgrade is null)
        {
            return new Loaded(null, null, DowngradeOutcome.NotFound);
        }

        var ticket = await ticketRepository.GetByIdAsync(downgrade.TicketId, cancellationToken);
        if (ticket is null)
        {
            return new Loaded(null, null, DowngradeOutcome.NotFound);
        }

        var authorized = await AuthorizationGate.EvaluateAsync(callerRoles, async () =>
            callerRoles.Any(CrossDepartmentDeciderRoles.Contains)
            || (callerRoles.Contains(Roles.DepartmentHead)
                && await userDepartmentAssignmentRepository.ExistsAsync(callerEmployeeId, ticket.CurrentDepartmentId, cancellationToken)));
        if (!authorized)
        {
            return new Loaded(null, null, DowngradeOutcome.Forbidden);
        }

        if (downgrade.RequestedByEmployeeId == callerEmployeeId)
        {
            return new Loaded(null, null, DowngradeOutcome.SelfApprovalForbidden);
        }

        var now = timeProvider.GetUtcNow().UtcDateTime;

        if (downgrade.Status != PriorityDowngradeRequestStatus.Pending)
        {
            return new Loaded(null, null, DowngradeOutcome.NotPending);
        }

        if (downgrade.IsExpiredAt(now))
        {
            await using var transaction = await unitOfWork.BeginTransactionAsync(cancellationToken);
            try
            {
                await ExpireAsync(downgrade, now, Guid.NewGuid(), cancellationToken);
                await unitOfWork.SaveChangesAsync(cancellationToken);
                await transaction.CommitAsync(cancellationToken);
            }
            catch (Exception ex) when (ex is TicketConcurrentlyModifiedException or DuplicateWriteException)
            {
                // Someone else decided or expired it concurrently; either way it is gone.
            }

            return new Loaded(null, null, DowngradeOutcome.Expired);
        }

        return new Loaded(downgrade, ticket, null);
    }

    private async Task ExpireAsync(
        PriorityDowngradeRequest downgrade, DateTime now, Guid correlationId, CancellationToken cancellationToken)
    {
        downgrade.MarkExpired(now);
        await auditWriter.WriteAsync(
            actorEmployeeId: null, "ExpirePriorityDowngrade", nameof(PriorityDowngradeRequest),
            downgrade.PriorityDowngradeRequestId.ToString(),
            beforeValue: $"Status=Pending;TicketId={downgrade.TicketId}",
            afterValue: $"Status=Expired;TicketId={downgrade.TicketId};ExpiresAtUtc={downgrade.ExpiresAtUtc:O}",
            correlationId, cancellationToken);
    }

    /// <summary>A Closed ticket, or one Resolved and awaiting closure, has a final priority: its SLA outcome is settled.</summary>
    private static bool IsPriorityFinal(Ticket ticket) => ticket.TicketStatus is TicketStatus.Closed or TicketStatus.Resolved;

    private static DowngradeResult<T> Fail<T>(DowngradeOutcome outcome) => DowngradeResult<T>.Failure(outcome);

    internal static PriorityDowngradeRequestResponseDto ToDto(PriorityDowngradeRequest r, DateTime nowUtc) => new(
        r.PriorityDowngradeRequestId, r.TicketId, r.CurrentPriorityId, r.RequestedPriorityId, r.Reason,
        r.EffectiveStatusAt(nowUtc).ToString(), r.RequestedByEmployeeId, r.RequestedAtUtc, r.ExpiresAtUtc,
        r.DecidedByEmployeeId, r.DecidedAtUtc, r.DecisionNote, r.RowVersion);

    private static TicketSlaPeriodDto ToPeriodDto(TicketSlaInstance p) => new(
        p.TicketSlaInstanceId, p.PriorityId, p.PeriodStartAtUtc, p.FirstResponseDueAtUtc, p.ResolutionDueAtUtc,
        p.FirstResponseBreached, p.ResolutionBreached, p.ChangeReason.ToString(), p.ApprovedByEmployeeId);
}
