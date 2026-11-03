using Microsoft.EntityFrameworkCore;
using TigerCS.Application.Modules.GenesysIntegration.Abstractions;
using TigerCS.Application.Modules.GenesysIntegration.Dto;
using TigerCS.Domain.Modules.GenesysIntegration;
using TigerCS.Domain.Modules.Ticketing;
using TigerCS.Infrastructure.Persistence;

namespace TigerCS.Infrastructure.Modules.GenesysIntegration.Repositories;

public sealed class GenesysQueueMappingRepository(TigerCsDbContext dbContext) : IGenesysQueueMappingRepository
{
    public Task<GenesysQueueMapping?> GetActiveByQueueIdAsync(string queueId, CancellationToken cancellationToken = default)
    {
        var value = queueId.Trim();
        return dbContext.GenesysQueueMappings.FirstOrDefaultAsync(
            m => m.IsActive && m.QueueId == value, cancellationToken);
    }

    public Task<GenesysQueueMapping?> GetByIdAsync(int genesysQueueMappingId, CancellationToken cancellationToken = default) =>
        dbContext.GenesysQueueMappings.FirstOrDefaultAsync(
            m => m.GenesysQueueMappingId == genesysQueueMappingId, cancellationToken);

    public async Task<IReadOnlyList<GenesysQueueMapping>> ListAsync(bool includeInactive, CancellationToken cancellationToken = default)
    {
        var query = dbContext.GenesysQueueMappings.AsQueryable();
        if (!includeInactive)
        {
            query = query.Where(m => m.IsActive);
        }

        return await query.OrderBy(m => m.QueueId).ToListAsync(cancellationToken);
    }

    public Task<bool> QueueIdExistsAsync(string queueId, int? excludeMappingId, CancellationToken cancellationToken = default)
    {
        var value = queueId.Trim();
        return dbContext.GenesysQueueMappings.AnyAsync(
            m => m.QueueId == value && (excludeMappingId == null || m.GenesysQueueMappingId != excludeMappingId),
            cancellationToken);
    }

    public async Task AddAsync(GenesysQueueMapping mapping, CancellationToken cancellationToken = default) =>
        await dbContext.GenesysQueueMappings.AddAsync(mapping, cancellationToken);
}

/// <summary>
/// Conversation-scoped reads/writes over the interaction and its transcript.
/// The unique filtered index on <c>TicketInteractions (GenesysConversationId,
/// GenesysServiceRequestId)</c> is what makes "one service request, one
/// interaction" (and, for a caller that sends no service request id, "one
/// conversation, one interaction") a database fact rather than an
/// assumption this repository would otherwise be making.
/// </summary>
public sealed class GenesysConversationRepository(TigerCsDbContext dbContext) : IGenesysConversationRepository
{
    public Task<TicketInteraction?> GetByConversationIdAsync(
        string genesysConversationId, CancellationToken cancellationToken = default)
    {
        var value = genesysConversationId.Trim();
        return dbContext.TicketInteractions
            .Where(i => i.GenesysConversationId == value)
            .OrderBy(i => i.GenesysServiceRequestId != null)
            .ThenBy(i => i.TicketInteractionId)
            .FirstOrDefaultAsync(cancellationToken);
    }

    public Task<TicketInteraction?> GetByConversationAndServiceRequestAsync(
        string genesysConversationId, string? serviceRequestId, CancellationToken cancellationToken = default)
    {
        var conversation = genesysConversationId.Trim();
        var serviceRequest = string.IsNullOrWhiteSpace(serviceRequestId) ? null : serviceRequestId.Trim();
        return dbContext.TicketInteractions.FirstOrDefaultAsync(
            i => i.GenesysConversationId == conversation && i.GenesysServiceRequestId == serviceRequest, cancellationToken);
    }

