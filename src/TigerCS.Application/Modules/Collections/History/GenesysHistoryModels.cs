using TigerCS.Domain.Modules.Collections.Review;

namespace TigerCS.Application.Modules.Collections.History;

/// <summary>
/// The upload states of a Genesys campaign submission. They describe ONLY what is known about the contact-list upload. None of them means a customer was called, contacted or reached:
/// the call result of a contact is not integrated, and the history says so ("Call outcome unavailable").
/// </summary>
public static class GenesysUploadStates
{
    /// <summary>Approved internally; the upload has not started. Not an upload.</summary>
    public const string Pending = "Pending";
    /// <summary>The upload is running now.</summary>
    public const string Uploading = "Uploading";
    /// <summary>Genesys confirmed it accepted every submitted record (it returned a contact id for each).</summary>
    public const string Uploaded = "Uploaded";
    /// <summary>Genesys explicitly rejected the operation (or it could not start): nothing was added to the contact list.</summary>
    public const string Failed = "Failed";
    /// <summary>Established partial completion: some records were confirmed by Genesys and the rest were rejected or are unconfirmed.</summary>
    public const string Partial = "Partial";
    /// <summary>Timeout or any uncertain remote outcome: the contacts may or may not exist in Genesys. Never shown as success.</summary>
    public const string Unknown = "Unknown";
    /// <summary>Approved but the send stopped before anything was submitted (cancelled, changed since approval, nothing eligible, confirmed-not-uploaded).</summary>
    public const string NotSent = "NotSent";

    public static readonly IReadOnlyList<string> All = [Pending, Uploading, Uploaded, Failed, Partial, Unknown, NotSent];

    public static bool IsKnown(string? value) => value is not null && All.Contains(value, StringComparer.Ordinal);

    public static string Label(string state) => state switch
    {
        NotSent => "Not sent",
        _ => state
    };

    /// <summary>What every record of every state lacks: Genesys call results are not integrated, so no record is ever "Called", "Contacted" or "Delivered".</summary>
    public const string CallOutcomeUnavailable = "Call outcome unavailable";

    public const string CallOutcomeNote =
        "Uploaded means Genesys added the contact to the outbound contact list. It does not mean the customer was called, contacted or reached: Genesys call results are not integrated, so call outcome is unavailable.";
}

/// <summary>The counts of one submission's records, from its dispatch items. "Submitted" = assigned to a Genesys batch and not excluded or released before the upload.</summary>
public sealed record SubmissionCounts(
    int Approved, int Submitted, int Uploaded, int Failed, int Unconfirmed, int Waiting, int Excluded, int Released,
    decimal SubmittedAmountAed, int SubmittedOtherCurrency, decimal ApprovedAmountAed)
{
    public static readonly SubmissionCounts Empty = new(0, 0, 0, 0, 0, 0, 0, 0, 0m, 0, 0m);

    /// <summary>Records whose remote outcome is not certain: Genesys timed out / gave no usable answer, or the worker stopped before the answer was recorded.</summary>
    public int Uncertain => Unconfirmed + Waiting;
}

/// <summary>
/// THE rule that turns a dispatch and its record counts into an upload state. It is applied in memory to a loaded submission; <c>GenesysHistoryStore</c> applies the SAME rule in SQL for the
/// status filter (a test proves the two agree on every combination), so a filter and the label shown never disagree.
/// </summary>
public static class GenesysUploadStateRules
{
    public static string Classify(DispatchStatus dispatch, SubmissionCounts c)
    {
        if (dispatch is DispatchStatus.Queued or DispatchStatus.Revalidating) return GenesysUploadStates.Pending;
        if (dispatch == DispatchStatus.Sending) return GenesysUploadStates.Uploading;
        if (c.Submitted == 0) return GenesysUploadStates.NotSent;
        if (c.Uploaded > 0) return c.Failed + c.Uncertain == 0 ? GenesysUploadStates.Uploaded : GenesysUploadStates.Partial;
        return c.Uncertain > 0 ? GenesysUploadStates.Unknown : GenesysUploadStates.Failed;
    }

