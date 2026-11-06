using TigerCS.Application.Modules.CustomerVerification.Abstractions;
using TigerCS.Application.Modules.Ticketing.Abstractions;
using TigerCS.Domain.Modules.ClassificationAndRouting;
using TigerCS.Domain.Modules.SlaAndEscalation;
using TigerCS.Domain.Modules.Ticketing;
using TigerCS.Infrastructure.Modules.Ticketing.Seed;

namespace TigerCS.Tests.Ticketing.Fakes;

public sealed class FakeCategoryRepository : ICategoryRepository
{
    private readonly Dictionary<int, Category> _categories = [];
    private int _nextId = 1;

    public Task<Category?> GetByIdAsync(int categoryId, CancellationToken cancellationToken = default) =>
        Task.FromResult(_categories.GetValueOrDefault(categoryId));

    public Category Seed(int departmentId, string name = "General Inquiry", bool isActive = true)
    {
        var category = new Category(name, departmentId, isActive: isActive);
        typeof(Category).GetProperty(nameof(Category.CategoryId))!.SetValue(category, _nextId++);
        _categories[category.CategoryId] = category;
        return category;
    }

    public Task<IReadOnlyCollection<Category>> ListAsync(bool activeOnly, int? departmentId, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyCollection<Category>>(
            _categories.Values
                .Where(c => !activeOnly || c.IsActive)
                .Where(c => departmentId is null || c.DepartmentId == departmentId)
                .OrderBy(c => c.Name, StringComparer.Ordinal)
                .ToList());
}

public sealed class FakePriorityRepository : IPriorityRepository
{
    private readonly Dictionary<byte, Priority> _priorities = new()
    {
        [(byte)PriorityLevel.Critical] = new Priority((byte)PriorityLevel.Critical, "Critical", 1),
        [(byte)PriorityLevel.High] = new Priority((byte)PriorityLevel.High, "High", 2),
        [(byte)PriorityLevel.Medium] = new Priority((byte)PriorityLevel.Medium, "Medium", 3),
        [(byte)PriorityLevel.Low] = new Priority((byte)PriorityLevel.Low, "Low", 4)
    };

    public Task<Priority?> GetByIdAsync(byte priorityId, CancellationToken cancellationToken = default) =>
        Task.FromResult(_priorities.GetValueOrDefault(priorityId));

    public Task<Priority?> GetByNameAsync(string name, CancellationToken cancellationToken = default) =>
        Task.FromResult(_priorities.Values.FirstOrDefault(p => string.Equals(p.Name, name.Trim(), StringComparison.OrdinalIgnoreCase)));
}

/// <summary>
/// In-memory <see cref="IChannelRepository"/>. Starts EMPTY — a test seeds
/// exactly the channels it needs (<see cref="SeedWellKnown"/> adds the
/// approved production reference rows under their <see cref="WellKnownChannels"/>
/// ids), so no test passes on a channel list it never declared.
/// </summary>
public sealed class FakeChannelRepository : IChannelRepository
{
    private readonly Dictionary<byte, Channel> _channels = [];
    private byte _nextId = 1;

    /// <summary>Test assertion helper — every channel, in insertion order.</summary>
    public IReadOnlyList<Channel> All => _channels.Values.ToList();

    public Dictionary<byte, int> References { get; } = [];

    /// <summary>The approved production reference channels under their fixed ids — mirrors ChannelReferenceData (ids 2 and 3 are inactive legacy rows).</summary>
    public FakeChannelRepository SeedWellKnown()
    {
        foreach (var channel in ChannelReferenceData.Channels())
        {
            Add(channel);
        }

        return this;
    }

    public Channel Add(Channel channel)
    {
        if (channel.ChannelId == 0)
        {
            while (_channels.ContainsKey(_nextId))
            {
                _nextId++;
            }

            typeof(Channel).GetProperty(nameof(Channel.ChannelId))!.SetValue(channel, _nextId++);
        }

        _channels[channel.ChannelId] = channel;
        return channel;
    }

    public Channel AddChannel(string name, string code, bool requiresPhone = true, bool isGenesysEnabled = false, int displayOrder = 0, bool isActive = true) =>
        Add(new Channel(name, code, requiresPhone, isGenesysEnabled, displayOrder, isActive));

