namespace TigerCS.Domain.Modules.Collections.Review;

public enum ReviewValidationStatus
{
    Ready = 1,
    NeedsReview = 2,
    Excluded = 3,
    AlreadySent = 4
}

public enum ReasonKind { NeedsReview = 1, Exclusion = 2, Warning = 3, ManualOnly = 4 }

public sealed record ReviewReasonInfo(string Code, ReasonKind Kind, string Explanation);

/// <summary>
/// The single catalogue of validation reasons: stable codes (filterable) with the plain-language explanation shown to
/// reviewers. A needs-review reason is a data problem a person could resolve; an exclusion is a policy decision that
/// keeps the record out of a send; a warning is shown but does not block.
/// </summary>
public static class ReviewReasons
{
    public const string SourceReconciliationRequired = "SourceReconciliationRequired";
    /// <summary>
    /// The unit's remaining amounts in the snapshot differ from a fresh read of the PACT procedure, and a refresh of the snapshot did not resolve the difference (a synchronization or
    /// mapping defect). Only this unit is held back; the reconciliation diagnostics list the instalments that differ.
    /// </summary>
    public const string SourceMismatchUnresolved = "SourceMismatchUnresolved";
    public const string AmountPrecisionNeedsReview = "AmountPrecisionNeedsReview";
    public const string NoValidContact = "NoValidContact";
    public const string PaymentStatusUnknown = "PaymentStatusUnknown";
    public const string StaleSource = "StaleSource";
    public const string MissingUnitIdentity = "MissingUnitIdentity";
    public const string MissingCustomerIdentity = "MissingCustomerIdentity";
    public const string UnitAllocationNeedsReview = "UnitAllocationNeedsReview";
    public const string AmbiguousInstalments = "AmbiguousInstalments";
    public const string DuplicateSourceRecord = "DuplicateSourceRecord";
    public const string ConflictingContactDetails = "ConflictingContactDetails";
    public const string ContradictoryPaymentStatus = "ContradictoryPaymentStatus";
    public const string CurrencyNeedsReview = "CurrencyNeedsReview";
    public const string OutsideSchedule = "OutsideSchedule";
    /// <summary>
    /// No longer produced: campaign types are independent, so a unit that qualifies for several reminder types on one day is eligible in each of them. Kept only so review data stored
    /// before that decision still reads; a refresh clears it.
    /// </summary>
    public const string ManualReminderTypeOverlap = "ManualReminderTypeOverlap";
    /// <summary>No longer produced (each unit is its own call, whatever its number); kept so review data stored before that decision still reads. A refresh clears it.</summary>
    public const string ManualSharedPhone = "ManualSharedPhone";
    public const string LegalNoticeReleaseRequired = "LegalNoticeReleaseRequired";
    public const string LegalCaseNotApproved = "LegalCaseNotApproved";
    /// <summary>No longer produced (each unit is its own call, whatever its number); kept so review data stored before that decision still reads. A refresh clears it.</summary>
    public const string SharedPhoneMultipleUnits = "SharedPhoneMultipleUnits";
    /// <summary>No longer produced (campaign types are independent); kept so review data stored before that decision still reads. A refresh clears it.</summary>
    public const string ReminderTypeOverlap = "ReminderTypeOverlap";
    public const string SourceAmountsInconsistent = "SourceAmountsInconsistent";
    public const string CrmCustomerAmbiguous = "CrmCustomerAmbiguous";
    public const string CrmCompanyAmbiguous = "CrmCompanyAmbiguous";
    public const string ContactSourceConflict = "ContactSourceConflict";
    public const string CrmUnavailable = "CrmUnavailable";
    /// <summary>Manual only (derived when listing, never stored): the customer was already added to a Genesys dispatch today (the contact-frequency limit).</summary>
    public const string ContactedToday = "ContactedToday";
    /// <summary>The unit is in the permanent special-cases list (<c>SpecialCaseUnitCodes</c>): never part of a normal campaign, whatever the date or any manual inclusion.</summary>
    public const string SpecialCaseExcluded = "SpecialCaseExcluded";
    /// <summary>The unit is in GULF_CUSTOMERS / CHEQUES: excluded except for a Manual campaign on the 14th (Dubai) or when it was included manually for that campaign.</summary>
    public const string GulfCustomerExcluded = "GulfCustomerExcluded";
    /// <summary>Warning (shown, never blocking): a GULF_CUSTOMERS unit is eligible for this Manual campaign because an authorized person included it.</summary>
    public const string ManuallyIncluded = "ManuallyIncluded";
    /// <summary>Warning (shown, never blocking): a GULF_CUSTOMERS unit is eligible for this Manual campaign because today is its monthly inclusion day.</summary>
    public const string GulfInclusionDay = "GulfInclusionDay";

