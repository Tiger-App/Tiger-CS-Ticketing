using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using TigerCS.Application.Modules.Collections.Review;
using TigerCS.Application.Modules.Collections.Services;
using TigerCS.Application.Modules.IdentityAndAccess.Abstractions;
using TigerCS.Domain.Modules.Collections;
using TigerCS.Domain.Modules.Collections.Review;

namespace TigerCS.Application.Modules.Collections.History;

/// <summary>
/// The Genesys Campaign History: a read-only account of every campaign submission TigerCS has made to Genesys, built ONLY from what the approval and upload already persisted
/// (dispatch = the approved submission, batches = the Genesys requests, items = one row per unit record, audit trail = downloaded files).
/// <list type="bullet">
/// <item><b>Read only.</b> Nothing here writes, queues, uploads, retries or calls Genesys or the financial source; opening a page can never trigger an upload. No resend action exists.</item>
/// <item><b>Status semantics.</b> <see cref="GenesysUploadStateRules"/>: Uploaded only when Genesys returned a contact id for every submitted record; a timeout or exception is Unknown, never success;
/// Partial only when part of the records is confirmed and the rest rejected or unconfirmed. Nothing is ever "Called", "Contacted" or "Delivered": call outcome is unavailable.</item>
/// <item><b>Units are never merged.</b> One row per unit record; a phone number shared by several units stays several rows (flagged, never collapsed or counted once).</item>
/// <item><b>Durable.</b> Items are frozen copies made at approval, so a later snapshot refresh or review-run prune never changes or removes a history row.</item>
/// <item><b>Export history is separate.</b> A downloaded "Export for Genesys" CSV is an audit entry, not a dispatch: it is listed apart and never counted as an upload.</item>
/// </list>
/// </summary>
public sealed class GenesysHistoryService(
    CollectionsOptions options, GenesysOutboundOptions genesys, CollectionsAuthorizationService authorization,
    IGenesysHistoryStore store, ILogger<GenesysHistoryService> logger,
    CollectionsUnitLinkService? links = null, IEmployeeRepository? employees = null)
{
    private readonly IEmployeeRepository? _employees = employees;

    public const int DefaultPageSize = 25, MaxPageSize = 100;

    public const string AutoSubmitterName = "Automatic (scheduler)";

    public const string ExportNotice =
        "These are downloaded CSV files from Export for Genesys. A download is not an upload: nothing was submitted to Genesys, and these files are not counted in the submission history or its totals.";

    // ---------------------------------------------------------------- submissions

    public async Task<CollectionsResult<GenesysHistoryPageDto>> ListAsync(CollectionsCaller caller, GenesysHistoryFilter filter, int page, int pageSize, CancellationToken ct)
    {
        var permissions = await authorization.ResolveAsync(caller, ct);
        if (!permissions.CanReadFinancials) return CollectionsResult<GenesysHistoryPageDto>.Fail(CollectionsOutcome.Forbidden);
        var spec = BuildSpec(filter, out var problem);
        if (spec is null) return CollectionsResult<GenesysHistoryPageDto>.Fail(CollectionsOutcome.InvalidRequest, problem);
        (page, pageSize) = Clamp(page, pageSize);

        var result = await store.ListSubmissionsAsync(spec, (page - 1) * pageSize, pageSize, ct);
        var names = new NameCache(this);
        var items = new List<GenesysSubmissionDto>();
        foreach (var row in result.Items) items.Add(await MapAsync(row, names, ct));
        return CollectionsResult<GenesysHistoryPageDto>.Ok(new(page, pageSize, result.TotalCount, items, Summarize(result.Summary),
            await OptionsAsync(names, ct), GenesysUploadStates.CallOutcomeNote, permissions.CanSendReminders));
    }

    public async Task<CollectionsResult<GenesysSubmissionDetailDto>> GetAsync(CollectionsCaller caller, Guid submissionId, CancellationToken ct)
    {
        if (!(await authorization.ResolveAsync(caller, ct)).CanReadFinancials) return CollectionsResult<GenesysSubmissionDetailDto>.Fail(CollectionsOutcome.Forbidden);
        var detail = await store.GetSubmissionAsync(submissionId, ct);
        if (detail is null) return CollectionsResult<GenesysSubmissionDetailDto>.Fail(CollectionsOutcome.NotFound);
        var names = new NameCache(this);
        var submission = await MapAsync(detail.Row, names, ct);
        var batches = detail.Batches.OrderBy(b => b.Sequence).Select(b => new GenesysBatchDetailDto(
            b.Sequence, b.ReminderType.ToString(), b.StageName, b.ContactListId, ListLabel(b.ContactListId), b.Status.ToString(), BatchLabel(b.Status),
            b.ContactCount, b.Status is GenesysBatchStatus.Pending or GenesysBatchStatus.Submitting ? null : b.ReturnedContactCount, b.HttpStatus, b.AttemptCount, b.StartedAtUtc, b.CompletedAtUtc,
            b.GenesysCampaignName, b.GenesysCampaignId, b.CampaignAction, Short(b.RequestHash), b.Error, b.ReconciliationNote, b.ReconciledAtUtc)).ToList();
        var d = detail.Row;
        return CollectionsResult<GenesysSubmissionDetailDto>.Ok(new(submission, batches, d.ApprovedCount, d.ExcludedAtDispatch, detail.SharedPhoneRecords,
            d.StatusReason, d.Phase, d.StartedAtUtc, d.CompletedAtUtc, links is not null, GenesysUploadStates.CallOutcomeNote));
    }

    public async Task<CollectionsResult<GenesysRecordsPageDto>> RecordsAsync(CollectionsCaller caller, Guid submissionId, string? result, string? search, int page, int pageSize, CancellationToken ct)
    {
        if (!(await authorization.ResolveAsync(caller, ct)).CanReadFinancials) return CollectionsResult<GenesysRecordsPageDto>.Fail(CollectionsOutcome.Forbidden);
        if (!string.IsNullOrWhiteSpace(result) && !RecordResultFilters.Contains(result.Trim(), StringComparer.OrdinalIgnoreCase))
            return CollectionsResult<GenesysRecordsPageDto>.Fail(CollectionsOutcome.InvalidRequest, $"Result must be one of: {string.Join(", ", RecordResultFilters)}.");
        (page, pageSize) = Clamp(page, pageSize);
        var detail = await store.GetSubmissionAsync(submissionId, ct);
        if (detail is null) return CollectionsResult<GenesysRecordsPageDto>.Fail(CollectionsOutcome.NotFound);
        var filter = new GenesysRecordFilter(string.IsNullOrWhiteSpace(result) ? null : RecordResultFilters.First(f => string.Equals(f, result.Trim(), StringComparison.OrdinalIgnoreCase)),
            string.IsNullOrWhiteSpace(search) ? null : search.Trim());
        var found = await store.ListRecordsAsync(detail.Row.DispatchId, filter, (page - 1) * pageSize, pageSize, ct);
        var finished = detail.Row.Status is not (DispatchStatus.Queued or DispatchStatus.Revalidating or DispatchStatus.Sending);

        // The CRM customer id is not frozen on the item: it is the CURRENT link of the unit in the local CRM owner data (one set-based read for the page), and absent when
        // CRM holds no single eligible customer for the unit or the feed is unavailable. It is never guessed.
        IReadOnlyDictionary<(int, string), int> crm = new Dictionary<(int, string), int>();
        if (links is not null && found.Items.Count > 0)
        {
            try
            {
                var set = await links.GetLinksAsync(found.Items.Select(r => (r.Item.CompanyId, r.Item.UnitCode)), ct);
                var map = new Dictionary<(int, string), int>();
                foreach (var r in found.Items)
                    if (set.Find(r.Item.CompanyId, r.Item.UnitCode) is { Status: CrmLinkStatus.Single } link && link.CustomerId > 0) map[(r.Item.CompanyId, r.Item.UnitCode)] = link.CustomerId;
                crm = map;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning("CRM customer ids for the history page could not be read ({ExceptionType}).", ex.GetType().Name);
            }
        }

        var items = found.Items.Select(r =>
        {
            var i = r.Item;
            var submitted = i.CollectionsGenesysBatchId is not null && i.Status is not (DispatchItemStatus.Excluded or DispatchItemStatus.Released);
            var text = GenesysUploadStateRules.RecordResult(i.Status, submitted, finished);
            return new GenesysSubmissionRecordDto(
                i.CollectionsDispatchItemId, i.CustomerName, crm.TryGetValue((i.CompanyId, i.UnitCode), out var crmId) ? crmId : null, i.UnitCode, i.Phone, i.Email,
                i.ReminderType.ToString(), CampaignReminderTypes.DefaultLabel(i.ReminderType), i.Amount, i.Currency, i.DueDate, text, ResultState(text), i.UploadedAtUtc, r.BatchStartedAtUtc,
                i.GenesysContactId, i.Status is DispatchItemStatus.UploadedToGenesys ? null : i.StatusReason, r.SharedPhone, SuppressionText(i.SuppressionStatus), GenesysUploadStates.CallOutcomeUnavailable);
        }).ToList();
        return CollectionsResult<GenesysRecordsPageDto>.Ok(new(page, pageSize, found.TotalCount, items, GenesysUploadStates.CallOutcomeNote));
    }

    public static readonly IReadOnlyList<string> RecordResultFilters = ["Uploaded", "Failed", "Unknown", "NotSent"];

    // ---------------------------------------------------------------- export history (downloaded files)

    public async Task<CollectionsResult<GenesysExportHistoryPageDto>> ExportsAsync(CollectionsCaller caller, DateOnly? from, DateOnly? to, int page, int pageSize, CancellationToken ct)
    {
        // The file was customer contact data and only the send right can make one: its audit trail is for the same people.
        if (!(await authorization.ResolveAsync(caller, ct)).CanSendReminders) return CollectionsResult<GenesysExportHistoryPageDto>.Fail(CollectionsOutcome.Forbidden);
        if (from is not null && to is not null && from > to) return CollectionsResult<GenesysExportHistoryPageDto>.Fail(CollectionsOutcome.InvalidRequest, "The From date is after the To date.");
        (page, pageSize) = Clamp(page, pageSize);
        var found = await store.ListExportAuditAsync(GenesysExportService.AuditAction, DubaiStart(from), to is { } t ? DubaiStart(t.AddDays(1)) : null, (page - 1) * pageSize, pageSize, ct);
        var names = new NameCache(this);
        var items = new List<GenesysExportHistoryDto>();
        foreach (var row in found.Items) items.Add(await MapExportAsync(row, names, ct));
        return CollectionsResult<GenesysExportHistoryPageDto>.Ok(new(page, pageSize, found.TotalCount, items, ExportNotice));
    }

    private async Task<GenesysExportHistoryDto> MapExportAsync(ExportAuditRow row, NameCache names, CancellationToken ct)
    {
        var d = ParseExport(row.Details);
        return new(row.AuditId, row.OccurredAtUtc, row.ActorEmployeeId, row.ActorEmployeeId is { } id ? await names.NameAsync(id, ct) : "System",
            d.Campaign, d.Campaign is not null && Enum.TryParse<CampaignReminderType>(d.Campaign, out var type) ? CampaignReminderTypes.DefaultLabel(type) : d.Campaign,
            d.ContactList, d.Mode, d.Rows, d.Units, d.Excluded, d.Total, d.Currency, d.DataVerified, d.SchemaVerified, d.Repeated, d.Sha is { Length: >= 12 } sha ? sha[..12] : d.Sha);
    }

    /// <summary>What the export audit entry says, read from its text. Only the leading counters and the file hash are read: the customer fingerprints at the end are ignored, and nothing about a customer is shown.</summary>
    public sealed record ParsedExport(string? Campaign, string? ContactList, int? Rows, int? Units, int? Excluded, decimal? Total, string? Currency, bool? DataVerified, bool? SchemaVerified, string? Mode, int? Repeated, string? Sha);

    public static ParsedExport ParseExport(string? details)
    {
        if (string.IsNullOrEmpty(details)) return new(null, null, null, null, null, null, null, null, null, null, null, null);
        var head = details.Split(";Customers=", 2)[0];
        string? Find(string pattern) => Regex.Match(head, pattern, RegexOptions.None, TimeSpan.FromMilliseconds(200)) is { Success: true } m ? m.Groups[1].Value : null;
        int? Int(string key) => int.TryParse(Find($@"(?:^|;){key}=(\d+)(?:;|$)"), NumberStyles.None, CultureInfo.InvariantCulture, out var v) ? v : null;
        bool? Flag(string key) => Find($@"(?:^|;){key}=(True|False)(?:;|$|\s)") is { } f ? f == "True" : null;
        var total = Regex.Match(head, @"(?:^|;)Total=([A-Z]{3}) (-?\d+(?:\.\d+)?)(?:;|$)", RegexOptions.None, TimeSpan.FromMilliseconds(200));
        var list = Find(@"(?:^|;)ContactList=([A-Za-z]+) \(");
        return new(Find(@"(?:^|;)Campaign=([A-Za-z]+)(?:;|$)"), list, Int("Rows"), Int("Units"), Int("Excluded"),
            total.Success ? decimal.Parse(total.Groups[2].Value, CultureInfo.InvariantCulture) : null, total.Success ? total.Groups[1].Value : null,
            Flag("DataVerified"), Flag("SchemaVerified"), Find(@"(?:^|;)Mode=([A-Za-z]+);Population="), Int("Repeated"), Find(@"Sha256=([0-9A-Fa-f]+)"));
    }

    // ---------------------------------------------------------------- mapping

    private async Task<GenesysSubmissionDto> MapAsync(SubmissionRow row, NameCache names, CancellationToken ct)
    {
        var c = row.Counts;
        var state = GenesysUploadStateRules.Classify(row.Status, c);
        var types = row.Types.Distinct().OrderBy(t => t).ToList();
        var lists = row.Batches.Select(b => b.ContactListId).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var campaigns = row.Batches.Select(b => b.CampaignId).Where(id => !string.IsNullOrEmpty(id)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var failure = row.Batches.Where(b => b.Status is GenesysBatchStatus.Failed or GenesysBatchStatus.UnknownOutcome && !string.IsNullOrWhiteSpace(b.Error)).Select(b => b.Error).FirstOrDefault();
        var reason = state is GenesysUploadStates.Failed or GenesysUploadStates.Unknown or GenesysUploadStates.Partial or GenesysUploadStates.NotSent
            ? failure ?? row.StatusReason : null;
        var planned = c.Submitted > 0;
        var amount = planned ? c.SubmittedAmountAed : ApprovedAed(row.ApprovedTotalsJson);
        var auto = row.InitiatedBy == DispatchService.AutoActorId;
        return new(row.PublicId, row.InitiatedAtUtc, row.StartedAtUtc,
            string.Join(", ", types), string.Join(", ", types.Select(CampaignReminderTypes.DefaultLabel)), string.Join(" + ", types.Select(CampaignReminderTypes.StageName)),
            auto ? CollectionsSendingModes.Auto : CollectionsSendingModes.Manual,
            lists.Count == 0 ? null : string.Join(", ", lists.Select(ListLabel)), lists.Count == 0 ? null : string.Join(", ", lists),
            row.InitiatedBy, auto ? AutoSubmitterName : await names.NameAsync(row.InitiatedBy, ct),
            planned ? c.Submitted : row.ApprovedCount, planned ? "Submitted" : "Approved", amount, "AED", planned ? c.SubmittedOtherCurrency : 0,
            state, GenesysUploadStates.Label(state), GenesysUploadStateRules.Explain(state, c, reason), reason,
            campaigns.Count == 0 ? null : string.Join(", ", campaigns), c.Uploaded, c.Failed, c.Uncertain, c.Excluded + c.Released, GenesysUploadStates.CallOutcomeUnavailable);
    }

    private static decimal ApprovedAed(string json)
    {
        try
        {
            var totals = JsonSerializer.Deserialize<Dictionary<string, string>>(json);
            return totals is not null && totals.TryGetValue("AED", out var text) && decimal.TryParse(text, NumberStyles.Number, CultureInfo.InvariantCulture, out var v) ? v : 0m;
        }
        catch (JsonException) { return 0m; }
    }

    private static GenesysHistorySummaryDto Summarize(IReadOnlyList<SummaryRow> rows)
    {
        var byStatus = GenesysUploadStates.All.ToDictionary(s => s, _ => 0);
        foreach (var r in rows) byStatus[GenesysUploadStateRules.Classify(r.Status, r.Counts)]++;
        return new(rows.Count, rows.Sum(r => r.Counts.Submitted), rows.Sum(r => r.Counts.SubmittedAmountAed), "AED", byStatus[GenesysUploadStates.Uploaded],
            byStatus[GenesysUploadStates.Failed] + byStatus[GenesysUploadStates.Partial] + byStatus[GenesysUploadStates.Unknown],
            byStatus[GenesysUploadStates.Pending] + byStatus[GenesysUploadStates.Uploading], byStatus, rows.Sum(r => r.Counts.Uploaded));
    }

    private async Task<GenesysHistoryOptionsDto> OptionsAsync(NameCache names, CancellationToken ct)
    {
        var people = new List<GenesysFilterOptionDto>();
        foreach (var id in await store.ListSubmitterIdsAsync(ct)) people.Add(new(id.ToString("D"), await names.NameAsync(id, ct)));
        return new(
            Enum.GetValues<CampaignReminderType>().Select(t => new GenesysFilterOptionDto(t.ToString(), CampaignReminderTypes.StageName(t))).ToList(),
            Enum.GetValues<CampaignReminderType>().Select(t => new GenesysFilterOptionDto(t.ToString(), ListLabel(t)))
                .Append(new GenesysFilterOptionDto(CampaignReminderTypes.ManualListKey, CampaignReminderTypes.ManualListName + " list")).ToList(),
            GenesysUploadStates.All.Select(s => new GenesysFilterOptionDto(s, GenesysUploadStates.Label(s))).ToList(),
            [new(CollectionsSendingModes.Manual, "Manual"), new(CollectionsSendingModes.Auto, "Auto")],
            people.OrderBy(p => p.Label, StringComparer.OrdinalIgnoreCase).ToList());
    }

    // ---------------------------------------------------------------- filter

    private GenesysHistorySpec? BuildSpec(GenesysHistoryFilter f, out string? problem)
    {
        problem = null;
        if (f.From is not null && f.To is not null && f.From > f.To) { problem = "The From date is after the To date."; return null; }
        CampaignReminderType? stage = null;
        if (!string.IsNullOrWhiteSpace(f.Stage))
        {
            if (!Enum.TryParse<CampaignReminderType>(f.Stage.Trim(), true, out var s) || !Enum.IsDefined(s)) { problem = "Unknown campaign stage."; return null; }
            stage = s;
        }
        string? status = null;
        if (!string.IsNullOrWhiteSpace(f.Status))
        {
            status = GenesysUploadStates.All.FirstOrDefault(x => string.Equals(x, f.Status.Trim(), StringComparison.OrdinalIgnoreCase));
            if (status is null) { problem = $"Status must be one of: {string.Join(", ", GenesysUploadStates.All)}."; return null; }
        }
        string? mode = null;
        if (!string.IsNullOrWhiteSpace(f.Mode))
        {
            if (!CollectionsSendingModes.TryParse(f.Mode, out var m)) { problem = "Mode must be Manual or Auto."; return null; }
            mode = m;
        }
        string? listId = null;
        if (!string.IsNullOrWhiteSpace(f.List))
        {
            var text = f.List.Trim();
            if (CampaignReminderTypes.IsManualList(text) && string.Equals(text.Replace(" ", ""), CampaignReminderTypes.ManualListKey, StringComparison.OrdinalIgnoreCase))
            {
                try { listId = genesys.ManualContactListId; }
                catch (InvalidOperationException ex) { problem = ex.Message; return null; }
            }
            else if (Enum.TryParse<CampaignReminderType>(text, true, out var type) && Enum.IsDefined(type))
            {
                try { listId = genesys.ContactListIdFor(type); }
                catch (InvalidOperationException) { problem = $"No contact list is configured for {CampaignReminderTypes.DefaultLabel(type)}."; return null; }
            }
            else if (Guid.TryParse(text, out var guid)) listId = guid.ToString("D");
            else { problem = "Unknown destination list."; return null; }
        }
        var search = string.IsNullOrWhiteSpace(f.Search) ? null : f.Search.Trim();
        if (search is { Length: > 100 }) search = search[..100];
        return new(DubaiStart(f.From), f.To is { } to ? DubaiStart(to.AddDays(1)) : null, stage, status, mode, f.EmployeeId, listId, search);
    }

    /// <summary>The UTC instant at which a Dubai calendar day begins.</summary>
    private DateTime? DubaiStart(DateOnly? day)
    {
        if (day is not { } d) return null;
        var zone = TimeZoneInfo.FindSystemTimeZoneById(options.TimeZoneId);
        return TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(d.ToDateTime(TimeOnly.MinValue), DateTimeKind.Unspecified), zone);
    }

    private static (int Page, int PageSize) Clamp(int page, int pageSize) =>
        (Math.Max(1, page), pageSize <= 0 ? DefaultPageSize : Math.Min(pageSize, MaxPageSize));

    // ---------------------------------------------------------------- labels

    /// <summary>The configured list a contact-list id belongs to ("Overdue list"), else a neutral label with the end of the id: never a guess.</summary>
    public string ListLabel(string listId)
    {
        try { if (string.Equals(genesys.ManualContactListId, listId, StringComparison.OrdinalIgnoreCase)) return CampaignReminderTypes.ManualListName + " list"; }
        catch (InvalidOperationException) { /* not configured: fall through to the neutral label */ }
        foreach (var type in Enum.GetValues<CampaignReminderType>())
            if (genesys.ContactListIds.TryGetValue(type.ToString(), out var id) && string.Equals(id?.Trim(), listId, StringComparison.OrdinalIgnoreCase)) return ListLabel(type);
        return "Other contact list …" + (listId.Length > 6 ? listId[^6..] : listId);
    }

    private static string ListLabel(CampaignReminderType type) => CampaignReminderTypes.DefaultLabel(type) + " list";

    private static string BatchLabel(GenesysBatchStatus s) => s switch
    {
        GenesysBatchStatus.Pending => "Pending",
        GenesysBatchStatus.Submitting => "Uploading",
        GenesysBatchStatus.Uploaded => "Uploaded",
        GenesysBatchStatus.Failed => "Failed",
        GenesysBatchStatus.UnknownOutcome => "Unknown",
        GenesysBatchStatus.ConfirmedNotUploaded => "Confirmed not uploaded",
        _ => s.ToString()
    };

    private static string ResultState(string text) => text switch
    {
        "Uploaded" => "uploaded",
        "Failed" => "failed",
        "Unknown" => "unknown",
        "Uploading" or "Pending" => "pending",
        _ => "notsent"
    };

    private static string? SuppressionText(ContactSuppressionStatus s) => s switch
    {
        ContactSuppressionStatus.Suppressed => "Made not callable in Genesys after the balance was paid",
        ContactSuppressionStatus.Failed => "Could not be made not callable after payment: needs attention",
        ContactSuppressionStatus.Unconfirmed => "Making it not callable after payment is unconfirmed",
        _ => null
    };

    private static string? Short(string? hash) => hash is { Length: >= 12 } ? hash[..12].ToLowerInvariant() : hash;

    /// <summary>Employee display names, each looked up once per request; the id is kept when a name cannot be found.</summary>
    private sealed class NameCache(GenesysHistoryService owner)
    {
        private readonly Dictionary<Guid, string> _names = [];

        public async Task<string> NameAsync(Guid id, CancellationToken ct)
        {
            if (id == DispatchService.AutoActorId) return AutoSubmitterName;
            if (_names.TryGetValue(id, out var cached)) return cached;
            string? name = null;
            if (owner._employees is not null)
            {
                try { name = (await owner._employees.GetByIdAsync(id, ct))?.DisplayName; }
                catch (Exception ex) when (ex is not OperationCanceledException) { name = null; }
            }
            return _names[id] = string.IsNullOrWhiteSpace(name) ? "Unknown employee" : name;
        }
    }
}
