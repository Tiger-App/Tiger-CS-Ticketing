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
/// "Export for Genesys": a download-only file a person can import into a Genesys outbound contact list by hand. It is NOT a send, and nothing that only a send needs is a condition of it.
/// <list type="bullet">
/// <item><b>Who is in it.</b> Every customer unit of the chosen campaign type that matches Tower, Payment status, From / To and Minimum Total (<see cref="ReviewQueryService.ResolveForExportAsync"/>),
/// in both modes, on any day, whatever its validation status: Ready, Needs review, held back (reconciliation, PaymentStatusUnknown, legal, shared phone) and already sent alike. The amounts are the
/// ones Preview shows: the one Manual calculation over each unit's stored instalments. Only the person's ticks narrow it (Manual); nothing has to be selected.</item>
/// <item><b>Rows.</b> <see cref="GenesysExportRows"/>: one row per phone number; several units of the same customer on one number are added up (earliest due date kept); units without a calculable
/// amount, without a callable number, with an ambiguous allocation between units, or sharing a number with a different customer are left out and COUNTED, never silently dropped and never guessed.
/// Each row is built by <see cref="DispatchService.ToPayload"/>, the code that builds a real upload, and written with <see cref="GenesysContactTemplate.WriteCsv(IEnumerable{GenesysContactPayload}, IReadOnlyList{string})"/>:
/// the six columns Phone, CustomerName, Email Address, ReminderType, AmountDue, DueDate, in that order and format. Nothing else about a customer is in the file.</item>
/// <item><b>Columns.</b> When the Genesys integration can read the chosen list, its real columns are used and a list that lacks or misnames a template column refuses the export (an incompatibility
/// is reported, never guessed around). When the list cannot be read (integration not configured, login refused, Genesys unreachable) the documented six-column template is used and the file, the
/// notices and the audit entry say "Template not verified against Genesys".</item>
/// <item><b>Unverified data.</b> An unreconciled source, stale approval data, data built for another day, or included units whose data needs review (PaymentStatusUnknown included) never stop the
/// file; they label it "Financial data not verified" (file name, notices, audit) and say exactly why. The source values are written as they are; an Unknown payment status is never written as unpaid.</item>
/// <item><b>Read only.</b> It uploads nothing, queues nothing and marks no one as sent. The send protections (Ready status, reconciliation, freshness, shared phone, already sent, contacted today,
/// idempotency) belong to <see cref="DispatchService"/> and are untouched: a customer in a file can still be sent, once, under every rule.</item>
/// <item><b>Audited.</b> Each export is written to the audit trail before the file is released (who, when, campaign, list, rows, units, exclusions, total, statuses, verification, the filter, a hash of
/// the file and a one-way fingerprint of each customer's unit and number; never a customer's data). If the audit entry cannot be written, no file is released. Customers exported within
/// <c>ExportRepeatLookbackDays</c> are counted and noted (<c>Repeated=n</c>) so repeated downloads are visible; a repeat never blocks a download and needs no confirmation.</item>
/// </list>
/// </summary>
public sealed class GenesysExportService(
    CollectionsOptions options, PactReceivablesOptions sourceOptions, GenesysOutboundOptions genesys, CollectionsAuthorizationService authorization, CollectionsClock clock,
    ReviewQueryService query, IGenesysOutboundClient client, IAuditEntryWriter audit, ICollectionsUnitOfWork unitOfWork, CollectionsReviewOptions reviewOptions, IGenesysExportHistory history,
    IGenesysExportLock exportLock, ILogger<GenesysExportService> logger)
{
    public const string AuditAction = "CollectionsGenesysFileExported", AuditEntityType = "CollectionsGenesysExport";

    /// <summary>How long an export waits for the export lock while another export is recording its audit entry, before it stops.</summary>
    public static readonly TimeSpan ExportLockWait = TimeSpan.FromSeconds(20);

    /// <summary>The label of a file whose contact-list columns were not read from Genesys.</summary>
    public const string NotVerifiedLabel = "Template not verified against Genesys";

    /// <summary>The label of a file whose amounts rest on unverified financial data.</summary>
    public const string UnverifiedDataLabel = "Financial data not verified";

    /// <summary>The reason code recorded when the approval data was built for another day than the export.</summary>
    public const string BuiltForAnotherDay = "BuiltForAnotherDay";

    /// <summary>The most earlier exports read for the repeat count. A window that holds this many cannot be counted in full; the file is still made and says so.</summary>
    public const int MaxHistoryEntries = 5000;

    /// <summary>A one-way fingerprint of a customer's unit (company, tenant, unit): the same customer is recognised in every campaign type and month, and no customer data is kept.</summary>
    public static string Fingerprint(string customerUnitKey) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("genesys-export|" + customerUnitKey)))[..12];

    /// <summary>The same one-way fingerprint for a phone number: the number that is dialled, whichever unit it was written against.</summary>
    public static string PhoneFingerprint(string phone) => Fingerprint("phone|" + phone);

    /// <param name="caller">Who exports; needs the send right (the file is customer contact data).</param>
    /// <param name="request">The campaign type, filters, contact list and mode; with the ticked customers (Manual) or every matching customer.</param>
    /// <param name="ct">Cancellation.</param>
    public async Task<CollectionsResult<GenesysExportDto>> ExportAsync(CollectionsCaller caller, SelectionRequest request, CancellationToken ct)
    {
        // Authorization first: no data is read for an unauthorized caller. The file is customer contact data, so it needs the same right as a send.
        if (!(await authorization.ResolveAsync(caller, ct)).CanSendReminders)
            return CollectionsResult<GenesysExportDto>.Fail(CollectionsOutcome.Forbidden);
        // The module and the financial source must be switched on: stored data is not released while its switch is off.
        if (!options.Enabled || !sourceOptions.Enabled) return CollectionsResult<GenesysExportDto>.Fail(CollectionsOutcome.Disabled);
        // The mode the export was started from is kept for the audit trail only: both modes export the same population, and neither sends.
        if (!CollectionsSendingModes.TryParse(request.Filter?.SendingMode, out var mode))
            return CollectionsResult<GenesysExportDto>.Fail(CollectionsOutcome.InvalidRequest, "Export for Genesys is for the Manual or the Auto list.");

        var resolved = await query.ResolveForExportAsync(request, ct);
        if (resolved.Error is not null) return CollectionsResult<GenesysExportDto>.Fail(CollectionsOutcome.InvalidRequest, resolved.Error);
        if (resolved.Candidates.Count == 0)
            return CollectionsResult<GenesysExportDto>.Fail(CollectionsOutcome.InvalidRequest, "No customers match these filters for this campaign type, so there is nothing to export.");
        var types = resolved.Candidates.Select(c => c.Record.ReminderType).Distinct().ToList();
        if (types.Count != 1) return CollectionsResult<GenesysExportDto>.Fail(CollectionsOutcome.InvalidRequest, "An export contains one campaign type.");
        var type = types[0];

        // The destination list: the campaign's own, or the existing list the person chose among those that suit it (the same rule as a send, so the file goes where a send would).
        if (!CampaignReminderTypes.TryResolveManualDestination(type, request.Filter?.ContactList, out var destination))
            return CollectionsResult<GenesysExportDto>.Fail(CollectionsOutcome.InvalidRequest, "That contact list does not suit this campaign.");
        string listId;
        try { listId = genesys.ContactListIdFor(destination); }
        catch (InvalidOperationException ex) { return CollectionsResult<GenesysExportDto>.Fail(CollectionsOutcome.InvalidRequest, ex.Message); }
        var listLabel = CampaignReminderTypes.DefaultLabel(destination);

        var schema = await ReadSchemaAsync(listId, listLabel, ct);
        if (schema.Problem is not null) return CollectionsResult<GenesysExportDto>.Fail(CollectionsOutcome.InvalidRequest, schema.Problem);

        var rows = GenesysExportRows.Build(resolved.Candidates);
        if (rows.Rows.Count == 0)
            return CollectionsResult<GenesysExportDto>.Fail(CollectionsOutcome.InvalidRequest,
                $"None of the {resolved.Candidates.Count:N0} matching customer(s) could be exported: {Summary(rows.Exclusions)}. Nothing was exported.");

        // The rows are built by the upload's own code, so every value is exactly what an upload would send.
        var payloads = rows.Rows.Select(r => DispatchService.ToPayload(Item(r), listId, genesys)).ToList();
        var csv = GenesysContactTemplate.WriteCsv(payloads, schema.Columns);
        // The file is checked against the template before it is released. This can only fail if a value broke a format rule.
        var check = GenesysContactTemplate.Verify(csv, GenesysContactTemplate.ExpectedReminderTypes(genesys));
        if (!check.IsMatch || check.RowCount != payloads.Count)
        {
            logger.LogError("Genesys export failed its own template check ({Issues} issues).", check.Issues.Count);
            return CollectionsResult<GenesysExportDto>.Fail(CollectionsOutcome.InvalidRequest,
                $"The file did not pass the Genesys template check ({check.Issues.Count} issue(s), for example {check.Issues.FirstOrDefault()?.Message ?? "unknown"}). Nothing was exported.");
        }

        var data = AssessData(resolved, rows);
        var units = rows.Rows.SelectMany(r => r.Units).ToList();
        var byStatus = units.GroupBy(u => u.Status).OrderBy(g => g.Key).ToDictionary(g => g.Key.ToString(), g => g.Count());
        // A customer is recognised by its unit AND by its number: the same customer on another unit, or the same unit under another number, is still the person being called again.
        var customers = units.Select(u => new ExportedCustomer(Fingerprint(ReviewQueryService.UnitKey(u.Record.CompanyId, u.Record.TenantId, u.Record.UnitCode)), PhoneFingerprint(u.Record.Phone))).ToList();
        var fingerprints = customers.SelectMany(c => new[] { c.Unit, c.Phone }).Distinct().ToList();
        var total = rows.Rows.Sum(r => r.Amount);                     // the total of the rows that are in the file, nothing else
        var currency = rows.Rows[0].Currency;
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(csv)));
        var now = clock.UtcNow;

        // The repeat count and the audit entry are one step under the export lock, so two simultaneous exports each see the other's entry. The lock covers only that step.
        var gate = await exportLock.TryAcquireAsync(ExportLockWait, ct);
        if (gate is null) return CollectionsResult<GenesysExportDto>.Fail(CollectionsOutcome.FinanceUnavailable, "Another export is being recorded. Try again in a moment.");
        Repeat repeat;
        try
        {
            repeat = await CountRepeatsAsync(customers, ct);
            // Audit first, then release: an export of customer contact data is never handed out without its record. The record holds counts, the filter and a hash, no customer data.
            try
            {
                await audit.WriteAsync(caller.EmployeeId, AuditAction, AuditEntityType, hash[..16], null,
                    AuditDetails(type, destination, listId, rows, total, currency, byStatus, data, schema, mode, request, repeat, hash, fingerprints, resolved.Skipped), Guid.NewGuid(), ct);
                await unitOfWork.SaveChangesAsync(ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                unitOfWork.DiscardPendingChanges();
                logger.LogError("Genesys export could not be audited ({ExceptionType}); the file was not released.", ex.GetType().Name);
                return CollectionsResult<GenesysExportDto>.Fail(CollectionsOutcome.FinanceUnavailable, "The export could not be recorded in the audit trail, so no file was released. Try again.");
            }
        }
        finally
        {
            await gate.DisposeAsync();
        }

        var notices = new List<string>();
        if (!schema.Verified) notices.Add(schema.Notice!);
        notices.AddRange(data.Notices);
        if (rows.Exclusions.Count > 0) notices.Add($"{rows.ExcludedUnits:N0} of the {resolved.Candidates.Count:N0} matching customer unit(s) are not in the file: {Summary(rows.Exclusions)}.");
        if (rows.CombinedRows > 0) notices.Add($"{rows.CombinedUnits:N0} unit(s) of the same customer on one number were combined into {rows.CombinedRows:N0} row(s), with the amounts added up and the earliest due date.");
        if (resolved.Skipped > 0) notices.Add($"{resolved.Skipped:N0} ticked customer(s) no longer match these filters (paid or changed) and are not in the file.");
        if (repeat.Notice is not null) notices.Add(repeat.Notice);
        // The labels travel in the file name (the CSV itself must stay the six-column template for the import), so an unverified file is never mistaken for a verified one.
        var labels = (schema.Verified ? "" : $" ({NotVerifiedLabel})") + (data.Verified ? "" : $" ({UnverifiedDataLabel})");
        var fileName = $"genesys-{type}-to-{destination}-{now:yyyyMMdd-HHmm}{labels}.csv";
        return CollectionsResult<GenesysExportDto>.Ok(new(fileName, csv, payloads.Count, CampaignReminderTypes.StageName(type), listLabel, listId, total, currency,
            GenesysContactTemplate.Columns.Concat(schema.Columns.Where(c => !GenesysContactTemplate.Columns.Contains(c, StringComparer.Ordinal))).ToList(), schema.Verified, notices, hash,
            repeat.Customers, repeat.Customers > 0 ? repeat.LastUtc : null, units.Count, rows.CombinedRows, rows.CombinedUnits,
            rows.Exclusions.Select(e => new GenesysExportExclusionDto(e.Code, e.Units, e.Explanation)).ToList(), byStatus, data.Verified, data.Reasons, resolved.Skipped));
    }

    /// <summary>The dispatch item a row would be uploaded as: the same fields a real send fills, so <see cref="DispatchService.ToPayload"/> formats the row exactly as an upload.</summary>
    private static CollectionsDispatchItem Item(GenesysExportRow row) => new()
    {
        RecordKey = row.First.RecordKey, ReminderType = row.ReminderType, CompanyId = row.First.CompanyId, TenantId = row.First.TenantId, UnitId = row.First.UnitId, UnitCode = row.First.UnitCode,
        CustomerName = row.CustomerName, Phone = row.Phone, Email = row.Email, Amount = row.Amount, Currency = row.Currency, DueDate = row.DueDate, VoiceEligible = true
    };

    private static string Summary(IReadOnlyList<GenesysExportExclusion> exclusions) =>
        string.Join("; ", exclusions.Select(e => $"{e.Units:N0} {e.Code} ({e.Explanation.TrimEnd('.')})"));

    // ---------------------------------------------------------------- verification of the data behind the amounts

    /// <param name="Verified">Nothing about the data is in doubt.</param>
    /// <param name="Reasons">Why not, as codes (<c>PaymentStatusUnknown:12</c> counts the units concerned).</param>
    /// <param name="Notices">The same, for people.</param>
    private sealed record DataVerification(bool Verified, IReadOnlyList<string> Reasons, IReadOnlyList<string> Notices);

    /// <summary>
    /// What is unverified about the data the amounts come from. Nothing here stops the file: each doubt is named, labelled and audited, so a person imports it knowing exactly what it rests on.
    /// A unit held back from SENDING by a policy rule (schedule, legal release, repeated calls, already sent) is not unverified data; it is counted separately so the file says it holds such units.
    /// </summary>
    private DataVerification AssessData(ReviewQueryService.ExportResolution resolved, GenesysExportRowSet rows)
    {
        var run = resolved.Run!;
        var reasons = new List<string>();
        var notices = new List<string>();
        if (!run.SourceReconciled)
        {
            reasons.Add(ReviewReasons.SourceReconciliationRequired);
            notices.Add("The financial source has not been reconciled against PACT, so the amounts are unverified.");
        }
        if (resolved.Stale)
        {
            reasons.Add(ReviewReasons.StaleSource);
            var read = run.SourceReadAtUtc;
            notices.Add(read is { } at
                ? $"The approval data is stale: the financial source was read {Age(clock.UtcNow - at)} ago ({at:dd MMM yyyy HH:mm} UTC), so the amounts may have changed since. Refresh it for current amounts."
                : "The approval data is stale and its read time is unknown, so the amounts may have changed since. Refresh it for current amounts.");
        }
        var today = clock.BusinessDate;
        if (run.AsOfDate != today)
        {
            reasons.Add(BuiltForAnotherDay);
            notices.Add($"The approval data was built for {run.AsOfDate:dd MMM yyyy}, not today ({today:dd MMM yyyy}): Due and Overdue were decided for that day. Refresh it for today's figures.");
        }
        var units = rows.Rows.SelectMany(r => r.Units).ToList();
        var needsReview = units.SelectMany(u => u.Reasons.Where(c => ReviewReasons.KindOf(c) == ReasonKind.NeedsReview).Distinct(StringComparer.Ordinal))
            .Where(c => c is not (ReviewReasons.SourceReconciliationRequired or ReviewReasons.StaleSource))   // already stated once for the whole file
            .GroupBy(c => c, StringComparer.Ordinal).OrderByDescending(g => g.Count()).ThenBy(g => g.Key, StringComparer.Ordinal).ToList();
        foreach (var reason in needsReview)
        {
            reasons.Add($"{reason.Key}:{reason.Count()}");
            notices.Add(reason.Key == ReviewReasons.PaymentStatusUnknown
                ? $"{reason.Count():N0} unit(s) have a remaining amount that could not be determined reliably (PaymentStatusUnknown): their source amounts are written as they are, not classified as unpaid."
                : $"{reason.Count():N0} unit(s) need review ({reason.Key}): {ReviewReasons.Explain(reason.Key)}");
        }
        var heldBack = units.Count(u => u.Status != ReviewValidationStatus.Ready);
        if (heldBack > 0)
        {
            var parts = units.Where(u => u.Status != ReviewValidationStatus.Ready).GroupBy(u => u.Status).OrderBy(g => g.Key)
                .Select(g => $"{StatusLabel(g.Key)} {g.Count():N0}");
            notices.Add($"{heldBack:N0} of the {units.Count:N0} customer unit(s) in this file are held back from sending ({string.Join(", ", parts)}). A file is not a send, so they are included; review them before importing.");
        }
        return new(reasons.Count == 0, reasons, notices);
    }

    private static string StatusLabel(ReviewValidationStatus status) => status switch
    {
        ReviewValidationStatus.NeedsReview => "Needs review", ReviewValidationStatus.Excluded => "Held back", ReviewValidationStatus.AlreadySent => "Already sent", _ => status.ToString()
    };

    /// <summary>An age for people: days, hours or minutes.</summary>
    public static string Age(TimeSpan age)
    {
        if (age < TimeSpan.Zero) age = TimeSpan.Zero;
        if (age.TotalDays >= 1) return $"{(int)age.TotalDays} day(s) {age.Hours} hour(s)";
        if (age.TotalHours >= 1) return $"{(int)age.TotalHours} hour(s) {age.Minutes} minute(s)";
        return $"{Math.Max(1, (int)age.TotalMinutes)} minute(s)";
    }

    // ---------------------------------------------------------------- the audit entry

    private static string AuditDetails(CampaignReminderType type, CampaignReminderType destination, string listId, GenesysExportRowSet rows, decimal total, string currency,
        IReadOnlyDictionary<string, int> byStatus, DataVerification data, Schema schema, string mode, SelectionRequest request, Repeat repeat, string hash, IReadOnlyList<string> fingerprints, int skipped)
    {
        var text = new StringBuilder();
        text.Append("Campaign=").Append(type).Append(";ContactList=").Append(destination).Append(" (").Append(listId).Append(')');
        text.Append(";Rows=").Append(rows.Rows.Count.ToString(CultureInfo.InvariantCulture)).Append(";Units=").Append(rows.Rows.Sum(r => r.Units.Count).ToString(CultureInfo.InvariantCulture));
        text.Append(";CombinedRows=").Append(rows.CombinedRows.ToString(CultureInfo.InvariantCulture)).Append(";CombinedUnits=").Append(rows.CombinedUnits.ToString(CultureInfo.InvariantCulture));
        text.Append(";Excluded=").Append(rows.ExcludedUnits.ToString(CultureInfo.InvariantCulture));
        text.Append(";Exclusions=").Append(string.Join(',', rows.Exclusions.Select(e => $"{e.Code}:{e.Units.ToString(CultureInfo.InvariantCulture)}")));
        text.Append(";Total=").Append(currency).Append(' ').Append(total.ToString("0.00", CultureInfo.InvariantCulture));
        text.Append(";IncludedByStatus=").Append(string.Join(',', byStatus.Select(s => $"{s.Key}:{s.Value.ToString(CultureInfo.InvariantCulture)}")));
        text.Append(";DataVerified=").Append(data.Verified).Append(";Unverified=").Append(string.Join(',', data.Reasons)).Append(data.Verified ? "" : $" ({UnverifiedDataLabel})");
        text.Append(";SchemaVerified=").Append(schema.Verified).Append(schema.Verified ? "" : $" ({NotVerifiedLabel})");
        text.Append(";Mode=").Append(mode).Append(";Population=Filtered");
        // The customer search text is a name or number typed by a person: the audit keeps only whether one was given.
        text.Append(";Filter=").Append(JsonSerializer.Serialize(request.Filter is { } f ? f with { Customer = null } : null));
        text.Append(";CustomerSearch=").Append(!string.IsNullOrWhiteSpace(request.Filter?.Customer));
        text.Append(";Selection=").Append(request.Mode).Append(";Skipped=").Append(skipped.ToString(CultureInfo.InvariantCulture));
        text.Append(";Repeated=").Append(repeat.Customers.ToString(CultureInfo.InvariantCulture)).Append(";RepeatCheck=").Append(repeat.Check);
        text.Append(";Sha256=").Append(hash);
        text.Append(";Customers=").Append(string.Join(',', fingerprints));                                                        // last: the repeat count reads from this marker on
        return text.ToString();
    }

    // ---------------------------------------------------------------- repeats (counted and noted, never a block)

    /// <param name="Customers">How many of the file's units were exported before within the look-back.</param>
    /// <param name="Exports">How many earlier exports held any of them.</param>
    /// <param name="LastUtc">The latest of those exports.</param>
    /// <param name="Check"><c>Checked</c>, <c>Off</c> (look-back 0), <c>Unavailable</c> (the history could not be read) or <c>Incomplete</c> (the window holds more entries than are read).</param>
    /// <param name="Notice">What to tell the person, if anything.</param>
    private sealed record Repeat(int Customers, int Exports, DateTime LastUtc, string Check, string? Notice);

    /// <summary>One unit of the file as the repeat count sees it: the fingerprint of its unit and of its number.</summary>
    private sealed record ExportedCustomer(string Unit, string Phone);

    private async Task<Repeat> CountRepeatsAsync(IReadOnlyList<ExportedCustomer> customers, CancellationToken ct)
    {
        var days = reviewOptions.ExportRepeatLookbackDays;
        if (days <= 0) return new(0, 0, default, "Off", null);
        IReadOnlyList<GenesysExportAuditRecord> earlier;
        try { earlier = await history.ListExportsAsync(AuditAction, clock.UtcNow.AddDays(-Math.Min(days, 365)), MaxHistoryEntries, ct); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError("Earlier Genesys exports could not be read ({ExceptionType}); the export is not checked for repeats.", ex.GetType().Name);
            return new(0, 0, default, "Unavailable", "Earlier exports could not be read, so this file was not checked for customers exported before. The export itself is recorded.");
        }
        // Each fingerprint maps back to the units it identifies: a number on two units is one person, and a unit under two numbers is one unit.
        var byFingerprint = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var c in customers) { AddTo(byFingerprint, c.Unit, c.Unit); AddTo(byFingerprint, c.Phone, c.Unit); }
        var repeated = new HashSet<string>(StringComparer.Ordinal);
        int exports = 0; var last = default(DateTime);
        foreach (var entry in earlier)
        {
            var hit = false;
            foreach (var fingerprint in PreviousFingerprints(entry.Details))
            {
                if (!byFingerprint.TryGetValue(fingerprint, out var units)) continue;
                hit = true;
                repeated.UnionWith(units);
            }
            if (!hit) continue;
            exports++;
            if (entry.OccurredAtUtc > last) last = entry.OccurredAtUtc;
        }
        var incomplete = earlier.Count >= MaxHistoryEntries;
        string? notice = null;
        if (repeated.Count > 0)
            notice = $"{repeated.Count:N0} of the {customers.Count:N0} customer unit(s) in this file were exported before within the last {days} days (latest {last:dd MMM yyyy HH:mm} UTC, {exports:N0} earlier export(s)). " +
                     "An export does not mark anyone as sent; the repeat is recorded in the audit trail.";
        if (incomplete)
            notice = (notice is null ? "" : notice + " ") + $"The last {days} days hold {MaxHistoryEntries:N0} exports or more, so earlier exports beyond that were not checked.";
        return new(repeated.Count, exports, last, incomplete ? "Incomplete" : "Checked", notice);
    }

    private static void AddTo(Dictionary<string, List<string>> map, string fingerprint, string unit)
    {
        if (!map.TryGetValue(fingerprint, out var units)) map[fingerprint] = units = [];
        units.Add(unit);
    }

    private static IEnumerable<string> PreviousFingerprints(string? details)
    {
        const string marker = ";Customers=";
        var at = details?.LastIndexOf(marker, StringComparison.Ordinal) ?? -1;
        return at < 0 ? [] : details![(at + marker.Length)..].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    // ---------------------------------------------------------------- the list's columns

    /// <param name="Columns">The columns the file is written with.</param>
    /// <param name="Verified">They are the list's real columns, read from Genesys.</param>
    /// <param name="Notice">When not verified: why, for the person.</param>
    /// <param name="Problem">When the list was read and does not fit the template: the exact incompatibility; the export is refused.</param>
    private sealed record Schema(IReadOnlyList<string> Columns, bool Verified, string? Notice, string? Problem);

    /// <summary>
    /// The columns of the chosen list. With the Genesys integration configured they are read from Genesys (read only). A list that was read and lacks or misnames a template column refuses the
    /// export: a real incompatibility is reported, never guessed around. A list that could NOT be read (integration not configured, login refused, Genesys unreachable or answering with an error)
    /// does not stop a download-only file: the documented six-column template is used and the file is labelled "Template not verified against Genesys", with the reason.
    /// </summary>
    private async Task<Schema> ReadSchemaAsync(string listId, string listLabel, CancellationToken ct)
    {
        if (!genesys.Enabled || string.IsNullOrWhiteSpace(genesys.ClientId) || string.IsNullOrWhiteSpace(genesys.ClientSecret))
            return Template($"the {listLabel} list's columns were not checked in Genesys because the integration is not configured here");
        GenesysContactListInfo list;
        try { list = await client.GetContactListAsync(listId, ct); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            logger.LogWarning("Genesys contact list read threw {ExceptionType}; the export uses the documented template.", ex.GetType().Name);
            return Template($"the {listLabel} contact list could not be read in Genesys (the request failed)");
        }
        if (list.Outcome != GenesysUploadOutcome.Accepted)
        {
            logger.LogWarning("Genesys contact list {ListId} could not be read ({Outcome}, HTTP {Status}); the export uses the documented template.", listId, list.Outcome, list.HttpStatus);
            return Template($"the {listLabel} contact list could not be read in Genesys ({list.Error?.TrimEnd('.')})");
        }
        var check = GenesysContactTemplate.CheckHeader(list.Columns);
        if (!check.Fits)
            return new([], false, null, $"The {listLabel} contact list does not match the template: " + string.Join("; ",
                check.Missing.Where(m => !check.Misnamed.Any(n => n.StartsWith(m + " ", StringComparison.Ordinal))).Select(m => $"missing column {m}").Concat(check.Misnamed.Select(m => $"misnamed column {m}"))) + ". Nothing was exported.");
        return new(list.Columns, true, null, null);
    }

    private static Schema Template(string why) =>
        new(GenesysContactTemplate.Columns, false, $"{NotVerifiedLabel}: {why}, so its columns were not checked. The file uses the documented six-column template; check it against the list before importing.", null);
}
