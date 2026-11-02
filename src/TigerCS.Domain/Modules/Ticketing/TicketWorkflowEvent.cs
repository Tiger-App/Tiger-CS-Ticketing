namespace TigerCS.Domain.Modules.Ticketing;

/// <summary>
/// The typed business events a ticket's workflow can emit (Workflow/
/// Automation phase 3). These are the machine-queryable trigger source the
/// phase-4 SLA calculation reads — never parsed out of human audit text.
/// Values map onto the phase-1 <c>SlaTriggerType</c> concepts where a
/// trigger exists; the maintenance trio additionally carries the Handover
/// dependency state.
/// </summary>
public enum WorkflowEventType : byte
{
    /// <summary>An approval cycle was opened.</summary>
    ApprovalRequested = 1,

    /// <summary>A required approval was granted — the semantic event behind the <c>SlaTriggerType.ApprovalReceived</c> trigger (Collections / Send Receipts: the 1 day runs from here, computed in phase 4).</summary>
    ApprovalReceived = 2,

    /// <summary>A required approval was rejected. What happens next stays an explicit operational action.</summary>
    ApprovalRejected = 3,

    /// <summary>Customer Service approved — the semantic event behind <c>SlaTriggerType.CustomerServiceApproved</c> (Handover: 1–4 days run from here, computed in phase 4).</summary>
    CustomerServiceApproved = 4,

    /// <summary>Prerequisites are satisfied — the semantic event behind <c>SlaTriggerType.PrerequisitesCompleted</c> (Registration / Register Unit). Recorded explicitly by an authorized actor, never inferred.</summary>
    PrerequisitesCompleted = 5,

    /// <summary>Handover: maintenance exists and the process waits on it. No duration is attached — the source defines none.</summary>
    MaintenanceRequired = 6,

    /// <summary>Handover: no maintenance dependency — after the NOC the customer proceeds to the tower/key handover.</summary>
    MaintenanceNotRequired = 7,

    /// <summary>Handover: the maintenance dependency completed. Not customer-caused; how it affects the clock is a phase-4 decision.</summary>
    MaintenanceCompleted = 8,

    /// <summary>
    /// The approved Reopen rule's lifecycle event: a Closed ticket was
    /// reopened into InProgress. Recorded here — rather than in a new
    /// activity store — because this table is already the ticket's typed,
    /// append-only event log, and is already read by
    /// <c>GET /api/tickets/{ticketId}/approvals</c>, which is what finally
    /// lets Ticket Details show the reopen reason that TicketStatusHistory
    /// has always recorded but nothing ever exposed. The column is a tinyint
    /// with no value constraint, so this member needs no schema change.
    /// </summary>
    Reopened = 9,

    /// <summary>
    /// Pending human work was raised on a customer interaction — the customer
    /// asked for a person, the virtual agent escalated, routing decided, or
    /// TigerCS noticed an AI conversation ended with nobody waiting on it.
    /// The note carries the trigger and the reason.
    ///
    /// <para>
    /// The five handoff members below are recorded here for the same reason
    /// <see cref="Reopened"/> is: this table is already the ticket's typed,
    /// append-only event log and is already read by
    /// <c>GET /api/tickets/{ticketId}/approvals</c>, so Ticket Details gains
    /// the handoff story without a second activity store. The column is a
    /// tinyint with no value constraint, so these need no schema change.
    /// </para>
    /// </summary>
    HandoffRequested = 10,

    /// <summary>Genesys named the agent taking the pending work. Not the same as a TigerCS agent claiming it — see <see cref="HandoffStarted"/>.</summary>
    HandoffAssigned = 11,

    /// <summary>A human agent accepted the pending work in TigerCS — the end of the Human Wait metric.</summary>
    HandoffStarted = 12,

    /// <summary>The human work finished. Says nothing about the ticket, which continues under its own lifecycle.</summary>
    HandoffCompleted = 13,

    /// <summary>The human work was stood down — the AI resumed, or a person is no longer needed. The note carries the required reason.</summary>
    HandoffCancelled = 14,

    /// <summary>
    /// The customer explicitly told the contact centre, during a Genesys
    /// interaction, that their issue is resolved. <b>Record-only:</b> it does
    /// not resolve or close the ticket — those stay the owner's and CS's own
    /// operations under their existing authorization — it gives them the
    /// customer's word, with when it was said. Stored here for the same
    /// reason as the handoff events, so it needs no schema change.
    /// </summary>
    CustomerConfirmedResolved = 15,

