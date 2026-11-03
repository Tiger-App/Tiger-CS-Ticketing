using TigerCS.Application.Modules.GenesysIntegration.Abstractions;
using TigerCS.Application.Modules.GenesysIntegration.Dto;
using TigerCS.Domain.Modules.GenesysIntegration;
using TigerCS.Domain.Modules.Ticketing;
using TigerCS.Tests.Ticketing.Fakes;

namespace TigerCS.Tests.GenesysIntegration.Fakes;

public sealed class FakeGenesysQueueMappingRepository : IGenesysQueueMappingRepository
{
    private readonly List<GenesysQueueMapping> _mappings = [];
    private int _nextId = 1;

    public IReadOnlyList<GenesysQueueMapping> All => _mappings;

    public GenesysQueueMapping Add(GenesysQueueMapping mapping)
    {
        typeof(GenesysQueueMapping).GetProperty(nameof(GenesysQueueMapping.GenesysQueueMappingId))!
            .SetValue(mapping, _nextId++);
        _mappings.Add(mapping);
        return mapping;
    }

    /// <summary>Maps a queue to a department, active — the common test setup.</summary>
    public GenesysQueueMapping Map(string queueId, int departmentId, bool isActive = true) =>
        Add(new GenesysQueueMapping(queueId, $"Queue {queueId}", departmentId, DateTime.UtcNow, isActive));

    public Task<GenesysQueueMapping?> GetActiveByQueueIdAsync(string queueId, CancellationToken cancellationToken = default) =>
        Task.FromResult(_mappings.FirstOrDefault(m =>
            m.IsActive && string.Equals(m.QueueId, queueId.Trim(), StringComparison.OrdinalIgnoreCase)));

    public Task<GenesysQueueMapping?> GetByIdAsync(int genesysQueueMappingId, CancellationToken cancellationToken = default) =>
        Task.FromResult(_mappings.FirstOrDefault(m => m.GenesysQueueMappingId == genesysQueueMappingId));

