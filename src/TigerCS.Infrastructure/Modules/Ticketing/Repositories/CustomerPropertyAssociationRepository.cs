using Microsoft.EntityFrameworkCore;
using TigerCS.Application.Modules.Ticketing.Abstractions;
using TigerCS.Domain.Modules.Ticketing;
using TigerCS.Infrastructure.Persistence;

namespace TigerCS.Infrastructure.Modules.Ticketing.Repositories;

public sealed class CustomerPropertyAssociationRepository(TigerCsDbContext dbContext) : ICustomerPropertyAssociationRepository
{
    public async Task<IReadOnlyList<CustomerPropertyAssociation>> ListByProfileAsync(Guid customerProfileId, CancellationToken cancellationToken = default) =>
        await dbContext.CustomerPropertyAssociations
            .Where(a => a.CustomerProfileId == customerProfileId)
            .OrderBy(a => a.ProjectName).ThenBy(a => a.UnitNumber)
            .ToListAsync(cancellationToken);

    public Task<CustomerPropertyAssociation?> GetAsync(Guid customerProfileId, long associationId, CancellationToken cancellationToken = default) =>
        dbContext.CustomerPropertyAssociations.FirstOrDefaultAsync(
            a => a.CustomerPropertyAssociationId == associationId && a.CustomerProfileId == customerProfileId, cancellationToken);

    public async Task AddAsync(CustomerPropertyAssociation association, CancellationToken cancellationToken = default) =>
        await dbContext.CustomerPropertyAssociations.AddAsync(association, cancellationToken);

    public void Remove(CustomerPropertyAssociation association) => dbContext.CustomerPropertyAssociations.Remove(association);
}
