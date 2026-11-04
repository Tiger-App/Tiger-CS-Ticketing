using TigerCS.Domain.Modules.Ticketing;

namespace TigerCS.Application.Modules.Ticketing.Abstractions;

public enum TicketSortBy
{
    CreatedAtUtc,
    Priority
}

/// <summary>
/// Server-computed query for the ticket queue (MVP-API-Contracts.md §3.2).
/// <see cref="VisibleDepartmentIds"/> is never client-supplied — the
/// application service resolves it from the caller's role/department
/// membership before this query is built, so department visibility is
/// enforced at the query itself, not filtered out after the fact.
/// </summary>
/// <remarks>
/// Dashboard Phase 1 adds the drill-down filters the Operational Dashboard
/// needs so every KPI/bar-row links into this same queue rather than a
/// one-off page. Each is optional and combines with AND like the rest:
/// <list type="bullet">
/// <item><see cref="ChannelId"/> — the ticket's originating interaction channel.</item>
/// <item><see cref="RequestTypeId"/> — the ticket's request type.</item>
/// <item><see cref="ActiveOnly"/> — any active status (Open/InProgress/PendingCustomer, plus the legacy PendingThirdParty, which is still unfinished work).</item>
/// <item><see cref="InDepartmentQueue"/> — active, with no current owner (it sits in its department's queue).</item>
/// <item><see cref="SlaBreached"/> — active, SlaState Breached.</item>
/// <item><see cref="DueToday"/> — active, current SLA period's unbreached resolution deadline falls on <see cref="NowUtc"/>'s UTC calendar day.</item>
/// <item><see cref="BacklogAge"/> — active, aged (from CreatedAtUtc to <see cref="NowUtc"/>) into that bucket.</item>
/// <item><see cref="PendingApprovalFor"/> — carries a current Pending approval this caller is authorized to action.</item>
/// <item><see cref="CreatedFromUtc"/>/<see cref="CreatedToUtc"/> — created within [from, to).</item>
/// <item><see cref="Unclassified"/> — no request type has been set on the ticket.</item>
/// <item><see cref="ClosureCategory"/> — Closed tickets in one Dashboard closure KPI (Reviewed, NotReviewed, ClosedByAi, ClosedByAgent). <see cref="ReviewsAreTest"/> says which kind of review counts as feedback: Test-mode reviews while customer notifications run in test mode, real ones otherwise.</item>
/// <item><see cref="ClosedFromUtc"/>/<see cref="ClosedToUtc"/> — Closed tickets whose latest closure falls within [from, to).</item>
/// </list>
/// <see cref="CreatedByEmployeeId"/> ("Created by Me") narrows the already-scoped result to tickets that employee created; the application service sets it from the caller, never from the request. A restricted CS Agent always sees their own created tickets, whatever department they now belong to.
/// <see cref="NowUtc"/> is the evaluation instant for the time-relative filters — supplied by the application service, never read from the clock inside the repository.
///
/// <para>
/// <see cref="AgentScopeEmployeeId"/> is set only for a restricted CS Agent (<c>TicketVisibilityRule.IsRestrictedCsAgent</c>): the ticket
/// must then be assigned to that employee, or one they handled on Genesys, <i>or</i> in <see cref="VisibleDepartmentIds"/> (empty for a CS
/// Agent). Like the department scope it is resolved by the application service from the caller, never client-supplied.
/// <see cref="CreatedOnlyScope"/> (the Genesys integration account) narrows that scope to tickets <i>created by</i> <see cref="AgentScopeEmployeeId"/> only — not owned or handled ones.
/// </para>
/// </remarks>
public sealed record TicketQuery(
    IReadOnlyCollection<int>? VisibleDepartmentIds,
    int? DepartmentId,
    int? CategoryId,
    byte? PriorityId,
    TicketStatus? TicketStatus,
    CrmVerificationStatus? VerificationStatus,
    Guid? OwnerEmployeeId,
    string? Search,
    TicketSortBy SortBy,
    bool SortDescending,
    int Page,
    int PageSize,
    byte? ChannelId = null,
    int? RequestTypeId = null,
    bool ActiveOnly = false,
    bool InDepartmentQueue = false,
    bool SlaBreached = false,
    bool DueToday = false,
    BacklogAgeBucket? BacklogAge = null,
    ApprovalApproverScope? PendingApprovalFor = null,
    DateTime? CreatedFromUtc = null,
    DateTime? CreatedToUtc = null,
    DateTime? NowUtc = null,
    bool Unclassified = false,
    TicketClosureCategory? ClosureCategory = null,
    DateTime? ClosedFromUtc = null,
    DateTime? ClosedToUtc = null,
    bool ReviewsAreTest = false,
    Guid? AgentScopeEmployeeId = null,
    Guid? CreatedByEmployeeId = null,
    bool Escalated = false,
    bool CreatedOnlyScope = false,
    Guid? OwnerVisibilityEmployeeId = null,
    bool IncludeClaimable = false,
    bool AvailableToTakeOnly = false,
    bool DepartmentFilterIncludesClaimable = false,
    bool IncludeGenesysIntake = false);

