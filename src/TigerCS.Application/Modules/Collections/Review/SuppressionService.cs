using System.Data.Common;
using Microsoft.Extensions.Logging;
using TigerCS.Application.Modules.Collections.Abstractions;
using TigerCS.Application.Modules.Collections.Services;
using TigerCS.Domain.Modules.Collections.Review;

namespace TigerCS.Application.Modules.Collections.Review;

public sealed record SuppressionSweepResult(bool Ran, string? Detail, int Examined, int Suppressed, int Failed, int Unconfirmed, int Unchanged);

/// <summary>
/// Paid-after-upload protection. An uploaded contact stays in the Genesys list until someone removes it, so a customer who pays after the upload could still be
/// called. This sweep re-reads current balances and, for every uploaded contact whose balance was paid or changed, sets the Genesys contact
/// <c>callable=false</c> (documented "Update a contact": PUT /api/v2/outbound/contactlists/{contactListId}/contacts/{contactId}).
/// Deleting is not used: the documented bulk delete only removes contacts not in use by a campaign.
///
/// <para><b>Limits (cannot be removed in code).</b> A call already dialled or connected cannot be recalled; a contact a running campaign has already
/// pulled into its dialling queue may be dialled before the next sweep; the sweep interval, the PACT read time and Genesys rate limits all add delay.
/// Whether Genesys honours <c>callable=false</c> for a contact already queued by an active campaign has to be proven on a test list before live use.</para>
/// </summary>
public sealed class SuppressionService(
    CollectionsOptions options, GenesysOutboundOptions genesys, CollectionsClock clock, IReviewStore store,
    IGenesysOutboundClient client, CurrentBalanceReader balances, ILogger<SuppressionService> logger)
{
    private static readonly SemaphoreSlim OneSweep = new(1, 1);

    public async Task<SuppressionSweepResult> SweepAsync(CancellationToken ct)
    {
        if (!options.Enabled || !genesys.Enabled || !genesys.SuppressionEnabled)
            return new(false, "Suppression is not enabled.", 0, 0, 0, 0, 0);
        if (!await OneSweep.WaitAsync(0, ct)) return new(false, "Another sweep is running in this process.", 0, 0, 0, 0, 0);
        try
        {
            var now = clock.UtcNow;
            var candidates = await store.GetSuppressionCandidatesAsync(now.AddDays(-Math.Max(1, genesys.SuppressionWindowDays)), Math.Max(1, genesys.MaxSuppressionsPerSweep), ct);
            if (candidates.Count == 0) return new(true, "Nothing to check.", 0, 0, 0, 0, 0);

            var asOfFor = candidates.ToDictionary(c => c.Item.CollectionsDispatchItemId, c => clock.ToBusinessDate(c.Item.UploadedAtUtc ?? now));
            IReadOnlyDictionary<DateOnly, IReadOnlyDictionary<string, CollectionsReviewRecord>> current;
            try
            {
                current = (await balances.ReadAsync(candidates.Select(c => c.Item.CompanyId).Distinct().ToList(), asOfFor.Values.Distinct().ToList(), ct)).ByAsOfDate;
            }
            catch (Exception ex) when ((ex is OperationCanceledException && !ct.IsCancellationRequested) || ex is PactReceivablesSourceException or DbException)
            {
                logger.LogWarning("Suppression sweep could not read current balances ({ExceptionType}); nothing was changed.", ex.GetType().Name);
                return new(false, "Current balances could not be read; nothing was changed.", candidates.Count, 0, 0, 0, 0);
            }

            int suppressed = 0, failed = 0, unconfirmed = 0, unchanged = 0;
            var changed = new List<CollectionsDispatchItem>();
            foreach (var candidate in candidates)
            {
                ct.ThrowIfCancellationRequested();
                var item = candidate.Item;
                item.BalanceCheckedAtUtc = now;
                var present = current[asOfFor[item.CollectionsDispatchItemId]].TryGetValue(item.RecordKey, out var record);
                // Paid or settled (record gone) and any other change to what was quoted both stop the call; the customer is re-approved from fresh data.
                decimal? nowAmount = record?.RemainingAmount; DateOnly? nowDue = record?.DueDate;
                if (present && item.ReminderType == CampaignReminderType.DirectSend)
                {
                    // A Direct Send contact was quoted its FILTERED amount: compare with the same calculation over the customer's current instalments. A filter that cannot be read verifies nothing.
                    var criteria = DirectSendFilters.FromStored(candidate.FilterJson);
                    var calc = criteria is null ? null : DirectSendFilters.CurrentAmount(record!, criteria, asOfFor[item.CollectionsDispatchItemId]);
                    nowAmount = calc is { Qualifies: true } ? calc.Amount : null; nowDue = calc?.EarliestDue;
                }
                var stillValid = present && nowAmount == item.Amount && nowDue == item.DueDate && record!.Phone == item.Phone;
                if (stillValid) { unchanged++; changed.Add(item); continue; }

                var result = await Suppress(candidate, ct);
                switch (result)
                {
                    case ContactSuppressionStatus.Suppressed: suppressed++; item.SuppressedAtUtc = clock.UtcNow; item.SuppressionError = null; break;
                    case ContactSuppressionStatus.Unconfirmed: unconfirmed++; break;
                    default: failed++; break;
                }
                item.SuppressionStatus = result;
                changed.Add(item);
            }
            await store.UpdateItemsAsync(changed, ct);
            logger.LogInformation("Suppression sweep: {Examined} examined, {Suppressed} suppressed, {Failed} failed, {Unconfirmed} unconfirmed, {Unchanged} unchanged.",
                candidates.Count, suppressed, failed, unconfirmed, unchanged);
            return new(true, null, candidates.Count, suppressed, failed, unconfirmed, unchanged);
        }
        finally { OneSweep.Release(); }
    }

    private async Task<ContactSuppressionStatus> Suppress(UploadedContactView candidate, CancellationToken ct)
    {
        var item = candidate.Item;
        GenesysSuppressResult result;
        try
        {
            result = await client.SetNotCallableAsync(candidate.ContactListId, item.GenesysContactId!,
                DispatchService.ToPayload(item, candidate.ContactListId, genesys), ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            logger.LogWarning("Suppression of an uploaded contact threw {ExceptionType}.", ex.GetType().Name);
            item.SuppressionError = "The request ended without a usable response.";
            return ContactSuppressionStatus.Unconfirmed;
        }
        item.SuppressionError = result.Error;
        switch (result.Outcome)
        {
            case GenesysUploadOutcome.Accepted when result.ContactMissing || result.CallableAfter == false:
                return ContactSuppressionStatus.Suppressed;
            case GenesysUploadOutcome.Accepted:
                // Genesys answered 2xx but did not say callable=false: do not believe it.
                item.SuppressionError = "Genesys did not confirm callable=false.";
                return ContactSuppressionStatus.Failed;
            case GenesysUploadOutcome.Unknown:
                return ContactSuppressionStatus.Unconfirmed;
            default:
                return ContactSuppressionStatus.Failed;
        }
    }
}
