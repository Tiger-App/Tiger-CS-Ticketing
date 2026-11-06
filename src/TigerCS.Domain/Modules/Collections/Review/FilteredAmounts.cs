using System.Globalization;

namespace TigerCS.Domain.Modules.Collections.Review;

/// <summary>One unpaid instalment of a customer: its due date and its REMAINING amount (never the plan amount).</summary>
public sealed record FilterInstalment(DateOnly Due, decimal Amount);

/// <summary>
/// What the person chose on the Manual page: Payment status, From / To date (inclusive, on the instalment due date) and Minimum Total. A null date is open-ended; a null or zero
/// minimum means no minimum.
/// </summary>
public sealed record AmountCriteria(CampaignPaymentFilter Status, DateOnly? From, DateOnly? To, decimal? MinTotal);

/// <param name="Amount">The qualifying amount: the sum of the selected instalments (0 when none).</param>
/// <param name="DueAmount">The part of <paramref name="Amount"/> due inside the current month.</param>
/// <param name="OverdueAmount">The part of <paramref name="Amount"/> due before the current month.</param>
/// <param name="EarliestDue">The earliest due date among the selected instalments (null when none).</param>
/// <param name="Reason"><c>Qualifies</c>, <c>NoQualifyingBalance</c> (nothing is left once the filters are applied), <c>BelowMinimum</c>, <c>AmbiguousInstalments</c> or
/// <c>InstalmentsUnavailable</c> (the stored instalments cannot be read).</param>
public sealed record FilteredAmount(decimal Amount, decimal DueAmount, decimal OverdueAmount, DateOnly? EarliestDue, string Reason)
{
    public bool Qualifies => Reason == Qualified;
    public const string Qualified = "Qualifies", None = "NoQualifyingBalance", BelowMinimum = "BelowMinimum", Ambiguous = "AmbiguousInstalments", Unavailable = "InstalmentsUnavailable";
}

/// <summary>
/// THE Manual amount. The amount a customer is listed with, approved with, revalidated against before the upload, quoted to Genesys and checked by the paid-after-upload sweep is always
/// this one calculation over the customer's stored unpaid instalments:
/// <list type="bullet">
/// <item><b>Payment status</b> (the Campaigns rule, <see cref="CollectionsDueMonth"/>): Due = instalments due inside the current Dubai month, Overdue = due before it, Both = either. Nothing due from
/// the next month on is ever included.</item>
/// <item><b>From / To</b>: only instalments whose due date is inside the range.</item>
/// <item><b>Minimum Total</b>: the customer is kept only when the filtered amount is GREATER than it (the rule the Campaigns page states).</item>
/// </list>
/// Nothing is ever double counted: a customer is one stored list of instalments, and several instalments on one due date cannot be told apart from a duplicated allocation, so they are refused
/// (<c>AmbiguousInstalments</c>) instead of being summed. The source amounts are only read, never changed.
/// </summary>
public static class FilteredAmounts
{
    /// <summary>Compact, culture-free storage: <c>yyyyMMdd=amount;yyyyMMdd=amount</c>.</summary>
    public static string Serialize(IEnumerable<FilterInstalment> instalments) =>
        string.Join(';', instalments.OrderBy(i => i.Due).ThenBy(i => i.Amount)
            .Select(i => i.Due.ToString("yyyyMMdd", CultureInfo.InvariantCulture) + "=" + i.Amount.ToString("0.####", CultureInfo.InvariantCulture)));

    /// <summary>The stored instalments, or null when the text is missing or malformed (never a partial list).</summary>
    public static IReadOnlyList<FilterInstalment>? TryParse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var list = new List<FilterInstalment>();
        foreach (var part in text.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var pair = part.Split('=');
            if (pair.Length != 2
                || !DateOnly.TryParseExact(pair[0], "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var due)
                || !decimal.TryParse(pair[1], NumberStyles.Number, CultureInfo.InvariantCulture, out var amount) || amount <= 0) return null;
            list.Add(new FilterInstalment(due, amount));
        }
        return list.Count == 0 ? null : list;
    }

    public static FilteredAmount Calculate(IReadOnlyList<FilterInstalment>? instalments, AmountCriteria criteria, DateOnly today)
    {
        if (instalments is null) return new(0m, 0m, 0m, null, FilteredAmount.Unavailable);
        var selected = instalments.Where(i => i.Amount > 0 && CollectionsDueMonth.IsInScope(i.Due, today)
            && criteria.Status switch
            {
                CampaignPaymentFilter.Due => CollectionsDueMonth.IsDue(i.Due, today),
                CampaignPaymentFilter.Overdue => CollectionsDueMonth.IsOverdue(i.Due, today),
                _ => true
            }
            && (criteria.From is not { } from || i.Due >= from) && (criteria.To is not { } to || i.Due <= to)).ToList();
        if (selected.Count == 0) return new(0m, 0m, 0m, null, FilteredAmount.None);
        var earliest = selected.Min(i => i.Due);
        if (selected.GroupBy(i => i.Due).Any(g => g.Count() > 1)) return new(0m, 0m, 0m, earliest, FilteredAmount.Ambiguous);
        var due = selected.Where(i => CollectionsDueMonth.IsDue(i.Due, today)).Sum(i => i.Amount);
        var overdue = selected.Where(i => CollectionsDueMonth.IsOverdue(i.Due, today)).Sum(i => i.Amount);
        var amount = due + overdue;
        return new(amount, due, overdue, earliest, criteria.MinTotal is { } min && min > 0 && amount <= min ? FilteredAmount.BelowMinimum : FilteredAmount.Qualified);
    }
}
