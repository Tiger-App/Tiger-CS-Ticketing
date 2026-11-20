namespace TigerCS.Domain.Modules.Ticketing;

/// <summary>
/// One customer interaction associated with a ticket — a ticket accumulates
/// <b>many</b> of these over its lifetime (the original inbound Genesys
/// call, a follow-up inbound call, an outbound call, another Genesys
/// conversation, a future WhatsApp exchange, a Face-to-Face follow-up), each
/// independently retaining its source, channel, customer phone, and — where
/// Genesys handled it — the Genesys context, verbatim. Persisted for audit,
/// history, reporting, and Ticket ↔ Genesys conversation traceability. This
/// is a <b>record of what Genesys (or the agent) said</b>, never routing
/// logic: Ticketing does not reproduce Called Number → Queue mapping, and
/// none of this data drives department routing inside Ticketing
/// (Category/Request Type do that, exactly as before).
///
/// <para>
/// <b>Exactly one interaction per ticket is the originating one</b>
/// (<see cref="IsOriginatingInteraction"/>) — the interaction the ticket
/// was created from, written by ticket creation in the same transaction. A
/// filtered unique index enforces at-most-one at the database. Later
/// interactions (recorded by future phases) are appended with the flag
/// false and never touch the originating row.
/// </para>
///
/// <para>
/// <b>Every Genesys field is nullable by design.</b> The exact Genesys API
/// contract is not finalized; the fields below are the context Genesys is
/// expected to provide (channel, customer phone, called/destination number,
/// queue id/name, agent id/name, conversation id, interaction start,
/// direction). Face-to-Face / walk-in interactions never have any of them —
/// <see cref="InteractionContextSource.Ticketing"/> rows enforce that they
/// stay null. Only the conversation id is required for a
/// <see cref="InteractionContextSource.Genesys"/> row, because without it
/// the row cannot link back to the interaction at all.
/// </para>
///
/// <para>
/// <b>One Genesys service request is exactly one interaction, on exactly one
/// ticket.</b> A conversation may carry several distinct service requests
/// (documents, then leasing), each with its own <see cref="GenesysServiceRequestId"/>
/// and its own ticket. (<see cref="GenesysConversationId"/>, <see cref="GenesysServiceRequestId"/>)
/// is unique (filtered unique index) — a conversation whose caller sends no
/// service request id keeps the original rule of exactly one — which is the
/// database-level half of "a retried or duplicated Genesys event never
/// creates a second ticket": the application checks first, and a lost race
/// surfaces as a unique-constraint violation the caller treats as "already
/// ingested". The conversation's lifecycle is recorded here too —
/// <see cref="EndedAtUtc"/>/<see cref="EndReason"/> via <see cref="End"/> —
/// and its transcript lives in <see cref="TicketInteractionMessage"/> rows
/// keyed by this interaction. Ending an interaction never changes the
/// ticket's own status: the chat ends, the NOC workflow continues.
/// </para>
///
/// <para>
/// <b>CustomerPhone here is the customer's identity input</b> (the number
/// CRM/PACT/Tasleeh verification searches with), distinct from
/// <see cref="CalledNumber"/> — the Tiger number the customer dialed,
/// meaningful only on the Genesys side. Genesys identifiers are external
/// identifiers stored as strings, never foreign keys, and are for
/// audit/support/integration surfaces — not for prominent display in the
/// main CS UI.
/// </para>
/// </summary>
public class TicketInteraction
{
    public const int CustomerNameMaxLength = 200;
    public const int CustomerEmailMaxLength = 256;
    public const int EndReasonMaxLength = 100;
    public const int GenesysAgentUserIdMaxLength = 64;
    public const int GenesysIdMaxLength = 64;
    public const int GenesysNameMaxLength = 200;
    public const int GenesysServiceRequestIdMaxLength = 64;

    public long TicketInteractionId { get; private set; }
    public long TicketId { get; private set; }

    /// <summary>True on the one interaction the ticket was created from — at most one per ticket (filtered unique index). Set at construction, never mutated.</summary>
    public bool IsOriginatingInteraction { get; private set; }

    public InteractionContextSource Source { get; private set; }
    /// <summary>The <see cref="Channel"/> this interaction happened on. On the originating interaction, this is the ticket's originating channel.</summary>
    public byte ChannelId { get; private set; }

    /// <summary>The customer's phone number for this interaction — the identity input for customer verification, preserved per interaction for reporting.</summary>
    public string CustomerPhone { get; private set; } = string.Empty;

