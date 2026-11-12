namespace TigerCS.Application.Modules.Ticketing.Dto;

/// <summary>
/// The Dashboard's operational summary (<c>GET /api/dashboard</c>): concise
/// KPI counts plus the Tickets Requiring Attention rows, all computed
/// server-side over the caller's own visible-department scope — a
/// department user's numbers cover their departments only, CS-layer roles
/// see across departments, exactly per the existing view authorization.
/// Every count is derived from real ticket/SLA/resolution data; nothing
/// here is estimated or fabricated.
/// </summary>
/// <param name="OpenTickets">Active tickets: Open, InProgress, PendingCustomer — plus any historical ticket still sitting in the legacy PendingThirdParty status, which is non-terminal and therefore still work in hand.</param>
/// <param name="Unassigned">Active tickets with no current owner.</param>
/// <param name="SlaAtRisk">Active tickets with a pending, unbreached SLA deadline due within the at-risk window.</param>
/// <param name="SlaBreached">Active tickets whose SlaState is Breached.</param>
/// <param name="CriticalOrHigh">Active tickets at priority Critical or High.</param>
/// <param name="PendingCustomer">Tickets currently in PendingCustomer.</param>
/// <param name="ResolvedToday">Tickets whose current resolution was recorded today (UTC day).</param>
/// <param name="Reopened">Active tickets that have been reopened at least once.</param>
/// <param name="MyTickets">Tickets currently assigned to the caller, in any status.</param>
/// <param name="AttentionTickets">The Tickets Requiring Attention rows — breached, due soon, critical/high, or unassigned — most urgent first.</param>
public sealed record DashboardSummaryDto(
    int OpenTickets,
    int Unassigned,
    int SlaAtRisk,
    int SlaBreached,
    int CriticalOrHigh,
    int PendingCustomer,
    int ResolvedToday,
    int Reopened,
    int MyTickets,
    IReadOnlyList<DashboardAttentionTicketDto> AttentionTickets);

/// <summary>
/// One Tickets Requiring Attention row — deliberately compact: the ticket's
/// one-line request summary, never a long description, and display
/// snapshots rather than raw external ids.
/// </summary>
/// <param name="TicketId">Links to Ticket Details.</param>
/// <param name="TicketNumber">The human-facing ticket number.</param>
/// <param name="CustomerName">The ticket-time customer name snapshot, when one exists (CRM Buyer tickets).</param>
/// <param name="UnitNumber">The unit number snapshot (CRM Buyer or manual), when one exists.</param>
/// <param name="PriorityId">1=Critical, 2=High, 3=Medium, 4=Low, or null while the ticket is Unclassified.</param>
/// <param name="TicketStatus">One of Open, InProgress, PendingCustomer — or the legacy PendingThirdParty on a historical ticket.</param>
/// <param name="SlaState">One of Running, Paused, Met, Breached, NotApplicable.</param>
/// <param name="SlaDueAtUtc">The current SLA period's pending resolution deadline, when one exists.</param>
/// <param name="CurrentOwnerEmployeeId">The current owner, or null when unassigned.</param>
/// <param name="CurrentDepartmentId">The department that currently holds the ticket.</param>
/// <param name="RequestSummary">The request, in the agent's words — rendered as a single scannable line.</param>
/// <param name="CreatedAtUtc">When the ticket was created, in UTC.</param>
public sealed record DashboardAttentionTicketDto(
    long TicketId,
    string TicketNumber,
    string? CustomerName,
    string? UnitNumber,
    byte? PriorityId,
    string TicketStatus,
    string SlaState,
    DateTime? SlaDueAtUtc,
    Guid? CurrentOwnerEmployeeId,
    int CurrentDepartmentId,
    string RequestSummary,
    DateTime CreatedAtUtc);

// ---- Operational Dashboard (Dashboard Phase 1): GET /api/dashboard/overview ----

/// <summary>
/// The Operational Dashboard's filters, bound from the query string. Every
/// filter is optional. The department filter can only narrow the caller's
/// visible scope, never widen it; the agent filter is an existing Ticketing
/// user's employee id (the ticket's current owner). Dates are UTC calendar
/// days — the same "UTC day" convention the dashboard already uses for
/// today — and default to the last 30 days when both are omitted.
/// </summary>
/// <param name="DateFrom">First UTC calendar day of the volume date range (inclusive).</param>
/// <param name="DateTo">Last UTC calendar day of the volume date range (inclusive).</param>
/// <param name="DepartmentId">Narrow to one department the caller may already see.</param>
/// <param name="OwnerEmployeeId">Narrow to tickets currently owned by one agent.</param>
/// <param name="ChannelId">Narrow to tickets whose originating interaction arrived on one channel.</param>
/// <param name="RequestTypeId">Narrow to one request type.</param>
/// <param name="TicketStatus">Narrow to one status: Open, InProgress, PendingCustomer, Resolved, Closed. The retired PendingThirdParty is no longer an offered filter, though a historical ticket still reports it as its own status.</param>
/// <param name="PriorityId">Narrow to one priority: 1=Critical, 2=High, 3=Medium, 4=Low.</param>
public sealed record DashboardOverviewRequestDto(
    DateOnly? DateFrom = null,
    DateOnly? DateTo = null,
    int? DepartmentId = null,
    Guid? OwnerEmployeeId = null,
    byte? ChannelId = null,
    int? RequestTypeId = null,
    string? TicketStatus = null,
    byte? PriorityId = null);

