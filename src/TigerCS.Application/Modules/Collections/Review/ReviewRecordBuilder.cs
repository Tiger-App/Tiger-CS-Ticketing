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
    IReadOnlyDictionary<string, string> StatusMap, CrmLinkSet? Crm = null, DateOnly? PolicyStart = null);

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
            var contactConflict = unitRows.Select(r => (r.Row.FullName.Trim(), Phone: PhoneNormalizer.Normalize(r.Row.Mobile).E164,
                Email: NormalizeEmail(r.Row.Email), r.Row.ProjectCode)).Distinct().Count() > 1;

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
                var statuses = stageRows.Select(r => PaymentStatusRules.Derive(r.Row.SourceStatus, r.Amount, r.Row.PlanAmount, r.Row.AllocatedAmount)).ToList();
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
                // Reconciliation is a property of the SOURCE, not of the customer: the run records it (CollectionsReviewRun.SourceReconciled) and the send refuses on it as one gate.
                // It is never stamped on each record, so a customer's own status says what is wrong with the customer's data, and nothing else.
                if (linked.Phone.Length == 0) reasons.Add(ReviewReasons.NoValidContact);
                if (linked.CrmCustomerAmbiguous) reasons.Add(ReviewReasons.CrmCustomerAmbiguous);
                if (linked.CrmCompanyAmbiguous) reasons.Add(ReviewReasons.CrmCompanyAmbiguous);
                if (linked.SourceConflict) reasons.Add(ReviewReasons.ContactSourceConflict);
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
    /// Repeated calls are blocked, not merged and not silently prioritised, until the business decides the policy. Among records that could
    /// be sent today (outside-schedule records are not candidates, so a unit's Follow Up on day 28 does not collide with its Current Month on day 14):
    /// a unit with several reminder types gets <c>ReminderTypeOverlap</c>; a phone number used by several units gets <c>SharedPhoneMultipleUnits</c>.
    /// </summary>
    private static void MarkRepeatedCalls(List<CollectionsReviewRecord> records)
    {
        static (int, string, int?, string) Unit(CollectionsReviewRecord r) => (r.CompanyId, r.TenantId, r.UnitId, r.UnitCode);
        var candidates = records.Where(r => !Parse(r.Reasons).Contains(ReviewReasons.OutsideSchedule)).ToList();
        var flagged = new Dictionary<CollectionsReviewRecord, List<string>>();
        void Flag(CollectionsReviewRecord r, string reason) { if (!flagged.TryGetValue(r, out var l)) flagged[r] = l = []; if (!l.Contains(reason)) l.Add(reason); }

        foreach (var group in candidates.GroupBy(Unit).Where(g => g.Count() > 1))
            foreach (var record in group) Flag(record, ReviewReasons.ReminderTypeOverlap);
        foreach (var group in candidates.Where(r => r.Phone.Length > 0).GroupBy(r => r.Phone).Where(g => g.Select(Unit).Distinct().Count() > 1))
            foreach (var record in group) Flag(record, ReviewReasons.SharedPhoneMultipleUnits);

        foreach (var (record, reasons) in flagged)
        {
            var all = Parse(record.Reasons).Concat(reasons).Distinct().ToList();
            record.Reasons = Serialize(all);
            record.ValidationStatus = ReviewReasons.StatusFor(all, alreadySent: false);
        }
        MarkManualRepeatedCalls(records, candidates);
    }

    /// <summary>
    /// For a record whose ONLY reason is <c>OutsideSchedule</c>, works out what the repeated-call rules would say if an authorized person sent that reminder type manually today, and
    /// stores it as a marker (the record's status does not change: in Auto mode it is outside the schedule either way). The would-be send set is the records scheduled today plus the
    /// outside-schedule records of the SAME reminder type, so manual sending never skips the duplicate-call protection that scheduled records get.
    /// </summary>
    private static void MarkManualRepeatedCalls(List<CollectionsReviewRecord> records, List<CollectionsReviewRecord> candidates)
    {
        static (int, string, int?, string) Unit(CollectionsReviewRecord r) => (r.CompanyId, r.TenantId, r.UnitId, r.UnitCode);
        static bool OnlyOutsideSchedule(CollectionsReviewRecord r) { var p = Parse(r.Reasons); return p.Count == 1 && p[0] == ReviewReasons.OutsideSchedule; }
        var manual = records.Where(OnlyOutsideSchedule).ToList();
        if (manual.Count == 0) return;
        var candidateUnits = candidates.Select(Unit).ToHashSet();
        var candidatePhoneUnits = candidates.Where(r => r.Phone.Length > 0).GroupBy(r => r.Phone).ToDictionary(g => g.Key, g => g.Select(Unit).ToHashSet());
        var sameTypePhoneUnits = manual.Where(r => r.Phone.Length > 0).GroupBy(r => (r.ReminderType, r.Phone)).ToDictionary(g => g.Key, g => g.Select(Unit).ToHashSet());
        foreach (var record in manual)
        {
            var markers = new List<string>();
            if (candidateUnits.Contains(Unit(record))) markers.Add(ReviewReasons.ManualReminderTypeOverlap);
            if (record.Phone.Length > 0)
            {
                var others = new HashSet<(int, string, int?, string)>(sameTypePhoneUnits[(record.ReminderType, record.Phone)]);
                if (candidatePhoneUnits.TryGetValue(record.Phone, out var scheduled)) others.UnionWith(scheduled);
                others.Remove(Unit(record));
                if (others.Count > 0) markers.Add(ReviewReasons.ManualSharedPhone);
            }
            if (markers.Count > 0) record.Reasons = Serialize(Parse(record.Reasons).Concat(markers));
        }
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
        var reasons = Parse(record.Reasons);
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
