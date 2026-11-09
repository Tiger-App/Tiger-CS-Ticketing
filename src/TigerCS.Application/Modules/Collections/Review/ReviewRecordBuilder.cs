using System.Globalization;
using System.Net.Mail;
using System.Security.Cryptography;
using System.Text;
using TigerCS.Application.Modules.Collections.Abstractions;
using TigerCS.Application.Modules.Collections.Services;
using TigerCS.Domain.Modules.Collections;
using TigerCS.Domain.Modules.Collections.Review;

namespace TigerCS.Application.Modules.Collections.Review;

/// <summary>
/// What one build of review records knows about its run. <c>PolicyStart</c> is the first due date the stage policy looks at (the configured PACT start date), the same as the Campaigns
/// preview's eligibility scope: instalments due before it never make a unit a candidate or change a stage amount, but they ARE kept in the unit's stored instalments, so a Manual amount, a
/// Due / Overdue figure and the Minimum Total see the customer's whole history exactly as the preview does. A null <c>PolicyStart</c> puts every instalment read in the policy scope.
/// </summary>
public sealed record ReviewBuildContext(
    DateOnly AsOfDate, DateTime SourceReadAtUtc, string SourceLabel, string Currency, bool SourceReconciled,
    bool LegalNoticeReleased, bool LegalCaseApproved, decimal FloatTolerance,
    IReadOnlyDictionary<string, string> StatusMap, CrmLinkSet? Crm = null, DateOnly? PolicyStart = null,
    IReadOnlySet<(int CompanyId, string TenantId, long UnitId)>? HeldUnits = null,
    TigerCS.Application.Modules.Collections.Exceptions.CollectionsExceptionPolicy? Exceptions = null);

public sealed record ReviewBuildResult(IReadOnlyList<CollectionsReviewRecord> Records, int FloatNormalizedRows, int SettledRows, int CancelledRows = 0);

/// <summary>
/// Turns one financial-source read into validated review records: one per unit and reminder type that the approved
/// campaign policy selects. Pure and deterministic. Nothing is guessed or merged: balances of different units stay
/// separate, a reason is attached to every unresolved doubt, and a record is Ready only when none remains.
/// </summary>
public static class ReviewRecordBuilder
{
    /// <summary>True when the PACT unit code carries a cancellation asterisk anywhere.</summary>
    public static bool IsCancelledUnit(string? unitCode) => unitCode is not null && unitCode.Contains('*');

