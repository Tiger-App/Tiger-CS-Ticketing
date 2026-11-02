using TigerCS.Application.Modules.Ticketing.Abstractions;
using TigerCS.Domain.Modules.Ticketing;

namespace TigerCS.Application.Modules.Ticketing.Dto;

// ---- Queries (MVP-API-Contracts.md §3.2/§3.3) ----

/// <summary>Filtering, sorting, and paging for the ticket queue (MVP-API-Contracts.md §3.2). Bound from the query string; every filter is optional and combines with AND.</summary>
/// <param name="DepartmentId">Narrow to one department. Cannot widen what the caller may already see.</param>
/// <param name="CategoryId">Narrow to one category.</param>
/// <param name="PriorityId">Narrow to one priority. 1=Critical, 2=High, 3=Medium, 4=Low.</param>
/// <param name="TicketStatus">Narrow to one status: Open, InProgress, PendingCustomer, Resolved, Closed. The retired PendingThirdParty is no longer an offered filter.</param>
/// <param name="VerificationStatus">Narrow to one verification status: Unverified, PendingCrmVerification, Verified.</param>
/// <param name="OwnerEmployeeId">Narrow to tickets currently owned by one employee.</param>
/// <param name="Search">Free-text search over the ticket number and request summary.</param>
/// <param name="SortBy">Which field to sort by.</param>
/// <param name="SortDir">Sort direction — asc or desc.</param>
/// <param name="Page">1-based page number.</param>
/// <param name="PageSize">Page size.</param>
/// <param name="ChannelId">Narrow to tickets whose originating interaction arrived on one channel (Dashboard drill-down).</param>
/// <param name="RequestTypeId">Narrow to one request type (Dashboard drill-down).</param>
/// <param name="ActiveOnly">True: only active tickets — Open, InProgress, PendingCustomer, and any historical ticket still in the legacy PendingThirdParty (the Dashboard's Open Tickets). A legacy pending ticket is non-terminal work and stays in the active set, so it never disappears from a queue before someone returns it to InProgress.</param>
/// <param name="InDepartmentQueue">True: only active tickets with no current owner — sitting in their department's queue.</param>
/// <param name="SlaBreached">True: only active tickets whose SLA state is Breached.</param>
/// <param name="DueToday">True: only active tickets whose current, unbreached SLA resolution deadline falls today (UTC calendar day).</param>
/// <param name="BacklogAge">Narrow active tickets to one age bucket by CreatedAtUtc: Under24Hours, OneToThreeDays, ThreeToSevenDays, OverSevenDays.</param>
/// <param name="PendingApproval">True: only tickets carrying a Pending approval the caller is authorized to action.</param>
/// <param name="CreatedFrom">Only tickets created on or after this UTC calendar day.</param>
/// <param name="CreatedTo">Only tickets created on or before this UTC calendar day.</param>
/// <param name="Unclassified">True: only tickets with no request type (nobody — human or Genesys AI — has classified them yet). A filter on the ticket queue only: it never implies, requests or creates a human handoff, and department visibility applies exactly as for every other filter.</param>
/// <param name="ClosureCategory">Only Closed tickets in one Dashboard closure KPI: Reviewed (customer feedback submitted), NotReviewed, ClosedByAi (latest closure audited as AutoCloseByAi) or ClosedByAgent (latest closure audited as Close).</param>
/// <param name="ClosedFrom">Only Closed tickets whose latest closure was on or after this UTC calendar day (Dashboard closure KPIs).</param>
/// <param name="ClosedTo">Only Closed tickets whose latest closure was on or before this UTC calendar day.</param>
/// <param name="CreatedByMe">True ("Created by Me"): only tickets the caller created — including ones that have since moved to another department, which a CS Agent keeps read-only access to. Never widens visibility: it only narrows the caller's own scope.</param>
/// <param name="Escalated">Supervisor review: only tickets an agent passed up as UnableToResolve or NeedsEscalation. Requires a supervisory role (CS Supervisor, CS Manager, Department Head, General Manager, Chairman/CEO); other callers get 403. Narrows the caller's normal visibility, never widens it.</param>
public sealed record TicketListRequestDto(
    int? DepartmentId,
    int? CategoryId,
    byte? PriorityId,
    string? TicketStatus,
    string? VerificationStatus,
    Guid? OwnerEmployeeId,
    string? Search,
    string? SortBy,
    string? SortDir,
    int Page,
    int PageSize,
    byte? ChannelId = null,
    int? RequestTypeId = null,
    bool? ActiveOnly = null,
    bool? InDepartmentQueue = null,
    bool? SlaBreached = null,
    bool? DueToday = null,
    string? BacklogAge = null,
    bool? PendingApproval = null,
    DateOnly? CreatedFrom = null,
    DateOnly? CreatedTo = null,
    bool? Unclassified = null,
    string? ClosureCategory = null,
    DateOnly? ClosedFrom = null,
    DateOnly? ClosedTo = null,
    bool? CreatedByMe = null,
    bool? Escalated = null);

