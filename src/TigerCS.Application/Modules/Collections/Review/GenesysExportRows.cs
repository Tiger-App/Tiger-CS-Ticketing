using TigerCS.Domain.Modules.Collections.Review;

namespace TigerCS.Application.Modules.Collections.Review;

/// <summary>One row of an Export for Genesys file: ONE customer unit (one record of the Preview), with its own phone, amount, due date and campaign type.</summary>
/// <param name="Phone">The E.164 number the row is dialled on.</param>
/// <param name="CustomerName">The customer's name.</param>
/// <param name="Email">The unit's e-mail (empty when none).</param>
/// <param name="ReminderType">The campaign type.</param>
/// <param name="Amount">The unit's own filtered amount (the one Preview shows).</param>
/// <param name="Currency">The currency of the amount.</param>
/// <param name="DueDate">The unit's own earliest qualifying due date.</param>
/// <param name="Units">The one unit in the row, with its status and reasons as the Manual page shows them (a list for the callers that count units).</param>
public sealed record GenesysExportRow(string Phone, string CustomerName, string Email, CampaignReminderType ReminderType, decimal Amount, string Currency, DateOnly DueDate,
    IReadOnlyList<ReviewQueryService.ExportCandidate> Units)
{
    public CollectionsReviewRecord First => Units[0].Record;
}

/// <summary>Why some matching customer units are not in the file, by reason, with how many units each reason kept out. Never silent: the counts travel to the page and the audit trail.</summary>
public sealed record GenesysExportExclusion(string Code, int Units, string Explanation);

/// <summary>One unit that is not in the file, named: the person sees exactly which record and why, and can fix the data instead of guessing.</summary>
public sealed record GenesysExportExcludedRecord(int CompanyId, string TenantId, string UnitCode, string CustomerName, string ReminderType, string Code);

/// <param name="Rows">The rows of the file, one per unit, in company / tenant / unit order.</param>
/// <param name="Exclusions">Units left out, by reason (empty when every matching unit is in the file).</param>
/// <param name="ExcludedRecords">The same units one by one, in the same order as the rows.</param>
public sealed record GenesysExportRowSet(IReadOnlyList<GenesysExportRow> Rows, IReadOnlyList<GenesysExportExclusion> Exclusions, IReadOnlyList<GenesysExportExcludedRecord> ExcludedRecords)
{
    public int ExcludedUnits => Exclusions.Sum(e => e.Units);
}

/// <summary>
/// Turns the customer units of an export (<see cref="ReviewQueryService.ResolveForExportAsync"/>) into contact rows: <b>one row per unit, exactly as Preview lists them</b>. A phone number
/// is a value in the row, never a key: nothing is merged, added up, de-duplicated or left out because of it. The same customer on several units gives one row per unit (each with its own
/// amount, due date and type); different customers on one number give one row each; the same customer on the same number with several units gives one row per unit. A unit is left out only
/// when a mandatory field of the six-column file is genuinely missing, and then it is named with its reason:
/// <list type="bullet">
/// <item><c>AmountUnavailable</c>: no calculated amount or due date (ambiguous or unreadable instalments). No financial value is ever invented.</item>
/// <item><c>AmountPrecisionUnresolved</c>: the source amount has unresolved decimals (<c>AmountPrecisionNeedsReview</c>); a two-decimal AmountDue would be a rounded guess.</item>
/// <item><c>NoCallablePhone</c>: no E.164 number Genesys could dial; no other number is substituted.</item>
/// </list>
/// A unit's status (Needs review, held back, already sent) and the reconciliation state never keep it out of a file: a file is not a send, and the send rules stay in <see cref="DispatchService"/>.
/// Pure: the same input always gives the same rows, and the totals of the rows are the totals of the file.
/// </summary>
public static class GenesysExportRows
{
    public const string AmountUnavailable = "AmountUnavailable", AmountPrecisionUnresolved = "AmountPrecisionUnresolved", NoCallablePhone = "NoCallablePhone";

    public static string Explain(string code) => code switch
    {
        AmountUnavailable => "The amount could not be calculated from the stored instalments (ambiguous or unreadable), so no value was invented and the unit was left out.",
        AmountPrecisionUnresolved => "A source amount of this unit has more than two decimals that are not rounding noise. The real balance must be confirmed in PACT; it was not rounded into the file.",
        NoCallablePhone => "No valid international (E.164) phone number, so Genesys could not dial the unit's customer; nothing was substituted.",
        _ => code
    };

    public static GenesysExportRowSet Build(IReadOnlyList<ReviewQueryService.ExportCandidate> candidates)
    {
        var ordered = candidates.OrderBy(c => c.Record.CompanyId).ThenBy(c => c.Record.TenantId, StringComparer.OrdinalIgnoreCase)
            .ThenBy(c => c.Record.UnitCode, StringComparer.OrdinalIgnoreCase).ThenBy(c => c.Record.RecordKey, StringComparer.Ordinal).ToList();
        var rows = new List<GenesysExportRow>();
        var excludedRecords = new List<GenesysExportExcludedRecord>();
        foreach (var candidate in ordered)
        {
            var r = candidate.Record;
            var code = r.RemainingAmount is not > 0 || r.DueDate is null ? AmountUnavailable
                // A balance that cannot be stated in fils is stored as read (never rounded); an AmountDue with two decimals would be a rounded, invented figure, so the unit stays out.
                : candidate.Reasons.Contains(ReviewReasons.AmountPrecisionNeedsReview, StringComparer.Ordinal) ? AmountPrecisionUnresolved
                : !DispatchService.IsVoiceEligible(r.Phone) ? NoCallablePhone
                : null;
            if (code is not null) { excludedRecords.Add(new(r.CompanyId, r.TenantId, r.UnitCode, r.CustomerName, r.ReminderType.ToString(), code)); continue; }
            rows.Add(new(r.Phone, r.CustomerName, r.Email, r.ReminderType, r.RemainingAmount!.Value, r.Currency, r.DueDate!.Value, [candidate]));
        }
        var exclusions = excludedRecords.GroupBy(e => e.Code, StringComparer.Ordinal).OrderBy(g => g.Key, StringComparer.Ordinal)
            .Select(g => new GenesysExportExclusion(g.Key, g.Count(), Explain(g.Key))).ToList();
        return new(rows, exclusions, excludedRecords);
    }
}