/// <summary>
/// The Operational Dashboard (<c>GET /api/dashboard/overview</c>): KPI
/// cards, the six operational breakdowns and the Recent/Critical list,
/// every number computed server-side over the caller's own visible
/// scope. KPIs, backlog ageing and the recent list are current-state
/// (all active tickets in scope, whatever their age); the volume
/// breakdowns cover tickets created within the effective date range.
/// </summary>
/// <param name="Filters">The filters as actually applied — including the defaulted date range and the effective department scope.</param>
/// <param name="Kpis">The six KPI counts.</param>
/// <param name="VolumeTotal">Tickets created in the date range, in scope — the denominator of the volume breakdowns.</param>
/// <param name="VolumeByChannel">Volume by originating channel (null id = no originating channel recorded).</param>
/// <param name="VolumeByRequestType">Volume by request type (null id = no request type).</param>
/// <param name="VolumeByDepartment">Volume by current responsible department (CurrentDepartmentId).</param>
/// <param name="StatusBreakdown">Volume by ticket status.</param>
/// <param name="PriorityBreakdown">Volume by priority (null id = Unclassified).</param>
/// <param name="BacklogAgeing">Active tickets by age bucket; percentages are of <see cref="DashboardKpisDto.OpenTickets"/>.</param>
/// <param name="RecentTickets">The Recent/Critical rows, most urgent first.</param>
/// <param name="FilterOptions">The picker options, restricted to the caller's scope.</param>
public sealed record DashboardOverviewDto(
    DashboardAppliedFiltersDto Filters,
    DashboardKpisDto Kpis,
    int VolumeTotal,
    IReadOnlyList<DashboardBreakdownItemDto> VolumeByChannel,
    IReadOnlyList<DashboardBreakdownItemDto> VolumeByRequestType,
    IReadOnlyList<DashboardBreakdownItemDto> VolumeByDepartment,
    IReadOnlyList<DashboardBreakdownItemDto> StatusBreakdown,
    IReadOnlyList<DashboardBreakdownItemDto> PriorityBreakdown,
    IReadOnlyList<DashboardBreakdownItemDto> BacklogAgeing,
    IReadOnlyList<DashboardRecentTicketDto> RecentTickets,
    DashboardFilterOptionsDto FilterOptions);

/// <summary>The filters as applied, echoed back so the page can render the effective state (e.g. the defaulted date range).</summary>
public sealed record DashboardAppliedFiltersDto(
    DateOnly DateFrom,
    DateOnly DateTo,
    int? DepartmentId,
    Guid? OwnerEmployeeId,
    byte? ChannelId,
    int? RequestTypeId,
    string? TicketStatus,
    byte? PriorityId);

