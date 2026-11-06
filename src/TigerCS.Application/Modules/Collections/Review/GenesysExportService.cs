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
/// <item>It is read only. It uploads nothing, queues nothing and does not mark anyone as sent (so it does not by itself stop a customer being exported or sent again); each export is written to the audit
/// trail (who, when, campaign, list, count, total, the filter and a hash of the file, never a customer's data) before the file is released.</item>
/// </list>
/// </summary>
public sealed class GenesysExportService(
    CollectionsOptions options, GenesysOutboundOptions genesys, CollectionsAuthorizationService authorization, CollectionsClock clock,
    ReviewQueryService query, IGenesysOutboundClient client, IAuditEntryWriter audit, ICollectionsUnitOfWork unitOfWork, ILogger<GenesysExportService> logger)
{
    public const string AuditAction = "CollectionsGenesysFileExported", AuditEntityType = "CollectionsGenesysExport";

    public async Task<CollectionsResult<GenesysExportDto>> ExportAsync(CollectionsCaller caller, SelectionRequest request, CancellationToken ct)
    {
        // Authorization first: no data is read for an unauthorized caller. The file is the same customer list a send would use, so it needs the same right.
        if (!(await authorization.ResolveAsync(caller, ct)).CanSendReminders)
            return CollectionsResult<GenesysExportDto>.Fail(CollectionsOutcome.Forbidden);
        if (!options.Enabled) return CollectionsResult<GenesysExportDto>.Fail(CollectionsOutcome.Disabled);
        if (!CollectionsSendingModes.IsManual(request.Filter?.SendingMode))
            return CollectionsResult<GenesysExportDto>.Fail(CollectionsOutcome.InvalidRequest, "Export for Genesys is a Manual action: choose Manual and a campaign type.");

        var resolved = await query.ResolveAsync(request, ct);
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

        var total = resolved.Records.Sum(r => r.RemainingAmount ?? 0m);
        var currency = resolved.Records[0].Currency;
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(csv)));
        var now = clock.UtcNow;
        var campaign = CampaignReminderTypes.StageName(type);

        // Audit first, then release: an export of customer contact data is never handed out without its record. The record holds counts, the filter and a hash, no customer data.
        try
        {
            await audit.WriteAsync(caller.EmployeeId, AuditAction, AuditEntityType, hash[..16], null,
                $"Campaign={type};ContactList={destination} ({listId});Rows={payloads.Count};Total={currency} {total:0.00};SchemaVerified={schema.Verified};Mode=Manual;Filter={JsonSerializer.Serialize(request.Filter)};Selection={request.Mode};Sha256={hash}",
                Guid.NewGuid(), ct);
            await unitOfWork.SaveChangesAsync(ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            unitOfWork.DiscardPendingChanges();
            logger.LogError("Genesys export could not be audited ({ExceptionType}); the file was not released.", ex.GetType().Name);
            return CollectionsResult<GenesysExportDto>.Fail(CollectionsOutcome.FinanceUnavailable, "The export could not be recorded in the audit trail, so no file was released. Try again.");
        }

        var notices = new List<string>();
        if (!schema.Verified) notices.Add(schema.Notice!);
        return CollectionsResult<GenesysExportDto>.Ok(new($"genesys-{type}-to-{destination}-{now:yyyyMMdd-HHmm}.csv", csv, payloads.Count, campaign, listLabel, listId, total, currency,
            GenesysContactTemplate.Columns.Concat(schema.Columns.Where(c => !GenesysContactTemplate.Columns.Contains(c, StringComparer.Ordinal))).ToList(), schema.Verified, notices, hash));
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
                $"The {listLabel} list's columns were not checked in Genesys because the integration is not configured here. The file uses the documented six-column template; check it against the list before importing.", null);
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