    public Task<Channel?> GetByIdAsync(byte channelId, CancellationToken cancellationToken = default) =>
        Task.FromResult(_channels.GetValueOrDefault(channelId));

    public Task<Channel?> GetByCodeAsync(string code, CancellationToken cancellationToken = default) =>
        Task.FromResult(_channels.Values.FirstOrDefault(c => string.Equals(c.Code, code.Trim(), StringComparison.OrdinalIgnoreCase)));

    public Task<IReadOnlyList<Channel>> ListAsync(bool activeOnly, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<Channel>>(
            _channels.Values
                .Where(c => !activeOnly || c.IsActive)
                .OrderBy(c => c.DisplayOrder)
                .ThenBy(c => c.Name, StringComparer.Ordinal)
                .ToList());

    public Task AddAsync(Channel channel, CancellationToken cancellationToken = default)
    {
        Add(channel);
        return Task.CompletedTask;
    }

    public Task<bool> CodeExistsAsync(string code, byte? excludeChannelId, CancellationToken cancellationToken = default) =>
        Task.FromResult(_channels.Values.Any(c =>
            string.Equals(c.Code, code.Trim(), StringComparison.OrdinalIgnoreCase)
            && (excludeChannelId is null || c.ChannelId != excludeChannelId)));

    public Task<int> CountReferencesAsync(byte channelId, CancellationToken cancellationToken = default) =>
        Task.FromResult(References.GetValueOrDefault(channelId));
}

public sealed class FakeIntakeRecordRepository : IIntakeRecordRepository
{
    private readonly Dictionary<long, IntakeRecord> _records = [];
    private long _nextId = 1;

    /// <summary>Test assertion helper — every intake record added so far, in insertion order.</summary>
    public IReadOnlyCollection<IntakeRecord> All => _records.Values;

    public Task<IntakeRecord?> GetByIdAsync(long intakeRecordId, CancellationToken cancellationToken = default) =>
        Task.FromResult(_records.GetValueOrDefault(intakeRecordId));

    public Task<IntakeRecord?> GetByLinkedTicketIdAsync(long ticketId, CancellationToken cancellationToken = default) =>
        Task.FromResult(_records.Values.FirstOrDefault(r => r.LinkedTicketId == ticketId));

    public Task<IReadOnlyList<long>> ListLinkedTicketIdsByPhoneNumberAsync(string phoneNumber, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<long>>(
            _records.Values
                .Where(r => r.LinkedTicketId is not null && CustomerPhoneNumber.AreSameNumber(r.PhoneNumber, phoneNumber))
                .Select(r => r.LinkedTicketId!.Value)
                .Distinct()
                .ToList());

    public Task AddAsync(IntakeRecord intakeRecord, CancellationToken cancellationToken = default)
    {
        typeof(IntakeRecord).GetProperty(nameof(IntakeRecord.IntakeRecordId))!.SetValue(intakeRecord, _nextId++);
        _records[intakeRecord.IntakeRecordId] = intakeRecord;
        return Task.CompletedTask;
    }
}

public sealed class FakeDepartmentCustomerLookupSourceRepository : IDepartmentCustomerLookupSourceRepository
{
    private readonly Dictionary<int, List<CustomerLookupSource>> _sourcesByDepartmentId = [];

    public FakeDepartmentCustomerLookupSourceRepository Seed(int departmentId, params CustomerLookupSource[] sources)
    {
        _sourcesByDepartmentId[departmentId] = sources.ToList();
        return this;
    }

    public Task<IReadOnlyCollection<CustomerLookupSource>> GetSourcesForDepartmentAsync(
        int departmentId, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyCollection<CustomerLookupSource>>(
            _sourcesByDepartmentId.GetValueOrDefault(departmentId, []));
}

public sealed class FakeTicketRepository : ITicketRepository
{
    private readonly Dictionary<long, Ticket> _tickets = [];
    private long _nextId = 1;

    /// <summary>Test assertion helper — every ticket added so far, in insertion order.</summary>
    public IReadOnlyCollection<Ticket> All => _tickets.Values;

