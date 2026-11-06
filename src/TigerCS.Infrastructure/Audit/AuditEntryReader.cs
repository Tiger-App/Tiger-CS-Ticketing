using Microsoft.EntityFrameworkCore;
using TigerCS.Application.Abstractions;
using TigerCS.Domain.Audit;
using TigerCS.Infrastructure.Persistence;

namespace TigerCS.Infrastructure.Audit;

/// <summary>Read-only audit access for the ticket Activity timeline. No tracking, no writes.</summary>
public sealed class AuditEntryReader(TigerCsDbContext dbContext) : IAuditEntryReader
{
    private const string TicketEntityType = "Ticket";
    private const string ConversationEntityType = "GenesysConversation";
    private const string NotificationEntityType = "Notification";

    public async Task<IReadOnlyList<AuditEntry>> ListForTicketAsync(
        long ticketId,
        IReadOnlyCollection<string> genesysConversationIds,
        IReadOnlyCollection<string> actions,
        CancellationToken cancellationToken = default)
    {
        var ticketKey = ticketId.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var conversations = genesysConversationIds.ToList();
        var actionList = actions.ToList();

        return await dbContext.AuditEntries.AsNoTracking()
            .Where(a => actionList.Contains(a.Action)
                && ((a.EntityType == TicketEntityType && a.EntityId == ticketKey)
                    // Notification events (queued / sent / failed / skipped) are keyed by the ticket under their own entity type.
                    || (a.EntityType == NotificationEntityType && a.EntityId == ticketKey)
                    || (a.EntityType == ConversationEntityType && a.EntityId != null && conversations.Contains(a.EntityId))))
            .OrderBy(a => a.OccurredAtUtc).ThenBy(a => a.AuditEntryId)
            .ToListAsync(cancellationToken);
    }
}
