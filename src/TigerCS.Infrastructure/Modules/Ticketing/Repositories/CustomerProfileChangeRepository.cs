using Microsoft.EntityFrameworkCore;
using TigerCS.Application.Modules.Ticketing.Abstractions;
using TigerCS.Domain.Modules.Ticketing;
using TigerCS.Infrastructure.Persistence;

namespace TigerCS.Infrastructure.Modules.Ticketing.Repositories;

public sealed class CustomerProfileChangeRepository(TigerCsDbContext dbContext) : ICustomerProfileChangeRepository
{
    public Task AddRangeAsync(IEnumerable<CustomerProfileChange> changes, CancellationToken cancellationToken = default) =>
        dbContext.CustomerProfileChanges.AddRangeAsync(changes, cancellationToken);

    public Task<bool> OperationExistsAsync(Guid customerProfileId, Guid correlationId, CancellationToken cancellationToken = default) =>
        dbContext.CustomerProfileChanges.AsNoTracking().AnyAsync(c => c.CustomerProfileId == customerProfileId && c.CorrelationId == correlationId, cancellationToken);

    public async Task<(IReadOnlyList<CustomerProfileChange> Items, int Total)> ListAsync(
        Guid customerProfileId, IReadOnlyCollection<string> customerKeys, int page, int pageSize, CancellationToken cancellationToken = default)
    {
        var query = dbContext.CustomerProfileChanges.AsNoTracking()
            .Where(c => c.CustomerProfileId == customerProfileId || customerKeys.Contains(c.CustomerKey));
        var total = await query.CountAsync(cancellationToken);
        var items = await query
            .OrderByDescending(c => c.ModifiedAtUtc).ThenByDescending(c => c.CustomerProfileChangeId)
            .Skip((page - 1) * pageSize).Take(pageSize)
            .ToListAsync(cancellationToken);
        return (items, total);
    }
}