    /// <summary>Mirrors the coarse part of the real AI-only candidate query (created before the cutoff, open for work, unowned); the service re-checks the rest.</summary>
    public Task<IReadOnlyList<long>> ListAiOnlyAutoCloseCandidateIdsAsync(
        DateTime createdBeforeUtc, long afterTicketId, int batchSize, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<long>>(
            _tickets.Values
                .Where(t => t.TicketId > afterTicketId && t.CreatedAtUtc < createdBeforeUtc
                    && t.TicketStatus is TigerCS.Domain.Modules.Ticketing.TicketStatus.Open or TigerCS.Domain.Modules.Ticketing.TicketStatus.InProgress or TigerCS.Domain.Modules.Ticketing.TicketStatus.PendingCustomer
                    && t.CurrentOwnerEmployeeId is null)
                .OrderBy(t => t.TicketId).Select(t => t.TicketId).Take(batchSize).ToList());

    /// <summary>The handlers the CS Agent visibility rule reads. Share this instance with the <c>TicketQueryAppService</c> under test.</summary>
    public FakeTicketInteractionHandlerRepository Handlers { get; } = new();

    /// <summary>Whether the ticket is inside the scope — the join the real handoff repository does against <c>Tickets</c>.</summary>
    public bool IsVisibleTo(long ticketId, IReadOnlyCollection<int>? departmentIds, Guid? agentEmployeeId) =>
        _tickets.TryGetValue(ticketId, out var ticket) && IsVisible(ticket, departmentIds, agentEmployeeId, includeCreated: false);

    /// <summary>
    /// The Identity data the agent rule reads (which owners are CS Agents, who is the responsible CS Agent of a ticket). Share the instance given to
    /// the <c>TicketQueryAppService</c> under test; without it no owner counts as a CS Agent and nobody follows a department employee's work.
    /// </summary>
    public IdentityAndAccess.Fakes.FakeUserDepartmentAssignmentRepository? Directory { get; set; }

    /// <summary>
    /// Mirrors <c>TicketQueryFilters.VisibleTo</c>: department scope for everyone; for a restricted CS Agent, assigned-to-me OR a department
    /// employee holds it and I am its responsible CS Agent OR a department their other role grants. Creating or handling grants nothing.
    /// </summary>
    private bool IsVisible(
        Ticket t, IReadOnlyCollection<int>? departmentIds, Guid? agentEmployeeId, bool includeCreated = true, bool createdOnly = false,
        Guid? ownerEmployeeId = null) =>
        agentEmployeeId is not { } agentId
            // A department-scoped caller (departmentIds not null) also always sees the tickets assigned to them; an empty scope (a regular
            // Department Employee) therefore sees exactly their own tickets.
            ? departmentIds is null || departmentIds.Contains(t.CurrentDepartmentId)
                || (ownerEmployeeId is { } ownerId && t.CurrentOwnerEmployeeId == ownerId)
            : createdOnly
            ? t.CreatedByEmployeeId == agentId
            : t.CurrentOwnerEmployeeId == agentId
                || FollowsDepartmentWork(t, agentId)
                || FollowsClosedTicket(t, agentId)
                || (departmentIds?.Contains(t.CurrentDepartmentId) ?? false);

    private bool FollowsClosedTicket(Ticket t, Guid agentId) =>
        Directory is { } directory
        && t.TicketStatus == TigerCS.Domain.Modules.Ticketing.TicketStatus.Closed
        && t.CurrentOwnerEmployeeId is null
        && directory.IsResponsibleForClosedTicketAsync(t.TicketId, t.CreatedByEmployeeId, agentId).GetAwaiter().GetResult();

    private bool FollowsDepartmentWork(Ticket t, Guid agentId) =>
        Directory is { } directory
        && t.CurrentOwnerEmployeeId is { } owner
        && owner != agentId
        && !(directory.RolesByEmployee.TryGetValue(owner, out var ownerRoles) && ownerRoles.Contains(TigerCS.Domain.Modules.IdentityAndAccess.Roles.CsAgent))
        && directory.IsResponsibleCsAgentAsync(t.TicketId, t.CreatedByEmployeeId, agentId).GetAwaiter().GetResult();

    public Task<Ticket?> GetByIdAsync(long ticketId, CancellationToken cancellationToken = default) =>
        Task.FromResult(_tickets.GetValueOrDefault(ticketId));

    public Task AddAsync(Ticket ticket, CancellationToken cancellationToken = default)
    {
        typeof(Ticket).GetProperty(nameof(Ticket.TicketId))!.SetValue(ticket, _nextId++);
        _tickets[ticket.TicketId] = ticket;
        return Task.CompletedTask;
    }

    public Task<int> CountByTicketNumberPrefixAsync(string ticketNumberPrefix, CancellationToken cancellationToken = default) =>
        Task.FromResult(_tickets.Values.Count(t => t.TicketNumber.StartsWith(ticketNumberPrefix, StringComparison.Ordinal)));

    public Task<TicketQueryResult> SearchAsync(TicketQuery query, CancellationToken cancellationToken = default)
    {
        var filtered = _tickets.Values.AsEnumerable();

        filtered = filtered.Where(t => IsVisible(t, query.VisibleDepartmentIds, query.AgentScopeEmployeeId, createdOnly: query.CreatedOnlyScope, ownerEmployeeId: query.OwnerVisibilityEmployeeId));

        if (query.CreatedByEmployeeId is { } createdBy)
        {
            filtered = filtered.Where(t => t.CreatedByEmployeeId == createdBy);
        }

        if (query.DepartmentId is { } departmentId)
        {
            filtered = filtered.Where(t => t.CurrentDepartmentId == departmentId);
        }

        if (query.CategoryId is { } categoryId)
        {
            filtered = filtered.Where(t => t.CategoryId == categoryId);
        }

        if (query.PriorityId is { } priorityId)
        {
            filtered = filtered.Where(t => t.PriorityId == priorityId);
        }

        if (query.TicketStatus is { } ticketStatus)
        {
            filtered = filtered.Where(t => t.TicketStatus == ticketStatus);
        }

        if (query.VerificationStatus is { } verificationStatus)
        {
            filtered = filtered.Where(t => t.VerificationStatus == verificationStatus);
        }

        if (query.OwnerEmployeeId is { } ownerEmployeeId)
        {
            filtered = filtered.Where(t => t.CurrentOwnerEmployeeId == ownerEmployeeId);
        }

        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            // Mirrors TicketRepository: number, summary, and the customer
            // name / unit number the ticket snapshotted (the intake-phone
            // leg needs the intake table and stays with the SQLite tests).
            var search = query.Search.Trim();
            filtered = filtered.Where(t =>
                t.TicketNumber.Contains(search, StringComparison.OrdinalIgnoreCase)
                || t.RequestSummary.Contains(search, StringComparison.OrdinalIgnoreCase)
                || (t.CrmBuyerCustomerName?.Contains(search, StringComparison.OrdinalIgnoreCase) ?? false)
                || (t.CrmBuyerUnitNumber?.Contains(search, StringComparison.OrdinalIgnoreCase) ?? false)
                || (t.ManualUnitNumber?.Contains(search, StringComparison.OrdinalIgnoreCase) ?? false));
        }

        var all = filtered.ToList();
        var totalCount = all.Count;

        // Mirrors TicketRepository exactly, the null-priority ordering
        // included: both LINQ-to-objects and SQL Server sort NULL first
        // ascending, so without the leading key an unclassified ticket would
        // outrank every Critical one. A fake that got this wrong would let
        // the real bug pass.
        IEnumerable<Ticket> sorted = query.SortBy switch
        {
            TicketSortBy.Priority => query.SortDescending
                ? all.OrderBy(t => t.PriorityId is null).ThenByDescending(t => t.PriorityId).ThenByDescending(t => t.CreatedAtUtc)
                : all.OrderBy(t => t.PriorityId is null).ThenBy(t => t.PriorityId).ThenByDescending(t => t.CreatedAtUtc),
            _ => query.SortDescending
                ? all.OrderByDescending(t => t.CreatedAtUtc)
                : all.OrderBy(t => t.CreatedAtUtc)
        };

        var page = sorted.Skip((query.Page - 1) * query.PageSize).Take(query.PageSize).ToList();
        return Task.FromResult(new TicketQueryResult(page, totalCount));
    }

