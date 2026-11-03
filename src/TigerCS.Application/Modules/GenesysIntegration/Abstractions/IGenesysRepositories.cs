using TigerCS.Domain.Modules.GenesysIntegration;
using TigerCS.Domain.Modules.Ticketing;

namespace TigerCS.Application.Modules.GenesysIntegration.Abstractions;

/// <summary>Genesys Queue → Department mapping configuration (rows an administrator enters; no queue id is ever hard-coded).</summary>
public interface IGenesysQueueMappingRepository
{
    /// <summary>The ACTIVE mapping for one Genesys queue id (case-insensitive), or null when the queue is unmapped.</summary>
    Task<GenesysQueueMapping?> GetActiveByQueueIdAsync(string queueId, CancellationToken cancellationToken = default);

    Task<GenesysQueueMapping?> GetByIdAsync(int genesysQueueMappingId, CancellationToken cancellationToken = default);

    /// <summary>Every mapping, ordered by queue id — the administration listing.</summary>
    Task<IReadOnlyList<GenesysQueueMapping>> ListAsync(bool includeInactive, CancellationToken cancellationToken = default);

    /// <summary>Case-insensitive queue-id uniqueness check, optionally excluding the mapping being edited.</summary>
    Task<bool> QueueIdExistsAsync(string queueId, int? excludeMappingId, CancellationToken cancellationToken = default);

    Task AddAsync(GenesysQueueMapping mapping, CancellationToken cancellationToken = default);
}

/// <summary>
/// Reads and writes over a Genesys conversation's interaction — the
/// conversation-scoped half of <c>ITicketInteractionRepository</c> (which
/// stays ticket-scoped). Kept a separate port so the ticket-creation path is
/// untouched by this phase's additions.
/// </summary>
public interface IGenesysConversationRepository
{
    /// <summary>
    /// The interaction recorded for one Genesys conversation id, or null when the conversation was never ingested. When the conversation
    /// carries several service requests this is the <b>default</b> one — the interaction with no service request id if there is one,
    /// otherwise the earliest — which is exactly the single interaction every conversation had before service requests existed. Callers that
    /// know the ticket or the service request use the more specific lookups below.
    /// </summary>
    Task<TicketInteraction?> GetByConversationIdAsync(string genesysConversationId, CancellationToken cancellationToken = default);

    /// <summary>
    /// The interaction of one exact (conversation, service request) pair — the idempotency identity of a Genesys ticket. A null
    /// <paramref name="serviceRequestId"/> matches only the interaction that has none. The uniqueness this relies on is a database index.
    /// </summary>
    Task<TicketInteraction?> GetByConversationAndServiceRequestAsync(
        string genesysConversationId, string? serviceRequestId, CancellationToken cancellationToken = default);

    /// <summary>The interaction a given ticket has in a given conversation, or null — how an update addressed to one ticket finds its own interaction among the conversation's siblings.</summary>
    Task<TicketInteraction?> GetByConversationAndTicketAsync(
        string genesysConversationId, long ticketId, CancellationToken cancellationToken = default);

    /// <summary>Every interaction id of the given conversations (one query), keyed by conversation id — the siblings whose transcripts together form the conversation.</summary>
    Task<IReadOnlyDictionary<string, IReadOnlyList<long>>> ListInteractionIdsByConversationIdsAsync(
        IReadOnlyCollection<string> genesysConversationIds, CancellationToken cancellationToken = default);