    public Task<IReadOnlyList<GenesysQueueMapping>> ListAsync(bool includeInactive, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<GenesysQueueMapping>>(
            _mappings.Where(m => includeInactive || m.IsActive).OrderBy(m => m.QueueId, StringComparer.Ordinal).ToList());

    public Task<bool> QueueIdExistsAsync(string queueId, int? excludeMappingId, CancellationToken cancellationToken = default) =>
        Task.FromResult(_mappings.Any(m =>
            string.Equals(m.QueueId, queueId.Trim(), StringComparison.OrdinalIgnoreCase)
            && (excludeMappingId is null || m.GenesysQueueMappingId != excludeMappingId)));

    public Task AddAsync(GenesysQueueMapping mapping, CancellationToken cancellationToken = default)
    {
        Add(mapping);
        return Task.CompletedTask;
    }
}

/// <summary>
/// Conversation-scoped view over the SAME interaction store the ticket
/// creation path writes to (<see cref="FakeTicketInteractionRepository"/>) —
/// deliberately not a second list. If these two diverged, a test could
/// "prove" idempotency against a store nothing actually wrote to.
/// </summary>
public sealed class FakeGenesysConversationRepository(FakeTicketInteractionRepository interactions)
    : IGenesysConversationRepository
{
    private readonly List<TicketInteractionMessage> _messages = [];
    private long _nextId = 1;

    public IReadOnlyList<TicketInteractionMessage> AllMessages => _messages;

    public Task<TicketInteraction?> GetByConversationIdAsync(
        string genesysConversationId, CancellationToken cancellationToken = default) =>
        Task.FromResult(
            interactions.All
                .Where(i => i.GenesysConversationId == genesysConversationId.Trim())
                .OrderBy(i => i.GenesysServiceRequestId != null)
                .ThenBy(i => i.TicketInteractionId)
                .FirstOrDefault());

    public Task<TicketInteraction?> GetByConversationAndServiceRequestAsync(
        string genesysConversationId, string? serviceRequestId, CancellationToken cancellationToken = default)
    {
        var serviceRequest = string.IsNullOrWhiteSpace(serviceRequestId) ? null : serviceRequestId.Trim();
        return Task.FromResult(interactions.All.FirstOrDefault(
            i => i.GenesysConversationId == genesysConversationId.Trim() && i.GenesysServiceRequestId == serviceRequest));
    }

    public Task<TicketInteraction?> GetByConversationAndTicketAsync(
        string genesysConversationId, long ticketId, CancellationToken cancellationToken = default) =>
        Task.FromResult(interactions.All.FirstOrDefault(
            i => i.GenesysConversationId == genesysConversationId.Trim() && i.TicketId == ticketId));

    public Task<IReadOnlyDictionary<string, IReadOnlyList<long>>> ListInteractionIdsByConversationIdsAsync(
        IReadOnlyCollection<string> genesysConversationIds, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyDictionary<string, IReadOnlyList<long>>>(
            interactions.All
                .Where(i => i.GenesysConversationId is not null && genesysConversationIds.Contains(i.GenesysConversationId))
                .GroupBy(i => i.GenesysConversationId!)
                .ToDictionary(g => g.Key, g => (IReadOnlyList<long>)g.Select(i => i.TicketInteractionId).OrderBy(id => id).ToList()));

    public Task<TicketInteraction?> GetByIdAsync(long ticketInteractionId, CancellationToken cancellationToken = default) =>
        Task.FromResult(interactions.All.FirstOrDefault(i => i.TicketInteractionId == ticketInteractionId));

    public Task<IReadOnlyList<long>> ListAwaitingReplyOlderThanAsync(
        DateTime cutoffUtc, int batchSize, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<long>>(
            interactions.All
                .Where(i => i.AwaitingCustomerReplySinceUtc is { } since && since < cutoffUtc)
                .OrderBy(i => i.AwaitingCustomerReplySinceUtc)
                .Select(i => i.TicketInteractionId)
                .Take(batchSize)
                .ToList());

    public Task<IReadOnlyList<long>> ListCustomerInactiveOlderThanAsync(
        DateTime cutoffUtc, int batchSize, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<long>>(
            interactions.All
                .Where(i => i.LastCustomerInteractionAtUtc is { } last && last < cutoffUtc && i.InactivityClosedAtUtc is null && i.EndedAtUtc is null)
                .OrderBy(i => i.LastCustomerInteractionAtUtc)
                .Select(i => i.TicketInteractionId)
                .Take(batchSize)
                .ToList());

    public Task<IReadOnlyList<TicketInteractionMessage>> ListMessagesAsync(
        long ticketInteractionId, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<TicketInteractionMessage>>(
            _messages.Where(m => m.TicketInteractionId == ticketInteractionId).OrderBy(m => m.Sequence).ToList());

    public Task<IReadOnlyDictionary<long, IReadOnlyList<TicketInteractionMessage>>> ListMessagesForInteractionsAsync(
        IReadOnlyCollection<long> ticketInteractionIds, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyDictionary<long, IReadOnlyList<TicketInteractionMessage>>>(
            _messages
                .Where(m => ticketInteractionIds.Contains(m.TicketInteractionId))
                .GroupBy(m => m.TicketInteractionId)
                .ToDictionary(g => g.Key, g => (IReadOnlyList<TicketInteractionMessage>)g.OrderBy(m => m.Sequence).ToList()));

    public Task<int> CountMessagesAsync(long ticketInteractionId, CancellationToken cancellationToken = default) =>
        Task.FromResult(_messages.Count(m => m.TicketInteractionId == ticketInteractionId));

    public Task AddMessageAsync(TicketInteractionMessage message, CancellationToken cancellationToken = default)
    {
        // Mirror the database's unique (interaction, sequence) index so no
        // test can pass while writing a transcript with a duplicate position.
        if (_messages.Any(m => m.TicketInteractionId == message.TicketInteractionId && m.Sequence == message.Sequence))
        {
            throw new InvalidOperationException(
                $"Interaction {message.TicketInteractionId} already has a message at sequence {message.Sequence}.");
        }

        typeof(TicketInteractionMessage).GetProperty(nameof(TicketInteractionMessage.TicketInteractionMessageId))!
            .SetValue(message, _nextId++);
        _messages.Add(message);
        return Task.CompletedTask;
    }
}

/// <summary>
/// Secure Screen Pop launches, mirroring the unique token-hash index.
/// <see cref="LoseNextSaveRace"/> simulates a concurrent redemption committing
/// first (the <c>RedeemedAtUtc</c> concurrency token), which the real store
/// reports as <c>false</c>.
/// </summary>
public sealed class FakeGenesysScreenPopLaunchStore : IGenesysScreenPopLaunchStore
{
    private readonly List<GenesysScreenPopLaunch> _launches = [];
    private long _nextId = 1;

    public IReadOnlyList<GenesysScreenPopLaunch> All => _launches;

    public bool LoseNextSaveRace { get; set; }

    public Task AddAsync(GenesysScreenPopLaunch launch, CancellationToken cancellationToken = default)
    {
        if (_launches.Any(l => l.TokenHash == launch.TokenHash))
        {
            throw new InvalidOperationException("UX_GenesysScreenPopLaunches_TokenHash violated.");
        }

        typeof(GenesysScreenPopLaunch).GetProperty(nameof(GenesysScreenPopLaunch.GenesysScreenPopLaunchId))!
            .SetValue(launch, _nextId++);
        _launches.Add(launch);
        return Task.CompletedTask;
    }

    public Task<GenesysScreenPopLaunch?> FindByTokenHashAsync(string tokenHash, CancellationToken cancellationToken = default) =>
        Task.FromResult(_launches.FirstOrDefault(l => l.TokenHash == tokenHash));

    public Task<bool> SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        if (LoseNextSaveRace)
        {
            LoseNextSaveRace = false;
            return Task.FromResult(false);
        }

        return Task.FromResult(true);
    }
}

/// <summary>
/// The Genesys agent → Ticketing user mapping (<c>AspNetUsers.GenesysUserId</c>
/// joined to the active-employee rule), as the resolver reads it. Mirrors
/// the database's filtered unique index <c>UX_AspNetUsers_GenesysUserId</c>:
/// mapping the same Genesys User ID to a second user is refused here exactly
/// as SQL Server refuses it, so no test can pass while relying on a
/// many-to-one mapping. Nothing here creates a user from a request.
/// </summary>
public sealed class FakeGenesysAgentMappingRepository : IGenesysAgentMappingRepository
{
    private readonly List<GenesysMappedAgent> _mappings = [];

    private readonly List<(Guid UserId, string Email)> _accountEmails = [];

    public IReadOnlyList<GenesysMappedAgent> All => _mappings;

    /// <summary>Sets a Ticketing account e-mail (<c>AspNetUsers.Email</c>) - several users may share one, as Identity does not forbid it here.</summary>
    public void SetAccountEmail(Guid userId, string email) => _accountEmails.Add((userId, email));

    /// <summary>A Ticketing user with an account e-mail but NO Genesys User ID yet (an admin mapping gap).</summary>
    public GenesysMappedAgent MapWithoutGenesysId(Guid userId, string email, string displayName = "Unmapped Agent", bool isActive = true)
    {
        var mapped = new GenesysMappedAgent(userId, string.Empty, null, $"user-{userId:N}", displayName, isActive);
        _mappings.Add(mapped);
        _accountEmails.Add((userId, email));
        return mapped;
    }

    public GenesysMappedAgent Map(Guid userId, string genesysUserId, string? genesysEmail = null, string displayName = "Mapped Agent", bool isActive = true)
    {
        if (_mappings.Any(m => string.Equals(m.GenesysUserId, genesysUserId, StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidOperationException(
                $"Genesys user '{genesysUserId}' is already mapped to a Ticketing user (UX_AspNetUsers_GenesysUserId).");
        }

        if (_mappings.Any(m => m.UserId == userId))
        {
            throw new InvalidOperationException($"Ticketing user {userId} already carries a Genesys mapping.");
        }

        var mapped = new GenesysMappedAgent(userId, genesysUserId, genesysEmail, $"user-{userId:N}", displayName, isActive);
        _mappings.Add(mapped);
        return mapped;
    }

    /// <summary>Removes the mapping — an administrator clearing <c>AspNetUsers.GenesysUserId</c>.</summary>
    public void Unmap(string genesysUserId) =>
        _mappings.RemoveAll(m => string.Equals(m.GenesysUserId, genesysUserId, StringComparison.OrdinalIgnoreCase));

    /// <summary>Deactivates the mapped user's employee record.</summary>
    public void Deactivate(string genesysUserId)
    {
        var index = _mappings.FindIndex(m => string.Equals(m.GenesysUserId, genesysUserId, StringComparison.OrdinalIgnoreCase));
        _mappings[index] = _mappings[index] with { IsActive = false };
    }

    public Task<GenesysMappedAgent?> FindByGenesysUserIdAsync(string genesysUserId, CancellationToken cancellationToken = default) =>
        Task.FromResult(_mappings.FirstOrDefault(m =>
            string.Equals(m.GenesysUserId, genesysUserId.Trim(), StringComparison.OrdinalIgnoreCase)));

    public Task<IReadOnlyList<GenesysMappedAgent>> FindByEmailAsync(string email, CancellationToken cancellationToken = default)
    {
        var wanted = email.Trim();
        var ids = _accountEmails.Where(a => string.Equals(a.Email, wanted, StringComparison.OrdinalIgnoreCase)).Select(a => a.UserId)
            .Concat(_mappings.Where(m => string.Equals(m.GenesysEmail, wanted, StringComparison.OrdinalIgnoreCase)).Select(m => m.UserId))
            .Distinct().OrderBy(id => id).Take(2).ToList();
        return Task.FromResult<IReadOnlyList<GenesysMappedAgent>>(_mappings.Where(m => ids.Contains(m.UserId)).OrderBy(m => m.UserId).ToList());
    }
}
