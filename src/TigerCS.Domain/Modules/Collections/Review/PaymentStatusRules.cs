namespace TigerCS.Domain.Modules.Collections.Review;

public enum ReviewPaymentStatus
{
    Unknown = 0,
    Unpaid = 1,
    /// <summary>Kept for rows stored before the remaining-amount rule; the rule itself no longer separates partly paid from unpaid.</summary>
    PartiallyPaid = 2,
    Paid = 3
}

/// <summary>
/// Payment status comes from the verified REMAINING amount PACT returns for the instalment, the one figure every procedure reports:
/// <list type="bullet">
/// <item>remaining = 0: <see cref="ReviewPaymentStatus.Paid"/>;</item>
/// <item>remaining &gt; 0: <see cref="ReviewPaymentStatus.Unpaid"/> (partly paid or not; the campaigns collect what remains, so the two are not told apart);</item>
/// <item>remaining missing, negative, or contradicted by the source: <see cref="ReviewPaymentStatus.Unknown"/>, which needs review. The source contradicts itself when it says <c>Paid</c> but a
/// remainder is owed, or when the V2 report's original and paid amounts do not add up to the remainder (or the remainder exceeds the original).</item>
/// </list>
/// A due date never decides payment status, and a configured text mapping never overrides the remaining amount.
/// </summary>
public static class PaymentStatusRules
{
    public static ReviewPaymentStatus Derive(string? sourceStatus, decimal? remaining, decimal? planAmount = null, decimal? allocatedAmount = null)
    {
        if (remaining is not { } amount || amount < 0) return ReviewPaymentStatus.Unknown;                                        // missing or invalid
        if (string.Equals(sourceStatus?.Trim(), "Paid", StringComparison.OrdinalIgnoreCase) && amount > 0) return ReviewPaymentStatus.Unknown;   // contradictory
        if (planAmount is { } original)
        {
            // The V2 breakdown must agree with the remainder: original = paid + remaining, and a remainder never exceeds the original.
            if (original < 0 || amount > original + Tolerance) return ReviewPaymentStatus.Unknown;
            if (allocatedAmount is { } paid && (paid < 0 || !AmountsAddUp(original, paid, amount))) return ReviewPaymentStatus.Unknown;
        }
        else if (allocatedAmount is { } paid && paid < 0) return ReviewPaymentStatus.Unknown;
        return amount == 0 ? ReviewPaymentStatus.Paid : ReviewPaymentStatus.Unpaid;
    }

    private const decimal Tolerance = 0.00005m;

    /// <summary>original = paid + remaining, to the source's four-decimal precision.</summary>
    public static bool AmountsAddUp(decimal original, decimal paid, decimal remaining) => Math.Abs(original - paid - remaining) < Tolerance;

    /// <summary>One unit combines several open instalments: all must be known and open, otherwise Unknown. (A Paid instalment is not a receivable and never reaches a record.)</summary>
    public static ReviewPaymentStatus Combine(IReadOnlyCollection<ReviewPaymentStatus> statuses)
    {
        if (statuses.Count == 0 || statuses.Any(s => s is ReviewPaymentStatus.Unknown or ReviewPaymentStatus.Paid)) return ReviewPaymentStatus.Unknown;
        return statuses.All(s => s == ReviewPaymentStatus.Unpaid) ? ReviewPaymentStatus.Unpaid : ReviewPaymentStatus.PartiallyPaid;
    }
}