    /// <summary>The customer's name as the channel reported it (e.g. the website chat form's Full Name), where available. Display/audit only — never a verified identity.</summary>
    public string? CustomerName { get; private set; }

    /// <summary>The customer's email as the channel reported it, where available. Display/audit only — never a verified identity.</summary>
    public string? CustomerEmail { get; private set; }

    /// <summary>The company/destination number the interaction arrived on (Genesys side), or null — never used by Ticketing for routing.</summary>
    public string? CalledNumber { get; private set; }

    public string? GenesysConversationId { get; private set; }

    /// <summary>
    /// Which distinct service request of the Genesys conversation this interaction (and therefore its ticket) belongs to — the caller's own
    /// identifier (e.g. "documents", "leasing-1"), stored verbatim. Null on every row created before multiple tickets per conversation
    /// existed and on any conversation whose caller sends none: such a conversation keeps its original "one conversation, one ticket" rule.
    /// (<see cref="GenesysConversationId"/>, <see cref="GenesysServiceRequestId"/>) is the unique idempotency identity of a Genesys ticket.
    /// </summary>
    public string? GenesysServiceRequestId { get; private set; }

    /// <summary>
    /// The ticket behind this interaction needs a person to work it (a leasing request, a complaint — anything the AI cannot complete).
    /// While true, no chatbot clock may close the ticket: neither inactivity rule tracks it, the AI cannot close it and document delivery
    /// leaves it alone. It ends only through the authorized TigerCS workflow (resolve / close by an employee). Distinct from an open
    /// <see cref="TicketAgentHandoff"/> (the queue of human work): this records that the ticket is human work at all.
    /// </summary>
    public bool RequiresHumanFollowUp { get; private set; }

    public string? GenesysQueueId { get; private set; }
    public string? GenesysQueueName { get; private set; }
    public string? GenesysAgentId { get; private set; }
    public string? GenesysAgentName { get; private set; }

    /// <summary>
    /// Interaction ownership (Genesys agent identity mapping): the immutable
    /// Genesys User ID of the agent that handled this interaction, exactly as
    /// Genesys sent it — recorded only once that id has been resolved to a
    /// Ticketing user (<see cref="HandledByUserId"/>), so the pair is always
    /// written together. Distinct from <see cref="GenesysAgentId"/>, which
    /// is the verbatim (unresolved) agent context Genesys reported.
    /// </summary>
    public string? GenesysAgentUserId { get; private set; }

    /// <summary>
    /// The Ticketing user (<c>AspNetUsers.Id</c>) that handled this
    /// interaction, resolved <b>server-side</b> from
    /// <see cref="GenesysAgentUserId"/> via <c>AspNetUsers.GenesysUserId</c>
    /// — never taken from a client. Null for historical rows, for
    /// system-generated interactions, and for a Genesys agent that is not
    /// mapped to a Ticketing user. This is "who handled the interaction";
    /// it is deliberately <b>not</b> the ticket's assignee/current owner,
    /// which lives on the <see cref="Ticket"/> and moves through its own
    /// assignment rules.
    /// </summary>
    public Guid? HandledByUserId { get; private set; }

    /// <summary>When the interaction started on the Genesys side, where provided — may precede ticket creation.</summary>
    public DateTime? InteractionStartedAtUtc { get; private set; }

    /// <summary>Interaction direction as reported by Genesys (e.g. "Inbound"), where available. Free text until the integration contract fixes an enumeration.</summary>
    public string? Direction { get; private set; }

    /// <summary>When the conversation ended/disconnected (Genesys' clock where it supplies one), or null while the interaction is still live or was never reported ended.</summary>
    public DateTime? EndedAtUtc { get; private set; }

    /// <summary>
    /// When TigerCS RECEIVED the end event (the server clock), as distinct from <see cref="EndedAtUtc"/> (the sender's clock for when the call ended). The anchor of the voice
    /// no-answer reconciliation window: a routing event (the answering agent) may still arrive after <c>ended</c>, so "nobody answered" is concluded only a configured time
    /// after the end was RECEIVED - never from a possibly delayed event time. Null for interactions ended before this column existed (the reconciler then falls back to <see cref="EndedAtUtc"/>).
    /// </summary>
    public DateTime? EndReceivedAtUtc { get; private set; }

