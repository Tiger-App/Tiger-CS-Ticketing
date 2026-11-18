using Microsoft.EntityFrameworkCore;
using TigerCS.Application.Modules.Ticketing.Abstractions;
using TigerCS.Domain.Modules.Ticketing;
using TigerCS.Infrastructure.Persistence;

namespace TigerCS.Infrastructure.Modules.Ticketing.Repositories;

public sealed class CustomerProfileOverrideRepository(TigerCsDbContext dbContext) : ICustomerProfileOverrideRepository
{
    public Task<CustomerProfileOverride?> GetByKeyAsync(string customerKey, CancellationToken cancellationToken = default) =>
        dbContext.CustomerProfileOverrides.FirstOrDefaultAsync(o => o.CustomerKey == customerKey, cancellationToken);

    public async Task AddAsync(CustomerProfileOverride profileOverride, CancellationToken cancellationToken = default) =>
        await dbContext.CustomerProfileOverrides.AddAsync(profileOverride, cancellationToken);

    public void Remove(CustomerProfileOverride profileOverride) => dbContext.CustomerProfileOverrides.Remove(profileOverride);
}
