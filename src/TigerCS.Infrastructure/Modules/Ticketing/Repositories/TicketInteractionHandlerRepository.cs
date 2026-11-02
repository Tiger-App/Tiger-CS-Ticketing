using Microsoft.EntityFrameworkCore;
using TigerCS.Application.Modules.Ticketing.Abstractions;
using TigerCS.Domain.Modules.Ticketing;
using TigerCS.Infrastructure.Persistence;

namespace TigerCS.Infrastructure.Modules.Ticketing.Repositories;

public sealed class TicketInteractionHandlerRepository(TigerCsDbContext dbContext) : ITicketInteractionHandlerRepository
{
    public async Task<bool> AddIfAbsentAsync(TicketInteractionHandler handler, CancellationToken cancellationToken = default)
    {
        var interactionId = handler.TicketInteractionId;
        var userId = handler.HandledByUserId;

        // Pending rows in this unit of work first (not yet visible to the database), then the table.
        var exists = dbContext.TicketInteractionHandlers.Local.Any(h => h.TicketInteractionId == interactionId && h.HandledByUserId == userId)
            || await dbContext.TicketInteractionHandlers.AnyAsync(
                h => h.TicketInteractionId == interactionId && h.HandledByUserId == userId, cancellationToken);
        if (exists)
        {
            return false;
        }

        await dbContext.TicketInteractionHandlers.AddAsync(handler, cancellationToken);
        return true;
    }

    public void Discard(TicketInteractionHandler handler) =>
        dbContext.Entry(handler).State = EntityState.Detached;

    public Task<bool> HasHandledAsync(long ticketId, Guid userId, CancellationToken cancellationToken = default) =>
        dbContext.TicketInteractionHandlers.AsNoTracking().AnyAsync(
            h => h.TicketId == ticketId && h.HandledByUserId == userId, cancellationToken);

    public async Task<bool> HasActiveHandlerGrantAsync(long ticketId, Guid userId, CancellationToken cancellationToken = default)
    {
        var lastTransferUtc = await dbContext.TicketWorkflowEvents.AsNoTracking()
            .Where(e => e.TicketId == ticketId
                && e.EventType == WorkflowEventType.TicketTransferred
                && e.TransferReasonCode != (byte)TicketTransferReason.SystemRouting)
            .MaxAsync(e => (DateTime?)e.OccurredAtUtc, cancellationToken);

        return await dbContext.TicketInteractionHandlers.AsNoTracking().AnyAsync(
            h => h.TicketId == ticketId && h.HandledByUserId == userId
                && (lastTransferUtc == null || h.RecordedAtUtc > lastTransferUtc), cancellationToken);
    }

    public async Task<IReadOnlyList<TicketInteractionHandler>> ListByTicketIdAsync(long ticketId, CancellationToken cancellationToken = default) =>
        await dbContext.TicketInteractionHandlers.AsNoTracking()
            .Where(h => h.TicketId == ticketId)
            .OrderBy(h => h.RecordedAtUtc).ThenBy(h => h.TicketInteractionHandlerId)
            .ToListAsync(cancellationToken);
}