    /// <summary>Test double — RowVersion concurrency is simulated by FakeTicketingUnitOfWork.ThrowTicketConcurrencyConflictOnCall instead, since there's no real change tracker here to prime.</summary>
    public void SetRowVersion(Ticket ticket, byte[] rowVersion)
    {
    }

    /// <summary>
    /// Moves the ticket's stored RowVersion, standing in for another request
    /// having written to it. Needed since Reopen compares the caller's token
    /// against the loaded ticket BEFORE its state checks, so a stale copy is
    /// answered as the concurrency conflict it is rather than as ineligibility.
    /// </summary>
    public static void SetStoredRowVersion(Ticket ticket, byte[] rowVersion) =>
        typeof(Ticket).GetProperty(nameof(Ticket.RowVersion))!.SetValue(ticket, rowVersion);

    /// <summary>How many times <see cref="SearchCustomerHistoryAsync"/> ran — the no-N+1 assertion for related-ticket/history reads (one scoped query per request, never one per row).</summary>
    public int SearchCustomerHistoryCallCount { get; private set; }

    public Task<CustomerHistoryQueryResult> SearchCustomerHistoryAsync(
        CustomerHistoryQuery query, CancellationToken cancellationToken = default)
    {
        SearchCustomerHistoryCallCount++;

        IEnumerable<Ticket> filtered;
        if (query is { IncludeLinkedExternalIdentity: true, CrmBuyerCustomerId: { } linkedCrmId,
            ExternalSource: { } linkedSource, ExternalCustomerId: { } linkedExternalId })
        {
            filtered = _tickets.Values.Where(t => t.CrmBuyerCustomerId == linkedCrmId
                || (t.CustomerVerificationSource == linkedSource && t.ExternalCustomerId == linkedExternalId));
        }
        else if (query.CrmBuyerCustomerId is { } crmBuyerCustomerId)
        {
            filtered = _tickets.Values.Where(t => t.CrmBuyerCustomerId == crmBuyerCustomerId);
        }
        else if (query is { ExternalSource: { } externalSource, ExternalCustomerId: { } externalCustomerId })
        {
            filtered = _tickets.Values.Where(t =>
                t.CustomerVerificationSource == externalSource && t.ExternalCustomerId == externalCustomerId);
        }
        else if (query.TicketIds is { Count: > 0 } ticketIds)
        {
            filtered = _tickets.Values.Where(t => ticketIds.Contains(t.TicketId));
        }
        else
        {
            return Task.FromResult(new CustomerHistoryQueryResult([], 0, 0, 0));
        }

        filtered = filtered.Where(t => IsVisible(t, query.VisibleDepartmentIds, query.AgentScopeEmployeeId, ownerEmployeeId: query.OwnerVisibilityEmployeeId));

        if (query.ExcludeTicketId is { } excludeTicketId)
        {
            filtered = filtered.Where(t => t.TicketId != excludeTicketId);
        }

        if (query.UnitNumber is { } unitNumber)
        {
            filtered = filtered.Where(t => t.CrmBuyerUnitNumber == unitNumber || t.ManualUnitNumber == unitNumber);
        }

        var all = filtered.ToList();
        var closedCount = all.Count(t => t.TicketStatus is TicketStatus.Resolved or TicketStatus.Closed);
        var ordered = query.OrderActiveFirst
            ? all.OrderBy(t => t.TicketStatus is TicketStatus.Resolved or TicketStatus.Closed ? 1 : 0)
                .ThenByDescending(t => t.CreatedAtUtc)
            : all.OrderByDescending(t => t.CreatedAtUtc);
        var items = ordered.Take(query.Limit).ToList();

        return Task.FromResult(new CustomerHistoryQueryResult(items, all.Count, all.Count - closedCount, closedCount));
    }