/// <summary>One row of the ticket queue (MVP-API-Contracts.md §3.2).</summary>
/// <param name="TicketId">The ticket.</param>
/// <param name="TicketNumber">The human-facing ticket number.</param>
/// <param name="CurrentDepartmentId">The department that currently holds it.</param>
/// <param name="CurrentOwnerEmployeeId">The current owner, or null when unassigned.</param>
/// <param name="CategoryId">The ticket's category, or null while the ticket is Unclassified.</param>
/// <param name="PriorityId">1=Critical, 2=High, 3=Medium, 4=Low, or null while Unclassified.</param>
/// <param name="TicketStatus">One of Open, InProgress, PendingCustomer, Resolved, Closed — or the legacy, no-longer-reachable PendingThirdParty on a historical ticket.</param>
/// <param name="VerificationStatus">One of Unverified, PendingCrmVerification, Verified.</param>
/// <param name="RequestSummary">The request, in the agent's words.</param>
/// <param name="CreatedAtUtc">When the ticket was created, in UTC.</param>
public sealed record TicketSummaryDto(
    long TicketId,
    string TicketNumber,
    int CurrentDepartmentId,
    Guid? CurrentOwnerEmployeeId,
    int? CategoryId,
    byte? PriorityId,
    string TicketStatus,
    string VerificationStatus,
    string RequestSummary,
    DateTime CreatedAtUtc);

/// <summary>One page of the ticket queue.</summary>
/// <param name="Items">This page's tickets.</param>
/// <param name="TotalCount">How many tickets match the filters in total.</param>
/// <param name="Page">The 1-based page number that was served.</param>
/// <param name="PageSize">The page size that was served.</param>
public sealed record TicketListResultDto(IReadOnlyList<TicketSummaryDto> Items, int TotalCount, int Page, int PageSize);

