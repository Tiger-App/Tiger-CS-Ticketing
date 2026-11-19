using TigerCS.Domain.Modules.Ticketing;

namespace TigerCS.Application.Modules.Ticketing.Abstractions;

/// <summary>Persistence of <see cref="CustomerPropertyAssociation"/> - the properties and units manually added to a customer.</summary>
public interface ICustomerPropertyAssociationRepository
{
    Task<IReadOnlyList<CustomerPropertyAssociation>> ListByProfileAsync(Guid customerProfileId, CancellationToken cancellationToken = default);

    /// <summary>The tracked association, only if it belongs to the customer (an id from another customer is never found).</summary>
    Task<CustomerPropertyAssociation?> GetAsync(Guid customerProfileId, long associationId, CancellationToken cancellationToken = default);

    Task AddAsync(CustomerPropertyAssociation association, CancellationToken cancellationToken = default);

    void Remove(CustomerPropertyAssociation association);
}
