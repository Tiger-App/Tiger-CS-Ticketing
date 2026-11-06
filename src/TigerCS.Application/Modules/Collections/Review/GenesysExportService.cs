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
/// "Export for Genesys": a file a person can import into a Genesys outbound contact list by hand, for the customers of a Manual selection. It is the same list the page showed:
/// <list type="bullet">
/// <item>The customers and their amounts come from <see cref="ReviewQueryService.ResolveAsync"/>, the code behind Preview and the approval: campaign, Tower, Payment status, Date range, Minimum Total and
/// the ticked customers (or all eligible) give exactly the customers and the filtered amounts that were shown. Held-back customers (reconciliation, contact, shared phone, legal, already sent, contacted
/// today) are never exported.</item>
/// <item>Each row is built by <see cref="DispatchService.ToPayload"/>, the code that builds the upload, and written with <see cref="GenesysContactTemplate"/>: the six columns Phone, CustomerName,
/// Email Address, ReminderType, AmountDue, DueDate, with an E.164 phone, an amount with two decimals and a yyyy-MM-dd date. Nothing else about a customer is in the file.</item>
/// <item>The columns come from the chosen list as it really is: when the Genesys integration is configured its columns are read (read only) and the export is refused if one of the six is missing or
/// misnamed; extra columns of the list are kept, empty. Without the integration the documented template is used and the result says it was not verified.</item>
/// <item>It is read only. It uploads nothing, queues nothing and does not mark anyone as sent: a file is no proof that Genesys received anything, so the send protections (already sent, contacted today,
/// idempotency) are neither used nor weakened here. Each export is written to the audit trail (who, when, campaign, list, count, total, the filter, a hash of the file and a one-way fingerprint of
/// each customer's unit, never a customer's data) before the file is released.</item>
/// <item>Because an export is not recorded as a send, the audit fingerprints are how a repeat is noticed: when any customer was exported within <c>ExportRepeatLookbackDays</c> the file is held
/// (<see cref="CollectionsOutcome.RepeatExport"/>) until the person confirms the repeat, and the confirmed repeat is audited as such.</item>
/// <item>When the list's columns could not be checked in Genesys the file is labelled "Template not verified against Genesys" (name and notices), never presented as verified.</item>
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

    /// <summary>
    /// The most earlier exports read for the repeat check. A window that holds this many cannot be checked in full, so the export stops rather than guessing that a customer is new.
    /// </summary>
    public const int MaxHistoryEntries = 5000;

    /// <summary>
    /// A one-way fingerprint of a customer's unit (company, tenant, unit): the same customer is recognised in every campaign type and month, and no customer data is kept.
    /// </summary>
    public static string Fingerprint(string customerUnitKey) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("genesys-export|" + customerUnitKey)))[..12];

    /// <summary>The same one-way fingerprint for a phone number: the number that is dialled, whichever unit it was written against.</summary>
    public static string PhoneFingerprint(string phone) => Fingerprint("phone|" + phone);

    /// <param name="caller">Who exports; needs the send right.</param>
    /// <param name="request">The Manual selection: campaign, filters, contact list and the ticked customers (or all eligible).</param>
    /// <param name="ct">Cancellation.</param>
    /// <param name="confirmRepeat">The person has seen that some customers were already exported and wants the file anyway.</param>
    /// <param name="expectedRepeats">How many repeated customers the person was shown with that confirmation. If the count is different now (a repeat appeared after the warning), the file is not made.</param>
    public async Task<CollectionsResult<GenesysExportDto>> ExportAsync(CollectionsCaller caller, SelectionRequest request, CancellationToken ct, bool confirmRepeat = false, int? expectedRepeats = null)
    {
        // Authorization first: no data is read for an unauthorized caller. The file is the same customer list a send would use, so it needs the same right.
        if (!(await authorization.ResolveAsync(caller, ct)).CanSendReminders)
            return CollectionsResult<GenesysExportDto>.Fail(CollectionsOutcome.Forbidden);
        // The financial source must be on too, as for the Auto export: stored data is not released while its switch is off.
        if (!options.Enabled || !sourceOptions.Enabled) return CollectionsResult<GenesysExportDto>.Fail(CollectionsOutcome.Disabled);
        // Both modes export the same customers. The schedule is never a condition of an export, so the population is the Manual one (financially eligible customers, including those outside
        // today's schedule) whichever mode the export was started from; the mode is kept for the audit trail only. Nothing is sent by either.
        if (!CollectionsSendingModes.TryParse(request.Filter?.SendingMode, out var mode))
            return CollectionsResult<GenesysExportDto>.Fail(CollectionsOutcome.InvalidRequest, "Export for Genesys is for the Manual or the Auto list.");
        var population = request with { Filter = (request.Filter ?? new ReviewFilter()) with { SendingMode = CollectionsSendingModes.Manual } };
        var resolved = await query.ResolveAsync(population, ct);
        if (resolved.Error is not null) return CollectionsResult<GenesysExportDto>.Fail(CollectionsOutcome.InvalidRequest, resolved.Error);
        if (resolved.Records.Count == 0) return CollectionsResult<GenesysExportDto>.Fail(CollectionsOutcome.InvalidRequest, "No Ready customers are selected.");
        if (query.IsStale(resolved.Run!)) return CollectionsResult<GenesysExportDto>.Fail(CollectionsOutcome.ReviewRequired, "The review data is stale. Refresh it and review again.");
        // Defence in depth, as for a send: such records are already held back, so this can only come from a stale or tampered list.
        if (ReviewQueryService.SharedPhones(resolved.Records).Records > 0 || resolved.Records.Any(r => !DispatchService.IsVoiceEligible(r.Phone)))
            return CollectionsResult<GenesysExportDto>.Fail(CollectionsOutcome.ReviewRequired, "The list contains records that share a phone number or have no callable phone. Review the current list.");
        var types = resolved.Records.Select(r => r.ReminderType).Distinct().ToList();
        if (types.Count != 1) return CollectionsResult<GenesysExportDto>.Fail(CollectionsOutcome.InvalidRequest, "An export contains one campaign type.");
        var type = types[0];

        // The destination list: the campaign's own, or the existing list the person chose among those that suit it (the same rule as a send).
        if (!CampaignReminderTypes.TryResolveManualDestination(type, request.Filter?.ContactList, out var destination))
            return CollectionsResult<GenesysExportDto>.Fail(CollectionsOutcome.InvalidRequest, "That contact list does not suit this campaign.");
        string listId;
        try { listId = genesys.ContactListIdFor(destination); }
        catch (InvalidOperationException ex) { return CollectionsResult<GenesysExportDto>.Fail(CollectionsOutcome.InvalidRequest, ex.Message); }
        var listLabel = CampaignReminderTypes.DefaultLabel(destination);

        var schema = await ReadSchemaAsync(listId, listLabel, ct);
        if (schema.Problem is not null) return CollectionsResult<GenesysExportDto>.Fail(CollectionsOutcome.InvalidRequest, schema.Problem);

        // The rows are built by the upload's own code, so every value is exactly what an upload would send.
        var payloads = resolved.Records.Select(r => DispatchService.ToPayload(new CollectionsDispatchItem
        {
            RecordKey = r.RecordKey, ReminderType = r.ReminderType, CompanyId = r.CompanyId, TenantId = r.TenantId, UnitId = r.UnitId, UnitCode = r.UnitCode,
            CustomerName = r.CustomerName, Phone = r.Phone, Email = r.Email, Amount = r.RemainingAmount!.Value, Currency = r.Currency, DueDate = r.DueDate!.Value, VoiceEligible = true
        }, listId, genesys)).ToList();
        var csv = GenesysContactTemplate.WriteCsv(payloads, schema.Columns);

        // The file is checked against the template before it is released. This can only fail if a value broke a format rule.
        var check = GenesysContactTemplate.Verify(csv, GenesysContactTemplate.ExpectedReminderTypes(genesys));
        if (!check.IsMatch || check.RowCount != payloads.Count)
        {
            logger.LogError("Genesys export failed its own template check ({Issues} issues).", check.Issues.Count);
            return CollectionsResult<GenesysExportDto>.Fail(CollectionsOutcome.InvalidRequest,
                $"The file did not pass the Genesys template check ({check.Issues.Count} issue(s), for example {check.Issues.FirstOrDefault()?.Message ?? "unknown"}). Nothing was exported.");
        }

        // A customer is recognised by its unit AND by its number: the same customer on another unit, or the same unit under another number, is still the person being called again.
        var customers = resolved.Records.Select(r => new ExportedCustomer(
            Fingerprint(ReviewQueryService.UnitKey(r.CompanyId, r.TenantId, r.UnitCode)), r.Phone.Length > 0 ? PhoneFingerprint(r.Phone) : null)).ToList();
        var fingerprints = customers.SelectMany(c => c.Phone is { } phone ? new[] { c.Unit, phone } : new[] { c.Unit }).Distinct().ToList();
        var total = resolved.Records.Sum(r => r.RemainingAmount ?? 0m);
        var currency = resolved.Records[0].Currency;
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(csv)));
        var now = clock.UtcNow;
        var campaign = CampaignReminderTypes.StageName(type);

        // The repeat check and the audit entry are one step under the export lock: a second export that starts at the same moment waits here and then sees this one's entry, instead of
        // both passing the check. The lock covers only that step; the file is already built.
        var gate = await exportLock.TryAcquireAsync(ExportLockWait, ct);
        if (gate is null) return CollectionsResult<GenesysExportDto>.Fail(CollectionsOutcome.FinanceUnavailable, "Another export is being recorded. Try again in a moment.");
        Repeat repeat;
        try
        {
            // Earlier exports of the same customers: warned about, and held until the person confirms. Failing to read the history stops the export (it is the only record of earlier files).
            repeat = await FindRepeatAsync(customers, ct);
            if (repeat.Problem is not null) return CollectionsResult<GenesysExportDto>.Fail(CollectionsOutcome.FinanceUnavailable, repeat.Problem);
            if (repeat.Customers > 0 && !confirmRepeat)
                return CollectionsResult<GenesysExportDto>.Fail(CollectionsOutcome.RepeatExport,
                    $"{repeat.Customers:N0} of the {customers.Count:N0} customers in this file were already exported (latest: {repeat.LastUtc:dd MMM yyyy HH:mm} UTC, {repeat.Exports} earlier export(s)). " +
                    "An export does not mark anyone as sent, so nothing stops a second file. Confirm to export them again.");
            // The confirmation was given for the repeats the person was shown. If the number has changed since (another export, another person), they are warned again first.
            if (repeat.Customers > 0 && expectedRepeats is { } shown && shown != repeat.Customers)
                return CollectionsResult<GenesysExportDto>.Fail(CollectionsOutcome.RepeatExport,
                    $"The number of customers already exported has changed, from {shown:N0} to {repeat.Customers:N0}, since the warning. Review the list and confirm again.");

            // Audit first, then release: an export of customer contact data is never handed out without its record. The record holds counts, the filter and a hash, no customer data.
            try
            {
                // The customer search text is a name or number typed by a person: the audit keeps only whether one was given.
                await audit.WriteAsync(caller.EmployeeId, AuditAction, AuditEntityType, hash[..16], null,
                    $"Campaign={type};ContactList={destination} ({listId});Rows={payloads.Count};Total={currency} {total.ToString("0.00", CultureInfo.InvariantCulture)};SchemaVerified={schema.Verified}{(schema.Verified ? "" : $" ({NotVerifiedLabel})")};Mode={mode};Population=Financial;Filter={JsonSerializer.Serialize(request.Filter is { } f ? f with { Customer = null } : null)};CustomerSearch={!string.IsNullOrWhiteSpace(request.Filter?.Customer)};Selection={request.Mode};Repeated={repeat.Customers}{(repeat.Customers > 0 ? ";RepeatConfirmed=true" : "")};Sha256={hash};Customers={string.Join(',', fingerprints)}",
                    Guid.NewGuid(), ct);
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
        if (resolved.Skipped > 0)
            notices.Add($"{resolved.Skipped:N0} selected customer(s) were left out because they are no longer Ready (paid, already sent or changed). The file holds the rest.");
        if (repeat.Customers > 0) notices.Add($"{repeat.Customers:N0} customer(s) in this file were exported before (latest {repeat.LastUtc:dd MMM yyyy HH:mm} UTC). You confirmed the repeat; it is recorded in the audit trail.");
        // The label travels in the file name (the CSV itself must stay the six-column template for the import), so a file that was not checked against the list is never mistaken for a verified one.
        var fileName = $"genesys-{type}-to-{destination}-{now:yyyyMMdd-HHmm}{(schema.Verified ? "" : $" ({NotVerifiedLabel})")}.csv";
        return CollectionsResult<GenesysExportDto>.Ok(new(fileName, csv, payloads.Count, campaign, listLabel, listId, total, currency,
            GenesysContactTemplate.Columns.Concat(schema.Columns.Where(c => !GenesysContactTemplate.Columns.Contains(c, StringComparer.Ordinal))).ToList(), schema.Verified, notices, hash,
            repeat.Customers, repeat.Customers > 0 ? repeat.LastUtc : null));
    }

    private sealed record Repeat(int Customers, int Exports, DateTime LastUtc, string? Problem);

    /// <summary>One record of the selection as the repeat check sees it: the fingerprint of its unit and of its number (when it has one).</summary>
    private sealed record ExportedCustomer(string Unit, string? Phone);

    /// <summary>How many of these customers appear in an earlier export within the look-back, how many earlier exports that is, and the latest of them.</summary>
    private async Task<Repeat> FindRepeatAsync(IReadOnlyList<ExportedCustomer> customers, CancellationToken ct)
    {
        var days = reviewOptions.ExportRepeatLookbackDays;
        if (days <= 0) return new(0, 0, default, null);
        IReadOnlyList<GenesysExportAuditRecord> earlier;
        try { earlier = await history.ListExportsAsync(AuditAction, clock.UtcNow.AddDays(-Math.Min(days, 365)), MaxHistoryEntries, ct); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError("Earlier Genesys exports could not be read ({ExceptionType}); the export was stopped.", ex.GetType().Name);
            return new(0, 0, default, "Earlier exports could not be checked, so no file was made. Try again.");
        }
        if (earlier.Count >= MaxHistoryEntries)
            return new(0, 0, default, $"{MaxHistoryEntries:N0} or more exports were made in the last {days} days, so the repeat check cannot cover them all and no file was made. Ask an administrator to review the export history.");
        // Each fingerprint maps back to the customers (units) it identifies: a number on two units is one person, and a unit under two numbers is one unit.
        var byFingerprint = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var c in customers)
        {
            AddTo(byFingerprint, c.Unit, c.Unit);
            if (c.Phone is { } phone) AddTo(byFingerprint, phone, c.Unit);
        }
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
        return new(repeated.Count, exports, last, null);
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

    private sealed record Schema(IReadOnlyList<string> Columns, bool Verified, string? Notice, string? Problem);

    /// <summary>
    /// The columns of the chosen list. With the Genesys integration configured they are read from Genesys (read only) and must fit the template; a list that cannot be read or does not fit refuses the
    /// export. Without the integration the documented template is used and the result says it was not verified.
    /// </summary>
    private async Task<Schema> ReadSchemaAsync(string listId, string listLabel, CancellationToken ct)
    {
        if (!genesys.Enabled || string.IsNullOrWhiteSpace(genesys.ClientId) || string.IsNullOrWhiteSpace(genesys.ClientSecret))
            return new(GenesysContactTemplate.Columns, false,
                $"{NotVerifiedLabel}: the {listLabel} list's columns were not checked in Genesys because the integration is not configured here. The file uses the documented six-column template; check it against the list before importing.", null);
        GenesysContactListInfo list;
        try { list = await client.GetContactListAsync(listId, ct); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            logger.LogWarning("Genesys contact list read threw {ExceptionType}.", ex.GetType().Name);
            return new([], false, null, $"The {listLabel} contact list could not be read in Genesys, so its columns cannot be checked. Nothing was exported.");
        }
        if (list.Outcome != GenesysUploadOutcome.Accepted)
            return new([], false, null, $"The {listLabel} contact list could not be read in Genesys ({list.Error}), so its columns cannot be checked. Nothing was exported.");
        var check = GenesysContactTemplate.CheckHeader(list.Columns);
        if (!check.Fits)
            return new([], false, null, $"The {listLabel} contact list does not match the template: " + string.Join("; ",
                check.Missing.Where(m => !check.Misnamed.Any(n => n.StartsWith(m + " ", StringComparison.Ordinal))).Select(m => $"missing column {m}").Concat(check.Misnamed.Select(m => $"misnamed column {m}"))) + ". Nothing was exported.");
        return new(list.Columns, true, null, null);
    }
}