/// <summary>Full ticket detail (MVP-API-Contracts.md §3.3), and the response of every ticket write.</summary>
/// <param name="TicketId">The ticket.</param>
/// <param name="TicketNumber">The human-facing ticket number.</param>
/// <param name="OriginatingDepartmentId">The department the ticket was raised in.</param>
/// <param name="CurrentDepartmentId">The department that currently holds it.</param>
/// <param name="CurrentOwnerEmployeeId">The current owner, or null when unassigned — including immediately after a transfer.</param>
/// <param name="UnitReferenceId">The verified unit, or null while the ticket is provisional.</param>
/// <param name="ContactReferenceId">The verified contact, or null while the ticket is provisional.</param>
/// <param name="CategoryId">The ticket's category, or null while the ticket is Unclassified — no category is ever invented to fill it.</param>
/// <param name="PriorityId">1=Critical, 2=High, 3=Medium, 4=Low, or null while Unclassified: no priority has been judged, so none is shown and none selects an SLA policy.</param>
/// <param name="TicketStatus">One of Open, InProgress, PendingCustomer, Resolved, Closed — or the legacy, no-longer-reachable PendingThirdParty on a historical ticket.</param>
/// <param name="VerificationStatus">One of Unverified, PendingCrmVerification, Verified.</param>
/// <param name="EscalationLevel">One of None, Level1, Level2, Level3, Level4.</param>
/// <param name="SlaState">One of Running, Paused, Met, Breached, NotApplicable.</param>
/// <param name="ResolutionOutcome">Set once the ticket is resolved. 1=Resolved, 2=Cancelled, 3=Rejected, 4=Duplicate.</param>
/// <param name="DuplicateOfTicketId">The ticket this one duplicates, when the outcome is Duplicate.</param>
/// <param name="RequestSummary">The request, in the agent's words.</param>
/// <param name="ReopenCount">How many times the ticket has been reopened.</param>
/// <param name="CreatedAtUtc">When the ticket was created, in UTC.</param>
/// <param name="RowVersion">The concurrency token, Base64-encoded. Send it back on the next write to this ticket.</param>
/// <param name="CrmBuyerCustomerId">The real CRM Buyer Lookup match's customer id, or null when no CRM Buyer match was linked at creation.</param>
/// <param name="CrmBuyerLeadId">The matched CRM Lead id, or null.</param>
/// <param name="CrmBuyerUnitId">The matched CRM unit id, or null.</param>
/// <param name="CrmBuyerProjectId">The matched CRM project id, or null.</param>
/// <param name="CrmBuyerCustomerName">Ticket-time display snapshot of the matched Buyer's name, or null.</param>
/// <param name="CrmBuyerProjectName">Ticket-time display snapshot of the matched unit's project name, or null.</param>
/// <param name="CrmBuyerUnitNumber">Ticket-time display snapshot of the matched unit's number, or null.</param>
/// <param name="ManualProjectName">The agent-entered Project name, when no CRM Buyer match was linked at creation.</param>
/// <param name="ManualUnitNumber">The agent-entered Unit Number, when no CRM Buyer match was linked at creation.</param>
/// <param name="CustomerVerificationSource">The external lookup source that verified the customer at creation ("Pact"/"Tasleeh"), or null for CRM Buyer tickets and plain manual entry.</param>
/// <param name="ExternalCustomerId">The source's own customer identifier (for PACT, its tenantID) — an external identifier only, never a local reference.</param>
/// <param name="ExternalUnitId">The source's own identifier for the selected unit (for PACT, its unitID) — an external identifier only.</param>
/// <param name="ResolvedAtUtc">When the current resolution was recorded, in UTC — null while the ticket has no current resolution. Populated on detail reads (<c>GET /api/tickets/{id}</c>); write responses leave it null and clients re-read.</param>
/// <param name="RequestTypeId">The configured request type the ticket follows, or null for a ticket created without one.</param>
/// <param name="RequestTypeName">The request type's name — populated on detail reads.</param>
/// <param name="WorkflowTemplateId">The exact workflow VERSION the ticket was pinned to at creation; null for a ticket with no request type. Never changes after creation.</param>
/// <param name="WorkflowName">The pinned version's workflow name — populated on detail reads.</param>
/// <param name="WorkflowVersionNumber">The pinned version's number ("V2") — populated on detail reads.</param>
/// <param name="OriginatingChannelId">The channel the ticket ENTERED the system on (its originating interaction's channel) — never changed by later interactions on other channels; null for tickets predating the interaction model. Populated on detail reads.</param>
/// <param name="OriginatingChannelName">The originating channel's display name, resolved from channel configuration — still shown for a channel that has since been deactivated. Populated on detail reads.</param>
/// <param name="ViewerActsAsInteractionHandler">True when the caller is a CS Agent Genesys reported as handling this ticket's interaction, belongs to its department and nobody else owns it: the server lets them Resolve, Close and Transfer it. Populated on detail reads; the UI uses it to show those actions.</param>
/// <param name="IsReadOnlyAccess">True when the caller may open this ticket but change nothing on it: they created it, it is outside their department, and they hold no other grant on it. The server refuses every modification (403) regardless; the UI uses this to hide the actions. Populated on detail reads.</param>
/// <param name="CreatedByEmployeeId">The employee who created the ticket. Populated on detail reads.</param>
/// <param name="IsClassified">False while the ticket has no Category yet — an inquiry captured before anyone read the request. The UI shows "Unclassified" and offers the classify action rather than rendering a missing category.</param>
/// <param name="FirstHandledByEmployeeId">The first mapped Genesys agent who handled the originating interaction; null for non-Genesys tickets and when no agent was recorded. Never changes after it is set, and is separate from <c>CurrentOwnerEmployeeId</c>.</param>
/// <param name="ResolvedByEmployeeId">Who recorded the current resolution; null until resolved (and for system closures this is the system actor on the resolution row).</param>
/// <param name="TransferCount">How many times the ticket changed hands by a person's action (department transfers and reassignments). Automatic request-type routing is not counted.</param>
/// <param name="EscalationCount">How many of those were recorded as UnableToResolve or NeedsEscalation.</param>
/// <param name="FirstHandledByName">Display name of <c>FirstHandledByEmployeeId</c>, read from the employee directory itself — so it resolves even when that person has since left the department or been deactivated.</param>
/// <param name="ResolvedByName">Display name of <c>ResolvedByEmployeeId</c>, resolved the same way.</param>
/// <param name="HandoffState">
/// Whether this ticket is waiting on a human agent, <b>derived</b> from the
/// one open <c>TicketAgentHandoff</c> row: WaitingForAgent, Assigned or
/// InProgress while work is outstanding, and "NotRequired" when none is.
/// Deliberately not a column on the ticket — a ticket accumulates many
/// interactions, and a persisted copy would be a second source of truth free
/// to drift from the row that actually owns this state.
/// </param>
/// <param name="HandoffRequestedAtUtc">When the outstanding human work was raised — the start of the Human Wait metric. Null when nothing is outstanding.</param>
/// <param name="IsReopenEligible">Whether FR-RES-04's lifecycle rule currently allows Reopen — Closed, closed as Resolved, and within the ISSUE-011 window measured from closure. Lifecycle only, never a permission statement: the Reopen endpoint separately enforces TicketRoleSets.Reopen AND the caller's access to the ticket. Populated on detail reads; false on write responses.</param>
/// <param name="ClosedAtUtc">When the ticket reached Closed, in UTC, read from lifecycle history — the moment the reopen window is measured from. Null while the ticket is not closed. Populated on detail reads.</param>
public sealed record TicketDetailDto(
    long TicketId,
    string TicketNumber,
    int OriginatingDepartmentId,
    int CurrentDepartmentId,
    Guid? CurrentOwnerEmployeeId,
    int? UnitReferenceId,
    int? ContactReferenceId,
    int? CategoryId,
    byte? PriorityId,
    string TicketStatus,
    string VerificationStatus,
    string EscalationLevel,
    string SlaState,
    byte? ResolutionOutcome,
    long? DuplicateOfTicketId,
    string RequestSummary,
    int ReopenCount,
    DateTime CreatedAtUtc,
    string RowVersion,
    int? CrmBuyerCustomerId = null,
    int? CrmBuyerLeadId = null,
    int? CrmBuyerUnitId = null,
    int? CrmBuyerProjectId = null,
    string? CrmBuyerCustomerName = null,
    string? CrmBuyerProjectName = null,
    string? CrmBuyerUnitNumber = null,
    string? ManualProjectName = null,
    string? ManualUnitNumber = null,
    string? CustomerVerificationSource = null,
    string? ExternalCustomerId = null,
    string? ExternalUnitId = null,
    DateTime? ResolvedAtUtc = null,
    bool IsReopenEligible = false,
    string HandoffState = "NotRequired",
    DateTime? HandoffRequestedAtUtc = null,
    DateTime? ClosedAtUtc = null,
    int? RequestTypeId = null,
    string? RequestTypeName = null,
    int? WorkflowTemplateId = null,
    string? WorkflowName = null,
    int? WorkflowVersionNumber = null,
    byte? OriginatingChannelId = null,
    string? OriginatingChannelName = null,
    bool IsClassified = true,
    bool IsReadOnlyAccess = false,
    bool ViewerActsAsInteractionHandler = false,
    Guid? CreatedByEmployeeId = null,
    Guid? FirstHandledByEmployeeId = null,
    Guid? ResolvedByEmployeeId = null,
    int TransferCount = 0,
    int EscalationCount = 0,
    string? FirstHandledByName = null,
    string? ResolvedByName = null);

