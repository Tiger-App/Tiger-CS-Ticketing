using TigerCS.Domain.Audit;

namespace TigerCS.Application.Abstractions;

/// <summary>
/// Read-only port over the append-only audit trail, used by the ticket Activity timeline. It only ever reads: nothing here
/// writes, rewrites or deletes an audit entry.
/// </summary>
public interface IAuditEntryReader
{
    /// <summary>
    /// The audit entries that describe one ticket: those recorded against the ticket itself (<c>EntityType = "Ticket"</c>, its id) and
    /// those recorded against any of its Genesys conversations (<c>EntityType = "GenesysConversation"</c>, the conversation id), limited to
    /// the given <paramref name="actions"/>. Oldest first.
    /// </summary>
    Task<IReadOnlyList<AuditEntry>> ListForTicketAsync(
        long ticketId,
        IReadOnlyCollection<string> genesysConversationIds,
        IReadOnlyCollection<string> actions,
        CancellationToken cancellationToken = default);
}