/// <summary>The KPI cards. All are current-state counts over active tickets in scope, except Pending Approval, which counts tickets (any status but the approval must be Pending) the caller may action, and the Awaiting Human Agent trio, which counts pending human work rather than tickets.</summary>
/// <param name="OpenTickets">Active tickets: Open, InProgress, PendingCustomer — plus any historical ticket still sitting in the legacy PendingThirdParty status, which is non-terminal and therefore still work in hand.</param>
/// <param name="MyTickets">Tickets (any status) whose CurrentOwnerEmployeeId is the caller.</param>
/// <param name="InDepartmentQueue">Active tickets with no current owner — queued to their responsible department.</param>
/// <param name="SlaBreached">Active tickets whose SlaState is Breached.</param>
/// <param name="DueToday">Active tickets whose current, unbreached SLA resolution deadline falls today (UTC calendar day).</param>
/// <param name="PendingApproval">Tickets carrying a Pending approval the caller is authorized to action.</param>
/// <param name="AwaitingHumanAgent">
/// Customer interactions waiting for a human to take them
/// (<c>AgentHandoffStatus.WaitingForAgent</c>), in the departments the caller
/// can see. Counts <b>work items, not tickets</b>: one ticket can carry
/// several conversations over its life, and what this measures is people
/// waiting to be spoken to.
/// </param>
/// <param name="OldestHumanWaitSeconds">
/// How long the longest-waiting of those has waited, in seconds — the number
/// that makes the queue's SLA risk legible. Zero when nothing is waiting.
/// </param>
/// <param name="HumanWaitRiskThresholdSeconds">
/// The age at which a waiting interaction is rendered as at risk. Served with
/// the data so the UI never hard-codes its own threshold, and configurable
/// through <c>Dashboard:HumanWaitRiskThresholdSeconds</c>.
/// <b>Not an SLA.</b> Human Wait is an operational KPI: it raises no breach
/// row, no escalation and no <c>SlaDeadlineType</c>.
/// </param>
/// <param name="ReviewedTickets">Distinct Closed tickets whose current cycle has a customer review submitted (Test-mode reviews excluded). Closed within the dashboard date range (latest closure date).</param>
/// <param name="NotReviewedTickets">Distinct Closed tickets with no customer review for their current cycle. Same range (latest closure date).</param>
/// <param name="ClosedByAiTickets">Distinct Closed tickets whose latest closure was audited as <c>AutoCloseByAi</c>. Same range (latest closure date).</param>
/// <param name="ReviewsAreTestMode">True while customer notifications run in test mode: Reviewed / Not Reviewed then evaluate Test-mode reviews (so UAT can validate them); production satisfaction statistics never include those.</param>
/// <param name="AverageReviewRating">Mean customer rating (1-5 scale, one decimal) over exactly the <c>ReviewedTickets</c>; null when there are none.</param>
/// <param name="ClosedByAgentTickets">Distinct Closed tickets whose latest closure was audited as <c>Close</c> (a person). Same range (latest closure date).</param>
public sealed record DashboardKpisDto(
    int OpenTickets,
    int MyTickets,
    int InDepartmentQueue,
    int SlaBreached,
    int DueToday,
    int PendingApproval,
    int AwaitingHumanAgent = 0,
    int OldestHumanWaitSeconds = 0,
    int HumanWaitRiskThresholdSeconds = 900,
    int ReviewedTickets = 0,
    int NotReviewedTickets = 0,
    int ClosedByAiTickets = 0,
    int ClosedByAgentTickets = 0,
    bool ReviewsAreTestMode = false,
    double? AverageReviewRating = null);

/// <summary>One bar row of a breakdown widget.</summary>
/// <param name="Key">The dimension value the drill-down filters on (a channel/request type/department/priority id, a status name, or an age bucket name); null when the value is not recorded on the ticket, in which case the row is informational and not a drill-down.</param>
/// <param name="Label">The display label — a real name from the master data, never a raw id.</param>
/// <param name="Count">The count.</param>
/// <param name="Percentage">Count as a percentage of the widget's total, rounded to one decimal; 0 when the total is 0.</param>
/// <param name="Code">The stable master-data code where the dimension has one (a channel's code), else null — lets the UI choose a glyph without comparing display names.</param>
public sealed record DashboardBreakdownItemDto(string? Key, string Label, int Count, double Percentage, string? Code = null);

/// <summary>
/// One Recent/Critical row — compact operational facts only, never the ticket description.
/// <c>ChannelId/ChannelName/ChannelCode</c> are the channel of the ticket's originating interaction (null when none is recorded).
/// <c>LastActionByName</c> is who performed the most recent meaningful action on the ticket — a person's name, or "AI Assistant" for the
/// virtual agent — and null when no meaningful action is recorded (background/system activity never counts).
/// </summary>
public sealed record DashboardRecentTicketDto(
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
    string TicketStatus,
    string SlaState,
    DateTime? SlaDueAtUtc,
    Guid? CurrentOwnerEmployeeId,
    string? OwnerName,
    DateTime CreatedAtUtc,
    string? DepartmentCode = null,
    byte? ChannelId = null,
    string? ChannelName = null,
    string? ChannelCode = null,
    string? LastActionByName = null);

/// <summary>A filter picker option. <see cref="GroupId"/> is the owning department for request types; <see cref="IsActive"/> false marks a retired channel kept for historical filtering.</summary>
public sealed record DashboardFilterOptionDto(string Id, string Label, int? GroupId = null, bool IsActive = true);

/// <summary>The filter pickers' options, from the existing master tables and restricted to the caller's scope — never hard-coded.</summary>
public sealed record DashboardFilterOptionsDto(
    IReadOnlyList<DashboardFilterOptionDto> Departments,
    IReadOnlyList<DashboardFilterOptionDto> Agents,
    IReadOnlyList<DashboardFilterOptionDto> Channels,
    IReadOnlyList<DashboardFilterOptionDto> RequestTypes,
    IReadOnlyList<DashboardFilterOptionDto> Statuses,
    IReadOnlyList<DashboardFilterOptionDto> Priorities);
