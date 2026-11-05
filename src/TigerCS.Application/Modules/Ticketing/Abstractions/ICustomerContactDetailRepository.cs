using TigerCS.Domain.Modules.Ticketing;

namespace TigerCS.Application.Modules.Ticketing.Abstractions;

/// <summary>Agent-entered customer name/e-mail, keyed by canonical phone number — see <see cref="CustomerContactDetail"/>.</summary>
public interface ICustomerContactDetailRepository
{
    /// <summary>The tracked row for one canonical phone, or null. Used by the save path, which updates it in place.</summary>
    Task<CustomerContactDetail?> GetByPhoneKeyAsync(string phoneKey, CancellationToken cancellationToken = default);

    /// <summary>Untracked rows for any of the given canonical phones, keyed by phone. Phones with no row are absent.</summary>
    Task<IReadOnlyDictionary<string, CustomerContactDetail>> ListByPhoneKeysAsync(
        IReadOnlyCollection<string> phoneKeys, CancellationToken cancellationToken = default);

    Task AddAsync(CustomerContactDetail detail, CancellationToken cancellationToken = default);
}