    /// <summary>One interaction by its own id (tracked, so the caller can change it), or null.</summary>
    Task<TicketInteraction?> GetByIdAsync(long ticketInteractionId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Ids of interactions that have been waiting on a customer reply since
    /// before <paramref name="cutoffUtc"/> (exclusive), oldest first, at most
    /// <paramref name="batchSize"/>. A candidate list only — the closing
    /// service re-reads and re-checks each one before acting.
    /// </summary>
    Task<IReadOnlyList<long>> ListAwaitingReplyOlderThanAsync(DateTime cutoffUtc, int batchSize, CancellationToken cancellationToken = default);

    /// <summary>
    /// Ids of live, not-yet-inactivity-closed interactions whose last tracked customer interaction is older than <paramref name="cutoffUtc"/>,
    /// oldest first, at most <paramref name="batchSize"/>. Candidates only — the closing service re-checks each one.
    /// </summary>
    Task<IReadOnlyList<long>> ListCustomerInactiveOlderThanAsync(DateTime cutoffUtc, int batchSize, CancellationToken cancellationToken = default);

    /// <summary>Every transcript message of one interaction, in <c>Sequence</c> order.</summary>
    Task<IReadOnlyList<TicketInteractionMessage>> ListMessagesAsync(long ticketInteractionId, CancellationToken cancellationToken = default);

    /// <summary>Transcript messages for several interactions at once — one query for a whole ticket's conversation history, never one per interaction.</summary>
    Task<IReadOnlyDictionary<long, IReadOnlyList<TicketInteractionMessage>>> ListMessagesForInteractionsAsync(
        IReadOnlyCollection<long> ticketInteractionIds, CancellationToken cancellationToken = default);

    /// <summary>How many transcript messages an interaction already has — the append point for a transcript delivered in parts.</summary>
    Task<int> CountMessagesAsync(long ticketInteractionId, CancellationToken cancellationToken = default);

    Task AddMessageAsync(TicketInteractionMessage message, CancellationToken cancellationToken = default);
}

public static class GenesysConversationRepositoryExtensions
{
    /// <summary>
    /// The interaction a conversation-addressed Genesys call refers to: the exact one of <paramref name="ticketId"/> when the caller names a
    /// ticket (a conversation may carry several), otherwise the conversation's default interaction — the single interaction every
    /// conversation had before service requests existed.
    /// </summary>
    public static Task<TicketInteraction?> ResolveAsync(
        this IGenesysConversationRepository repository, string conversationId, long? ticketId, CancellationToken cancellationToken = default) =>
        ticketId is { } id
            ? repository.GetByConversationAndTicketAsync(conversationId, id, cancellationToken)
            : repository.GetByConversationIdAsync(conversationId, cancellationToken);
}

/// <summary>
/// The Genesys agent → Ticketing user mapping (<c>AspNetUsers.GenesysUserId</c>),
/// read-only from the integration's side. Implemented in Infrastructure over
/// Identity's user store; Application never sees the Identity user type.
/// <b>Nothing here creates a user</b> — mapping is an administrative act.
/// </summary>
public interface IGenesysAgentMappingRepository
{
    /// <summary>
    /// The Ticketing user mapped to one Genesys User ID, or null when no user
    /// carries that id. Matches on <c>GenesysUserId</c> only — never on
    /// email, user name or display name — and relies on the filtered unique
    /// index <c>UX_AspNetUsers_GenesysUserId</c> for there being at most one.
    /// </summary>
    Task<Dto.GenesysMappedAgent?> FindByGenesysUserIdAsync(string genesysUserId, CancellationToken cancellationToken = default);

    /// <summary>
    /// <b>Fallback only</b>, for a transcript <c>senderId</c> that arrives in e-mail form instead of as the Genesys User ID GUID. Returns
    /// every DISTINCT Ticketing user whose account e-mail (<c>AspNetUsers.NormalizedEmail</c>) or recorded <c>GenesysEmail</c> equals
    /// <paramref name="email"/> exactly (case-insensitive, whole address - no partial or display-name matching), at most two (two already
    /// means "ambiguous"). Inactive users are returned too, flagged, so a duplicate can never silently disappear. Empty = nobody.
    /// </summary>
    Task<IReadOnlyList<Dto.GenesysMappedAgent>> FindByEmailAsync(string email, CancellationToken cancellationToken = default);
}

/// <summary>
/// The Secure Screen Pop launches (<c>GenesysScreenPopLaunches</c>). Looked up
/// by token <b>hash</b> only — the raw token never reaches persistence.
/// Saves on its own because issue and redemption are each one self-contained
/// write (the launch row plus its audit entry, through the same context).
/// </summary>
public interface IGenesysScreenPopLaunchStore
{
    Task AddAsync(GenesysScreenPopLaunch launch, CancellationToken cancellationToken = default);

    Task<GenesysScreenPopLaunch?> FindByTokenHashAsync(string tokenHash, CancellationToken cancellationToken = default);

    /// <summary>
    /// Persists pending changes. Returns false — writing nothing — when a
    /// concurrent redemption of the same launch committed first
    /// (<c>RedeemedAtUtc</c> is a concurrency token), so a token can never be
    /// redeemed twice even under a race.
    /// </summary>
    Task<bool> SaveChangesAsync(CancellationToken cancellationToken = default);
}
