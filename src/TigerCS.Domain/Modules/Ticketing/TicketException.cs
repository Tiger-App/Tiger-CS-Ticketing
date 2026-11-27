namespace TigerCS.Domain.Modules.Ticketing;

public abstract class TicketException(string message) : Exception(message);

/// <summary>Ticket.ReconcileVerification links a unit/contact pair onto a ticket that does not have one yet — a ticket already Verified has nothing left to reconcile.</summary>
public sealed class TicketAlreadyVerifiedException(long ticketId)
    : TicketException($"Ticket {ticketId} is already Verified — it has no unit/contact left to reconcile.")
{
    public long TicketId { get; } = ticketId;
}

/// <summary>Solution-Analysis.md §5.6 transition table / ADR-0008: the requested TicketStatus is not reachable from the ticket's current TicketStatus.</summary>
public sealed class InvalidTicketStatusTransitionException(long ticketId, TicketStatus fromStatus, TicketStatus toStatus)
    : TicketException($"Ticket {ticketId} cannot transition TicketStatus from {fromStatus} to {toStatus}.")
{
    public long TicketId { get; } = ticketId;
    public TicketStatus FromStatus { get; } = fromStatus;
    public TicketStatus ToStatus { get; } = toStatus;
}

/// <summary>MVP-API-Contracts.md §3.7's "must go through Open→InProgress with an assigned owner" requirement — a ticket cannot start being worked before it has an owner.</summary>
public sealed class TicketNotAssignedException(long ticketId)
    : TicketException($"Ticket {ticketId} has no current owner — assign it before starting work.");

/// <summary>Solution-Analysis.md §5.6: Resolve is only valid from InProgress or PendingCustomer, never directly from Open, a terminal status, or the legacy PendingThirdParty (which returns to InProgress first).</summary>
public sealed class TicketNotEligibleForResolutionException(long ticketId, TicketStatus actualStatus)
    : TicketException($"Ticket {ticketId} cannot be resolved from TicketStatus {actualStatus}.")
{
    public long TicketId { get; } = ticketId;
    public TicketStatus ActualStatus { get; } = actualStatus;
}

/// <summary>MVP-API-Contracts.md §3.10: `409 not-yet-resolved` — Close requires a current TicketResolutions row.</summary>
public sealed class TicketNotYetResolvedException(long ticketId)
    : TicketException($"Ticket {ticketId} has no current resolution — resolve it before closing.")
{
    public long TicketId { get; } = ticketId;
}

/// <summary>MVP-ERD.md §2.10: DuplicateOfTicketId must reference a genuine, non-duplicate original — no chains of duplicates pointing to duplicates.</summary>
public sealed class DuplicateChainNotAllowedException(long ticketId, long duplicateOfTicketId)
    : TicketException($"Ticket {ticketId} cannot be marked a duplicate of {duplicateOfTicketId}, which is itself a duplicate.")
{
    public long TicketId { get; } = ticketId;
    public long DuplicateOfTicketId { get; } = duplicateOfTicketId;
}

/// <summary>MVP-ERD.md §2.3's write-once OriginatingDepartmentId / §3.6: a transfer to the ticket's own current department is not a valid transfer.</summary>
public sealed class TicketAlreadyInTargetDepartmentException(long ticketId, int departmentId)
    : TicketException($"Ticket {ticketId} is already in department {departmentId}.")
{
    public long TicketId { get; } = ticketId;
    public int DepartmentId { get; } = departmentId;
}

/// <summary>
/// Closed-ticket immutability (PR correction): a Closed ticket accepts no
/// further mutation of any kind — Assign, Transfer, ChangeStatus, Resolve,
/// and Close-again all reject with this exception before any state changes
/// or writes occur. Reading a Closed ticket's detail, history, and notes
/// remains unaffected — this exception is only ever thrown from a mutating
/// domain method.
/// </summary>
public sealed class TicketClosedException(long ticketId)
    : TicketException($"Ticket {ticketId} is Closed and no longer accepts mutations.")
{
    public long TicketId { get; } = ticketId;
}

/// <summary>FR-RES-04 / MVP-API-Contracts.md §3.11: Reopen is only valid from Closed (approved business rule) — a Resolved or actively-worked ticket has nothing to reopen.</summary>
public sealed class TicketNotEligibleForReopenException(long ticketId, TicketStatus actualStatus)
    : TicketException($"Ticket {ticketId} cannot be reopened from TicketStatus {actualStatus} — Reopen is only valid from Closed.")
{
    public long TicketId { get; } = ticketId;
    public TicketStatus ActualStatus { get; } = actualStatus;
}

/// <summary>
/// <b>Closed is terminal (approved, every channel).</b> A Closed ticket is never reopened - not by a person, an approval, an API call, a background job or any
/// automation. The domain refuses it here so that no caller, present or future, can bypass the application-level policy. A customer who writes again after
/// closure gets a new linked ticket instead.
/// </summary>
public sealed class ClosedTicketIsTerminalException(long ticketId)
    : TicketException($"Ticket {ticketId} is Closed, and Closed is terminal: it is never reopened. Raise a new linked ticket instead.")
{
    public long TicketId { get; } = ticketId;
}

