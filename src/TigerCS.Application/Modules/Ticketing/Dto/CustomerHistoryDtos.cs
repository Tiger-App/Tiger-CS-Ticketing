namespace TigerCS.Application.Modules.Ticketing.Dto;

/// <summary>
/// Customer → Previous Ticket History. Sourced exclusively from the
/// existing Tickets table — never a live CRM call — so this works even when
/// CRM is offline. Exactly one identity is ever used per result:
/// <see cref="VerificationType"/> <c>"Verified"</c> means every ticket in
/// <see cref="Tickets"/> shares the same <c>CrmBuyerCustomerId</c> (a phone
/// number may match more than one CRM customer, so phone is never used to
/// widen a verified customer's history); <c>"Unverified"</c> means there was
/// no CrmBuyerCustomerId to key on and the persisted
/// <c>IntakeRecord.PhoneNumber</c> snapshot was used as a fallback key
/// instead — a weaker identity, and always labelled as such rather than
/// silently presented the same way as CRM-verified history.
/// </summary>
/// <param name="VerificationType">One of "Verified" (keyed by CrmBuyerCustomerId), "ExternalVerified" (keyed by the persisted external source + external customer id — PACT/Tasleeh), or "Unverified" (keyed by phone-number snapshot).</param>
/// <param name="CrmBuyerCustomerId">The CRM customer id this history was queried for, when <paramref name="VerificationType"/> is "Verified"; otherwise null.</param>
/// <param name="PhoneNumberSnapshot">The phone-number snapshot this history was queried for, when <paramref name="VerificationType"/> is "Unverified"; otherwise null.</param>
/// <param name="CustomerDisplayName">Best-effort display name for the customer identity, when one is known (the CRM Buyer's ticket-time name snapshot). Null when no ticket in the history carried one.</param>
/// <param name="TotalTickets">Count of every matching ticket — not just the ones returned in <paramref name="Tickets"/>.</param>
/// <param name="OpenTickets">Count of matching tickets whose TicketStatus is not Resolved or Closed.</param>
/// <param name="ClosedTickets">Count of matching tickets whose TicketStatus is Resolved or Closed.</param>
/// <param name="Tickets">The newest tickets first, limited to the caller's requested page size — never the customer's entire history unbounded.</param>
/// <param name="ExternalSource">The external verification source this history was queried for, when <paramref name="VerificationType"/> is "ExternalVerified" (e.g. "Pact"/"Tasleeh"); otherwise null.</param>
/// <param name="ExternalCustomerId">The source's own customer identifier this history was queried for, when <paramref name="VerificationType"/> is "ExternalVerified"; otherwise null.</param>
/// <param name="CustomerKey">The Customers directory key of this customer (crm:{id}, ext:{source}:{id} or phone:{number}) — the Customer Profile the Web links to; null when the anchor ticket has no customer identity.</param>
public sealed record CustomerHistoryDto(
    string VerificationType,
    int? CrmBuyerCustomerId,
    string? PhoneNumberSnapshot,
    string? CustomerDisplayName,
    int TotalTickets,
    int OpenTickets,
    int ClosedTickets,
    IReadOnlyList<CustomerHistoryTicketDto> Tickets,
    string? ExternalSource = null,
    string? ExternalCustomerId = null,
    string? CustomerKey = null);

/// <summary>One row of a customer's ticket history — fields drawn from the Ticket aggregate alone, no other lookup performed per row.</summary>
/// <param name="TicketId">The ticket — links to <c>GET /api/tickets/{ticketId}</c> / the Ticket Details page.</param>
/// <param name="TicketNumber">The human-facing ticket number.</param>
/// <param name="CreatedAtUtc">When the ticket was created, in UTC — the sort key (newest first).</param>
/// <param name="TicketStatus">One of Open, InProgress, PendingCustomer, Resolved, Closed — or the legacy, no-longer-reachable PendingThirdParty on a historical ticket.</param>
/// <param name="PriorityId">1=Critical, 2=High, 3=Medium, 4=Low, or null while the ticket is Unclassified.</param>
/// <param name="CategoryId">The ticket's category, or null while the ticket is Unclassified.</param>
/// <param name="CurrentDepartmentId">The department that currently holds the ticket.</param>
/// <param name="ProjectName">The CRM Buyer or manually-entered project name snapshot, or null when neither is set.</param>
/// <param name="UnitNumber">The CRM Buyer or manually-entered unit number snapshot, or null when neither is set.</param>
/// <param name="VerificationStatus">One of Unverified, PendingCrmVerification, Verified.</param>
/// <param name="RequestSummary">The ticket's request summary — a one-line subject for list scanning; full detail stays on Ticket Details.</param>
/// <param name="ResolvedAtUtc">When the current resolution was recorded, in UTC — null while the ticket has no current resolution.</param>
/// <param name="IsReopenEligible">Whether FR-RES-04's lifecycle rule currently allows Reopen (Resolved/Closed, within the ISSUE-011 window). Lifecycle only — permissions are enforced at the Reopen endpoint.</param>
/// <param name="ClosedAtUtc">When the ticket was closed, in UTC - null unless it is Closed.</param>
/// <param name="AssignedAgentEmployeeId">The employee the ticket is currently assigned to, or null when unassigned.</param>
/// <param name="AssignedAgentName">That employee's display name, resolved server-side so a viewer who cannot list users still sees who handled the ticket.</param>
/// <param name="RequestTypeId">The ticket's request type, or null while unclassified.</param>
/// <param name="RequestTypeName">The request type's name, or null when unclassified.</param>
public sealed record CustomerHistoryTicketDto(
    long TicketId,
    string TicketNumber,
    DateTime CreatedAtUtc,
    string TicketStatus,
    byte? PriorityId,
    int? CategoryId,
    int CurrentDepartmentId,
    string? ProjectName,
    string? UnitNumber,
    string VerificationStatus,
    string? RequestSummary = null,
    DateTime? ResolvedAtUtc = null,
    bool IsReopenEligible = false,
    DateTime? ClosedAtUtc = null,
    Guid? AssignedAgentEmployeeId = null,
    string? AssignedAgentName = null,
    int? RequestTypeId = null,
    string? RequestTypeName = null);

public enum CustomerHistoryOutcome
{
    Success,
    NotFound,
    Forbidden
}

public sealed record CustomerHistoryResult(CustomerHistoryOutcome Outcome, CustomerHistoryDto? Response = null)
{
    public static CustomerHistoryResult Success(CustomerHistoryDto response) => new(CustomerHistoryOutcome.Success, response);
    public static CustomerHistoryResult Failure(CustomerHistoryOutcome outcome) => new(outcome);
}