    /// <summary>
    /// In-memory dashboard aggregate over the fake's tickets. SLA-derived
    /// values (at-risk count, attention due dates) come back empty — the
    /// fake holds no TicketSlaInstances; wire <see cref="Resolutions"/> to
    /// the test's FakeTicketResolutionRepository for a ResolvedToday count.
    /// </summary>
    public FakeTicketResolutionRepository? Resolutions { get; set; }

    public Task<DashboardSnapshot> GetDashboardSnapshotAsync(
        DashboardSnapshotQuery query, CancellationToken cancellationToken = default)
    {
        var visible = _tickets.Values.AsEnumerable();
        visible = visible.Where(t => IsVisible(t, query.VisibleDepartmentIds, query.AgentScopeEmployeeId, createdOnly: query.CreatedOnlyScope, ownerEmployeeId: query.CallerEmployeeId));

        var visibleList = visible.ToList();
        var active = visibleList
            .Where(t => t.TicketStatus is TicketStatus.Open or TicketStatus.InProgress
                or TicketStatus.PendingCustomer or TicketStatus.PendingThirdParty)
            .ToList();

        var resolvedToday = Resolutions is null
            ? 0
            : visibleList.Count(t => Resolutions.Added.Any(r =>
                r.TicketId == t.TicketId && r.IsCurrent && r.ResolvedAtUtc >= query.ResolvedTodayStartUtc));

        var attention = active
            .Where(t => t.SlaState == SlaState.Breached || t.PriorityId <= 2 || t.CurrentOwnerEmployeeId == null)
            .OrderBy(t => t.SlaState == SlaState.Breached ? 0 : t.PriorityId == 1 ? 2 : t.PriorityId == 2 ? 3 : 4)
            .ThenBy(t => t.PriorityId is null)
            .ThenBy(t => t.PriorityId)
            .ThenBy(t => t.CreatedAtUtc)
            .Take(query.AttentionLimit)
            .Select(t => new DashboardAttentionTicket(t, SlaDueAtUtc: null))
            .ToList();

        return Task.FromResult(new DashboardSnapshot(
            active.Count,
            active.Count(t => t.CurrentOwnerEmployeeId == null),
            SlaAtRisk: 0,
            active.Count(t => t.SlaState == SlaState.Breached),
            // An unclassified ticket has no priority, so it is neither
            // Critical nor High — same explicit rule as TicketRepository.
            active.Count(t => t.PriorityId != null && t.PriorityId <= 2),
            visibleList.Count(t => t.TicketStatus == TicketStatus.PendingCustomer),
            resolvedToday,
            active.Count(t => t.ReopenCount > 0),
            active.Count(t => t.CurrentOwnerEmployeeId == query.CallerEmployeeId),
            attention));
    }
}

public sealed class FakeTicketRequesterSnapshotRepository : ITicketRequesterSnapshotRepository
{
    public List<TicketRequesterSnapshot> Added { get; } = [];