    /// <summary>
    /// The request type of an already-classified ticket was replaced (the
    /// Genesys AI refined its classification while the conversation
    /// progressed). Append-only history of the change; the note carries the
    /// previous and the new request type and the source. Stored here for the
    /// same reason as the handoff events, so it needs no schema change.
    /// </summary>
    RequestTypeReclassified = 16,

    /// <summary>
    /// The ticket changed hands: a department transfer, a reassignment from one
    /// employee to another, or an escalation. The structured facts (reason,
    /// from/to department, receiving employee) live in the typed columns of
    /// the event; the acting employee is <see cref="TicketWorkflowEvent.ActorEmployeeId"/>
    /// (null = the system, e.g. automatic request-type routing). A Genesys
    /// call transfer never writes this event — that is queue/agent routing of
    /// the call, recorded on the interaction and the audit trail, and says
    /// nothing about whether the agent could resolve the issue.
    /// </summary>
    TicketTransferred = 17,

    /// <summary>
    /// A mapped Genesys agent first took the customer's interaction (the first handler). Recorded with the agent in
    /// <see cref="TicketWorkflowEvent.ToEmployeeId"/> and no transfer reason: this is who answered, not a hand-over.
    /// </summary>
    GenesysAgentConnected = 18,

    /// <summary>
    /// Genesys moved a live conversation from one agent to another. History only: it carries no transfer reason, is
    /// never an escalation and is not counted as a ticket transfer — whether the first agent could resolve the issue is
    /// a judgement only an explicit TigerCS transfer or escalation records. The previous agent, when mapped, is the
    /// actor; the receiving agent, when mapped, is <see cref="TicketWorkflowEvent.ToEmployeeId"/>; unmapped agents
    /// are preserved verbatim in the note.
    /// </summary>
    GenesysCallTransferred = 19,

    /// <summary>
    /// Genesys reported a Call Center call as answered by an agent TigerCS cannot hand the ticket to — not mapped to a Ticketing user,
    /// deactivated, locked out, a service account, or not an operational Call Center member. The ticket is deliberately left in the Call
    /// Center queue, unowned, and this event is the flag that a supervisor must assign it manually: it is never given to a different person
    /// by Round Robin. History only; the note names the Genesys agent and the reason. No schema change (the column is a tinyint).
    /// </summary>
    ManualAssignmentRequired = 20
}

/// <summary>
/// Why a ticket changed hands. Only <see cref="UnableToResolve"/> and
/// <see cref="NeedsEscalation"/> count as escalations; every other reason is a
/// plain transfer, and <see cref="SystemRouting"/> is the system moving a
/// ticket to the department its request type is configured for — never an
/// agent's judgement.
/// </summary>
public enum TicketTransferReason : byte
{
    /// <summary>The caller gave no structured reason (older clients, pre-tracking data).</summary>
    Unspecified = 0,

    /// <summary>The handling agent could not resolve the customer's issue.</summary>
    UnableToResolve = 1,

    /// <summary>The issue needs a more senior or specialist owner.</summary>
    NeedsEscalation = 2,

    /// <summary>The ticket was in the wrong department.</summary>
    WrongDepartment = 3,

    /// <summary>The customer asked for a different person or department.</summary>
    CustomerRequest = 4,

    /// <summary>Automatic request-type routing moved the ticket.</summary>
    SystemRouting = 5,

    /// <summary>Any other reason; the free-text note says which.</summary>
    Other = 6
}

/// <summary>
/// One typed, append-only workflow event of a ticket — the trustworthy
/// timestamp store for conditional SLA triggers and dependency state.
/// Written in the same transaction (and under the same correlation id) as
/// the approval/audit rows describing the same action; never updated, never
/// deleted. Distinct from <see cref="TicketStatusHistory"/> (which records
/// the five lifecycle dimensions) and from <c>AuditEntries</c> (the
/// human-auditable trail): this table exists precisely so phase 4 reads
/// typed events instead of parsing either of those.
/// </summary>
public class TicketWorkflowEvent
{
    public long TicketWorkflowEventId { get; private set; }
    public long TicketId { get; private set; }
    public WorkflowEventType EventType { get; private set; }

    /// <summary>When the business event happened — the value a phase-4 SLA trigger reads.</summary>
    public DateTime OccurredAtUtc { get; private set; }

