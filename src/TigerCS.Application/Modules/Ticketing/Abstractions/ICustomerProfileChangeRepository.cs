using TigerCS.Domain.Modules.Ticketing;

namespace TigerCS.Application.Modules.Ticketing.Abstractions;

/// <summary>
/// Append-only access to the customer change history. There is deliberately no update or delete member: history can be added to and read,
/// nothing else.
/// </summary>
public interface ICustomerProfileChangeRepository
{
    Task AddRangeAsync(IEnumerable<CustomerProfileChange> changes, CancellationToken cancellationToken = default);

    /// <summary>True when any change of this operation (correlation id) is already recorded for the customer - the retry check.</summary>
    Task<bool> OperationExistsAsync(Guid customerProfileId, Guid correlationId, CancellationToken cancellationToken = default);

    /// <summary>One page of a customer's history, newest first, for the given stable identifier and any earlier directory keys.</summary>
    Task<(IReadOnlyList<CustomerProfileChange> Items, int Total)> ListAsync(
        Guid customerProfileId, IReadOnlyCollection<string> customerKeys, int page, int pageSize, CancellationToken cancellationToken = default);
}