public enum TicketQueryOutcome
{
    Success,
    NotFound,
    Forbidden
}

public sealed record TicketQueryResultDto<T>(TicketQueryOutcome Outcome, T? Response = null) where T : class
{
    public static TicketQueryResultDto<T> Success(T response) => new(TicketQueryOutcome.Success, response);
    public static TicketQueryResultDto<T> Failure(TicketQueryOutcome outcome) => new(outcome);
}

// ---- Assignment / transfer (MVP-API-Contracts.md §3.5/§3.6) ----

/// <summary>Assign or reassign a ticket (MVP-API-Contracts.md §3.5).</summary>
/// <param name="AssignedEmployeeId">Required. Must be an active member of the ticket's current department.</param>
/// <param name="RowVersion">Required. The <c>rowVersion</c> from the ticket you read. A stale value is answered with 409.</param>
/// <param name="ReasonCode">Required (422 <c>transfer-reason-required</c> otherwise) when this REASSIGNS a ticket that already has a different owner: 1=UnableToResolve, 2=NeedsEscalation, 3=WrongDepartment, 4=CustomerRequest, 6=Other. Not needed for the first assignment out of the department queue. 1 and 2 record an escalation.</param>
/// <param name="Reason">Optional free-text context for <paramref name="ReasonCode"/>.</param>
public sealed record AssignTicketRequestDto(Guid AssignedEmployeeId, byte[] RowVersion, TicketTransferReason? ReasonCode = null, string? Reason = null);

