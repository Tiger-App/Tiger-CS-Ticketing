using System.Text.Json;
using TigerCS.Domain.Modules.Collections;
using TigerCS.Domain.Modules.Collections.Review;

namespace TigerCS.Application.Modules.Collections.Review;

/// <summary>
/// Turns the Direct Send filter (Payment status, From / To, Minimum Total) into the criteria of <see cref="DirectSendAmounts"/>. The same criteria are used to list, approve, revalidate before
/// the upload and run the paid-after-upload check, so the amount of a customer can never differ between them.
/// </summary>
public static class DirectSendFilters
{
    /// <param name="filter">The Direct Send filter.</param>
    /// <param name="defaultMinimum">The minimum applied when the filter carries none (the review default); null = no minimum.</param>
    public static DirectSendCriteria Criteria(ReviewFilter filter, decimal? defaultMinimum)
    {
        CampaignPaymentFilters.TryParse(filter.DueStatus, out var status);   // an unknown value was already refused when the filter was parsed
        return new(status, filter.DueFrom, filter.DueTo, filter.MinRemaining ?? defaultMinimum);
    }

    /// <summary>The criteria a dispatch was approved with (its stored filter). Null when the stored filter cannot be read: nothing can then be verified and the contact is not sent / is suppressed.</summary>
    public static DirectSendCriteria? FromStored(string? filterJson)
    {
        if (string.IsNullOrWhiteSpace(filterJson)) return null;
        try { return JsonSerializer.Deserialize<ReviewFilter>(filterJson) is { } filter ? Criteria(filter, null) : null; }
        catch (JsonException) { return null; }
    }

    /// <summary>The Direct Send destination (<c>CurrentMonth</c> / <c>FollowUp</c>) an approval was made with, read from its stored filter; null when missing or unreadable.</summary>
    public static string? StoredContactList(string? filterJson)
    {
        if (string.IsNullOrWhiteSpace(filterJson)) return null;
        try { return JsonSerializer.Deserialize<ReviewFilter>(filterJson)?.ContactList; }
        catch (JsonException) { return null; }
    }

    /// <summary>The amount and earliest due date a Direct Send item is worth NOW: calculated from the customer's current instalments with the approval's own filter.</summary>
    public static DirectSendAmount CurrentAmount(CollectionsReviewRecord current, DirectSendCriteria criteria, DateOnly today) =>
        DirectSendAmounts.Calculate(DirectSendAmounts.TryParse(current.InstalmentsJson), criteria, today);
}