    /// <summary>Why the conversation ended, as reported (e.g. "AgentDisconnect", "CustomerDisconnect", "Timeout") — free text until the Genesys contract fixes an enumeration; null when not reported.</summary>
    public string? EndReason { get; private set; }

    /// <summary>
    /// Chatbot inactivity timer: when the chatbot asked the customer a
    /// question and began waiting for the reply, as recorded by TigerCS (the
    /// server clock, never a client-supplied time). Null whenever the
    /// interaction is <b>not</b> waiting on the customer — the customer
    /// replied, a human took over, the conversation ended, or it was never a
    /// chatbot exchange. It is the persisted deadline source for the
    /// background job, so the timeout survives restarts; it is also an EF
    /// concurrency token, so a reply that clears it while the job is closing
    /// the ticket makes the job's write lose rather than close a ticket the
    /// customer just answered.
    /// </summary>
    public DateTime? AwaitingCustomerReplySinceUtc { get; private set; }

    /// <summary>The integration account that reported the prompt. The inactivity closure is attributed to it in the audit trail and on the resolution row (which needs a real user) — the closure itself is flagged as a system action.</summary>
    public Guid? AwaitingCustomerReplyReportedByEmployeeId { get; private set; }

    /// <summary>
    /// The last time the customer actually did something in this conversation (opened it, sent a message, answered the bot, asked for a
    /// document), as recorded by TigerCS's own clock — never the ticket's creation time. The source of the Website/Chatbot
    /// "customer inactive" closure. Null = not tracked (a legacy row, a voice call, a Call Center ticket, or tracking stopped because a
    /// human took over); such a row is never closed by that rule. Also an EF concurrency token, so a customer message that lands while
    /// the job is closing the ticket makes the job's write lose.
    /// </summary>
    public DateTime? LastCustomerInteractionAtUtc { get; private set; }

    /// <summary>The integration account that reported the last customer interaction; the inactivity closure is attributed to it.</summary>
    public Guid? LastCustomerInteractionReportedByEmployeeId { get; private set; }

    /// <summary>When the inactivity timeout closed this interaction's ticket, or null. Write-once: it is what makes a repeated timeout run a no-op.</summary>
    public DateTime? InactivityClosedAtUtc { get; private set; }

    /// <summary>When this row was recorded by Ticketing (creation/audit timestamp), as distinct from <see cref="InteractionStartedAtUtc"/> — Genesys' own clock.</summary>
    public DateTime CreatedAtUtc { get; private set; }

    /// <summary>A Genesys conversation on a chat/messaging channel (website chat, WhatsApp, social DM): the Website/Chatbot "customer inactive" rule's scope. Voice calls and locally-created interactions are not.</summary>
    public bool IsWebsiteOrChatbotConversation => Source == InteractionContextSource.Genesys && ChannelId != WellKnownChannels.Phone;

    /// <summary>Whether <see cref="End"/> has been recorded. Derived — the interaction's own lifecycle, independent of the ticket's <see cref="TicketStatus"/>.</summary>
    public bool IsEnded => EndedAtUtc is not null;

    private TicketInteraction() { }

    private TicketInteraction(
        long ticketId, bool isOriginatingInteraction, InteractionContextSource source,
        byte channelId, string? customerPhone, DateTime createdAtUtc)
    {
        if (channelId == 0)
        {
            throw new ArgumentException("ChannelId is required.", nameof(channelId));
        }

        // The phone is the identity input customer verification searches
        // with, but whether one MUST exist is the channel's RequiresPhone
        // configuration (enforced at intake) — a Face-to-Face / kiosk
        // interaction may legitimately carry none, stored as empty.

        TicketId = ticketId;
        IsOriginatingInteraction = isOriginatingInteraction;
        Source = source;
        ChannelId = channelId;
        CustomerPhone = customerPhone ?? string.Empty;
        CreatedAtUtc = createdAtUtc;
    }

    /// <summary>A Face-to-Face / locally-created interaction: channel and phone the agent entered; every Genesys field stays null, by construction.</summary>
    public static TicketInteraction CreateLocal(
        long ticketId, byte channelId, string? customerPhone, DateTime createdAtUtc, bool isOriginatingInteraction = false) =>
        new(ticketId, isOriginatingInteraction, InteractionContextSource.Ticketing, channelId, customerPhone, createdAtUtc);

