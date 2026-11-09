using TigerCS.Domain.Modules.Collections.Review;

namespace TigerCS.Application.Modules.Collections.History;

/// <summary>The normalized filter the store runs in SQL. Dates are UTC instants (the Dubai day boundaries already applied).</summary>
public sealed record GenesysHistorySpec(
    DateTime? FromUtc, DateTime? ToUtcExclusive, CampaignReminderType? Stage, string? Status, string? Mode, Guid? EmployeeId, string? ListId, string? Search, int? CompanyId = null);

/// <summary>One Genesys batch of a submission in the shape the list needs.</summary>
public sealed record SubmissionBatchRow(CampaignReminderType Type, string ContactListId, string? CampaignId, string? RequestHash, GenesysBatchStatus Status, string? Error, DateTime? StartedAtUtc);

/// <summary>A submission (one approved, frozen dispatch) with its counts, stages and batches.</summary>
public sealed record SubmissionRow(
    long DispatchId, Guid PublicId, DateTime InitiatedAtUtc, DateTime? StartedAtUtc, DateTime? CompletedAtUtc, DispatchStatus Status, string? StatusReason, string? Phase,
    Guid InitiatedBy, int ApprovedCount, string ApprovedTotalsJson, int ExcludedAtDispatch, SubmissionCounts Counts,
    IReadOnlyList<CampaignReminderType> Types, IReadOnlyList<SubmissionBatchRow> Batches, IReadOnlyList<int>? CompanyIds = null);

public sealed record SummaryRow(DispatchStatus Status, SubmissionCounts Counts);

public sealed record SubmissionPage(int TotalCount, IReadOnlyList<SubmissionRow> Items, IReadOnlyList<SummaryRow> Summary);

public sealed record SubmissionDetailRow(SubmissionRow Row, IReadOnlyList<CollectionsGenesysBatch> Batches, int SharedPhoneRecords);

/// <param name="Result">Optional record-result filter: Uploaded, Failed, Unknown, NotSent (excluded / released / waiting).</param>
/// <param name="Search">A unit code, customer name, phone, email or exact Genesys contact id.</param>
public sealed record GenesysRecordFilter(string? Result = null, string? Search = null);

public sealed record RecordRow(CollectionsDispatchItem Item, bool SharedPhone, DateTime? BatchStartedAtUtc);

public sealed record RecordPage(int TotalCount, IReadOnlyList<RecordRow> Items);

public sealed record ExportAuditRow(long AuditId, DateTime OccurredAtUtc, Guid? ActorEmployeeId, string? Details);

public sealed record ExportAuditPage(int TotalCount, IReadOnlyList<ExportAuditRow> Items);

/// <summary>
/// Read-only access to the dispatch, batch, item and audit data behind the Genesys Campaign History. Nothing here writes, calls Genesys or reads the financial source:
/// every figure comes from the rows the approval and upload already persisted (a history stays readable after any later snapshot refresh, because items are frozen copies).
/// </summary>
public interface IGenesysHistoryStore
{
    Task<SubmissionPage> ListSubmissionsAsync(GenesysHistorySpec spec, int skip, int take, CancellationToken ct);
    Task<SubmissionDetailRow?> GetSubmissionAsync(Guid publicId, CancellationToken ct);
    Task<RecordPage> ListRecordsAsync(long dispatchId, GenesysRecordFilter filter, int skip, int take, CancellationToken ct);
    /// <summary>The distinct employees who approved a submission (Auto's system actor excluded).</summary>
    Task<IReadOnlyList<Guid>> ListSubmitterIdsAsync(CancellationToken ct);
    /// <summary>Downloaded "Export for Genesys" files from the audit trail, newest first.</summary>
    Task<ExportAuditPage> ListExportAuditAsync(string action, DateTime? fromUtc, DateTime? toUtcExclusive, int skip, int take, CancellationToken ct);
}