    /// <summary>The plain-language meaning of the state for this submission; never claims a call or delivery.</summary>
    public static string Explain(string state, SubmissionCounts c, string? reason) => state switch
    {
        GenesysUploadStates.Pending => "Approved internally. The upload to Genesys has not started yet.",
        GenesysUploadStates.Uploading => "The upload to Genesys is in progress.",
        GenesysUploadStates.Uploaded => $"Genesys accepted the upload and returned a contact id for all {c.Submitted} record(s). This does not mean the customers were called.",
        GenesysUploadStates.Failed => "Genesys rejected the upload (or it could not start). Nothing was added to the contact list." + (string.IsNullOrWhiteSpace(reason) ? "" : " " + reason.Trim()),
        GenesysUploadStates.Partial => $"Genesys confirmed {c.Uploaded} of {c.Submitted} record(s). {c.Failed} were rejected and {c.Uncertain} have an unconfirmed outcome; check the contact list in Genesys before sending those again.",
        GenesysUploadStates.Unknown => "The outcome is uncertain (the request timed out or Genesys gave no usable answer). The contacts may or may not be in the Genesys list: check it before sending again." + (string.IsNullOrWhiteSpace(reason) ? "" : " " + reason.Trim()),
        GenesysUploadStates.NotSent => "Approved, but nothing was submitted to Genesys." + (string.IsNullOrWhiteSpace(reason) ? "" : " " + reason.Trim()),
        _ => ""
    };

    /// <summary>The result of one record's upload, in words that never say it was called or contacted.</summary>
    public static string RecordResult(DispatchItemStatus status, bool submitted, bool dispatchFinished) => status switch
    {
        DispatchItemStatus.UploadedToGenesys => "Uploaded",
        DispatchItemStatus.Failed => "Failed",
        DispatchItemStatus.UnknownOutcome => "Unknown",
        DispatchItemStatus.Excluded => "Excluded before upload",
        DispatchItemStatus.Released => "Not sent",
        DispatchItemStatus.Approved => !submitted ? (dispatchFinished ? "Not sent" : "Pending") : (dispatchFinished ? "Unknown" : "Uploading"),
        _ => "Unknown"
    };
}

// ---------------------------------------------------------------- query

/// <param name="From">First Dubai calendar day (inclusive) of the approval time.</param>
/// <param name="To">Last Dubai calendar day (inclusive).</param>
/// <param name="Stage">A campaign stage / reminder type name (CurrentMonth, FollowUp, Overdue, LegalNotice, LegalCase).</param>
/// <param name="Status">One of <see cref="GenesysUploadStates.All"/>.</param>
/// <param name="Mode">Manual or Auto.</param>
/// <param name="EmployeeId">The employee who approved the submission (an Auto submission has no employee: filter it with Mode).</param>
/// <param name="List">A destination contact list: a reminder-type name (its configured list) or the list id.</param>
/// <param name="Search">A unit code, customer name, or an exact Genesys contact / campaign id inside the submission.</param>
/// <param name="Company">4 = Tiger Group Dubai, 32 = Tiger Group Sharjah.</param>
public sealed record GenesysHistoryFilter(
    DateOnly? From = null, DateOnly? To = null, string? Stage = null, string? Status = null, string? Mode = null,
    Guid? EmployeeId = null, string? List = null, string? Search = null, int? Company = null);

// ---------------------------------------------------------------- DTOs

public sealed record GenesysSubmissionDto(
    Guid SubmissionId, DateTime ApprovedAtUtc, DateTime? SubmittedAtUtc,
    string ReminderType, string ReminderLabel, string StageName, string Mode,
    string? ContactListLabel, string? ContactListId, Guid SubmittedById, string SubmittedByName,
    int UnitRecords, string FiguresBasis, decimal TotalAmount, string Currency, int OtherCurrencyRecords,
    string Status, string StatusLabel, string StatusExplanation, string? Error,
    string? GenesysReference, int UploadedRecords, int FailedRecords, int UnconfirmedRecords, int ExcludedRecords,
    string CallOutcome, IReadOnlyList<string>? Companies = null, GenesysDeliveryDto? Delivery = null, int ApprovedRecords = 0);