    /// <summary>
    /// A Genesys-provided interaction. Only the conversation id is mandatory —
    /// every other field is optional until the Genesys API contract is
    /// finalized, and absent values are stored as null rather than guessed.
    /// </summary>
    public static TicketInteraction CreateFromGenesys(
        long ticketId,
        byte channelId,
        string? customerPhone,
        string genesysConversationId,
        string? calledNumber,
        string? genesysQueueId,
        string? genesysQueueName,
        string? genesysAgentId,
        string? genesysAgentName,
        DateTime? interactionStartedAtUtc,
        string? direction,
        DateTime createdAtUtc,
        bool isOriginatingInteraction = false,
        string? customerName = null,
        string? customerEmail = null,
        string? genesysServiceRequestId = null,
        bool requiresHumanFollowUp = false)
    {
        if (string.IsNullOrWhiteSpace(genesysConversationId))
        {
            throw new ArgumentException(
                "GenesysConversationId is required for a Genesys-sourced interaction — without it the row cannot link back to the conversation.",
                nameof(genesysConversationId));
        }

        return new TicketInteraction(
            ticketId, isOriginatingInteraction, InteractionContextSource.Genesys, channelId, customerPhone, createdAtUtc)
        {
            GenesysConversationId = genesysConversationId.Trim(),
            CalledNumber = calledNumber,
            GenesysQueueId = genesysQueueId,
            GenesysQueueName = genesysQueueName,
            GenesysAgentId = genesysAgentId,
            GenesysAgentName = genesysAgentName,
            InteractionStartedAtUtc = interactionStartedAtUtc,
            Direction = direction,
            CustomerName = Truncate(customerName, CustomerNameMaxLength),
            CustomerEmail = Truncate(customerEmail, CustomerEmailMaxLength),
            GenesysServiceRequestId = Truncate(genesysServiceRequestId, GenesysServiceRequestIdMaxLength),
            RequiresHumanFollowUp = requiresHumanFollowUp
        };
    }

    /// <summary>
    /// Marks the ticket as human work: from now on no chatbot clock or automatic completion may close it. Stops both inactivity clocks.
    /// Idempotent; returns true when this call changed anything. It is never undone by Genesys — a person resolves or closes the ticket.
    /// </summary>
    public bool MarkRequiresHumanFollowUp()
    {
        var changed = !RequiresHumanFollowUp || AwaitingCustomerReplySinceUtc is not null || LastCustomerInteractionAtUtc is not null;
        RequiresHumanFollowUp = true;
        AwaitingCustomerReplySinceUtc = null;
        AwaitingCustomerReplyReportedByEmployeeId = null;
        LastCustomerInteractionAtUtc = null;
        LastCustomerInteractionReportedByEmployeeId = null;
        return changed;
    }

    /// <summary>
    /// Records that the conversation ended or was disconnected — for any
    /// reason: the agent ended it, the customer closed the browser, the
    /// connection dropped, Genesys timed it out. Write-once: a redelivered
    /// end event for an already-ended interaction is refused here
    /// (<see cref="TicketInteractionAlreadyEndedException"/>) so the caller
    /// can treat it as "already processed" rather than moving the recorded
    /// end time. Deliberately touches nothing on the <see cref="Ticket"/>:
    /// an ended chat is not a closed ticket — the ticket keeps following the
    /// existing TigerCS workflow.
    /// </summary>
    public void End(DateTime endedAtUtc, string? endReason, DateTime? receivedAtUtc = null)
    {
        if (EndedAtUtc is { } already)
        {
            throw new TicketInteractionAlreadyEndedException(TicketInteractionId, already);
        }

        EndedAtUtc = endedAtUtc;
        EndReceivedAtUtc = receivedAtUtc;
        EndReason = Truncate(endReason, EndReasonMaxLength);

        // An ended conversation is not waiting on anybody: the inactivity
        // timer must never fire for it.
        AwaitingCustomerReplySinceUtc = null;
        AwaitingCustomerReplyReportedByEmployeeId = null;
        LastCustomerInteractionAtUtc = null;
        LastCustomerInteractionReportedByEmployeeId = null;
    }