/// <summary>Transfer a ticket to another department (MVP-API-Contracts.md §3.6).</summary>
/// <param name="TargetDepartmentId">Required. Must be an active department, and not the ticket's current one.</param>
/// <param name="Reason">Required. Why the ticket is being transferred.</param>
/// <param name="RowVersion">Required. The <c>rowVersion</c> from the ticket you read. A stale value is answered with 409.</param>
/// <param name="AssignToEmployeeId">
/// Optional. Transfer-and-assign: once the ticket is in the target
/// department, assign it to this employee in the same operation. The employee
/// must be a member of the TARGET department (422 <c>employee-not-in-department</c>
/// otherwise, and nothing is changed). When supplied, the target department's
/// automatic assignment rules are not run — the explicit choice wins. When
/// omitted the existing behaviour applies: the owner is cleared and the
/// target department's rules decide.
/// </param>
/// <param name="ReasonCode">Optional structured reason (1=UnableToResolve, 2=NeedsEscalation, 3=WrongDepartment, 4=CustomerRequest, 6=Other). Omitted is recorded as Unspecified; <c>Reason</c> stays the required free text.</param>
public sealed record TransferTicketRequestDto(int TargetDepartmentId, string Reason, byte[] RowVersion, Guid? AssignToEmployeeId = null, TicketTransferReason? ReasonCode = null);

public enum TicketMutationOutcome
{
    Success,
    NotFound,
    Forbidden,
    ConcurrencyConflict,

    /// <summary>Closed-ticket immutability (PR correction): Assign/Transfer/ChangeStatus/Resolve/Close all reject once TicketStatus is Closed — no database changes are made.</summary>
    TicketClosed,

    /// <summary>MVP-API-Contracts.md §3.5: AssignedEmployeeId is not an active member of the ticket's CurrentDepartmentId.</summary>
    EmployeeNotInDepartment,

    /// <summary>MVP-API-Contracts.md §3.6: target department doesn't exist or is inactive.</summary>
    TargetDepartmentInactive,

    /// <summary>MVP-API-Contracts.md §3.6: target equals the ticket's current department.</summary>
    AlreadyInTargetDepartment,

    InvalidStatusTransition,
    TicketNotAssigned,
    NotEligibleForResolution,
    NotYetResolved,
    DuplicateChainNotAllowed,

    /// <summary>FR-RES-04 (approved rule): Reopen is only valid from Closed — never from Resolved or an actively-worked status.</summary>
    NotEligibleForReopen,

    /// <summary>Approved rule: the ticket was closed as Cancelled, Rejected or Duplicate — terminal dispositions that are never reopened.</summary>
    ResolutionOutcomeNotReopenable,