    public static ReviewBuildResult Build(IEnumerable<PactReceivableInstalment> source, ReviewBuildContext context)
    {
        var floatNormalized = 0;
        var settled = 0;
        var rows = new List<(PactReceivableInstalment Row, DateOnly Due, decimal Amount, MoneyResult Money)>();
        var cancelled = 0;
        foreach (var row in source)
        {
            // PACT marks a cancelled unit with an asterisk anywhere in its code ('*', '**'): dropped before any matching or grouping, and never matched to a CRM unit with the mark removed.
            if (IsCancelledUnit(row.UnitCode)) { cancelled++; continue; }
            var money = MoneyNormalizer.Normalize(row.Amount, row.AmountIsFloatingPoint, context.FloatTolerance);
            if (money.Precision == AmountPrecision.FloatingPointNoise) floatNormalized++;
            // Settled or noise-only remainders are not receivables. An unresolved precision issue is kept and flagged.
            if (money.IsResolved && money.Value <= 0) { settled++; continue; }
            if (money.Raw <= 0) { settled++; continue; }
            rows.Add((row, DateOnly.FromDateTime(row.DueDate), money.Value ?? money.Raw, money));
        }

        // The deployed procedures can repeat one instalment under several units of a tenant.
        var ambiguousTenants = rows.GroupBy(r => (r.Row.CompanyId, Tenant: r.Row.TenantId.Trim()))
            .Where(g => g.GroupBy(r => r.Due).Any(d => d.Select(r => (r.Row.UnitId, Code: r.Row.UnitCode.Trim())).Distinct().Count() > 1)
                || g.GroupBy(r => r.Row.UnitId).Any(u => u.Select(r => r.Row.UnitCode.Trim()).Distinct().Count() > 1)
                || g.GroupBy(r => r.Row.UnitCode.Trim()).Any(u => u.Select(r => r.Row.UnitId).Distinct().Count() > 1))
            .Select(g => g.Key).ToHashSet();

        var records = new List<CollectionsReviewRecord>();
        foreach (var unit in rows.GroupBy(r => (r.Row.CompanyId, Tenant: r.Row.TenantId.Trim(), r.Row.UnitId, Code: r.Row.UnitCode.Trim())))
        {
            var unitRows = unit.ToList();
            var first = unitRows[0].Row;
            // The contact to call: CRM first, PACT completing only what CRM lacks (CollectionsContactLinker). The CRM links come from the one bulk read of the local CRM owner data;
            // without CRM (switched off) the contact is PACT's, exactly as before.
            var crm = context.Crm ?? CrmLinkSet.None;
            var linked = crm.Link(unit.Key.CompanyId, unit.Key.Code,
                new SourceContact(first.FullName.Trim(), PhoneNormalizer.Normalize(first.Mobile).E164, NormalizeEmail(first.Email)));
            var email = linked.Email;
            // PACT rows that disagree on name / phone / e-mail only matter when CRM does not supply the contact: for a uniquely identified CRM customer the CRM contact is used.
            var crmSupplies = linked.CrmStatus == CrmLinkStatus.Single;
            var contactConflict = unitRows.Select(r => crmSupplies ? ("", "", "", r.Row.ProjectCode) : (r.Row.FullName.Trim(), PhoneNormalizer.Normalize(r.Row.Mobile).E164,
                NormalizeEmail(r.Row.Email), r.Row.ProjectCode)).Distinct().Count() > 1;

            // The customer's unpaid instalments, ALL of them, stored on every stage record: a Manual send calculates its amount from them and the person's filters, like the preview's
            // Due / Overdue / Total columns. The stage policy itself looks only from the configured start date on, exactly as the preview decides who is a candidate.
            var instalmentsJson = FilteredAmounts.Serialize(unitRows.Select(r => new FilterInstalment(r.Due, r.Amount)));
            var policyRows = context.PolicyStart is { } policyStart ? unitRows.Where(r => r.Due >= policyStart).ToList() : unitRows;
            foreach (var stage in Enum.GetValues<CollectionsCampaignStage>())
            {
                var instalments = policyRows.Select(r => new CampaignInstalment(r.Due, r.Amount)).ToList();
                var amount = CollectionsCampaignPolicy.Evaluate(instalments, stage, context.AsOfDate);
                if (amount.Reason is "NoQualifyingBalance" or "BelowThreshold") continue;

                var qualifying = CollectionsCampaignPolicy.Qualifying(instalments, stage, context.AsOfDate);
                var stageRows = policyRows.Where(r => qualifying.Any(q => q.DueDate == r.Due && q.RemainingAmount == r.Amount)).ToList();
                var reasons = new List<string>();

                if (amount.Reason == "AmbiguousInstalments")
                {
                    var sameDate = stageRows.GroupBy(r => r.Due).Where(g => g.Count() > 1).ToList();
                    var exact = sameDate.Where(g => g.Select(r => (r.Row.VoucherNumber, r.Row.ChequeNumber, r.Row.Amount)).Distinct().Count() == 1).ToList();
                    if (exact.Count > 0) reasons.Add(ReviewReasons.DuplicateSourceRecord);
                    if (sameDate.Count > exact.Count) reasons.Add(ReviewReasons.AmbiguousInstalments);
                }
                // A balance that cannot be stated in fils anywhere in the unit's unpaid instalments holds every stage record of the unit: a Manual amount is calculated from all of them, so an
                // unresolved instalment outside this stage would otherwise be summed in and rounded into a sendable amount.
                var unresolved = unitRows.Any(r => !r.Money.IsResolved);
                if (unresolved) reasons.Add(ReviewReasons.AmountPrecisionNeedsReview);

                // Payment status follows the verified remaining amount (0 = Paid, more = Unpaid); only a missing, invalid or contradicted remainder is Unknown and holds the record.
                // The decision is taken on the actual remaining amount BEFORE rounding: a balance rounded to fils (or one that cannot be) is judged by its original value, so rounding can neither turn a
                // small positive balance into "paid" nor make a rounded amount contradict the source's own original / paid amounts. Exact values and float noise keep their normalized amount.
                var statuses = stageRows.Select(r => PaymentStatusRules.Derive(r.Row.SourceStatus, r.Money.Precision is AmountPrecision.Rounded or AmountPrecision.Unresolved ? r.Money.Raw : r.Amount,
                    r.Row.PlanAmount, r.Row.AllocatedAmount)).ToList();
                if (stageRows.Any(r => r.Row is { PlanAmount: { } plan, AllocatedAmount: { } paid } && !PaymentStatusRules.AmountsAddUp(plan, paid, r.Money.Raw)))
                    reasons.Add(ReviewReasons.SourceAmountsInconsistent);
                if (statuses.Contains(ReviewPaymentStatus.Paid) || stageRows.Any(r => string.Equals(r.Row.SourceStatus?.Trim(), "Paid", StringComparison.OrdinalIgnoreCase)))
                    reasons.Add(ReviewReasons.ContradictoryPaymentStatus);
                var payment = PaymentStatusRules.Combine(statuses);
                if (payment == ReviewPaymentStatus.Unknown && !reasons.Contains(ReviewReasons.ContradictoryPaymentStatus))
                    reasons.Add(ReviewReasons.PaymentStatusUnknown);

                if (unit.Key.UnitId is not > 0 || string.IsNullOrWhiteSpace(unit.Key.Code) || unit.Key.Code == "0")
                    reasons.Add(ReviewReasons.MissingUnitIdentity);
                if (string.IsNullOrWhiteSpace(unit.Key.Tenant) || string.IsNullOrWhiteSpace(first.FullName) || unit.Key.CompanyId is not (4 or 32))
                    reasons.Add(ReviewReasons.MissingCustomerIdentity);
                if (contactConflict) reasons.Add(ReviewReasons.ConflictingContactDetails);
                if (ambiguousTenants.Contains((unit.Key.CompanyId, unit.Key.Tenant))) reasons.Add(ReviewReasons.UnitAllocationNeedsReview);
                if (!context.Currency.Equals("AED", StringComparison.Ordinal)) reasons.Add(ReviewReasons.CurrencyNeedsReview);
                // Reconciliation is a property of the SOURCE, not of the customer: the run records it (CollectionsReviewRun.SourceReconciled) and the send refuses on it as one gate per company.
                // It is never stamped on each record, so a customer's own status says what is wrong with the customer's data, and nothing else. The one exception IS the customer's data:
                // a unit whose snapshot amounts differ persistently from PACT after a refresh is held back on its own (SourceGate.HeldUnits), while every other unit stays as it is.
                if (context.HeldUnits is { Count: > 0 } held && held.Contains((unit.Key.CompanyId, unit.Key.Tenant, (long)(unit.Key.UnitId ?? 0)))) reasons.Add(ReviewReasons.SourceMismatchUnresolved);
                // The two unit-level exclusion policies (permanent special cases; GULF_CUSTOMERS / CHEQUES). Stored on EVERY record of the unit, whatever the date: the day-14 window and a manual inclusion
                // are decided when the record is read (Manual only, with today's date and the campaign's inclusions), so a record built yesterday can never carry yesterday's decision into an upload.
                if (context.Exceptions is { HasExclusions: true } exceptions)
                {
                    if (exceptions.IsSpecialCase(unit.Key.Code)) reasons.Add(ReviewReasons.SpecialCaseExcluded);
                    if (exceptions.IsGulfCustomer(unit.Key.Code)) reasons.Add(ReviewReasons.GulfCustomerExcluded);
                }
                if (linked.Phone.Length == 0) reasons.Add(ReviewReasons.NoValidContact);
                if (linked.CrmCustomerAmbiguous) reasons.Add(ReviewReasons.CrmCustomerAmbiguous);
                if (linked.CrmCompanyAmbiguous) reasons.Add(ReviewReasons.CrmCompanyAmbiguous);
                if (crm.Availability.Blocks) reasons.Add(ReviewReasons.CrmUnavailable);

                if (!CollectionsCampaignPolicy.ScheduledDates(stage, context.AsOfDate).Contains(context.AsOfDate))
                    reasons.Add(ReviewReasons.OutsideSchedule);
                if (stage == CollectionsCampaignStage.LegalNotice && !context.LegalNoticeReleased) reasons.Add(ReviewReasons.LegalNoticeReleaseRequired);
                if (stage == CollectionsCampaignStage.LegalReferral && !context.LegalCaseApproved) reasons.Add(ReviewReasons.LegalCaseNotApproved);

                var cycle = $"{context.AsOfDate.ToString("yyyy-MM", CultureInfo.InvariantCulture)}:{stage}";
                var recordKey = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
                    $"{unit.Key.CompanyId}:{unit.Key.Tenant}:{unit.Key.UnitId}:{unit.Key.Code}:{cycle}")));
                var distinctReasons = reasons.Distinct().ToList();
                records.Add(new CollectionsReviewRecord
                {
                    RecordKey = recordKey,
                    CycleKey = cycle,
                    ReminderType = CampaignReminderTypes.FromStage(stage),
                    CompanyId = unit.Key.CompanyId,
                    TenantId = unit.Key.Tenant,
                    CustomerName = linked.Name.Length > 0 ? linked.Name : first.FullName.Trim(),
                    Phone = linked.Phone,
                    Email = email,
                    UnitId = unit.Key.UnitId,
                    UnitCode = unit.Key.Code,
                    ProjectCode = first.ProjectCode?.Trim() ?? "",
                    TowerId = first.TowerId,
                    TowerNumber = first.TowerNumber?.Trim(),
                    TowerName = first.TowerName?.Trim(),
                    PaymentStatus = payment,
                    SourceStatus = string.Join(",", stageRows.Select(r => r.Row.SourceStatus?.Trim() ?? "").Distinct()),
                    RemainingAmount = unresolved || amount.Amount is null ? null : amount.Amount,
                    RawRemainingAmount = stageRows.Sum(r => r.Money.Raw),
                    Currency = context.Currency,
                    DueDate = amount.EarliestDueDate,
                    InstalmentCount = stageRows.Count,
                    Reasons = Serialize(distinctReasons),
                    ValidationStatus = ReviewReasons.StatusFor(distinctReasons, alreadySent: false),
                    SourceReadAtUtc = context.SourceReadAtUtc,
                    Source = context.SourceLabel,
                    InstalmentsJson = instalmentsJson
                });
            }
        }

        MarkRepeatedCalls(records);
        return new ReviewBuildResult(records, floatNormalized, settled, cancelled);
    }

    /// <summary>
    /// Repeated calls to ONE UNIT are blocked, not merged and not silently prioritised, until the business decides the policy. Among records that could be sent today (outside-schedule
    /// records are not candidates, so a unit's Follow Up on day 28 does not collide with its Current Month on day 14) a unit with several reminder types gets <c>ReminderTypeOverlap</c>.
    /// A phone number is NOT a unit: by the confirmed business decision every property unit is its own contact record and receives its own call, even when several units (of one customer
    /// or of different customers) share a number. A repeated number alone therefore never holds a record back (<c>SharedPhoneMultipleUnits</c> and <c>ManualSharedPhone</c> are no longer
    /// produced; they remain defined for review data stored before the decision).
    /// </summary>
    private static void MarkRepeatedCalls(List<CollectionsReviewRecord> records)
    {
        static (int, string, int?, string) Unit(CollectionsReviewRecord r) => (r.CompanyId, r.TenantId, r.UnitId, r.UnitCode);
        var candidates = records.Where(r => !Parse(r.Reasons).Contains(ReviewReasons.OutsideSchedule)).ToList();
        var flagged = new Dictionary<CollectionsReviewRecord, List<string>>();
        void Flag(CollectionsReviewRecord r, string reason) { if (!flagged.TryGetValue(r, out var l)) flagged[r] = l = []; if (!l.Contains(reason)) l.Add(reason); }

        foreach (var group in candidates.GroupBy(Unit).Where(g => g.Count() > 1))
            foreach (var record in group) Flag(record, ReviewReasons.ReminderTypeOverlap);

        foreach (var (record, reasons) in flagged)
        {
            var all = Parse(record.Reasons).Concat(reasons).Distinct().ToList();
            record.Reasons = Serialize(all);
            record.ValidationStatus = ReviewReasons.StatusFor(all, alreadySent: false);
        }
        MarkManualRepeatedCalls(records, candidates);
    }

    /// <summary>
    /// For a record whose ONLY reason is <c>OutsideSchedule</c>, works out what the repeated-call rule would say if an authorized person sent that reminder type manually today, and
    /// stores it as a marker (the record's status does not change: in Auto mode it is outside the schedule either way): the unit is already scheduled today under another reminder type
    /// (<c>ManualReminderTypeOverlap</c>), so manual sending never skips the one-call-per-unit protection that scheduled records get. A shared number is not a marker (see <see cref="MarkRepeatedCalls"/>).
    /// </summary>
    private static void MarkManualRepeatedCalls(List<CollectionsReviewRecord> records, List<CollectionsReviewRecord> candidates)
    {
        static (int, string, int?, string) Unit(CollectionsReviewRecord r) => (r.CompanyId, r.TenantId, r.UnitId, r.UnitCode);
        // A GULF_CUSTOMERS unit carries its exclusion beside the schedule; the marker is still worked out so that, once the unit is allowed (day 14 or a manual inclusion), the one-call-per-unit protection holds.
        static bool OnlyOutsideSchedule(CollectionsReviewRecord r)
        {
            var p = Parse(r.Reasons).Where(c => c != ReviewReasons.GulfCustomerExcluded).ToList();
            return p.Count == 1 && p[0] == ReviewReasons.OutsideSchedule;
        }
        var manual = records.Where(OnlyOutsideSchedule).ToList();
        if (manual.Count == 0) return;
        var candidateUnits = candidates.Select(Unit).ToHashSet();
        foreach (var record in manual)
            if (candidateUnits.Contains(Unit(record))) record.Reasons = Serialize(Parse(record.Reasons).Append(ReviewReasons.ManualReminderTypeOverlap));
    }

    /// <summary>
    /// Whether a Manual send may send this record now: Ready, or held back by the stage schedule ALONE (<c>OutsideSchedule</c> as its only reason, so no reconciliation, contact, CRM, amount,
    /// legal-release, repeated-call or Manual marker reason). The schedule is the one rule Manual waives; every other rule keeps the record out. Used by the approval, the list and the
    /// revalidation right before the upload, so they can never disagree.
    /// </summary>
    public static bool IsManuallySendable(CollectionsReviewRecord record)
    {
        if (record.ValidationStatus == ReviewValidationStatus.Ready) return true;
        if (record.ValidationStatus != ReviewValidationStatus.Excluded) return false;
        // Warnings (a manual inclusion, the GULF_CUSTOMERS day) are shown but never block and never waive anything.
        var reasons = Parse(record.Reasons).Where(c => ReviewReasons.KindOf(c) != ReasonKind.Warning).ToList();
        return reasons.Count == 1 && reasons[0] == ReviewReasons.OutsideSchedule;
    }

    public static string Serialize(IEnumerable<string> reasons)
    {
        var list = reasons.ToList();
        return list.Count == 0 ? "" : ";" + string.Join(";", list) + ";";
    }

    public static IReadOnlyList<string> Parse(string? reasons) =>
        string.IsNullOrEmpty(reasons) ? [] : reasons.Split(';', StringSplitOptions.RemoveEmptyEntries);

    private static string NormalizeEmail(string? value)
    {
        var trimmed = value?.Trim() ?? "";
        return trimmed.Length > 0 && MailAddress.TryCreate(trimmed, out var address) && address.Address == trimmed ? address.Address : "";
    }
}