public sealed record TicketQueryResult(IReadOnlyList<Ticket> Items, int TotalCount);

/// <summary>
/// Customer History (Customer → Previous Ticket History, this increment).
/// Exactly one of <see cref="CrmBuyerCustomerId"/>/<see cref="TicketIds"/> is
/// ever meaningful — the CRM-verified identity (<c>CrmBuyerCustomerId</c>)
/// and the phone-snapshot fallback identity are never combined into one
/// query (see <c>CustomerHistoryAppService</c>'s remarks for why: a phone
/// number is not a trusted unique customer identity, so it must never widen
/// a verified customer's own history). <see cref="TicketIds"/> is the
/// fallback path's pre-resolved set — the ticket ids linked from an
/// IntakeRecord recorded against the same phone number
/// (<c>IIntakeRecordRepository.ListLinkedTicketIdsByPhoneNumberAsync</c>) —
/// kept out of this repository so it never needs to join across the
/// IntakeRecord aggregate itself.
/// </summary>
/// <remarks>
/// The Customer Workspace phase adds a third identity:
/// <see cref="ExternalSource"/> + <see cref="ExternalCustomerId"/> — the
/// persisted external verification identity a PACT/Tasleeh ticket carries
/// (<c>Ticket.CustomerVerificationSource</c>/<c>Ticket.ExternalCustomerId</c>).
/// Exactly one of the three identities is ever set per query; the external
/// pair always travels together, and external history is never matched by
/// display name or phone number — two customers sharing a phone must never
/// share a history.
///
/// <para>
/// Duplicate-ticket awareness (Phase E) adds two refinements, both applied
/// inside this same single query — never a per-row follow-up:
/// <see cref="UnitNumber"/> narrows the identity's history to one unit
/// (exact match on the ticket's CRM Buyer or manual unit-number snapshot —
/// deterministic, no fuzzy matching), and <see cref="OrderActiveFirst"/>
/// sorts currently-active tickets ahead of Resolved/Closed ones (newest
/// first within each group), so the most actionable related tickets
/// surface within the limit.
/// </para>
/// </remarks>
public sealed record CustomerHistoryQuery(
    IReadOnlyCollection<int>? VisibleDepartmentIds,
    int? CrmBuyerCustomerId,
    IReadOnlyCollection<long>? TicketIds,
    long? ExcludeTicketId,
    int Limit,
    string? ExternalSource = null,
    string? ExternalCustomerId = null,
    string? UnitNumber = null,
    bool OrderActiveFirst = false,
    bool IncludeLinkedExternalIdentity = false,
    Guid? AgentScopeEmployeeId = null,
    Guid? OwnerVisibilityEmployeeId = null);

/// <summary>
/// <see cref="Tickets"/> is the newest-first page (bounded by
/// <see cref="CustomerHistoryQuery.Limit"/>); <see cref="TotalCount"/>/
/// <see cref="OpenCount"/>/<see cref="ClosedCount"/> are computed over every
/// matching row, not just the returned page — "Total Tickets: 6, Open: 2,
/// Closed: 4" must count the customer's whole history even when only the 5
/// most recent rows are shown.
/// </summary>
public sealed record CustomerHistoryQueryResult(
    IReadOnlyList<Ticket> Tickets, int TotalCount, int OpenCount, int ClosedCount);