    public Task AddAsync(TicketRequesterSnapshot snapshot, CancellationToken cancellationToken = default)
    {
        Added.Add(snapshot);
        return Task.CompletedTask;
    }

    /// <summary>Reads back from <see cref="Added"/>, so a test that creates a ticket through the real service sees the same snapshot the acknowledgement handler would.</summary>
    public Task<TicketRequesterSnapshot?> GetByTicketIdAsync(long ticketId, CancellationToken cancellationToken = default) =>
        Task.FromResult(Added.FirstOrDefault(s => s.TicketId == ticketId));
}

public sealed class FakeTicketStatusHistoryRepository : ITicketStatusHistoryRepository
{
    public List<TicketStatusHistory> Added { get; } = [];

    public Task AddAsync(TicketStatusHistory entry, CancellationToken cancellationToken = default)
    {
        Added.Add(entry);
        return Task.CompletedTask;
    }

    /// <summary>
    /// Mirrors the real repository's ordering — latest occurrence wins, ties
    /// broken by insertion order — so a ticket closed, reopened and closed
    /// again reports its latest closure here exactly as it would in SQL.
    /// </summary>
    public Task<TicketStatusHistory?> GetLatestTransitionIntoAsync(
        long ticketId, TicketStatusDimension dimension, byte newValue, CancellationToken cancellationToken = default) =>
        Task.FromResult(Added
            .Select((entry, index) => (entry, index))
            .Where(x => x.entry.TicketId == ticketId && x.entry.Dimension == dimension && x.entry.NewValue == newValue)
            .OrderByDescending(x => x.entry.OccurredAtUtc).ThenByDescending(x => x.index)
            .Select(x => x.entry)
            .FirstOrDefault());

