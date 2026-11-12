using TigerCS.Domain.Modules.Ticketing;

namespace TigerCS.Application.Modules.Ticketing.Abstractions;

/// <summary>
/// The Operational Dashboard's one read (Dashboard Phase 1). Follows the
/// exact scoping discipline of <see cref="TicketQuery"/>:
/// <see cref="ScopeDepartmentIds"/> is resolved server-side by the
/// application service from the caller's roles/department membership,
/// already intersected with any requested department filter — it is never
/// client-supplied and a filter can only narrow it, never widen it. Null
/// means no department restriction (cross-department view role); an empty
/// set means "nothing visible" and every count comes back zero.
///
/// <para>
/// <b>Two time contexts, deliberately distinct.</b> KPIs, Open Backlog
/// Ageing and the Recent/Critical list are <i>current-state</i>: they cover
/// every active ticket in scope regardless of when it was created, so an
/// old open ticket is never hidden by a recent date range. The volume
/// breakdowns (channel, request type, department, status, priority) are
/// <i>historical</i>: tickets created within
/// [<see cref="RangeStartUtc"/>, <see cref="RangeEndUtc"/>). The dimension
/// filters (department, agent, channel, request type, status, priority)
/// apply to both.
/// </para>
///
/// <para>
/// The four closure KPIs (Reviewed, Not Reviewed, Closed by AI, Closed by
/// Agent) are the exception to "KPIs are current-state": they count Closed
/// tickets, which are by definition not part of the active backlog, so they
/// follow the date range applied to the ticket's <b>latest closure date</b>
/// ([<see cref="RangeStartUtc"/>, <see cref="RangeEndUtc"/>)) and the same
/// dimension filters — except the status filter: they always represent
/// Closed tickets, whatever status the Dashboard has selected.
/// </para>
/// </summary>
/// <param name="ScopeDepartmentIds">Null = no restriction; otherwise the departments the counts may cover.</param>
/// <param name="CallerEmployeeId">For the My Tickets count.</param>
/// <param name="ApproverScope">For the Pending Approval count — approvals the caller is authorized to action.</param>
/// <param name="OwnerEmployeeId">Agent filter: tickets currently owned by this employee.</param>
/// <param name="ChannelId">Channel filter: the ticket's originating interaction channel.</param>
/// <param name="RequestTypeId">Request type filter.</param>
/// <param name="TicketStatus">Status filter.</param>
/// <param name="PriorityId">Priority filter.</param>
/// <param name="RangeStartUtc">Inclusive start of the volume date range.</param>
/// <param name="RangeEndUtc">Exclusive end of the volume date range.</param>
/// <param name="NowUtc">The evaluation instant — passed in so the query is deterministic under test.</param>
/// <param name="TodayStartUtc">Start of "today" (UTC calendar day) for Due Today.</param>
/// <param name="AtRiskWindow">How far ahead of a pending SLA deadline ranks as "due soon" in the Recent/Critical list.</param>
/// <param name="RecentLimit">Maximum Recent/Critical rows.</param>
/// <param name="AgentScopeEmployeeId">Set only for a restricted CS Agent: every count and list is then limited to tickets assigned to, or handled on Genesys by, that employee (plus <paramref name="ScopeDepartmentIds"/>, empty for a CS Agent). See <c>TicketQuery</c>.</param>
/// <param name="DepartmentId">Department filter applied on top of the scope (the current department). Used when the scope is not itself a department set, i.e. for a restricted CS Agent; for everyone else the requested department is already folded into <paramref name="ScopeDepartmentIds"/>.</param>
/// <param name="ReviewsAreTest">Which reviews count as customer feedback for the Reviewed / Not Reviewed KPIs: Test-mode reviews while customer notifications run in test mode (UAT), real reviews otherwise. Production satisfaction statistics are unaffected.</param>
/// <param name="CreatedOnlyScope">The Genesys integration account: with <paramref name="AgentScopeEmployeeId"/>, only tickets that employee created.</param>
/// <param name="IncludeGenesysIntake">An eligible Call Center CS Agent: also include the unassigned Genesys-originated tickets of the CS intake / classification queue (<c>TicketQueryFilters.GenesysIntake</c>).</param>
/// <param name="ClosedAssigneeEmployeeId">Server-resolved from the authenticated caller, never client-supplied: when set (an employee, not a manager), the four closure KPIs count only Closed tickets assigned to this employee (current owner, or the last assignee of a Closed ticket).</param>
/// <param name="OwnerVisibilityEmployeeId">The caller: a department-scoped viewer additionally sees every ticket currently assigned to them, whatever department it is in now. Never client-supplied.</param>
public sealed record DashboardOverviewQuery(
    IReadOnlyCollection<int>? ScopeDepartmentIds,
    Guid CallerEmployeeId,
    ApprovalApproverScope ApproverScope,
    Guid? OwnerEmployeeId,
    byte? ChannelId,
    int? RequestTypeId,
    TicketStatus? TicketStatus,
    byte? PriorityId,
    DateTime RangeStartUtc,
    DateTime RangeEndUtc,
    DateTime NowUtc,
    DateTime TodayStartUtc,
    TimeSpan AtRiskWindow,
    int RecentLimit,
    bool ReviewsAreTest = false,
    Guid? AgentScopeEmployeeId = null,
    int? DepartmentId = null,
    bool CreatedOnlyScope = false,
    Guid? OwnerVisibilityEmployeeId = null,
    bool IncludeGenesysIntake = false,
    Guid? ClosedAssigneeEmployeeId = null);