/// <summary>
/// The Dashboard's one aggregate read (Customer Workspace phase).
/// <see cref="VisibleDepartmentIds"/> follows <see cref="TicketQuery"/>'s
/// rule exactly: resolved server-side from the caller's roles/department
/// membership, never client-supplied — every count and every attention row
/// is scoped by it at the query itself. "Active" throughout means
/// TicketStatus Open/InProgress/PendingCustomer, plus the legacy
/// PendingThirdParty — retired as a target, but still unfinished work.
/// </summary>
/// <param name="VisibleDepartmentIds">Null = no restriction (cross-department view role); otherwise the caller's own departments.</param>
/// <param name="CallerEmployeeId">For the My Tickets count.</param>
/// <param name="NowUtc">The evaluation instant — passed in so the query is deterministic under test.</param>
/// <param name="ResolvedTodayStartUtc">Start of "today" for the Resolved Today count, in UTC.</param>
/// <param name="AtRiskWindow">How far ahead of a pending SLA deadline counts as "at risk".</param>
/// <param name="AttentionLimit">Maximum attention rows to return.</param>
/// <param name="AgentScopeEmployeeId">Set only for a restricted CS Agent: every count and attention row is limited to tickets assigned to, or handled on Genesys by, that employee (plus <paramref name="VisibleDepartmentIds"/>, empty for a CS Agent).</param>
/// <param name="CreatedOnlyScope">The Genesys integration account: with <paramref name="AgentScopeEmployeeId"/>, only tickets that employee created.</param>
/// <param name="IncludeGenesysIntake">An eligible Call Center CS Agent: also include the unassigned Genesys-originated tickets of the CS intake / classification queue (<c>TicketQueryFilters.GenesysIntake</c>).</param>
public sealed record DashboardSnapshotQuery(
    IReadOnlyCollection<int>? VisibleDepartmentIds,
    Guid CallerEmployeeId,
    DateTime NowUtc,
    DateTime ResolvedTodayStartUtc,
    TimeSpan AtRiskWindow,
    int AttentionLimit,
    Guid? AgentScopeEmployeeId = null,
    bool CreatedOnlyScope = false,
    bool IncludeGenesysIntake = false);

/// <summary>One "requiring attention" row: the ticket plus its current SLA period's pending resolution deadline (null when no current period or the resolution clock already breached).</summary>
public sealed record DashboardAttentionTicket(Ticket Ticket, DateTime? SlaDueAtUtc);

/// <summary>
/// Every dashboard count, computed server-side over the caller's visible
/// scope — never assembled from per-status queue pages. Counts that depend
/// on data the caller's scope cannot see are simply smaller; nothing is
/// ever fabricated.
/// </summary>
public sealed record DashboardSnapshot(
    int OpenTickets,
    int Unassigned,
    int SlaAtRisk,
    int SlaBreached,
    int CriticalOrHigh,
    int PendingCustomer,
    int ResolvedToday,
    int Reopened,
    int MyTickets,
    IReadOnlyList<DashboardAttentionTicket> AttentionTickets);

public interface ITicketRepository
{
    Task<Ticket?> GetByIdAsync(long ticketId, CancellationToken cancellationToken = default);

    Task AddAsync(Ticket ticket, CancellationToken cancellationToken = default);

    /// <summary>
    /// Count of existing tickets whose TicketNumber already starts with
    /// <paramref name="ticketNumberPrefix"/> (e.g. "TG-CS-20260820-") — used
    /// to compute the next per-department-per-day sequence segment
    /// (FR-TKT-01). The unique index on TicketNumber remains the actual
    /// correctness backstop under concurrency (TicketingUnitOfWork retries
    /// on a collision); this count only picks a good first guess.
    /// </summary>
    Task<int> CountByTicketNumberPrefixAsync(string ticketNumberPrefix, CancellationToken cancellationToken = default);

    /// <summary>The ticket queue (MVP-API-Contracts.md §3.2) — paginated, sorted, department-visibility-scoped.</summary>
    Task<TicketQueryResult> SearchAsync(TicketQuery query, CancellationToken cancellationToken = default);

    /// <summary>
    /// Customer History — every ticket for one customer identity (Section
    /// "Customer → Previous Ticket History", this increment), newest first,
    /// filtered and limited entirely in the query (never loaded in full and
    /// filtered in memory). <see cref="CustomerHistoryQuery.VisibleDepartmentIds"/>
    /// applies the exact same department-visibility scoping as
    /// <see cref="SearchAsync"/> — never client-supplied.
    /// </summary>
    Task<CustomerHistoryQueryResult> SearchCustomerHistoryAsync(CustomerHistoryQuery query, CancellationToken cancellationToken = default);

    /// <summary>
    /// The Dashboard's operational counts and Tickets Requiring Attention
    /// rows, in one scoped aggregate read — never assembled client-side from
    /// per-status queue pages. Same department-visibility discipline as
    /// <see cref="SearchAsync"/>.
    /// </summary>
    Task<DashboardSnapshot> GetDashboardSnapshotAsync(DashboardSnapshotQuery query, CancellationToken cancellationToken = default);

    /// <summary>
    /// Primes EF Core's concurrency check: the client-supplied `If-Match`
    /// value becomes the tracked entity's "original" RowVersion, so
    /// SaveChanges' generated UPDATE includes `WHERE RowVersion = @clientValue`
    /// and fails with <c>DbUpdateConcurrencyException</c>
    /// if another request already changed the row. Must be called before any
    /// mutation on a ticket fetched for an assignment/transfer/status/
    /// resolve/close/reconciliation write.
    /// </summary>
    void SetRowVersion(Ticket ticket, byte[] rowVersion);
}