/// <summary>
/// The approved Reopen rule covers work that was genuinely completed:
/// Cancelled, Rejected and Duplicate are terminal dispositions, and a
/// customer returning on one of those raises a new ticket instead.
/// </summary>
public sealed class TicketResolutionOutcomeNotReopenableException(long ticketId, byte? resolutionOutcome)
    : TicketException(
        $"Ticket {ticketId} was closed as "
        + $"{(resolutionOutcome is { } o && Enum.IsDefined(typeof(ResolutionOutcome), o) ? ((ResolutionOutcome)o).ToString() : "an unknown outcome")}"
        + " — only a ticket closed as Resolved may be reopened.")
{
    public long TicketId { get; } = ticketId;
    public byte? ResolutionOutcome { get; } = resolutionOutcome;
}

/// <summary>
/// <c>Ticket.AcknowledgementSentAtUtc</c> is write-once (MVP-Data-Dictionary.md
/// §2.10). Thrown when a redelivered <c>TicketCreated</c> Outbox message
/// reaches a ticket whose automated acknowledgement was already delivered —
/// the domain-level half of "duplicate processing does not send twice"
/// (ADR-0013/ADR-0014). Callers treat it as "already done", not as an error.
/// </summary>
public sealed class AcknowledgementAlreadySentException(long ticketId, DateTime sentAtUtc)
    : TicketException($"Ticket {ticketId} already recorded its automated acknowledgement as sent at {sentAtUtc:O} — the field is write-once.")
{
    public long TicketId { get; } = ticketId;
    public DateTime SentAtUtc { get; } = sentAtUtc;
}

/// <summary>Workflow/SLA Configuration phase 2: a ticket's request type is write-once — re-classification is not a supported operation in this phase.</summary>
public sealed class TicketRequestTypeAlreadySetException(long ticketId, int requestTypeId)
    : TicketException($"Ticket {ticketId} is already classified as RequestType {requestTypeId} — the classification is write-once.")
{
    public long TicketId { get; } = ticketId;
    public int RequestTypeId { get; } = requestTypeId;
}

/// <summary>A ticket's workflow version is pinned once at creation and never re-pinned.</summary>
public sealed class TicketWorkflowVersionAlreadyPinnedException(long ticketId, int workflowTemplateId)
    : TicketException($"Ticket {ticketId} is already pinned to workflow version {workflowTemplateId}; the pinned version never changes.")
{
    public long TicketId { get; } = ticketId;
    public int WorkflowTemplateId { get; } = workflowTemplateId;
}

/// <summary>
/// <see cref="Ticket.Classify"/> performs a ticket's <i>initial</i>
/// classification, so a ticket that already carries a category is refused.
///
/// <para>
/// This is a <b>Phase 1 safeguard, not a confirmed final business rule.</b>
/// Reclassification is deferred because its semantics are undefined: whether
/// the SLA period is recomputed, restarted or left alone, what happens to a
/// workflow version already pinned and executing, and what happens to
/// approvals raised under the previous request type. Refusing keeps a
/// ticket's business meaning from moving underneath machinery that has
/// already acted on it; when those semantics are decided, a deliberate
/// reclassification operation replaces this refusal.
/// </para>
/// </summary>
public sealed class TicketAlreadyClassifiedException(long ticketId, int categoryId)
    : TicketException($"Ticket {ticketId} is already classified under category {categoryId}.")
{
    public long TicketId { get; } = ticketId;
    public int CategoryId { get; } = categoryId;
}

/// <summary>MVP-API-Contracts.md section 3.4: a priority decrease without an approved downgrade request is refused (downgrade-requires-approval).</summary>
public sealed class PriorityDowngradeRequiresApprovalException(long ticketId, byte currentPriorityId, byte requestedPriorityId)
    : TicketException($"Ticket {ticketId} priority cannot drop from {currentPriorityId} to {requestedPriorityId} without an approved downgrade request.")
{
    public long TicketId { get; } = ticketId;
    public byte CurrentPriorityId { get; } = currentPriorityId;
    public byte RequestedPriorityId { get; } = requestedPriorityId;
}

/// <summary>
/// A queue-managed ticket is a read-only mirror of the Queueing System: its lifecycle and assignment change only while the integration applies a Queueing System event.
/// Thrown by every lifecycle / assignment mutator of <see cref="Ticket"/> as the domain's last line of defence (the application guards answer first with a clean
/// outcome). Nothing has changed when it is thrown.
/// </summary>
public sealed class QueueManagedTicketMutationException(long ticketId)
    : TicketException($"Ticket {ticketId} is managed by the Queueing System; its status, assignment and closure change only when the Queueing System says so.")
{
    public long TicketId { get; } = ticketId;
}