/// <summary>One aggregated row: a dimension value (null key = the value is not recorded on the ticket, e.g. no originating channel, no request type, Unclassified priority) with its display label and count. <paramref name="Code"/> is the stable master-data code where the dimension has one (channels), so the UI can pick a glyph without comparing names.</summary>
public sealed record DashboardCountRow<TKey>(TKey? Key, string Label, int Count, string? Code = null) where TKey : struct;

/// <summary>One Recent/Critical row — display facts resolved by joins in the same query, never per row afterwards.</summary>
public sealed record DashboardRecentTicketRow(
    long TicketId,
    string TicketNumber,
    string? CustomerName,
    string? UnitNumber,
    string? ProjectName,
    int CurrentDepartmentId,
    string? DepartmentName,
    int? RequestTypeId,
    string? RequestTypeName,
    byte? PriorityId,
    TicketStatus TicketStatus,
    SlaState SlaState,
    DateTime? SlaDueAtUtc,
    Guid? CurrentOwnerEmployeeId,
    string? OwnerName,
    DateTime CreatedAtUtc,
    string? DepartmentCode = null,
    byte? ChannelId = null,
    string? ChannelName = null,
    string? ChannelCode = null,
    string? LastActionByName = null);

/// <summary>
/// Every dashboard number, computed SQL-side over the query's scope. Counts
/// that depend on data the scope cannot see are simply smaller; nothing is
/// ever fabricated. <see cref="VolumeTotal"/> is the denominator of the
/// volume breakdowns (every ticket created in range, in scope);
/// <see cref="OpenTickets"/> is the denominator of the ageing buckets.
/// </summary>
public sealed record DashboardOverviewSnapshot(
    int OpenTickets,
    int MyTickets,
    int InDepartmentQueue,
    int SlaBreached,
    int DueToday,
    int PendingApproval,
    int VolumeTotal,
    IReadOnlyList<DashboardCountRow<byte>> VolumeByChannel,
    IReadOnlyList<DashboardCountRow<int>> VolumeByRequestType,
    IReadOnlyList<DashboardCountRow<int>> VolumeByDepartment,
    IReadOnlyList<DashboardCountRow<TicketStatus>> StatusBreakdown,
    IReadOnlyList<DashboardCountRow<byte>> PriorityBreakdown,
    IReadOnlyList<DashboardCountRow<BacklogAgeBucket>> BacklogAgeing,
    IReadOnlyList<DashboardRecentTicketRow> RecentTickets,
    DashboardClosureCounts? Closure = null);

/// <summary>
/// The four closure KPIs (see <see cref="TicketClosureCategory"/>): distinct
/// Closed tickets in scope whose latest closure falls within the query's date range.
/// Reviewed + NotReviewed is every such closed ticket; ClosedByAi +
/// ClosedByAgent is at most that, because inactivity and document-delivery
/// closures are neither.
/// </summary>
/// <remarks><c>AverageReviewRating</c> is the mean 1-5 rating over exactly the Reviewed tickets (one review per ticket - the current cycle's, of the notification mode's kind); null when none.</remarks>
public sealed record DashboardClosureCounts(int Reviewed, int NotReviewed, int ClosedByAi, int ClosedByAgent, double? AverageReviewRating = null);

/// <summary>A filter option: the id the filter sends back and the label the picker shows. <see cref="GroupId"/> groups request types under their department.</summary>
public sealed record DashboardOptionRow<TKey>(TKey Id, string Label, int? GroupId = null, bool IsActive = true);

/// <summary>The master data the dashboard filters offer — read from the existing tables, restricted to the caller's scope.</summary>
public sealed record DashboardFilterOptionsSnapshot(
    IReadOnlyList<DashboardOptionRow<int>> Departments,
    IReadOnlyList<DashboardOptionRow<Guid>> Agents,
    IReadOnlyList<DashboardOptionRow<byte>> Channels,
    IReadOnlyList<DashboardOptionRow<int>> RequestTypes,
    IReadOnlyList<DashboardOptionRow<byte>> Priorities);

/// <summary>
/// The Operational Dashboard read model (Dashboard Phase 1) — read-only,
/// aggregated in the database (never "load every ticket and count in C#"),
/// scoped exactly like the ticket queue.
/// </summary>
public interface IDashboardQueryRepository
{
    Task<DashboardOverviewSnapshot> GetOverviewAsync(DashboardOverviewQuery query, CancellationToken cancellationToken = default);

    /// <summary>
    /// The filter pickers' options. Departments are the scope's departments
    /// (all active ones for a cross-department role); agents are the active
    /// employees assigned to <paramref name="agentDepartmentIds"/> (null =
    /// any department); channels include retired ones so historical volume
    /// stays filterable; request types are the active ones of
    /// <paramref name="requestTypeDepartmentIds"/> (null = every department).
    /// </summary>
    Task<DashboardFilterOptionsSnapshot> GetFilterOptionsAsync(
        IReadOnlyCollection<int>? scopeDepartmentIds,
        IReadOnlyCollection<int>? agentDepartmentIds,
        IReadOnlyCollection<int>? requestTypeDepartmentIds,
        CancellationToken cancellationToken = default);
}