    public Task<TicketInteraction?> GetByConversationAndTicketAsync(
        string genesysConversationId, long ticketId, CancellationToken cancellationToken = default)
    {
        var conversation = genesysConversationId.Trim();
        return dbContext.TicketInteractions
            .Where(i => i.GenesysConversationId == conversation && i.TicketId == ticketId)
            .OrderBy(i => i.TicketInteractionId)
            .FirstOrDefaultAsync(cancellationToken);
    }

    public async Task<IReadOnlyDictionary<string, IReadOnlyList<long>>> ListInteractionIdsByConversationIdsAsync(
        IReadOnlyCollection<string> genesysConversationIds, CancellationToken cancellationToken = default)
    {
        if (genesysConversationIds.Count == 0)
        {
            return new Dictionary<string, IReadOnlyList<long>>();
        }

        var rows = await dbContext.TicketInteractions
            .AsNoTracking()
            .Where(i => i.GenesysConversationId != null && genesysConversationIds.Contains(i.GenesysConversationId))
            .Select(i => new { ConversationId = i.GenesysConversationId!, i.TicketInteractionId })
            .ToListAsync(cancellationToken);

        return rows
            .GroupBy(r => r.ConversationId)
            .ToDictionary(g => g.Key, g => (IReadOnlyList<long>)g.Select(r => r.TicketInteractionId).OrderBy(id => id).ToList());
    }

    public Task<TicketInteraction?> GetByIdAsync(long ticketInteractionId, CancellationToken cancellationToken = default) =>
        dbContext.TicketInteractions.FirstOrDefaultAsync(i => i.TicketInteractionId == ticketInteractionId, cancellationToken);

    public async Task<IReadOnlyList<long>> ListAwaitingReplyOlderThanAsync(
        DateTime cutoffUtc, int batchSize, CancellationToken cancellationToken = default) =>
        await dbContext.TicketInteractions
            .AsNoTracking()
            .Where(i => i.AwaitingCustomerReplySinceUtc != null && i.AwaitingCustomerReplySinceUtc < cutoffUtc)
            .OrderBy(i => i.AwaitingCustomerReplySinceUtc)
            .Select(i => i.TicketInteractionId)
            .Take(batchSize)
            .ToListAsync(cancellationToken);

    public async Task<IReadOnlyList<long>> ListCustomerInactiveOlderThanAsync(
        DateTime cutoffUtc, int batchSize, CancellationToken cancellationToken = default) =>
        await dbContext.TicketInteractions
            .AsNoTracking()
            .Where(i => i.LastCustomerInteractionAtUtc != null && i.LastCustomerInteractionAtUtc < cutoffUtc
                && i.InactivityClosedAtUtc == null && i.EndedAtUtc == null)
            .OrderBy(i => i.LastCustomerInteractionAtUtc)
            .Select(i => i.TicketInteractionId)
            .Take(batchSize)
            .ToListAsync(cancellationToken);

    public async Task<IReadOnlyList<TicketInteractionMessage>> ListMessagesAsync(
        long ticketInteractionId, CancellationToken cancellationToken = default) =>
        await dbContext.TicketInteractionMessages
            .Where(m => m.TicketInteractionId == ticketInteractionId)
            .OrderBy(m => m.Sequence)
            .ToListAsync(cancellationToken);

    /// <summary>One query for every interaction's transcript — never one per interaction (the same no-N+1 discipline the customer-history read follows).</summary>
    public async Task<IReadOnlyDictionary<long, IReadOnlyList<TicketInteractionMessage>>> ListMessagesForInteractionsAsync(
        IReadOnlyCollection<long> ticketInteractionIds, CancellationToken cancellationToken = default)
    {
        if (ticketInteractionIds.Count == 0)
        {
            return new Dictionary<long, IReadOnlyList<TicketInteractionMessage>>();
        }

        var messages = await dbContext.TicketInteractionMessages
            .Where(m => ticketInteractionIds.Contains(m.TicketInteractionId))
            .OrderBy(m => m.TicketInteractionId)
            .ThenBy(m => m.Sequence)
            .ToListAsync(cancellationToken);

        return messages
            .GroupBy(m => m.TicketInteractionId)
            .ToDictionary(g => g.Key, g => (IReadOnlyList<TicketInteractionMessage>)g.ToList());
    }

