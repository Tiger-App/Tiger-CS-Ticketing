using TigerCS.Application.Abstractions;
using TigerCS.Application.Modules.Collections.Abstractions;
using TigerCS.Application.Modules.Collections.Dto;
using TigerCS.Application.Modules.Collections.Services;
using TigerCS.Application.Modules.CustomerVerification.Abstractions;
using TigerCS.Application.Modules.IdentityAndAccess.Abstractions;
using TigerCS.Application.Modules.Ticketing.Abstractions;
using TigerCS.Domain.Modules.Collections.TowerAssignments;

namespace TigerCS.Application.Modules.Collections.TowerAssignments;

/// <summary>
/// The monthly tower -> collector mapping that Collections ticket routing reads. Nothing is hard-coded: towers come from the existing tower list (<c>CollectionsTowers</c>), collectors from
/// the Collections department's active members. Every change is written to the append-only <see cref="CollectionsTowerAssignmentHistory"/> and to the audit trail in the same transaction.
/// Saving a different month never touches another month, which is what keeps the history. The one-collector-per-tower-per-month rule is a unique index, so two concurrent requests cannot
/// both win: the loser gets a Conflict.
/// </summary>
public sealed class CollectionsTowerAssignmentAppService(
    CollectionsTeam team,
    ICollectionsTowerAssignmentRepository repository,
    ICollectionsTowerCatalog towerCatalog,
    IEmployeeRepository employees,
    ITicketingUnitOfWork unitOfWork,
    IAuditEntryWriter auditWriter,
    TimeProvider timeProvider)
{
    private const int HistoryPageSize = 200;

    public async Task<CollectionsResult<TowerAssignmentMonthDto>> GetMonthAsync(
        Guid callerId, IReadOnlyCollection<string> callerRoles, int year, int month, CancellationToken cancellationToken = default)
    {
        if (!await team.CanViewTowersAsync(callerId, callerRoles, cancellationToken))
            return CollectionsResult<TowerAssignmentMonthDto>.Fail(CollectionsOutcome.Forbidden);
        if (!ValidMonth(year, month))
            return CollectionsResult<TowerAssignmentMonthDto>.Fail(CollectionsOutcome.InvalidRequest, "Choose a valid month and year.");

        IReadOnlyList<CollectionsTowerDto> catalogue;
        try { catalogue = await towerCatalog.ListActiveAsync(cancellationToken); }
        catch (PactReceivablesSourceException ex) { return CollectionsResult<TowerAssignmentMonthDto>.Fail(CollectionsOutcome.FinanceUnavailable, ex.Message); }

        var rows = await repository.ListByMonthAsync(year, month, cancellationToken);
        var members = await team.ListActiveMembersAsync(cancellationToken);
        var names = members.ToDictionary(m => m.EmployeeId, m => m.Name);

        var collectors = new List<CollectorTowersDto>();
        foreach (var employeeId in members.Select(m => m.EmployeeId).Concat(rows.Select(r => r.EmployeeId)).Distinct())
        {
            var isMember = names.ContainsKey(employeeId);
            var name = isMember ? names[employeeId] : await NameAsync(employeeId, cancellationToken);
            var towers = rows.Where(r => r.EmployeeId == employeeId)
                .OrderBy(r => r.TowerCode, StringComparer.OrdinalIgnoreCase)
                .Select(ToRowDto).ToList();
            collectors.Add(new(employeeId, name, isMember, towers));
        }

        var held = rows.Select(r => r.TowerId).ToHashSet();
        return CollectionsResult<TowerAssignmentMonthDto>.Ok(new(
            year, month, await team.CanManageTowersAsync(callerId, callerRoles, cancellationToken),
            [.. collectors.OrderBy(c => c.EmployeeName, StringComparer.OrdinalIgnoreCase)],
            catalogue,
            [.. catalogue.Where(t => !held.Contains(t.TowerId)).Select(t => t.TowerId)],
            rows.Count(r => r.Status == TowerAssignmentStatus.Draft)));
    }

    public async Task<CollectionsResult<TowerAssignmentMonthDto>> SaveEmployeeTowersAsync(
        Guid callerId, IReadOnlyCollection<string> callerRoles, Guid employeeId, SaveEmployeeTowersRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!await team.CanManageTowersAsync(callerId, callerRoles, cancellationToken))
            return CollectionsResult<TowerAssignmentMonthDto>.Fail(CollectionsOutcome.Forbidden);
        if (!ValidMonth(request.Year, request.Month))
            return CollectionsResult<TowerAssignmentMonthDto>.Fail(CollectionsOutcome.InvalidRequest, "Choose a valid month and year.");

        // The collector must be an ACTIVE employee of the Collections department - checked here, never trusted from the page.
        if (await team.GetDepartmentAsync(cancellationToken) is not { } collections
            || !await team.IsActiveMemberAsync(employeeId, collections.DepartmentId, cancellationToken))
            return CollectionsResult<TowerAssignmentMonthDto>.Fail(CollectionsOutcome.InvalidRequest, "The selected employee is not an active member of the Collections department.");

        IReadOnlyList<CollectionsTowerDto> catalogue;
        try { catalogue = await towerCatalog.ListActiveAsync(cancellationToken); }
        catch (PactReceivablesSourceException ex) { return CollectionsResult<TowerAssignmentMonthDto>.Fail(CollectionsOutcome.FinanceUnavailable, ex.Message); }

        var wanted = (request.TowerIds ?? []).Distinct().ToList();
        var byId = catalogue.ToDictionary(t => t.TowerId);
        if (wanted.FirstOrDefault(id => !byId.ContainsKey(id)) is var unknown && unknown != 0)
            return CollectionsResult<TowerAssignmentMonthDto>.Fail(CollectionsOutcome.InvalidRequest, $"Tower {unknown} is not an active tower.");

        var rows = await repository.ListByMonthAsync(request.Year, request.Month, cancellationToken);
        var heldByOthers = rows.Where(r => wanted.Contains(r.TowerId) && r.EmployeeId != employeeId).ToList();
        if (heldByOthers.Count > 0 && !request.MoveFromOthers)
        {
            var list = string.Join(", ", heldByOthers.Select(r => $"{r.TowerCode} ({r.EmployeeId})"));
            return CollectionsResult<TowerAssignmentMonthDto>.Fail(CollectionsOutcome.Conflict,
                $"These towers are already assigned to another collector for {request.Year}-{request.Month:00}: {string.Join(", ", heldByOthers.Select(r => r.TowerCode))}. Move them explicitly or remove them from the other collector first.");
        }

        var now = timeProvider.GetUtcNow().UtcDateTime;
        var status = request.Activate ? TowerAssignmentStatus.Active : TowerAssignmentStatus.Draft;
        var correlationId = Guid.NewGuid();
        await using var transaction = await unitOfWork.BeginTransactionAsync(cancellationToken);

        foreach (var removed in rows.Where(r => r.EmployeeId == employeeId && !wanted.Contains(r.TowerId)).ToList())
        {
            await repository.AddHistoryAsync(new(removed, employeeId, null, TowerAssignmentAction.Removed, now, callerId), cancellationToken);
            repository.Remove(removed);
        }

        foreach (var towerId in wanted)
        {
            var existing = rows.FirstOrDefault(r => r.TowerId == towerId);
            if (existing is null)
            {
                var tower = byId[towerId];
                var created = new CollectionsTowerAssignment(tower.TowerId, tower.TowerNumber, tower.TowerName, tower.CompanyId, request.Year, request.Month, employeeId, status, now, callerId);
                await repository.AddAsync(created, cancellationToken);
                await repository.AddHistoryAsync(new(created, null, employeeId, TowerAssignmentAction.Added, now, callerId), cancellationToken);
                if (status == TowerAssignmentStatus.Active)
                    await repository.AddHistoryAsync(new(created, null, employeeId, TowerAssignmentAction.Activated, now, callerId), cancellationToken);
                continue;
            }

            if (existing.EmployeeId != employeeId)
            {
                var previous = existing.EmployeeId;
                existing.ReassignTo(employeeId, now, callerId);
                await repository.AddHistoryAsync(new(existing, previous, employeeId, TowerAssignmentAction.Reassigned, now, callerId), cancellationToken);
            }

            if (request.Activate && existing.Activate(now, callerId))
                await repository.AddHistoryAsync(new(existing, existing.EmployeeId, existing.EmployeeId, TowerAssignmentAction.Activated, now, callerId), cancellationToken);
        }

        await auditWriter.WriteAsync(
            callerId, "SaveCollectionsTowerAssignments", "CollectionsTowerAssignment", $"{request.Year}-{request.Month:00}",
            beforeValue: string.Join(',', rows.Where(r => r.EmployeeId == employeeId).Select(r => r.TowerId).Order()),
            afterValue: $"EmployeeId={employeeId};Towers={string.Join(',', wanted.Order())};Status={status}", correlationId, cancellationToken);

        if (await TrySaveAsync(transaction, cancellationToken) is { } failure)
            return CollectionsResult<TowerAssignmentMonthDto>.Fail(CollectionsOutcome.Conflict, failure);
        return await GetMonthAsync(callerId, callerRoles, request.Year, request.Month, cancellationToken);
    }

    public async Task<CollectionsResult<TowerAssignmentMonthDto>> ActivateAsync(
        Guid callerId, IReadOnlyCollection<string> callerRoles, ActivateTowerAssignmentsRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!await team.CanManageTowersAsync(callerId, callerRoles, cancellationToken))
            return CollectionsResult<TowerAssignmentMonthDto>.Fail(CollectionsOutcome.Forbidden);
        if (!ValidMonth(request.Year, request.Month))
            return CollectionsResult<TowerAssignmentMonthDto>.Fail(CollectionsOutcome.InvalidRequest, "Choose a valid month and year.");

        var now = timeProvider.GetUtcNow().UtcDateTime;
        var rows = await repository.ListByMonthAsync(request.Year, request.Month, cancellationToken);
        await using var transaction = await unitOfWork.BeginTransactionAsync(cancellationToken);
        var activated = 0;
        foreach (var row in rows.Where(r => request.EmployeeId is null || r.EmployeeId == request.EmployeeId))
        {
            if (!row.Activate(now, callerId)) continue;
            activated++;
            await repository.AddHistoryAsync(new(row, row.EmployeeId, row.EmployeeId, TowerAssignmentAction.Activated, now, callerId), cancellationToken);
        }

        await auditWriter.WriteAsync(
            callerId, "ActivateCollectionsTowerAssignments", "CollectionsTowerAssignment", $"{request.Year}-{request.Month:00}",
            beforeValue: null, afterValue: $"Activated={activated};EmployeeId={request.EmployeeId?.ToString() ?? "all"}", Guid.NewGuid(), cancellationToken);

        if (await TrySaveAsync(transaction, cancellationToken) is { } failure)
            return CollectionsResult<TowerAssignmentMonthDto>.Fail(CollectionsOutcome.Conflict, failure);
        return await GetMonthAsync(callerId, callerRoles, request.Year, request.Month, cancellationToken);
    }

    public async Task<CollectionsResult<CopyPreviousMonthResultDto>> CopyPreviousMonthAsync(
        Guid callerId, IReadOnlyCollection<string> callerRoles, CopyPreviousMonthRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!await team.CanManageTowersAsync(callerId, callerRoles, cancellationToken))
            return CollectionsResult<CopyPreviousMonthResultDto>.Fail(CollectionsOutcome.Forbidden);
        if (!ValidMonth(request.Year, request.Month))
            return CollectionsResult<CopyPreviousMonthResultDto>.Fail(CollectionsOutcome.InvalidRequest, "Choose a valid month and year.");

        var previous = new DateOnly(request.Year, request.Month, 1).AddMonths(-1);
        IReadOnlyList<CollectionsTowerDto> catalogue;
        try { catalogue = await towerCatalog.ListActiveAsync(cancellationToken); }
        catch (PactReceivablesSourceException ex) { return CollectionsResult<CopyPreviousMonthResultDto>.Fail(CollectionsOutcome.FinanceUnavailable, ex.Message); }

        var source = await repository.ListByMonthAsync(previous.Year, previous.Month, cancellationToken);
        var current = (await repository.ListByMonthAsync(request.Year, request.Month, cancellationToken)).Select(r => r.TowerId).ToHashSet();
        var activeTowers = catalogue.ToDictionary(t => t.TowerId);
        var activeMembers = (await team.ListActiveMembersAsync(cancellationToken)).Select(m => m.EmployeeId).ToHashSet();

        var now = timeProvider.GetUtcNow().UtcDateTime;
        int copied = 0, existing = 0, unavailable = 0;
        await using var transaction = await unitOfWork.BeginTransactionAsync(cancellationToken);
        foreach (var row in source.OrderBy(r => r.TowerCode, StringComparer.OrdinalIgnoreCase))
        {
            if (current.Contains(row.TowerId)) { existing++; continue; }
            if (!activeTowers.TryGetValue(row.TowerId, out var tower) || !activeMembers.Contains(row.EmployeeId)) { unavailable++; continue; }
            var created = new CollectionsTowerAssignment(tower.TowerId, tower.TowerNumber, tower.TowerName, tower.CompanyId, request.Year, request.Month, row.EmployeeId, TowerAssignmentStatus.Draft, now, callerId);
            await repository.AddAsync(created, cancellationToken);
            await repository.AddHistoryAsync(new(created, null, row.EmployeeId, TowerAssignmentAction.Added, now, callerId), cancellationToken);
            copied++;
        }

        await auditWriter.WriteAsync(
            callerId, "CopyCollectionsTowerAssignments", "CollectionsTowerAssignment", $"{request.Year}-{request.Month:00}",
            beforeValue: $"From={previous.Year}-{previous.Month:00}", afterValue: $"Copied={copied};SkippedAssigned={existing};SkippedUnavailable={unavailable}", Guid.NewGuid(), cancellationToken);

        if (await TrySaveAsync(transaction, cancellationToken) is { } failure)
            return CollectionsResult<CopyPreviousMonthResultDto>.Fail(CollectionsOutcome.Conflict, failure);
        return CollectionsResult<CopyPreviousMonthResultDto>.Ok(new(copied, existing, unavailable));
    }

    public async Task<CollectionsResult<IReadOnlyList<TowerAssignmentHistoryDto>>> ListHistoryAsync(
        Guid callerId, IReadOnlyCollection<string> callerRoles, int? year, int? month, int? towerId, CancellationToken cancellationToken = default)
    {
        if (!await team.CanViewTowersAsync(callerId, callerRoles, cancellationToken))
            return CollectionsResult<IReadOnlyList<TowerAssignmentHistoryDto>>.Fail(CollectionsOutcome.Forbidden);

        var entries = await repository.ListHistoryAsync(year, month, towerId, HistoryPageSize, cancellationToken);
        var cache = new Dictionary<Guid, string>();
        async Task<string> Name(Guid id) => cache.TryGetValue(id, out var n) ? n : cache[id] = await NameAsync(id, cancellationToken);
        var result = new List<TowerAssignmentHistoryDto>();
        foreach (var e in entries)
            result.Add(new(
                e.CollectionsTowerAssignmentHistoryId, e.TowerId, e.TowerCode, e.TowerName, e.Year, e.Month,
                e.PreviousEmployeeId, e.PreviousEmployeeId is { } p ? await Name(p) : null,
                e.NewEmployeeId, e.NewEmployeeId is { } n2 ? await Name(n2) : null,
                e.Action.ToString(), e.ChangedByEmployeeId, await Name(e.ChangedByEmployeeId), e.ChangedAtUtc));
        return CollectionsResult<IReadOnlyList<TowerAssignmentHistoryDto>>.Ok(result);
    }

    private async Task<string?> TrySaveAsync(ITicketingTransaction transaction, CancellationToken cancellationToken)
    {
        try
        {
            await unitOfWork.SaveChangesAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is TicketConcurrentlyModifiedException or DuplicateWriteException)
        {
            return "Another change to the tower assignments was saved at the same moment. Reload the page and try again.";
        }

        await transaction.CommitAsync(cancellationToken);
        return null;
    }

    private async Task<string> NameAsync(Guid employeeId, CancellationToken cancellationToken) =>
        (await employees.GetByIdAsync(employeeId, cancellationToken))?.DisplayName ?? employeeId.ToString();

    private static bool ValidMonth(int year, int month) => year is >= 2000 and <= 2200 && month is >= 1 and <= 12;

    private static TowerAssignmentRowDto ToRowDto(CollectionsTowerAssignment r) => new(r.TowerId, r.TowerCode, r.TowerName, r.CompanyId, r.Status.ToString());
}
