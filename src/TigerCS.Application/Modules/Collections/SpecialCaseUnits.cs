using TigerCS.Domain.Modules.Collections;

namespace TigerCS.Application.Modules.Collections;

/// <summary>
/// The "special case" units (<c>CollectionsSource:PactReceivables:SpecialCaseUnitCodes</c>, copied from the original <c>Helper.SEPCIAL_CASES</c>): units that are handled outside the normal
/// Collections campaigns. They are never listed, previewed, approved, exported or sent by the Campaigns pipeline (Manual, Auto, Export for Genesys, Send to Genesys). They are NOT removed
/// from the financial source, so Receivables and the reconciliation still see them. Only the units that are listed are excluded; other units of the same customer are untouched.
/// Codes are compared in one normalized form (<see cref="CollectionsUnitKey.Normalize"/>: trimmed, upper case, <c>TP</c> prefix), so <c>tp129-8303 </c> and <c>TP129-8303</c> are the same unit.
/// </summary>
public sealed class SpecialCaseUnits
{
    public static readonly SpecialCaseUnits None = new([]);

    private readonly HashSet<string> keys;

    public SpecialCaseUnits(IEnumerable<string?> codes)
    {
        keys = codes.Select(CollectionsUnitKey.Normalize).Where(k => k is not null).Select(k => k!).ToHashSet(StringComparer.Ordinal);
        // For the database: a stored code is compared as TRIM + UPPER, with and without the "TP" prefix.
        StoredVariants = keys.SelectMany(k => new[] { k, k[2..] }).Distinct(StringComparer.Ordinal).ToList();
    }

    public static SpecialCaseUnits From(PactReceivablesOptions? options) => options is null ? None : new(options.SpecialCaseUnitCodes ?? []);

    public bool Any => keys.Count > 0;

    /// <summary>The upper-case, trimmed forms a stored unit code is matched against in the database (null when nothing is configured).</summary>
    public IReadOnlyList<string>? StoredVariants { get; }

    public bool IsSpecial(string? unitCode) => keys.Count > 0 && CollectionsUnitKey.Normalize(unitCode) is { } key && keys.Contains(key);
}
