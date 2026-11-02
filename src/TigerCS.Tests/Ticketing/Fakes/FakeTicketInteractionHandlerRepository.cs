using TigerCS.Application.Modules.Ticketing.Abstractions;
using TigerCS.Domain.Modules.Ticketing;

namespace TigerCS.Tests.Ticketing.Fakes;

/// <summary>In-memory <see cref="ITicketInteractionHandlerRepository"/>: unique per (interaction, user), exactly like the table's unique index.</summary>
public sealed class FakeTicketInteractionHandlerRepository : ITicketInteractionHandlerRepository
{
    private readonly List<TicketInteractionHandler> _handlers = [];
    private long _nextId = 1;

    public IReadOnlyList<TicketInteractionHandler> All => _handlers;

    /// <summary>
    /// When true every user counts as having handled every ticket. For tests whose subject is some OTHER rule (reopen windows, SLA clocks,
    /// classification…) and that use a CS Agent as an arbitrary actor on a ticket they neither own nor handled: it keeps CS Agent ticket
    /// visibility out of their way without changing what they assert. Tests OF visibility never use it.
    /// </summary>
    public bool EveryUserHandlesEveryTicket { get; init; }

    /// <summary>A repository on which every user has handled every ticket — see <see cref="EveryUserHandlesEveryTicket"/>.</summary>
    public static FakeTicketInteractionHandlerRepository Permissive() => new() { EveryUserHandlesEveryTicket = true };

    public Task<bool> AddIfAbsentAsync(TicketInteractionHandler handler, CancellationToken cancellationToken = default)
    {
        if (_handlers.Any(h => h.TicketInteractionId == handler.TicketInteractionId && h.HandledByUserId == handler.HandledByUserId))
        {
            return Task.FromResult(false);
        }

        typeof(TicketInteractionHandler).GetProperty(nameof(TicketInteractionHandler.TicketInteractionHandlerId))!
            .SetValue(handler, _nextId++);
        _handlers.Add(handler);
        return Task.FromResult(true);
    }

    public void Discard(TicketInteractionHandler handler) => _handlers.Remove(handler);

    /// <summary>When a ticket was last manually transferred — handling recorded before it no longer carries modify access. Set by tests that model a transfer.</summary>
    public Dictionary<long, DateTime> ManualTransferAtUtc { get; } = [];

    public Task<bool> HasActiveHandlerGrantAsync(long ticketId, Guid userId, CancellationToken cancellationToken = default) =>
        Task.FromResult(EveryUserHandlesEveryTicket
            || _handlers.Any(h => h.TicketId == ticketId && h.HandledByUserId == userId
                && (!ManualTransferAtUtc.TryGetValue(ticketId, out var at) || h.RecordedAtUtc > at)));

    public bool HasHandledSync(long ticketId, Guid userId) =>
        EveryUserHandlesEveryTicket || _handlers.Any(h => h.TicketId == ticketId && h.HandledByUserId == userId);

    public Task<bool> HasHandledAsync(long ticketId, Guid userId, CancellationToken cancellationToken = default) =>
        Task.FromResult(HasHandledSync(ticketId, userId));

    public Task<IReadOnlyList<TicketInteractionHandler>> ListByTicketIdAsync(long ticketId, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<TicketInteractionHandler>>([.. _handlers.Where(h => h.TicketId == ticketId)]);
}
