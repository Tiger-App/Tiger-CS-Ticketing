using Microsoft.EntityFrameworkCore;
using TigerCS.Application.Modules.Ticketing.Abstractions;
using TigerCS.Domain.Modules.Ticketing;
using TigerCS.Infrastructure.Persistence;

namespace TigerCS.Infrastructure.Modules.Ticketing.Repositories;

public sealed class CustomerContactDetailRepository(TigerCsDbContext dbContext) : ICustomerContactDetailRepository
{
    public Task<CustomerContactDetail?> GetByPhoneKeyAsync(string phoneKey, CancellationToken cancellationToken = default) =>
        dbContext.CustomerContactDetails.FirstOrDefaultAsync(c => c.PhoneKey == phoneKey, cancellationToken);

    public async Task<IReadOnlyDictionary<string, CustomerContactDetail>> ListByPhoneKeysAsync(
        IReadOnlyCollection<string> phoneKeys, CancellationToken cancellationToken = default) =>
        await dbContext.CustomerContactDetails.AsNoTracking()
            .Where(c => phoneKeys.Contains(c.PhoneKey))
            .ToDictionaryAsync(c => c.PhoneKey, cancellationToken);

    public async Task AddAsync(CustomerContactDetail detail, CancellationToken cancellationToken = default) =>
        await dbContext.CustomerContactDetails.AddAsync(detail, cancellationToken);
}
