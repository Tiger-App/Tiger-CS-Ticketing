using TigerCS.Application.Modules.Ticketing.Abstractions;
using TigerCS.Application.Modules.Ticketing.Dto;
using TigerCS.Domain.Modules.Ticketing;

namespace TigerCS.Application.Modules.Ticketing.Services;

/// <summary>
/// Reads a customer's change history. Read-only by construction: it depends only on the append-only repository's read members, and
/// nothing in the application can edit or delete a history row.
///
/// <para>
/// The history holds personal data, so the endpoint requires the <c>Customers.ViewHistory</c> permission, and the customer must also be one
/// the caller can see in the directory - the history is never a way to browse a customer the caller could not otherwise open.
/// </para>
/// </summary>
public sealed class CustomerChangeHistoryAppService(
    CustomerDirectoryAppService directoryAppService,
    ICustomerProfileOverrideRepository overrides,
    ICustomerProfileChangeRepository changes)
{
    public const int DefaultPageSize = 25;
    public const int MaxPageSize = 100;

    public async Task<CustomerChangeHistoryResult> ListAsync(
        Guid callerEmployeeId, IReadOnlyCollection<string> callerRoles, string customerKey, int page, int pageSize,
        CancellationToken cancellationToken = default)
    {
        if (!CustomerIdentity.TryParse(customerKey, out var identity))
        {
            return CustomerChangeHistoryResult.Failure(CustomerChangeHistoryOutcome.InvalidKey);
        }

        var profile = await directoryAppService.GetProfileAsync(callerEmployeeId, callerRoles, identity.Key, cancellationToken);
        if (profile.Outcome != CustomerDirectoryProfileOutcome.Success)
        {
            return CustomerChangeHistoryResult.Failure(CustomerChangeHistoryOutcome.NotFound);
        }

        page = page < 1 ? 1 : page;
        pageSize = pageSize is < 1 or > MaxPageSize ? DefaultPageSize : pageSize;

        var anchor = await overrides.GetByKeyAsync(identity.Key, cancellationToken);
        if (anchor is null)
        {
            return CustomerChangeHistoryResult.Success(new CustomerChangeHistoryDto([], 0, page, pageSize));   // never edited: no history yet
        }

        var (items, total) = await changes.ListAsync(anchor.CustomerProfileId, [identity.Key], page, pageSize, cancellationToken);
        return CustomerChangeHistoryResult.Success(new CustomerChangeHistoryDto(
            items.Select(c => new CustomerChangeDto(
                c.CustomerProfileChangeId, c.FieldName, CustomerProfileFields.Label(c.FieldName), c.OldValue, c.NewValue,
                c.ModifiedByEmployeeId, c.ModifiedByName, c.ModifiedAtUtc, c.Source, c.ChangeReason, c.RelatedTicketId, c.CorrelationId)).ToList(),
            total, page, pageSize));
    }
}