    public static readonly IReadOnlyList<ReviewReasonInfo> All =
    [
        new(SourceReconciliationRequired, ReasonKind.NeedsReview, "The financial source has not been reconciled with PACT accounts for this procedure yet, so its balances cannot be trusted for a send. This is a property of the source (one gate for every customer), not of this customer's data."),
        new(SourceMismatchUnresolved, ReasonKind.NeedsReview, "This unit's remaining amounts in the snapshot differ from a fresh read of PACT, and refreshing the snapshot did not resolve the difference. That is a synchronization or mapping defect to investigate (see Financial reconciliation); only this unit is held back."),
        new(AmountPrecisionNeedsReview, ReasonKind.NeedsReview, "A source amount cannot be stated in fils: a positive balance below 0.005 (which would round to zero and must not be treated as paid) or a value that is not a valid balance. The real balance must be confirmed in PACT. (Extra decimals on a normal balance are simply rounded to two places and are not a reason to hold a customer.)"),
        new(NoValidContact, ReasonKind.NeedsReview, "There is no valid phone number that the voice campaign can call (missing, wrong length, not a UAE/international number, or several numbers in one field)."),
        new(PaymentStatusUnknown, ReasonKind.NeedsReview, "The remaining amount of this balance cannot be determined reliably (missing, negative, or contradicted by the source's status or its original and paid amounts). It stays visible but cannot be sent."),
        new(StaleSource, ReasonKind.NeedsReview, "The financial data was read too long ago. Refresh the data before sending."),
        new(MissingUnitIdentity, ReasonKind.NeedsReview, "The record has no valid unit identity, so the balance cannot be tied to one apartment."),
        new(MissingCustomerIdentity, ReasonKind.NeedsReview, "The customer, company or name is missing on the source record."),
        new(UnitAllocationNeedsReview, ReasonKind.NeedsReview, "The same instalment appears under several units of this customer; the balance may be allocated to the wrong unit."),
        new(AmbiguousInstalments, ReasonKind.NeedsReview, "Several instalments share one due date, so the amount may be double counted."),
        new(DuplicateSourceRecord, ReasonKind.NeedsReview, "The source returned the identical instalment (same voucher, date and amount) more than once. It was not merged automatically."),
        new(ConflictingContactDetails, ReasonKind.NeedsReview, "The source rows for this unit carry different names, phones or emails."),
        new(ContradictoryPaymentStatus, ReasonKind.NeedsReview, "The source says Paid but still shows an amount remaining."),
        new(CurrencyNeedsReview, ReasonKind.NeedsReview, "The currency is not AED; amounts cannot be added or sent until reviewed."),
        new(OutsideSchedule, ReasonKind.Exclusion, "Today is not a scheduled send day for this reminder type. In Manual mode an authorized person can still send it today."),
        new(ManualReminderTypeOverlap, ReasonKind.ManualOnly, "Stored before campaigns became independent: qualifying for another reminder type on the same day no longer holds a record back. Refresh the approval data."),
        new(ManualSharedPhone, ReasonKind.ManualOnly, "Stored before the per-unit calling decision: a phone number shared by several units no longer holds a record back (each unit is its own call). Refresh the approval data."),
        new(LegalNoticeReleaseRequired, ReasonKind.Exclusion, "Sending legal notices to customers has not been released (needs an approved legal workflow)."),
        new(LegalCaseNotApproved, ReasonKind.Exclusion, "Legal Case records are internal Legal referrals; calling customers for them needs an explicit business decision."),
        new(SourceAmountsInconsistent, ReasonKind.NeedsReview, "The source's original amount, paid amount and remaining amount do not add up (original must equal paid plus remaining)."),
        new(CrmCustomerAmbiguous, ReasonKind.NeedsReview, "More than one CRM customer holds an eligible (Sold / Contract, not cancelled) sale of this unit. Nobody was chosen; the contact is not taken from CRM until a person decides."),
        new(CrmCompanyAmbiguous, ReasonKind.NeedsReview, "The CRM sale of this unit cannot be tied to one company: the same unit code exists in both companies' PACT data. No company was guessed."),
        new(ContactSourceConflict, ReasonKind.NeedsReview, "CRM and PACT give different phone numbers (or e-mails) for this unit. Two different people are never merged; CRM alone is shown and a person must confirm."),
        new(CrmUnavailable, ReasonKind.NeedsReview, "CRM contact data is switched on but could not be used (never loaded, too old, or unreadable), so the contact may be incomplete. This is a service problem, not a missing value in CRM; nothing is sent until it is back."),
        new(ContactedToday, ReasonKind.Exclusion, "This unit was already added to a Genesys contact list today for THIS campaign type (Already Sent is scoped to one campaign type). Other campaign types are not affected."),
        new(SharedPhoneMultipleUnits, ReasonKind.Exclusion, "Stored before the per-unit calling decision: a phone number shared by several units no longer holds a record back (each unit is its own call). Refresh the approval data."),
        new(SpecialCaseExcluded, ReasonKind.Exclusion, "This unit is a permanent special case: it is never part of a normal Collections campaign, on any date and whatever is selected."),
        new(GulfCustomerExcluded, ReasonKind.Exclusion, "This unit belongs to GULF_CUSTOMERS (cheques). It is excluded from campaigns except in a Manual campaign on the 14th of the month, or when an authorized person includes it manually for that campaign."),
        new(ManuallyIncluded, ReasonKind.Warning, "Manually included: an authorized person added this GULF_CUSTOMERS unit to this Manual campaign. Every other safety check still applies."),
        new(GulfInclusionDay, ReasonKind.Warning, "GULF_CUSTOMERS unit eligible today: the 14th of the month is its Manual campaign day. Every other safety check still applies."),
        new(ReminderTypeOverlap, ReasonKind.Exclusion, "Stored before campaigns became independent: qualifying for more than one reminder type on the same day no longer holds a record back. Refresh the approval data.")
    ];

    private static readonly Dictionary<string, ReviewReasonInfo> ByCode = All.ToDictionary(r => r.Code, StringComparer.Ordinal);

    public static string Explain(string code) => ByCode.TryGetValue(code, out var info) ? info.Explanation : code;

    public static ReasonKind? KindOf(string code) => ByCode.TryGetValue(code, out var info) ? info.Kind : null;

    public static ReviewValidationStatus StatusFor(IEnumerable<string> reasonCodes, bool alreadySent)
    {
        if (alreadySent) return ReviewValidationStatus.AlreadySent;
        var kinds = reasonCodes.Select(KindOf).ToList();
        if (kinds.Contains(ReasonKind.NeedsReview)) return ReviewValidationStatus.NeedsReview;
        if (kinds.Contains(ReasonKind.Exclusion)) return ReviewValidationStatus.Excluded;
        return ReviewValidationStatus.Ready;
    }
}