    /// <summary>ISSUE-011/BR-020: the reopen window has passed — create a new linked ticket instead.</summary>
    ReopenWindowExpired,

    /// <summary>Approved rule: Reopen requires a reason, enforced in the application service so no caller can bypass it by not going through the controller.</summary>
    ReopenReasonRequired,

    /// <summary>Approved rule: Reopen requires the department that takes the reopened work on.</summary>
    TargetDepartmentRequired,

    /// <summary>Reassigning a ticket that already has a different owner needs an explicit transfer reason (Unable to Resolve, Needs Escalation, Wrong Department, Customer Request or Other). The first assignment out of the department queue needs none.</summary>
    TransferReasonRequired,

    /// <summary>Reconciliation-specific: the session's raw unit context doesn't match the ticket's originating raw unit number.</summary>
    ReconciliationUnitMismatch,

    /// <summary>Reconciliation-specific: the ticket is already Verified — there is no unit/contact left to reconcile.</summary>
    AlreadyVerified,

    VerificationSessionNotFound,
    VerificationSessionForbidden,
    VerificationSessionNotConfirmed,
    VerificationSessionAlreadyConsumed,
    VerificationSessionExpired,

    /// <summary>Workflow/Automation phase 2: the target Pending status needs a PendingReason and none was supplied — a ticket is never pending without a recorded why.</summary>
    PendingReasonRequired,

    /// <summary>Workflow/Automation phase 2: the ticket's request-type workflow configuration does not allow this action (e.g. Pending Customer on a request type whose template forbids it, or Reopen where the request type disables it).</summary>
    NotAllowedForRequestType,

    /// <summary>Workflow/Automation phase 2: the department's workflow settings disable this operation (assignment, internal reassignment, or transfer out) for the ticket's current department.</summary>
    DisabledByDepartmentSettings,

    /// <summary>Call Center first: a Call Center agent transfers a classified ticket to the department that owns its request type, not to any other.</summary>
    TransferTargetMustMatchRequestType,

    /// <summary>Classification: the ticket already carries a category. Re-categorising an existing ticket is a different operation, not built in this phase.</summary>
    AlreadyClassified,

    /// <summary>MVP-API-Contracts.md section 3.4: a priority decrease on an already-classified ticket is never a bare write; it must go through an approved downgrade request (403, <c>downgrade-requires-approval</c>).</summary>
    DowngradeRequiresApproval,

    /// <summary>Classification: the selected category was not found, or is deactivated.</summary>
    CategoryNotFound,

    /// <summary>Classification: the selected category belongs to a different department than the ticket's — a ticket is never filed under another department's category.</summary>
    CategoryDepartmentMismatch,

    /// <summary>Classification: the selected priority does not exist.</summary>
    PriorityNotFound
}

public sealed record TicketMutationResult(TicketMutationOutcome Outcome, TicketDetailDto? Response = null)
{
    public static TicketMutationResult Success(TicketDetailDto response) => new(TicketMutationOutcome.Success, response);
    public static TicketMutationResult Failure(TicketMutationOutcome outcome) => new(outcome);
}

// ---- Status / resolve / close (MVP-API-Contracts.md §3.7/§3.9/§3.10) ----

/// <summary>Move a ticket within the working sub-machine (MVP-API-Contracts.md §3.7).</summary>
/// <param name="NewStatus">Required. InProgress or PendingCustomer — the only targets the working sub-machine has. Open is never a target (a ticket does not go back to unstarted), Resolved and Closed are reached through their own endpoints, and the retired PendingThirdParty is refused outright.</param>
/// <param name="RowVersion">Required. The <c>rowVersion</c> from the ticket you read. A stale value is answered with 409.</param>
/// <param name="PendingReason">Required when NewStatus is PendingCustomer — a ticket is never pending without a recorded why. Ignored for other targets.</param>
public sealed record ChangeStatusRequestDto(string NewStatus, byte[] RowVersion, string? PendingReason = null);