    /// <summary>
    /// The chatbot sent a message that needs a customer reply: start the
    /// inactivity timer. <b>Idempotent — a repeated delivery never restarts
    /// the timer</b>: while the interaction is already waiting this returns
    /// false and changes nothing, so a retried webhook cannot postpone the
    /// deadline. A new timer starts only after
    /// <see cref="CancelAwaitingCustomerReply"/> (the customer replied) and
    /// the chatbot asks again. Refused (false) for an ended interaction or
    /// one already closed by inactivity.
    /// </summary>
    public bool BeginAwaitingCustomerReply(DateTime nowUtc, Guid reportedByEmployeeId)
    {
        if (reportedByEmployeeId == Guid.Empty)
        {
            throw new ArgumentException("ReportedByEmployeeId must be a real integration account.", nameof(reportedByEmployeeId));
        }

        if (IsEnded || RequiresHumanFollowUp || InactivityClosedAtUtc is not null || AwaitingCustomerReplySinceUtc is not null)
        {
            return false;
        }

        AwaitingCustomerReplySinceUtc = nowUtc;
        AwaitingCustomerReplyReportedByEmployeeId = reportedByEmployeeId;
        return true;
    }

    /// <summary>The customer replied, a human took over, or human follow-up was requested: stop waiting. Returns true when a timer was actually running.</summary>
    public bool CancelAwaitingCustomerReply()
    {
        if (AwaitingCustomerReplySinceUtc is null)
        {
            return false;
        }

        AwaitingCustomerReplySinceUtc = null;
        AwaitingCustomerReplyReportedByEmployeeId = null;
        return true;
    }

    /// <summary>
    /// The customer did something: start (or restart) the inactivity clock from <paramref name="nowUtc"/>. Never moves backwards, and
    /// is refused (false) once the conversation ended or its ticket was closed for inactivity. Starts tracking on a row that had none.
    /// </summary>
    public bool RecordCustomerInteraction(DateTime nowUtc, Guid reportedByEmployeeId)
    {
        if (reportedByEmployeeId == Guid.Empty)
        {
            throw new ArgumentException("ReportedByEmployeeId must be a real integration account.", nameof(reportedByEmployeeId));
        }

        if (IsEnded || RequiresHumanFollowUp || InactivityClosedAtUtc is not null || (LastCustomerInteractionAtUtc is { } last && nowUtc <= last))
        {
            return false;
        }

        LastCustomerInteractionAtUtc = nowUtc;
        LastCustomerInteractionReportedByEmployeeId = reportedByEmployeeId;
        return true;
    }

    /// <summary>Starts tracking at creation without needing a later call: the customer opening the conversation is the first interaction.</summary>
    public void StartTrackingCustomerInteraction(DateTime nowUtc, Guid reportedByEmployeeId) =>
        RecordCustomerInteraction(nowUtc, reportedByEmployeeId);

    /// <summary>Stops the "customer inactive" clock (a human took over, the ticket is owned or closed, it left the rule's scope). A later customer interaction starts it again. True when it was running.</summary>
    public bool StopTrackingCustomerInteraction()
    {
        if (LastCustomerInteractionAtUtc is null)
        {
            return false;
        }

        LastCustomerInteractionAtUtc = null;
        LastCustomerInteractionReportedByEmployeeId = null;
        return true;
    }

    /// <summary>True when the customer has been silent for strictly more than <paramref name="timeout"/> since <see cref="LastCustomerInteractionAtUtc"/> and the conversation is still live.</summary>
    public bool IsCustomerInactive(DateTime nowUtc, TimeSpan timeout) =>
        LastCustomerInteractionAtUtc is { } last && !RequiresHumanFollowUp && InactivityClosedAtUtc is null && EndedAtUtc is null && nowUtc - last > timeout;

    /// <summary>True when the timer has been running for strictly longer than <paramref name="timeout"/> at <paramref name="nowUtc"/> — "more than 5 minutes", not "5 minutes or more".</summary>
    public bool IsInactivityTimeoutDue(DateTime nowUtc, TimeSpan timeout) =>
        AwaitingCustomerReplySinceUtc is { } since && !RequiresHumanFollowUp && InactivityClosedAtUtc is null && nowUtc - since > timeout;

    /// <summary>Records that the timeout closed the ticket and stops the timer. Write-once.</summary>
    public void RecordInactivityClosure(DateTime nowUtc)
    {
        if (InactivityClosedAtUtc is not null)
        {
            return;
        }

        InactivityClosedAtUtc = nowUtc;
        AwaitingCustomerReplySinceUtc = null;
        LastCustomerInteractionAtUtc = null;
        LastCustomerInteractionReportedByEmployeeId = null;
    }