    public Task<int> CountMessagesAsync(long ticketInteractionId, CancellationToken cancellationToken = default) =>
        dbContext.TicketInteractionMessages.CountAsync(m => m.TicketInteractionId == ticketInteractionId, cancellationToken);

    public async Task AddMessageAsync(TicketInteractionMessage message, CancellationToken cancellationToken = default) =>
        await dbContext.TicketInteractionMessages.AddAsync(message, cancellationToken);
}

/// <summary>
/// The Genesys agent → Ticketing user mapping, read from
/// <c>AspNetUsers.GenesysUserId</c> joined to <c>Employees</c> for the
/// active-employee rule. Matches on the Genesys User ID and nothing else;
/// the filtered unique index <c>UX_AspNetUsers_GenesysUserId</c> is what
/// makes "at most one" a database fact. Read-only: no code path creates a
/// user from a Genesys request.
/// </summary>
public sealed class GenesysAgentMappingRepository(TigerCsDbContext dbContext) : IGenesysAgentMappingRepository
{
    public async Task<GenesysMappedAgent?> FindByGenesysUserIdAsync(
        string genesysUserId, CancellationToken cancellationToken = default)
    {
        var value = genesysUserId.Trim();

        var row = await dbContext.Users.AsNoTracking()
            .Where(u => u.GenesysUserId == value)
            .Select(u => new
            {
                u.Id,
                u.GenesysUserId,
                u.GenesysEmail,
                u.UserName,
                Employee = dbContext.Employees.AsNoTracking()
                    .Where(e => e.EmployeeId == u.Id)
                    .Select(e => new { e.DisplayName, e.DeactivatedAtUtc })
                    .FirstOrDefault()
            })
            .Take(2)
            .ToListAsync(cancellationToken);

        // Two rows for one Genesys id can only mean the unique index is missing or disabled. Never pick one of them.
        if (row.Count > 1)
        {
            var first = row[0];
            return new GenesysMappedAgent(first.Id, first.GenesysUserId!, first.GenesysEmail, first.UserName, first.Employee?.DisplayName, IsActive: false, IsAmbiguous: true);
        }

        return row.Count == 0
            ? null
            : ToMapped(row[0].Id, row[0].GenesysUserId!, row[0].GenesysEmail, row[0].UserName, row[0].Employee?.DisplayName, row[0].Employee is not null && row[0].Employee!.DeactivatedAtUtc == null);
    }

    private static GenesysMappedAgent ToMapped(Guid id, string genesysUserId, string? email, string? userName, string? displayName, bool isActive) =>
        // The existing rule (ActiveEmployeeRequirement): an Employee row that is not deactivated. No Employee row is not a valid, active Ticketing user either.
        new(id, genesysUserId, email, userName, displayName, isActive);
}

/// <summary>
/// Secure Screen Pop launches, looked up by token hash through the unique
/// index <c>UX_GenesysScreenPopLaunches_TokenHash</c>. A lost race on
/// <c>RedeemedAtUtc</c> (its concurrency token) is answered with false, not
/// an exception: "someone redeemed it first" is an expected outcome.
/// </summary>
public sealed class GenesysScreenPopLaunchStore(TigerCsDbContext dbContext) : IGenesysScreenPopLaunchStore
{
    public async Task AddAsync(GenesysScreenPopLaunch launch, CancellationToken cancellationToken = default) =>
        await dbContext.GenesysScreenPopLaunches.AddAsync(launch, cancellationToken);

    public Task<GenesysScreenPopLaunch?> FindByTokenHashAsync(string tokenHash, CancellationToken cancellationToken = default) =>
        dbContext.GenesysScreenPopLaunches.FirstOrDefaultAsync(l => l.TokenHash == tokenHash, cancellationToken);

    public async Task<bool> SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
            return true;
        }
        catch (DbUpdateConcurrencyException)
        {
            return false;
        }
    }
}