/// <summary>Resolve a ticket (MVP-API-Contracts.md §3.9).</summary>
/// <param name="ResolutionOutcome">Required. One of Resolved, Cancelled, Rejected, Duplicate.</param>
/// <param name="ResolutionNote">Required. How the ticket was resolved.</param>
/// <param name="ReasonCode">Optional reason code.</param>
/// <param name="DuplicateOfTicketId">The ticket this one duplicates. Required when the outcome is Duplicate; the target must exist and must not itself be a duplicate.</param>
/// <param name="RowVersion">Required. The <c>rowVersion</c> from the ticket you read. A stale value is answered with 409.</param>
public sealed record ResolveTicketRequestDto(
    string ResolutionOutcome, string ResolutionNote, byte? ReasonCode, long? DuplicateOfTicketId, byte[] RowVersion);

/// <summary>Close a resolved ticket (MVP-API-Contracts.md §3.10).</summary>
/// <param name="RowVersion">Required. The <c>rowVersion</c> from the ticket you read. A stale value is answered with 409.</param>
public sealed record CloseTicketRequestDto(byte[] RowVersion);

/// <summary>Reopen a closed ticket (MVP-API-Contracts.md §3.11, FR-RES-04).</summary>
/// <param name="Reason">Required. Why the ticket is being reopened — recorded on the status-history row, on the Reopened workflow event Ticket Details renders, and in the audit trail. Enforced by the application service, not only by model validation.</param>
/// <param name="TargetDepartmentId">Required. The department that takes responsibility for the reopened work. Must exist and be active; may be the ticket's current department. The previous owner is cleared either way and the existing assignment automation re-runs against this department.</param>
/// <param name="RowVersion">Required. The <c>rowVersion</c> from the ticket you read. A stale value is answered with 409 — including when a concurrent reopen already won the race.</param>
public sealed record ReopenTicketRequestDto(string Reason, int TargetDepartmentId, byte[] RowVersion);

// ---- Notes (MVP-API-Contracts.md §4.1/§4.2) ----

/// <summary>Add a note to a ticket (MVP-API-Contracts.md §4.1).</summary>
/// <param name="NoteText">Required. The note's text.</param>
public sealed record CreateNoteRequestDto(string NoteText);

/// <summary>A ticket note (MVP-API-Contracts.md §4.1/§4.2).</summary>
/// <param name="TicketNoteId">The note.</param>
/// <param name="TicketId">The ticket it belongs to.</param>
/// <param name="NoteText">The note's text.</param>
/// <param name="AuthorEmployeeId">Who wrote it.</param>
/// <param name="CreatedAtUtc">When it was written, in UTC.</param>
public sealed record TicketNoteResponseDto(long TicketNoteId, long TicketId, string NoteText, Guid AuthorEmployeeId, DateTime CreatedAtUtc);

public enum NoteOutcome
{
    Success,
    TicketNotFound,
    Forbidden
}

public sealed record NoteResult(NoteOutcome Outcome, TicketNoteResponseDto? Response = null)
{
    public static NoteResult Success(TicketNoteResponseDto response) => new(NoteOutcome.Success, response);
    public static NoteResult Failure(NoteOutcome outcome) => new(outcome);
}

/// <summary>One page of a ticket's notes (MVP-API-Contracts.md §4.2).</summary>
/// <param name="Items">This page's notes.</param>
/// <param name="TotalCount">How many notes the ticket has in total.</param>
/// <param name="Page">The 1-based page number that was served.</param>
/// <param name="PageSize">The page size that was served.</param>
public sealed record TicketNoteListResultDto(IReadOnlyList<TicketNoteResponseDto> Items, int TotalCount, int Page, int PageSize);

// ---- CRM reconciliation (task item 6 — no reference API contract existed for this; designed this increment) ----

/// <summary>Reconcile a provisional ticket against a confirmed verification session.</summary>
/// <param name="VerificationSessionId">Required. A confirmed, unexpired, not-yet-consumed session owned by the caller, whose unit matches the ticket's originating raw unit number.</param>
/// <param name="RowVersion">Required. The <c>rowVersion</c> from the ticket you read. A stale value is answered with 409.</param>
public sealed record ReconcileTicketRequestDto(Guid VerificationSessionId, byte[] RowVersion);