    /// <summary>
    /// Fills in the handling agent when a later event names one and the
    /// interaction does not yet know it (the start event of a queued chat
    /// often precedes agent assignment) — apply-if-absent, so an agent
    /// recorded at creation is never overwritten by a later, different value.
    /// </summary>
    public void RecordAgentIfAbsent(string? genesysAgentId, string? genesysAgentName)
    {
        if (GenesysAgentId is null && !string.IsNullOrWhiteSpace(genesysAgentId))
        {
            GenesysAgentId = genesysAgentId;
        }

        if (GenesysAgentName is null && !string.IsNullOrWhiteSpace(genesysAgentName))
        {
            GenesysAgentName = genesysAgentName;
        }
    }

    /// <summary>
    /// Records where the conversation is <b>now</b>: Genesys moved it to
    /// another queue, or connected / transferred it to another agent.
    /// Unlike <see cref="RecordAgentIfAbsent"/> this overwrites — it is an
    /// explicit "the routing changed" fact, not a late fill-in — so the
    /// queue and agent context always name the current routing, while the
    /// first agent who handled the interaction stays recorded in
    /// <see cref="HandledByUserId"/> / <see cref="GenesysAgentUserId"/>,
    /// which this never touches. The history of every change lives on the
    /// audit trail.
    ///
    /// <para>
    /// Only supplied values move: a queue change that names no agent leaves
    /// the agent as it was, and the reverse. Returns true when anything
    /// actually changed, so a redelivered routing event writes nothing.
    /// Touches nothing on the <see cref="Ticket"/> — which department owns
    /// the case, and who owns it, move only through their own TigerCS
    /// operations.
    /// </para>
    /// </summary>
    public bool RecordRouting(string? genesysQueueId, string? genesysQueueName, string? genesysAgentId, string? genesysAgentName)
    {
        var changed = false;
        Apply(Truncate(genesysQueueId, GenesysIdMaxLength), () => GenesysQueueId, v => GenesysQueueId = v);
        Apply(Truncate(genesysQueueName, GenesysNameMaxLength), () => GenesysQueueName, v => GenesysQueueName = v);
        Apply(Truncate(genesysAgentId, GenesysIdMaxLength), () => GenesysAgentId, v => GenesysAgentId = v);
        Apply(Truncate(genesysAgentName, GenesysNameMaxLength), () => GenesysAgentName, v => GenesysAgentName = v);
        return changed;

        void Apply(string? value, Func<string?> current, Action<string> set)
        {
            if (value is not null && !string.Equals(current(), value, StringComparison.Ordinal))
            {
                set(value);
                changed = true;
            }
        }
    }

    /// <summary>
    /// Fills in when the interaction started on the Genesys side, when the
    /// creating event did not carry it. Apply-if-absent: a start time, once
    /// known, never moves. Returns true when this call recorded it.
    /// </summary>
    public bool RecordStartedAtIfAbsent(DateTime? startedAtUtc)
    {
        if (InteractionStartedAtUtc is not null || startedAtUtc is null)
        {
            return false;
        }

        InteractionStartedAtUtc = startedAtUtc;
        return true;
    }

    /// <summary>
    /// Records which Ticketing user handled this interaction, as resolved
    /// server-side from the Genesys User ID. Apply-if-absent, mirroring
    /// <see cref="RecordAgentIfAbsent"/>: the first resolved agent is the
    /// one recorded, and a later, different agent never overwrites it.
    /// Returns true when the ownership was written by this call, false when
    /// it was already recorded. Both values are required together — a
    /// Genesys agent id without its resolved user (or the reverse) is never
    /// stored, because the pair is what makes the row auditable.
    /// </summary>
    public bool RecordHandlingAgentIfAbsent(string genesysAgentUserId, Guid handledByUserId)
    {
        if (string.IsNullOrWhiteSpace(genesysAgentUserId))
        {
            throw new ArgumentException("GenesysAgentUserId is required to record interaction ownership.", nameof(genesysAgentUserId));
        }

        if (handledByUserId == Guid.Empty)
        {
            throw new ArgumentException("HandledByUserId must be a resolved Ticketing user id.", nameof(handledByUserId));
        }

        if (HandledByUserId is not null)
        {
            return false;
        }

        GenesysAgentUserId = Truncate(genesysAgentUserId, GenesysAgentUserIdMaxLength);
        HandledByUserId = handledByUserId;
        return true;
    }

    private static string? Truncate(string? value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var trimmed = value.Trim();
        return trimmed.Length <= maxLength ? trimmed : trimmed[..maxLength];
    }
}