    public Task<IReadOnlyDictionary<long, DateTime>> ListLatestTransitionMomentsAsync(
        IReadOnlyCollection<long> ticketIds,
        TicketStatusDimension dimension,
        byte newValue,
        CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyDictionary<long, DateTime>>(Added
            .Where(e => ticketIds.Contains(e.TicketId) && e.Dimension == dimension && e.NewValue == newValue)
            .GroupBy(e => e.TicketId)
            .ToDictionary(g => g.Key, g => g.Max(e => e.OccurredAtUtc)));

    public Task<IReadOnlyList<TicketStatusHistory>> ListByTicketIdAsync(
        long ticketId, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<TicketStatusHistory>>(
            [.. Added.Where(e => e.TicketId == ticketId).OrderBy(e => e.OccurredAtUtc)]);
}

public sealed class FakeTicketAssignmentRepository : ITicketAssignmentRepository
{
    public List<TicketAssignment> Added { get; } = [];

    public Task<TicketAssignment?> GetCurrentAsync(long ticketId, CancellationToken cancellationToken = default) =>
        Task.FromResult(Added.LastOrDefault(a => a.TicketId == ticketId && a.IsCurrent));

    public Task<bool> HasSupersededAssignmentAsync(long ticketId, Guid employeeId, CancellationToken cancellationToken = default) =>
        Task.FromResult(Added.Any(a => a.TicketId == ticketId && a.AssignedEmployeeId == employeeId && !a.IsCurrent));

    public Task<IReadOnlyList<TicketAssignment>> ListByTicketIdAsync(long ticketId, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<TicketAssignment>>([.. Added.Where(a => a.TicketId == ticketId).OrderBy(a => a.AssignedAtUtc)]);

    public Task AddAsync(TicketAssignment assignment, CancellationToken cancellationToken = default)
    {
        Added.Add(assignment);
        return Task.CompletedTask;
    }
}

public sealed class FakeTicketResolutionRepository : ITicketResolutionRepository
{
    public List<TicketResolution> Added { get; } = [];

    /// <summary>
    /// Behave like the real repository inside a unit of work: a row staged with AddAsync is NOT returned by a read until the work is saved
    /// (EF's query does not see Added entities, and the fakes never "save"). Off by default so existing tests keep their simpler model.
    /// </summary>
    public bool HideUnsavedRows { get; init; }

    public Task<TicketResolution?> GetCurrentAsync(long ticketId, CancellationToken cancellationToken = default) =>
        Task.FromResult(HideUnsavedRows ? null : Added.LastOrDefault(r => r.TicketId == ticketId && r.IsCurrent));

    public Task<IReadOnlyDictionary<long, TicketResolution>> ListCurrentByTicketIdsAsync(
        IReadOnlyCollection<long> ticketIds, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyDictionary<long, TicketResolution>>(
            Added.Where(r => r.IsCurrent && ticketIds.Contains(r.TicketId))
                .GroupBy(r => r.TicketId)
                .ToDictionary(g => g.Key, g => g.Last()));

    public Task AddAsync(TicketResolution resolution, CancellationToken cancellationToken = default)
    {
        Added.Add(resolution);
        return Task.CompletedTask;
    }
}

public sealed class FakeTicketNoteRepository : ITicketNoteRepository
{
    public List<TicketNote> Added { get; } = [];

    public Task AddAsync(TicketNote note, CancellationToken cancellationToken = default)
    {
        typeof(TicketNote).GetProperty(nameof(TicketNote.TicketNoteId))!.SetValue(note, Added.Count + 1L);
        Added.Add(note);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<TicketNote>> ListByTicketIdAsync(
        long ticketId, int page, int pageSize, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<TicketNote>>(
            Added.Where(n => n.TicketId == ticketId)
                .OrderByDescending(n => n.CreatedAtUtc)
                .Skip((page - 1) * pageSize)
                .Take(pageSize)
                .ToList());

    public Task<int> CountByTicketIdAsync(long ticketId, CancellationToken cancellationToken = default) =>
        Task.FromResult(Added.Count(n => n.TicketId == ticketId));
}

public sealed class FakeTicketingUnitOfWork : ITicketingUnitOfWork
{
    public int SaveChangesCallCount { get; private set; }

    /// <summary>
    /// Optional Outbox writer whose staged rows follow this unit of work's
    /// own fate, so an atomicity test can assert the real property: an Outbox
    /// message becomes durable only when the business transaction commits,
    /// and a rollback leaves none behind (ADR-0013).
    /// </summary>
    public TigerCS.Tests.Notifications.Fakes.FakeOutboxWriter? OutboxWriter { get; set; }

    public int TransactionsBegun { get; private set; }
    public int TransactionsCommitted { get; private set; }
    public int TransactionsRolledBack { get; private set; }

    /// <summary>1-based SaveChangesAsync call number on which to throw VerificationSessionConcurrentlyConsumedException — mirrors FakeCustomerVerificationUnitOfWork's ThrowDuplicateWriteExceptionOnCall. Null = never.</summary>
    public int? ThrowConcurrencyConflictOnCall { get; set; }

    /// <summary>1-based SaveChangesAsync call number on which to throw TicketConcurrentlyModifiedException (a lost RowVersion race on assignment/transfer/status/resolve/close/reconciliation). Null = never.</summary>
    public int? ThrowTicketConcurrencyConflictOnCall { get; set; }

    /// <summary>1-based SaveChangesAsync call number on which to throw DuplicateWriteException (a TicketNumber collision). Null = never.</summary>
    public int? ThrowDuplicateWriteExceptionOnCall { get; set; }

    public Task<ITicketingTransaction> BeginTransactionAsync(CancellationToken cancellationToken = default)
    {
        TransactionsBegun++;
        return Task.FromResult<ITicketingTransaction>(new FakeTicketingTransaction(this));
    }

    public Task SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        SaveChangesCallCount++;

        if (ThrowConcurrencyConflictOnCall == SaveChangesCallCount)
        {
            ThrowConcurrencyConflictOnCall = null;
            throw new VerificationSessionConcurrentlyConsumedException(new InvalidOperationException("Simulated concurrency conflict."));
        }

        if (ThrowTicketConcurrencyConflictOnCall == SaveChangesCallCount)
        {
            ThrowTicketConcurrencyConflictOnCall = null;
            throw new TicketConcurrentlyModifiedException(new InvalidOperationException("Simulated ticket RowVersion conflict."));
        }

        if (ThrowDuplicateWriteExceptionOnCall == SaveChangesCallCount)
        {
            ThrowDuplicateWriteExceptionOnCall = null;
            throw new DuplicateWriteException(new InvalidOperationException("Simulated unique-constraint violation."));
        }

        return Task.CompletedTask;
    }

    private sealed class FakeTicketingTransaction(FakeTicketingUnitOfWork owner) : ITicketingTransaction
    {
        private bool _resolved;

        public Task CommitAsync(CancellationToken cancellationToken = default)
        {
            owner.TransactionsCommitted++;
            owner.OutboxWriter?.Commit();
            _resolved = true;
            return Task.CompletedTask;
        }

        public Task RollbackAsync(CancellationToken cancellationToken = default)
        {
            owner.TransactionsRolledBack++;
            owner.OutboxWriter?.Rollback();
            _resolved = true;
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            // Standard transaction semantics: disposing without an explicit
            // Commit is a rollback (mirrors IDbContextTransaction).
            if (!_resolved)
            {
                owner.TransactionsRolledBack++;
                owner.OutboxWriter?.Rollback();
                _resolved = true;
            }

            return ValueTask.CompletedTask;
        }
    }
}

/// <summary>
/// A stand-in for the Dashboard Phase 1 read model in tests that exercise
/// the older KPI summary only. The real aggregates are proven against SQL
/// in <c>Ticketing.Dashboard.DashboardOverviewTests</c>; this stub simply
/// reports nothing.
/// </summary>
public sealed class FakeDashboardQueryRepository : IDashboardQueryRepository
{
    public Task<DashboardOverviewSnapshot> GetOverviewAsync(DashboardOverviewQuery query, CancellationToken cancellationToken = default) =>
        Task.FromResult(new DashboardOverviewSnapshot(0, 0, 0, 0, 0, 0, 0, [], [], [], [], [],
            Enum.GetValues<BacklogAgeBucket>().Select(b => new DashboardCountRow<BacklogAgeBucket>(b, b.ToString(), 0)).ToList(), []));

    public Task<DashboardFilterOptionsSnapshot> GetFilterOptionsAsync(
        IReadOnlyCollection<int>? scopeDepartmentIds, IReadOnlyCollection<int>? agentDepartmentIds,
        IReadOnlyCollection<int>? requestTypeDepartmentIds, CancellationToken cancellationToken = default) =>
        Task.FromResult(new DashboardFilterOptionsSnapshot([], [], [], [], []));
}
