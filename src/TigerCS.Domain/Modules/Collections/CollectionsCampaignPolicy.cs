namespace TigerCS.Domain.Modules.Collections;

/// <summary>
/// How a campaign is sent: exactly two modes.
/// <b>Auto</b> (default) follows the approved policy and the stage schedule: on a scheduled date the Auto dispatcher prepares and dispatches the eligible campaign by itself, but only while
/// the separate <c>AutoSendingEnabled</c> switch is on (off by default).
/// <b>Manual</b> lets an authorized person choose any existing campaign type and send it to Genesys at any time, with no send date and no stage schedule: they filter the customers (Tower,
/// Payment status, Date range, Minimum Total), pick the customers, choose a suitable existing contact list and confirm. The amount sent follows the filters.
/// Reconciliation, permissions, contact validation, suppression, duplicate-send protection, the legal restrictions and idempotency apply unchanged in both modes.
/// </summary>
public static class CollectionsSendingModes
{
    public const string Auto = "Auto", Manual = "Manual";

    /// <summary>Blank means Auto. Anything but Auto / Manual is refused (false).</summary>
    public static bool TryParse(string? value, out string mode)
    {
        mode = Auto;
        if (string.IsNullOrWhiteSpace(value)) return true;
        var text = value.Trim();
        if (string.Equals(text, Manual, StringComparison.OrdinalIgnoreCase)) { mode = Manual; return true; }
        return string.Equals(text, Auto, StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsManual(string? value) => string.Equals(value?.Trim(), Manual, StringComparison.OrdinalIgnoreCase);
}

public enum CollectionsCampaignStage
{
    OverdueReminder = 1,
    CurrentMonthReminder = 2,
    FollowUpReminder = 3,
    LegalNotice = 4,
    LegalReferral = 5
}

/// <summary>Unit-level review reasons of a campaign candidate (bit mask). The remaining reasons (stale source, coverage, currency, release gates) do
/// not depend on the unit and are added by the application.</summary>
[Flags]
public enum CollectionsCampaignFlags
{
    None = 0,
    AmbiguousInstalments = 1,
    AmountPrecisionNeedsReview = 2,
    MissingUnitIdentity = 4,
    ConflictingContactDetails = 8,
    UnitAllocationNeedsReview = 16,
    ContradictoryPaymentStatus = 32,
    NoValidContact = 64,
    /// <summary>More than one distinct CRM customer holds an eligible (Sold / Contract, not cancelled) sale of the unit: the customer cannot be chosen automatically.</summary>
    CrmCustomerAmbiguous = 128,
    /// <summary>CRM and PACT give different phone numbers (or, without a phone to confirm, different e-mails) for the unit: two people are never merged.</summary>
    ContactSourceConflict = 256,
    /// <summary>The CRM sale of the unit cannot be tied to one company (both companies hold the same unit key): nobody is chosen and the unit needs review.</summary>
    CrmCompanyAmbiguous = 512,
    /// <summary>CRM contact data is enabled but could not be used (never loaded, stale, or unreadable): the contact may be incomplete, so the unit is not treated as ready. This is NOT "CRM holds no value".</summary>
    CrmUnavailable = 1024,
    /// <summary>The stage rule finds no qualifying balance for the unit (nothing inside its eligibility scope). The unit stays listed with its real balances; it is simply not ready to send.</summary>
    NoQualifyingBalance = 2048,
    /// <summary>The stage amount does not exceed the stage threshold. Same as above: listed, not ready.</summary>
    BelowThreshold = 4096
}

public sealed record CampaignInstalment(DateOnly DueDate, decimal RemainingAmount);
public sealed record CampaignAmount(decimal? Amount, DateOnly? EarliestDueDate, string Reason);

/// <summary>The 7 October 2026 communication table, with thresholds evaluated per unit.
/// Values are the source's remaining principal, never the original plan or the customer's total balance.</summary>
public static class CollectionsCampaignPolicy
{
    public static IReadOnlyList<DateOnly> ScheduledDates(CollectionsCampaignStage stage, DateOnly businessDate)
    {
        var last = DateTime.DaysInMonth(businessDate.Year, businessDate.Month);
        DateOnly Day(int day) => new(businessDate.Year, businessDate.Month, Math.Min(day, last));
        return stage switch
        {
            CollectionsCampaignStage.OverdueReminder => [Day(1)],
            CollectionsCampaignStage.CurrentMonthReminder => [Day(14)],
            CollectionsCampaignStage.FollowUpReminder => [Day(28)],
            CollectionsCampaignStage.LegalNotice => [Day(12), Day(14)],
            CollectionsCampaignStage.LegalReferral => [Day(30)],
            _ => throw new ArgumentOutOfRangeException(nameof(stage))
        };
    }

    /// <summary>
    /// The due-date range a stage looks at: instalments due on or after <c>From</c> (null = no lower bound) and before <c>ToExclusive</c>.
    /// Shared by the in-memory evaluation and the SQL campaign engine so both use one definition.
    /// </summary>
    public static (DateOnly? From, DateOnly ToExclusive) StageRange(CollectionsCampaignStage stage, DateOnly businessDate)
    {
        if (!Enum.IsDefined(stage)) throw new ArgumentOutOfRangeException(nameof(stage));
        var monthStart = new DateOnly(businessDate.Year, businessDate.Month, 1);
        return stage switch
        {
            CollectionsCampaignStage.OverdueReminder => (null, businessDate.AddMonths(-1)),
            CollectionsCampaignStage.LegalNotice => (monthStart.AddMonths(-1), monthStart),
            CollectionsCampaignStage.LegalReferral => (null, businessDate.AddMonths(-3)),
            _ => (monthStart, monthStart.AddMonths(1))
        };
    }

    /// <summary>A stage amount qualifies when the unit's total remaining stage amount is strictly greater than this.</summary>
    public static decimal Threshold(CollectionsCampaignStage stage) => stage switch
    {
        CollectionsCampaignStage.LegalNotice => 1500m,
        CollectionsCampaignStage.LegalReferral => 20000m,
        _ => 0m
    };

    /// <summary>The instalments that count for <paramref name="stage"/> on <paramref name="businessDate"/> (calendar-month conventions).</summary>
    public static IReadOnlyList<CampaignInstalment> Qualifying(IEnumerable<CampaignInstalment> instalments,
        CollectionsCampaignStage stage, DateOnly businessDate)
    {
        var (from, toExclusive) = StageRange(stage, businessDate);
        return instalments.Where(i => i.RemainingAmount > 0 && (from is null || i.DueDate >= from) && i.DueDate < toExclusive).ToList();
    }

    public static CampaignAmount Evaluate(IEnumerable<CampaignInstalment> instalments,
        CollectionsCampaignStage stage, DateOnly businessDate)
    {
        var rows = Qualifying(instalments, stage, businessDate);
        if (rows.Count == 0) return new(0m, null, "NoQualifyingBalance");
        var earliest = rows.Min(i => i.DueDate);
        // The existing source cannot distinguish valid same-date instalments from duplicated allocations.
        if (rows.GroupBy(i => i.DueDate).Any(g => g.Count() > 1))
            return new(null, earliest, "AmbiguousInstalments");
        var amount = rows.Sum(i => i.RemainingAmount);
        return new(amount, earliest, amount > Threshold(stage) ? "Qualifies" : "BelowThreshold");
    }

    /// <summary>
    /// Default "To" of the due-date window: the last day of the preview month, for every stage. The window only feeds the Due / Overdue / Total figures (the stage
    /// eligibility is <see cref="StageRange"/>, unchanged), and Due is the whole current month, including the days still to come. The caller clamps it to the end of
    /// the current month (<see cref="CollectionsDueMonth.ClampTo"/>).
    /// </summary>
    public static DateOnly DefaultDateTo(CollectionsCampaignStage stage, DateOnly businessDate) =>
        new(businessDate.Year, businessDate.Month, DateTime.DaysInMonth(businessDate.Year, businessDate.Month));

    /// <summary>
    /// Flags (without changing eligibility) where the due-date window excludes instalments the confirmed stage rule would
    /// otherwise qualify. The window is an additional reviewer filter; the communication policy above is unchanged.
    /// </summary>
    public static IReadOnlyList<string> RangeNotes(CollectionsCampaignStage stage, DateOnly businessDate, DateOnly from, DateOnly to)
    {
        var monthStart = new DateOnly(businessDate.Year, businessDate.Month, 1);
        static string F(DateOnly d) => d.ToString("dd MMM yyyy", System.Globalization.CultureInfo.InvariantCulture);
        var notes = new List<string>();
        switch (stage)
        {
            case CollectionsCampaignStage.OverdueReminder:
            case CollectionsCampaignStage.LegalReferral:
                var cutoff = stage == CollectionsCampaignStage.OverdueReminder ? businessDate.AddMonths(-1) : businessDate.AddMonths(-3);
                notes.Add($"Stage rule: instalments due before {F(cutoff)}. Instalments due before {F(from)} are excluded by the From date.");
                if (to >= cutoff) notes.Add($"Instalments due on or after {F(cutoff)} do not qualify for this stage even though they are inside the date range.");
                break;
            case CollectionsCampaignStage.LegalNotice:
                if (from > monthStart.AddMonths(-1) || to < monthStart.AddDays(-1))
                    notes.Add("The date range does not cover the whole previous calendar month that this stage uses.");
                break;
            default:
                if (from > monthStart || to < monthStart.AddMonths(1).AddDays(-1))
                    notes.Add($"This stage includes the whole preview month ({F(monthStart)} to {F(monthStart.AddMonths(1).AddDays(-1))}, including upcoming dates); the date range excludes part of it.");
                break;
        }
        return notes;
    }
}
