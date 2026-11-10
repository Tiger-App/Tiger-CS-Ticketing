using TigerCS.Domain.Modules.Collections;
using TigerCS.Domain.Modules.Collections.Review;

namespace TigerCS.Application.Modules.Collections.Exceptions;

/// <summary>Exact, normalized unit-code matching: surrounding white space and letter case are ignored; nothing else (no prefix, wildcard or partial match).</summary>
public static class UnitCodeNormalizer
{
    public static string Normalize(string? unitCode) => CollectionsUnitKey.Normalize(unitCode) ?? "";
}

/// <param name="Excluded">True when the unit is not part of the campaign.</param>
/// <param name="ReasonCode">The <see cref="ReviewReasons"/> code of the exclusion (<c>SpecialCaseExcluded</c> or <c>GulfCustomerExcluded</c>); null when allowed.</param>
/// <param name="Notice">A non-blocking <see cref="ReviewReasons"/> warning shown for a unit that is allowed only by an exception (<c>GulfInclusionDay</c> or <c>ManuallyIncluded</c>); null otherwise.</param>
public sealed record ExceptionDecision(bool Excluded, string? ReasonCode, string? Notice = null)
{
    public static readonly ExceptionDecision Allowed = new(false, null);
}

/// <summary>
/// The two independent unit-level exclusion policies of Collections campaigns. Pure: it reads the configured lists once and decides from the unit code, the Dubai business date and whether
/// the unit was included manually. Every place that lists, approves, exports or sends a unit asks this one class, so they cannot disagree.
/// <list type="number">
/// <item><b>Permanent special cases</b> (<c>SpecialCasesConfigured</c> + <c>SpecialCaseUnitCodes</c>): excluded from every campaign, on every date, in both modes. Nothing overrides it.</item>
/// <item><b>GULF_CUSTOMERS / CHEQUES</b> (<c>GulfCustomersConfigured</c> + <c>GulfCustomerUnitCodes</c>): excluded, except in a MANUAL campaign on the inclusion day of the month (the 14th, Dubai date) or
/// when an authorized person included the unit manually in that campaign. Auto never includes them, so the day-14 window is an eligibility window, not a schedule.</item>
/// </list>
/// A unit in both lists stays excluded (the special case wins). Only the unit itself is affected: other units of the same customer and shared phone numbers are never considered.
/// </summary>
public sealed class CollectionsExceptionPolicy
{
    private readonly SpecialCaseUnits special;
    private readonly HashSet<string> gulf;
    private readonly int inclusionDay;

    public CollectionsExceptionPolicy(PactReceivablesOptions options)
    {
        // The one special-case list and normalization of the Campaigns pipeline (SpecialCaseUnits): active whenever codes are configured.
        special = SpecialCaseUnits.From(options);
        gulf = options.GulfCustomersConfigured ? ToSet(options.GulfCustomerUnitCodes) : [];
        inclusionDay = options.GulfCustomersInclusionDay;
    }

    private static HashSet<string> ToSet(IEnumerable<string>? codes) =>
        (codes ?? []).Select(UnitCodeNormalizer.Normalize).Where(c => c.Length > 0).ToHashSet(StringComparer.Ordinal);

    /// <summary>True when at least one unit can be affected (nothing is evaluated or logged otherwise).</summary>
    public bool HasExclusions => special.Any || gulf.Count > 0;

    public IReadOnlyCollection<string> GulfUnitCodes => gulf;

    public bool IsSpecialCase(string? unitCode) => special.IsSpecial(unitCode);

    public bool IsGulfCustomer(string? unitCode) => gulf.Contains(UnitCodeNormalizer.Normalize(unitCode));

    /// <summary>The monthly GULF_CUSTOMERS day (the 14th) in the business time zone; <paramref name="businessDate"/> must already be the Dubai date.</summary>
    public bool IsInclusionDay(DateOnly businessDate) => businessDate.Day == inclusionDay;

    /// <summary>The campaign a manual inclusion belongs to: Manual, the Dubai business date and the campaign type.</summary>
    public static string CampaignKey(DateOnly businessDate, CampaignReminderType type) => $"Manual:{businessDate:yyyy-MM-dd}:{type}";

    /// <param name="unitCode">The unit's code (normalized here).</param>
    /// <param name="businessDate">The Dubai business date the decision is for (today at upload time).</param>
    /// <param name="manual">True for a Manual campaign; Auto never includes a GULF_CUSTOMERS unit.</param>
    /// <param name="manuallyIncluded">True when an active manual inclusion exists for this unit in this campaign (only meaningful for Manual).</param>
    public ExceptionDecision Evaluate(string? unitCode, DateOnly businessDate, bool manual, bool manuallyIncluded)
    {
        if (!HasExclusions) return ExceptionDecision.Allowed;
        var code = UnitCodeNormalizer.Normalize(unitCode);
        if (special.IsSpecial(unitCode)) return new(true, ReviewReasons.SpecialCaseExcluded);   // permanent: no date and no inclusion overrides it
        if (!gulf.Contains(code)) return ExceptionDecision.Allowed;
        if (!manual) return new(true, ReviewReasons.GulfCustomerExcluded);
        if (manuallyIncluded) return new(false, null, ReviewReasons.ManuallyIncluded);
        if (IsInclusionDay(businessDate)) return new(false, null, ReviewReasons.GulfInclusionDay);
        return new(true, ReviewReasons.GulfCustomerExcluded);
    }

    /// <summary>
    /// The decision for a campaign that has no Manual context (Auto Preview, Auto dispatcher, the stored builds): special and GULF_CUSTOMERS units are always excluded.
    /// </summary>
    public bool IsExcludedFromAuto(string? unitCode) => Evaluate(unitCode, default, manual: false, manuallyIncluded: false).Excluded;
}

/// <summary>Applies a policy decision to a (detached) review record: the policy reasons are replaced by what the decision says, and the status follows the reasons exactly as the builder derives it.</summary>
public static class ExceptionRecordRules
{
    private static readonly string[] PolicyCodes =
        [ReviewReasons.SpecialCaseExcluded, ReviewReasons.GulfCustomerExcluded, ReviewReasons.ManuallyIncluded, ReviewReasons.GulfInclusionDay];

    /// <summary>Mutates <paramref name="record"/> (a copy, never a tracked entity). Records that carry no policy reason and are allowed are left exactly as they are.</summary>
    public static void Apply(CollectionsReviewRecord record, ExceptionDecision decision)
    {
        var current = TigerCS.Application.Modules.Collections.Review.ReviewRecordBuilder.Parse(record.Reasons);
        var withoutPolicy = current.Where(c => !PolicyCodes.Contains(c)).ToList();
        if (decision is { Excluded: false, Notice: null } && withoutPolicy.Count == current.Count) return;
        if (decision.Excluded) withoutPolicy.Add(decision.ReasonCode!);
        else if (decision.Notice is not null) withoutPolicy.Add(decision.Notice);
        record.Reasons = TigerCS.Application.Modules.Collections.Review.ReviewRecordBuilder.Serialize(withoutPolicy);
        record.ValidationStatus = ReviewReasons.StatusFor(withoutPolicy, alreadySent: false);
    }
}
