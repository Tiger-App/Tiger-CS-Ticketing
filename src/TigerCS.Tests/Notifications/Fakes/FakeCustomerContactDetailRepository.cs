using TigerCS.Application.Modules.Ticketing.Abstractions;
using TigerCS.Domain.Modules.Ticketing;

namespace TigerCS.Tests.Notifications.Fakes;

/// <summary>In-memory customer contact details (the name/email an agent typed in for a manually entered customer), keyed by canonical phone.</summary>
public sealed class FakeCustomerContactDetailRepository : ICustomerContactDetailRepository
{
    private readonly Dictionary<string, CustomerContactDetail> _byPhone = [];

    public CustomerContactDetail Save(string phone, string name, string? email)
    {
        var detail = new CustomerContactDetail(CustomerPhoneNumber.Normalize(phone), name, email, Guid.NewGuid(), DateTime.UtcNow);
        _byPhone[detail.PhoneKey] = detail;
        return detail;
    }

    public Task<CustomerContactDetail?> GetByPhoneKeyAsync(string phoneKey, CancellationToken cancellationToken = default) =>
        Task.FromResult(_byPhone.GetValueOrDefault(phoneKey));

    public Task<IReadOnlyDictionary<string, CustomerContactDetail>> ListByPhoneKeysAsync(
        IReadOnlyCollection<string> phoneKeys, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyDictionary<string, CustomerContactDetail>>(
            _byPhone.Where(e => phoneKeys.Contains(e.Key)).ToDictionary(e => e.Key, e => e.Value));

    public Task AddAsync(CustomerContactDetail detail, CancellationToken cancellationToken = default)
    {
        _byPhone[detail.PhoneKey] = detail;
        return Task.CompletedTask;
    }
}
