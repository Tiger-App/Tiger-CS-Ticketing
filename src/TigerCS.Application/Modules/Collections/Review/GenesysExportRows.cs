using TigerCS.Domain.Modules.Collections.Review;

namespace TigerCS.Application.Modules.Collections.Review;

/// <summary>One row of an Export for Genesys file: one phone number, with the units behind it (one, or several units of the same customer) and the amount they add up to.</summary>
/// <param name="Phone">The E.164 number the row is dialled on.</param>
/// <param name="CustomerName">The customer's name (of the first unit, by unit code, when several are combined).</param>
/// <param name="Email">The first e-mail among the units (empty when none).</param>
/// <param name="ReminderType">The campaign type.</param>
/// <param name="Amount">The sum of the units' filtered amounts: each unit's own stored instalments, so nothing is counted twice.</param>
/// <param name="Currency">The currency of the amounts.</param>
/// <param name="DueDate">The earliest qualifying due date among the units.</param>
/// <param name="Units">The units in the row, in unit-code order, each with its status and reasons as the Manual page shows them; the first is the row's identity for the payload.</param>
public sealed record GenesysExportRow(string Phone, string CustomerName, string Email, CampaignReminderType ReminderType, decimal Amount, string Currency, DateOnly DueDate,
    IReadOnlyList<ReviewQueryService.ExportCandidate> Units)
{
    public CollectionsReviewRecord First => Units[0].Record;
}

/// <summary>Why some matching customer units are not in the file, by reason, with how many units each reason kept out. Never silent: the counts travel to the page and the audit trail.</summary>
public sealed record GenesysExportExclusion(string Code, int Units, string Explanation);

/// <param name="Rows">The rows of the file, in company / tenant / unit order.</param>
/// <param name="Exclusions">Units left out, by reason (empty when every matching unit is in the file).</param>
/// <param name="CombinedRows">Rows that hold several units of one customer.</param>
/// <param name="CombinedUnits">Units that were folded into those rows.</param>
public sealed record GenesysExportRowSet(IReadOnlyList<GenesysExportRow> Rows, IReadOnlyList<GenesysExportExclusion> Exclusions, int CombinedRows, int CombinedUnits)
{
    public int ExcludedUnits => Exclusions.Sum(e => e.Units);
}

/// <summary>
/// Turns the customer units of an export (<see cref="ReviewQueryService.ResolveForExportAsync"/>) into contact rows, deterministically and without guessing:
/// <list type="bullet">
/// <item>A unit without a calculated amount or due date (ambiguous or unreadable instalments), or whose source amount has unresolved decimals (<c>AmountPrecisionNeedsReview</c>: the stored
/// value is the raw one, and a two-decimal AmountDue would be a rounded guess), is left out and counted: no financial value is ever invented.</item>
/// <item>A unit without a callable E.164 number is left out and counted: Genesys could not dial it, and no other number is substituted.</item>
/// <item>Several units of the SAME customer (same company and tenant) on one number become ONE row: the amounts are added (each unit has its own stored instalments, so nothing is counted twice)
/// and the earliest due date is kept. When the source allocates instalments ambiguously between those units (<c>UnitAllocationNeedsReview</c>, <c>DuplicateSourceRecord</c>,
/// <c>AmbiguousInstalments</c>) the sum could double count, so the units are left out and counted instead.</item>
/// <item>DIFFERENT customers on one number are never merged and never both dialled: the number cannot be tied to one person, so those units are left out and counted.</item>
/// </list>
/// Pure: the same input always gives the same rows, and the totals of the rows are the totals of the file.
/// </summary>
public static class GenesysExportRows
{
    public const string AmountUnavailable = "AmountUnavailable", AmountPrecisionUnresolved = "AmountPrecisionUnresolved", NoCallablePhone = "NoCallablePhone",
        UnitAllocationAmbiguous = "UnitAllocationAmbiguous", SharedPhoneDifferentCustomers = "SharedPhoneDifferentCustomers";

    private static readonly string[] AmbiguousAllocation = [ReviewReasons.UnitAllocationNeedsReview, ReviewReasons.DuplicateSourceRecord, ReviewReasons.AmbiguousInstalments];

