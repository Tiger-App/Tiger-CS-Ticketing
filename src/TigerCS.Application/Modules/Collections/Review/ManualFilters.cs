using System.Text.Json;
using TigerCS.Domain.Modules.Collections;
using TigerCS.Domain.Modules.Collections.Review;

namespace TigerCS.Application.Modules.Collections.Review;

/// <summary>
/// Turns the Manual filter (Payment status, From / To, Minimum Total) into the criteria of <see cref="FilteredAmounts"/>. The same criteria are used to list, approve, revalidate before
/// the upload and run the paid-after-upload check, so the amount of a customer can never differ between them.
/// </summary>
public static class ManualFilters
{
    /// <param name="filter">The Direct Send filter.</param>
    /// <param name="defaultMinimum">The minimum applied when the filter carries none (the review default); null = no minimum.</param>
    public static AmountCriteria Criteria(ReviewFilter filter, decimal? defaultMinimum)
    {
        CampaignPaymentFilters.TryParse(filter.DueStatus, out var status);   // an unknown value was already refused when the filter was parsed
        return new(status, filter.DueFrom, filter.DueTo, filter.MinRemaining ?? defaultMinimum);
    }

    /// <summary>
    /// The criteria a MANUAL dispatch was approved with (its stored filter). Null for any other dispatch (Auto sends the stage amount). A Manual filter that cannot be read is also null, and the
    /// stage amount then differs from the filtered amount that was quoted, so nothing unverifiable is sent and an uploaded contact is suppressed.
    /// </summary>
    public static AmountCriteria? FromStored(string? filterJson) => ReadStored(filterJson) is { } filter && CollectionsSendingModes.IsManual(filter.SendingMode) ? Criteria(filter, null) : null;

    private static ReviewFilter? ReadStored(string? filterJson)
    {
        if (string.IsNullOrWhiteSpace(filterJson)) return null;
        try { return JsonSerializer.Deserialize<ReviewFilter>(filterJson); }
        catch (JsonException) { return null; }
    }

    /// <summary>The contact list a Manual approval chose (a campaign type name), read from its stored filter; null when none was chosen (the campaign type's own list) or the filter is unreadable.</summary>
    public static string? StoredContactList(string? filterJson) => ReadStored(filterJson)?.ContactList;

    /// <summary>The amount and earliest due date a Direct Send item is worth NOW: calculated from the customer's current instalments with the approval's own filter.</summary>
    public static FilteredAmount CurrentAmount(CollectionsReviewRecord current, AmountCriteria criteria, DateOnly today) =>
        FilteredAmounts.Calculate(FilteredAmounts.TryParse(current.InstalmentsJson), criteria, today);
}
