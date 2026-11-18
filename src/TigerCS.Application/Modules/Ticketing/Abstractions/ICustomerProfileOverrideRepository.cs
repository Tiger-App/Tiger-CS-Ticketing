using TigerCS.Domain.Modules.Ticketing;

namespace TigerCS.Application.Modules.Ticketing.Abstractions;

/// <summary>Persistence of <see cref="CustomerProfileOverride"/> - the phone / project / unit entered by hand for a Customers-directory customer.</summary>
public interface ICustomerProfileOverrideRepository
{
    /// <summary>The tracked override for one customer key, or null when the customer has none.</summary>
    Task<CustomerProfileOverride?> GetByKeyAsync(string customerKey, CancellationToken cancellationToken = default);

    Task AddAsync(CustomerProfileOverride profileOverride, CancellationToken cancellationToken = default);

    void Remove(CustomerProfileOverride profileOverride);
}