    /// <summary>Null = the system. Approval-linked events carry the deciding/requesting employee.</summary>
    public Guid? ActorEmployeeId { get; private set; }

    /// <summary>The approval cycle this event belongs to, for the four approval event types; null for prerequisite/maintenance events.</summary>
    public long? TicketApprovalId { get; private set; }

    /// <summary>Short human context (e.g. the recorded reason) — display only; consumers key on <see cref="EventType"/> and <see cref="OccurredAtUtc"/>, never on this text.</summary>
    public string? Note { get; private set; }

    public Guid CorrelationId { get; private set; }

    /// <summary><see cref="TicketTransferReason"/> as a byte, for <see cref="WorkflowEventType.TicketTransferred"/> only; null for every other event type.</summary>
    public byte? TransferReasonCode { get; private set; }

    /// <summary>The department the ticket left (<see cref="WorkflowEventType.TicketTransferred"/> only).</summary>
    public int? FromDepartmentId { get; private set; }

    /// <summary>The department that holds the ticket after the transfer (<see cref="WorkflowEventType.TicketTransferred"/> only).</summary>
    public int? ToDepartmentId { get; private set; }

    /// <summary>The employee who received the ticket, when one did; null when it went to a department queue (<see cref="WorkflowEventType.TicketTransferred"/> only).</summary>
    public Guid? ToEmployeeId { get; private set; }

    private TicketWorkflowEvent() { }

    public TicketWorkflowEvent(
        long ticketId,
        WorkflowEventType eventType,
        DateTime occurredAtUtc,
        Guid? actorEmployeeId,
        long? ticketApprovalId,
        string? note,
        Guid correlationId)
    {
        if (!Enum.IsDefined(eventType))
        {
            throw new ArgumentException($"EventType {eventType} is not a defined workflow event type.", nameof(eventType));
        }

        TicketId = ticketId;
        EventType = eventType;
        OccurredAtUtc = occurredAtUtc;
        ActorEmployeeId = actorEmployeeId;
        TicketApprovalId = ticketApprovalId;
        Note = note;
        CorrelationId = correlationId;
    }

    /// <summary>The one way a <see cref="WorkflowEventType.TicketTransferred"/> event is built, so its structured columns can never be half-filled.</summary>
    public static TicketWorkflowEvent ForTransfer(
        long ticketId,
        DateTime occurredAtUtc,
        Guid? actorEmployeeId,
        TicketTransferReason reason,
        int fromDepartmentId,
        int toDepartmentId,
        Guid? toEmployeeId,
        string? note,
        Guid correlationId)
    {
        if (!Enum.IsDefined(reason))
        {
            throw new ArgumentException($"Reason {reason} is not a defined transfer reason.", nameof(reason));
        }

        return new TicketWorkflowEvent(
            ticketId, WorkflowEventType.TicketTransferred, occurredAtUtc, actorEmployeeId, ticketApprovalId: null,
            note is { Length: > 500 } ? note[..500] : note, correlationId)
        {
            TransferReasonCode = (byte)reason,
            FromDepartmentId = fromDepartmentId,
            ToDepartmentId = toDepartmentId,
            ToEmployeeId = toEmployeeId
        };
    }

    /// <summary>
    /// Records where ownership landed for an event whose TYPE already says why it happened (a reopen, a handoff accept,
    /// a Genesys agent change). Deliberately leaves <see cref="TransferReasonCode"/> null, so such an event can never be
    /// read as an escalation or counted as a transfer.
    /// </summary>
    public TicketWorkflowEvent WithOwnership(int? fromDepartmentId, int? toDepartmentId, Guid? toEmployeeId)
    {
        if (EventType == WorkflowEventType.TicketTransferred)
        {
            throw new InvalidOperationException("A transfer event is built with ForTransfer, which also carries its reason.");
        }

        FromDepartmentId = fromDepartmentId;
        ToDepartmentId = toDepartmentId;
        ToEmployeeId = toEmployeeId;
        return this;
    }

    /// <summary>True for the two reasons that mean the handling agent passed the ticket up rather than simply moving it.</summary>
    public bool IsEscalation =>
        EventType == WorkflowEventType.TicketTransferred
        && TransferReasonCode is (byte)TicketTransferReason.UnableToResolve or (byte)TicketTransferReason.NeedsEscalation;
}