public sealed record GenesysHistorySummaryDto(
    int Submissions, int Records, decimal TotalAmount, string Currency, int Uploaded, int NeedAttention, int Pending, IReadOnlyDictionary<string, int> ByStatus, int ConfirmedRecords = 0);

public sealed record GenesysFilterOptionDto(string Value, string Label);

public sealed record GenesysHistoryOptionsDto(
    IReadOnlyList<GenesysFilterOptionDto> Stages, IReadOnlyList<GenesysFilterOptionDto> Lists, IReadOnlyList<GenesysFilterOptionDto> Statuses,
    IReadOnlyList<GenesysFilterOptionDto> Modes, IReadOnlyList<GenesysFilterOptionDto> Employees, IReadOnlyList<GenesysFilterOptionDto>? Companies = null);

public sealed record GenesysHistoryPageDto(
    int Page, int PageSize, int TotalCount, IReadOnlyList<GenesysSubmissionDto> Items, GenesysHistorySummaryDto Summary, GenesysHistoryOptionsDto Options, string CallOutcomeNote,
    bool CanSeeExportHistory, DateTime? LastSyncedAtUtc = null, bool CanSync = false);

public sealed record GenesysBatchDetailDto(
    int Sequence, string ReminderType, string? StageName, string ContactListId, string? ContactListLabel, string Status, string StatusLabel,
    int Contacts, int? ReturnedContacts, int? HttpStatus, int Attempts, DateTime? StartedAtUtc, DateTime? CompletedAtUtc,
    string? GenesysCampaignName, string? GenesysCampaignId, string? CampaignAction, string? RequestFingerprint, string? Error, string? ReconciliationNote, DateTime? ReconciledAtUtc);

public sealed record GenesysSubmissionDetailDto(
    GenesysSubmissionDto Submission, IReadOnlyList<GenesysBatchDetailDto> Batches, int ApprovedRecords, int ExcludedAtDispatch, int SharedPhoneRecords,
    string? StatusReason, string? Phase, DateTime? StartedAtUtc, DateTime? CompletedAtUtc, bool CrmLinkAvailable, string CallOutcomeNote, bool CanSync = false);

public sealed record GenesysSubmissionRecordDto(
    long RecordId, string CustomerName, int? CrmCustomerId, string UnitCode, string Phone, string Email, string ReminderType, string ReminderLabel,
    decimal Amount, string Currency, DateOnly DueDate, string Result, string ResultState, DateTime? UploadedAtUtc, DateTime? SubmittedAtUtc, string? GenesysContactId,
    string? Error, bool SharedPhone, string? Suppression, string CallOutcome,
    string? MessageStatus = null, DateTime? SentAtUtc = null, DateTime? DeliveredAtUtc = null, string? FailureReason = null, string? InteractionId = null, int MessageAttempts = 0);

public sealed record GenesysRecordsPageDto(int Page, int PageSize, int TotalCount, IReadOnlyList<GenesysSubmissionRecordDto> Items, string CallOutcomeNote);

/// <summary>One downloaded "Export for Genesys" file as the audit trail holds it. A download is NOT a submission and is never counted as an upload.</summary>
public sealed record GenesysExportHistoryDto(
    long AuditId, DateTime ExportedAtUtc, Guid? ExportedById, string ExportedByName, string? ReminderType, string? ReminderLabel, string? ContactList, string? Mode,
    int? Rows, int? Units, int? Excluded, decimal? TotalAmount, string? Currency, bool? DataVerified, bool? SchemaVerified, int? Repeated, string? FileFingerprint);

public sealed record GenesysExportHistoryPageDto(int Page, int PageSize, int TotalCount, IReadOnlyList<GenesysExportHistoryDto> Items, string Notice);
