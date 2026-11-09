using System.Globalization;

namespace TigerCS.Domain.Modules.Collections.Review;

public enum AmountPrecision
{
    /// <summary>Exactly representable in fils (two decimals).</summary>
    Exact = 0,

    /// <summary>A floating-point source value within tolerance of a fils amount; normalized to that amount.</summary>
    FloatingPointNoise,

    /// <summary>More than two decimals on a valid positive balance: rounded to fils (half away from zero) by the business rule. The original value is kept alongside.</summary>
    Rounded,

    /// <summary>No fils amount can honestly stand for the value: a positive balance that rounds to zero (below 0.005), or a negative one. It stays blocked and is never rounded or treated as paid.</summary>
    Unresolved
}

/// <param name="Value">The amount in fils precision, or null when the precision is Unresolved.</param>
/// <param name="Raw">The value exactly as read from the source.</param>
/// <param name="Precision">How the value relates to a two-decimal amount.</param>
public sealed record MoneyResult(decimal? Value, decimal Raw, AmountPrecision Precision)
{
    public bool IsResolved => Precision != AmountPrecision.Unresolved;
}

/// <summary>
/// Monetary values are decimal throughout. A valid positive remaining amount with more than two decimals is rounded to fils with
/// <see cref="MidpointRounding.AwayFromZero"/> (business decision: extra decimals alone are not a reason to hold a customer); the original value is always kept (<see cref="MoneyResult.Raw"/>) for
/// traceability, and Paid / Unpaid is decided from the amount BEFORE rounding. A floating-point source within the float tolerance of a fils amount is representation noise and is snapped to it; a
/// remainder that snaps to zero is a settled instalment. A positive balance that would round to zero (below 0.005) or a negative one is <b>unresolved</b>: blocked, never rounded away into "paid".
/// </summary>
public static class MoneyNormalizer
{
    public const decimal DefaultFloatTolerance = 0.0001m;

    public static MoneyResult Normalize(decimal raw, bool sourceIsFloatingPoint, decimal floatTolerance = DefaultFloatTolerance)
    {
        var rounded = decimal.Round(raw, 2, MidpointRounding.AwayFromZero);
        if (raw == rounded) return new(rounded, raw, AmountPrecision.Exact);
        if (sourceIsFloatingPoint && Math.Abs(raw - rounded) <= floatTolerance)
            return new(rounded, raw, AmountPrecision.FloatingPointNoise);
        // Rounding is only for a valid, positive balance that still has an amount after rounding: 0.004 must not silently become a settled 0.00.
        if (raw > 0 && rounded > 0) return new(rounded, raw, AmountPrecision.Rounded);
        return new(null, raw, AmountPrecision.Unresolved);
    }

    /// <summary>Invariant, culture-independent, always two decimals, no thousands separator (the Genesys AmountDue format).</summary>
    public static string Format(decimal amount) => amount.ToString("0.00", CultureInfo.InvariantCulture);
}
