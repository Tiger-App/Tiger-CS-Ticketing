using System.Globalization;

namespace TigerCS.Domain.Modules.Collections;

/// <summary>
/// The floor of an APARTMENT, derived from its PACT unit code. Neither PACT's receivables output, the local snapshot (<c>dbo.CollectionsRcvLine</c> / <c>CollectionsRcvAccountUnit</c>), the review records nor the
/// CRM unit-owner feed carry a floor column (CRM's own <c>tblUnits</c> floor is not part of the feed), so the floor is read from the unit number. No database change is needed.
/// <para>
/// The unit code is <c>[TP]&lt;tower&gt;-&lt;unit part&gt;</c>. The unit part must contain EXACTLY ONE apartment number and nothing else but single-letter block / suffix segments:
/// an apartment number is 3 or 4 digits with an optional single trailing letter. The floor is the number divided by 100 (whole part).
/// </para>
/// <code>
/// TP140-101 -> 1      TP140-1921 -> 19     TP140-0507 -> 5      TP130-601-A -> 6      TP136-C-402 -> 4      TP120-601A -> 6
/// TP140-P12 / TP140-12 / TP140-SHOP1 / TP140 / TP140-101-X7 / TP140-101-202 -> none (not a recognisable apartment: never guessed)
/// </code>
/// <para>A unit code that does not follow this pattern (parking, retail, a tower with another numbering) has NO floor: it is never listed in the Floor dropdown and never matches a Floor filter, instead of being
/// assigned a wrong floor. Mirrors SQL <c>dbo.fn_CollectionsUnitFloor</c> (V015) exactly. Cancelled units (a <c>*</c> anywhere in the code) have no floor.</para>
/// </summary>
public static class CollectionsUnitFloor
{
    /// <summary>The highest floor a 4-digit apartment number can denote (9999 / 100).</summary>
    public const int MaxFloor = 99;

    /// <summary>The floor of an apartment unit code, or null when the code is not a recognisable apartment number.</summary>
    public static int? Parse(string? unitCode)
    {
        var value = unitCode?.Trim();
        if (string.IsNullOrEmpty(value) || value.Contains('*')) return null;
        if (value.StartsWith("TP", StringComparison.OrdinalIgnoreCase)) value = value[2..].TrimStart();
        var hyphen = value.IndexOf('-');
        if (hyphen < 0) return null;                                       // a tower only, no unit part
        string? apartment = null;
        var segments = 0;
        foreach (var raw in value[(hyphen + 1)..].Split('-'))
        {
            var segment = raw.Trim();
            if (++segments > 3) return null;
            if (IsApartmentNumber(segment))
            {
                if (apartment is not null) return null;                    // two apartment numbers: ambiguous
                apartment = segment;
            }
            else if (!(segment.Length == 1 && char.IsAsciiLetter(segment[0]))) return null;   // only a single-letter block / suffix may accompany the number
        }
        if (apartment is null) return null;
        var digits = apartment.Length > 0 && char.IsAsciiLetter(apartment[^1]) ? apartment[..^1] : apartment;
        return int.Parse(digits, NumberStyles.None, CultureInfo.InvariantCulture) / 100;
    }

    /// <summary>3 or 4 digits, optionally followed by ONE letter (<c>601A</c>).</summary>
    private static bool IsApartmentNumber(string segment)
    {
        var digits = segment.Length > 0 && char.IsAsciiLetter(segment[^1]) ? segment[..^1] : segment;
        return digits.Length is 3 or 4 && digits.All(char.IsAsciiDigit);
    }

    /// <summary>"Ground floor" for 0, otherwise "Floor N".</summary>
    public static string Label(int floor) => floor == 0 ? "Ground floor" : $"Floor {floor.ToString(CultureInfo.InvariantCulture)}";

    /// <summary>Whether a unit belongs to one of the chosen floors. An empty / null selection = All floors (no filtering, every unit, apartment or not).</summary>
    public static bool Matches(string? unitCode, IReadOnlySet<int>? floors) =>
        floors is not { Count: > 0 } || (Parse(unitCode) is { } floor && floors.Contains(floor));

    /// <summary>The chosen floors normalised: distinct, valid (0-99), ascending. Junk is dropped; empty = All floors.</summary>
    public static IReadOnlyList<int> Normalize(IEnumerable<int>? floors) =>
        (floors ?? []).Where(f => f is >= 0 and <= MaxFloor).Distinct().Order().ToList();

    /// <summary>True when any selected value is not a valid floor, so a malformed request is refused instead of silently turning into All floors.</summary>
    public static bool HasInvalid(IEnumerable<int>? floors) => (floors ?? []).Any(f => f is < 0 or > MaxFloor);
}