    public static string Explain(string code) => code switch
    {
        AmountUnavailable => "The amount could not be calculated from the stored instalments (ambiguous or unreadable), so no value was invented and the customer was left out.",
        AmountPrecisionUnresolved => "A source amount of this customer has more than two decimals that are not rounding noise. The real balance must be confirmed in PACT; it was not rounded into the file.",
        NoCallablePhone => "No valid international (E.164) phone number, so Genesys could not dial the customer; nothing was substituted.",
        UnitAllocationAmbiguous => "Several units of the same customer share the number, but the source allocates instalments between those units ambiguously, so their amounts were not added up and the units were left out.",
        SharedPhoneDifferentCustomers => "The same phone number belongs to different customers. They were not merged and not exported, so the number is not dialled for the wrong person.",
        _ => code
    };

    public static GenesysExportRowSet Build(IReadOnlyList<ReviewQueryService.ExportCandidate> candidates)
    {
        var excluded = new Dictionary<string, int>(StringComparer.Ordinal);
        void Exclude(string code, int units) => excluded[code] = excluded.GetValueOrDefault(code) + units;

        var callable = new List<ReviewQueryService.ExportCandidate>();
        foreach (var candidate in candidates)
        {
            var r = candidate.Record;
            if (r.RemainingAmount is not > 0 || r.DueDate is null) { Exclude(AmountUnavailable, 1); continue; }
            // A balance that cannot be stated in fils is stored as read (never rounded); an AmountDue with two decimals would be a rounded, invented figure, so the unit stays out.
            if (candidate.Reasons.Contains(ReviewReasons.AmountPrecisionNeedsReview, StringComparer.Ordinal)) { Exclude(AmountPrecisionUnresolved, 1); continue; }
            if (!DispatchService.IsVoiceEligible(r.Phone)) { Exclude(NoCallablePhone, 1); continue; }
            callable.Add(candidate);
        }

        var rows = new List<GenesysExportRow>();
        int combinedRows = 0, combinedUnits = 0;
        foreach (var number in callable.GroupBy(c => c.Record.Phone, StringComparer.Ordinal))
        {
            var units = number.OrderBy(c => c.Record.CompanyId).ThenBy(c => c.Record.TenantId, StringComparer.OrdinalIgnoreCase)
                .ThenBy(c => c.Record.UnitCode, StringComparer.OrdinalIgnoreCase).ThenBy(c => c.Record.RecordKey, StringComparer.Ordinal).ToList();
            if (units.Count == 1) { rows.Add(Row(units)); continue; }
            if (units.Select(c => (c.Record.CompanyId, Tenant: c.Record.TenantId.Trim())).Distinct().Count() > 1) { Exclude(SharedPhoneDifferentCustomers, units.Count); continue; }
            if (units.Any(c => c.Reasons.Any(reason => AmbiguousAllocation.Contains(reason, StringComparer.Ordinal)))) { Exclude(UnitAllocationAmbiguous, units.Count); continue; }
            rows.Add(Row(units));
            combinedRows++; combinedUnits += units.Count;
        }

        var ordered = rows.OrderBy(r => r.First.CompanyId).ThenBy(r => r.First.TenantId, StringComparer.OrdinalIgnoreCase)
            .ThenBy(r => r.First.UnitCode, StringComparer.OrdinalIgnoreCase).ThenBy(r => r.First.RecordKey, StringComparer.Ordinal).ToList();
        var exclusions = excluded.OrderBy(e => e.Key, StringComparer.Ordinal).Select(e => new GenesysExportExclusion(e.Key, e.Value, Explain(e.Key))).ToList();
        return new(ordered, exclusions, combinedRows, combinedUnits);
    }

    private static GenesysExportRow Row(IReadOnlyList<ReviewQueryService.ExportCandidate> units)
    {
        var first = units[0].Record;
        return new(first.Phone, first.CustomerName, units.Select(u => u.Record.Email).FirstOrDefault(e => e.Length > 0) ?? "", first.ReminderType,
            units.Sum(u => u.Record.RemainingAmount!.Value), first.Currency, units.Min(u => u.Record.DueDate!.Value), units);
    }
}
