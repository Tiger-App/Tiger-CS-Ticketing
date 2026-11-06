using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using TigerCS.Application.Abstractions;
using TigerCS.Application.Modules.Collections.Abstractions;
using TigerCS.Application.Modules.Collections.Services;
using TigerCS.Domain.Modules.Collections;
using TigerCS.Domain.Modules.Collections.Review;

namespace TigerCS.Application.Modules.Collections.Review;

/// <summary>
/// The Manual "Export review CSV": a file for people to review the Manual list, not a file for Genesys. It holds every customer of the filtered list, held-back ones included, with the amount
/// calculated from the same filters as the page (<see cref="ReviewQueryService.QueryAsync"/>, the code behind the table), the status and the exact reasons. It reads the stored review data, uploads
/// nothing and changes nothing; like the Auto review export it needs the send right, and each export is written to the audit trail (counts, filter and file hash, never a customer's data).
/// </summary>
public sealed class ManualReviewExportService(
    CollectionsOptions options, PactReceivablesOptions sourceOptions, CollectionsAuthorizationService authorization, CollectionsClock clock, ReviewQueryService query,
    IAuditEntryWriter audit, ICollectionsUnitOfWork unitOfWork, ILogger<ManualReviewExportService> logger)
{
    public const string AuditAction = "CollectionsReviewFileExported", AuditEntityType = "CollectionsReviewExport";

    /// <summary>The most rows one review file holds; a longer list must be narrowed with the filters.</summary>
    public const int MaxRows = 20000;

    private static readonly string[] Header =
    [
        "Customer", "Phone", "Email", "Project", "Tower", "Unit", "Campaign", "Amount", "Currency", "EarliestDueDate", "Status", "Reasons", "PreviousDispatch", "ApprovalDataAsOf"
    ];

    public async Task<CollectionsResult<ReviewExportDto>> ExportAsync(CollectionsCaller caller, ReviewFilter? filter, CancellationToken ct)
    {
        if (!(await authorization.ResolveAsync(caller, ct)).CanSendReminders)
            return CollectionsResult<ReviewExportDto>.Fail(CollectionsOutcome.Forbidden);
        if (!options.Enabled || !sourceOptions.Enabled) return CollectionsResult<ReviewExportDto>.Fail(CollectionsOutcome.Disabled);
        if (!CollectionsSendingModes.IsManual(filter?.SendingMode))
            return CollectionsResult<ReviewExportDto>.Fail(CollectionsOutcome.InvalidRequest, "This review export is for Manual. Auto has its own review export.");

        // The whole list from one calculation: paging would recalculate the run once per page.
        var list = await query.ManualListAsync(caller, filter, ct);
        if (!list.IsSuccess) return CollectionsResult<ReviewExportDto>.Fail(list.Outcome, list.Detail);
        var rows = list.Value!.Items;
        if (rows.Count > MaxRows)
            return CollectionsResult<ReviewExportDto>.Fail(CollectionsOutcome.InvalidRequest, $"{rows.Count:N0} customers match; a review file holds at most {MaxRows:N0}. Narrow the filters and export again.");

        var csv = Write(rows, list.Value.Run);
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(csv)));
        var ready = rows.Count(r => r.ValidationStatus == "Ready");
        try
        {
            await audit.WriteAsync(caller.EmployeeId, AuditAction, AuditEntityType, hash[..16], null,
                $"Campaign={filter?.ReminderType};Rows={rows.Count};Ready={ready};Mode=Manual;Filter={JsonSerializer.Serialize(filter)};Sha256={hash}", Guid.NewGuid(), ct);
            await unitOfWork.SaveChangesAsync(ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            unitOfWork.DiscardPendingChanges();
            logger.LogError("Review export could not be audited ({ExceptionType}); the file was not released.", ex.GetType().Name);
            return CollectionsResult<ReviewExportDto>.Fail(CollectionsOutcome.FinanceUnavailable, "The export could not be recorded in the audit trail, so no file was released. Try again.");
        }
        return CollectionsResult<ReviewExportDto>.Ok(new($"collections-{filter?.ReminderType}-manual-review-{clock.UtcNow:yyyyMMdd-HHmm}.csv", csv, rows.Count, ready, hash));
    }

    private static string Write(IReadOnlyList<ReviewRecordDto> rows, ReviewRunDto run)
    {
        var text = new StringBuilder();
        text.Append(string.Join(',', Header)).Append("\r\n");
        foreach (var r in rows)
        {
            var tower = CollectionsTowerName.IsUsable(r.TowerName, r.TowerNumber) ? r.TowerName! : r.TowerNumber ?? "";
            string[] cells =
            [
                r.CustomerName, r.Phone, r.Email, r.ProjectCode, tower, r.UnitCode, CampaignReminderTypes.StageName(Enum.Parse<CampaignReminderType>(r.ReminderType)),
                r.RemainingAmount?.ToString("0.00", CultureInfo.InvariantCulture) ?? "", r.Currency, r.DueDate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? "", r.ValidationStatus,
                string.Join("; ", r.Reasons.Select(x => x.Explanation)),
                r.PreviousDispatchStatus is null ? "" : $"{r.PreviousDispatchStatus} {r.PreviousDispatchAtUtc:yyyy-MM-dd}", run.AsOfDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
            ];
            // A review file is opened in a spreadsheet: every cell is neutralized, including the phone.
            text.Append(string.Join(',', cells.Select(v => CollectionsCampaignCsv.Cell(v, review: true, isPhone: false)))).Append("\r\n");
        }
        return text.ToString();
    }
}
