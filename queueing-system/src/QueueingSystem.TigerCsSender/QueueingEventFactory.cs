namespace QueueingSystem.TigerCsSender;

/// <summary>
/// Hands out the <c>EventSequence</c> of a queue ticket. <b>The Queueing System's real implementation must be durable</b> (a column on the queue ticket row, incremented in
/// the same transaction that records the event): a sequence that restarts or repeats after a restart breaks TigerCS's ordering guarantee.
/// </summary>
public interface IEventSequenceProvider
{
    /// <summary>The next sequence number of this queue ticket: 1 for its first event, then 2, 3 ... never reused.</summary>
    Task<long> NextAsync(string externalTicketId, CancellationToken cancellationToken = default);
}

/// <summary>A process-local counter - for tests and single-process demos only. It forgets everything on restart.</summary>
public sealed class InMemoryEventSequenceProvider : IEventSequenceProvider
{
    private readonly Dictionary<string, long> _counters = new(StringComparer.Ordinal);

    public Task<long> NextAsync(string externalTicketId, CancellationToken cancellationToken = default)
    {
        lock (_counters)
        {
            _counters[externalTicketId] = _counters.GetValueOrDefault(externalTicketId) + 1;
            return Task.FromResult(_counters[externalTicketId]);
        }
    }
}

/// <summary>
/// Builds events that satisfy the contract by construction: a fresh EventId, the next EventSequence of the queue ticket, the time the event happened, and the members each
/// event type requires. Call it <b>when the event happens</b> (inside the queue's own transaction) and store the result in the outbox; do not build the event at send time -
/// a resend must carry the same EventId, sequence, time and content.
/// </summary>
public sealed class QueueingEventFactory(IEventSequenceProvider sequences, Func<DateTime>? utcNow = null, Func<string>? eventIds = null)
{
    private readonly Func<DateTime> _now = utcNow ?? (() => DateTime.UtcNow);
    private readonly Func<string> _newEventId = eventIds ?? (() => Guid.NewGuid().ToString());

    /// <summary>A customer took a queue ticket. Must be the ticket's first event (sequence 1).</summary>
    public async Task<QueueingEventRequest> CreatedAsync(
        string externalTicketId, string queueNumber, string companyId, string ticketTypeId, string? customerPhone,
        string? customerName = null, string? customerEmail = null, DateTime? occurredAtUtc = null, CancellationToken cancellationToken = default)
    {
        var sequence = await sequences.NextAsync(externalTicketId, cancellationToken);
        if (sequence != 1)
        {
            throw new InvalidOperationException($"TicketCreated must be the first event of queue ticket '{externalTicketId}' (sequence 1), but the next sequence is {sequence}.");
        }

        return await BuildAsync(QueueingEventType.TicketCreated, externalTicketId, sequence, occurredAtUtc, e => e with
        {
            QueueNumber = queueNumber, CompanyId = companyId, TicketTypeId = ticketTypeId, CustomerPhone = customerPhone,
            CustomerName = customerName, CustomerEmail = customerEmail
        });
    }

    public Task<QueueingEventRequest> AssignedAsync(string externalTicketId, string assignedUserId, string? counterId = null, string? performedByUserId = null, DateTime? occurredAtUtc = null, CancellationToken cancellationToken = default) =>
        StateAsync(QueueingEventType.TicketAssigned, externalTicketId, occurredAtUtc, e => e with { AssignedUserId = assignedUserId, CounterId = counterId, PerformedByUserId = performedByUserId }, cancellationToken);

    public Task<QueueingEventRequest> ReassignedAsync(string externalTicketId, string assignedUserId, string? reason = null, string? counterId = null, string? performedByUserId = null, DateTime? occurredAtUtc = null, CancellationToken cancellationToken = default) =>
        StateAsync(QueueingEventType.TicketReassigned, externalTicketId, occurredAtUtc, e => e with { AssignedUserId = assignedUserId, Reason = reason, CounterId = counterId, PerformedByUserId = performedByUserId }, cancellationToken);

    public Task<QueueingEventRequest> ReturnedAsync(string externalTicketId, string? reason = null, string? counterId = null, string? performedByUserId = null, DateTime? occurredAtUtc = null, CancellationToken cancellationToken = default) =>
        StateAsync(QueueingEventType.TicketReturned, externalTicketId, occurredAtUtc, e => e with { Reason = reason, CounterId = counterId, PerformedByUserId = performedByUserId }, cancellationToken);

    public Task<QueueingEventRequest> CommentAddedAsync(string externalTicketId, string comment, string? counterId = null, string? performedByUserId = null, DateTime? occurredAtUtc = null, CancellationToken cancellationToken = default) =>
        StateAsync(QueueingEventType.TicketCommentAdded, externalTicketId, occurredAtUtc, e => e with { Comment = comment, CounterId = counterId, PerformedByUserId = performedByUserId }, cancellationToken);

    public Task<QueueingEventRequest> RecalledAsync(string externalTicketId, string? reason = null, string? counterId = null, string? performedByUserId = null, DateTime? occurredAtUtc = null, CancellationToken cancellationToken = default) =>
        StateAsync(QueueingEventType.TicketRecalled, externalTicketId, occurredAtUtc, e => e with { Reason = reason, CounterId = counterId, PerformedByUserId = performedByUserId }, cancellationToken);

    public Task<QueueingEventRequest> ClosedAsync(string externalTicketId, string? reason = null, string? counterId = null, string? performedByUserId = null, DateTime? occurredAtUtc = null, CancellationToken cancellationToken = default) =>
        StateAsync(QueueingEventType.TicketClosed, externalTicketId, occurredAtUtc, e => e with { Reason = reason, CounterId = counterId, PerformedByUserId = performedByUserId }, cancellationToken);

    public Task<QueueingEventRequest> CancelledAsync(string externalTicketId, string? reason = null, string? counterId = null, string? performedByUserId = null, DateTime? occurredAtUtc = null, CancellationToken cancellationToken = default) =>
        StateAsync(QueueingEventType.TicketCancelled, externalTicketId, occurredAtUtc, e => e with { Reason = reason, CounterId = counterId, PerformedByUserId = performedByUserId }, cancellationToken);

    private async Task<QueueingEventRequest> StateAsync(
        QueueingEventType type, string externalTicketId, DateTime? occurredAtUtc, Func<QueueingEventRequest, QueueingEventRequest> fill, CancellationToken cancellationToken)
    {
        var sequence = await sequences.NextAsync(externalTicketId, cancellationToken);
        return await BuildAsync(type, externalTicketId, sequence, occurredAtUtc, fill);
    }

    private Task<QueueingEventRequest> BuildAsync(
        QueueingEventType type, string externalTicketId, long sequence, DateTime? occurredAtUtc, Func<QueueingEventRequest, QueueingEventRequest> fill)
    {
        var request = fill(new QueueingEventRequest
        {
            EventId = _newEventId(),
            EventType = type,
            ExternalTicketId = externalTicketId,
            EventSequence = sequence,
            OccurredAtUtc = DateTime.SpecifyKind(occurredAtUtc ?? _now(), DateTimeKind.Utc)
        });

        var problems = request.Validate(_now());
        return problems.Count > 0
            ? throw new ArgumentException("The event does not satisfy the TigerCS contract: " + string.Join(" ", problems.Values))
            : Task.FromResult(request);
    }
}
