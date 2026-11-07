using System.Data.Common;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using TigerCS.Application.Modules.Collections.Abstractions;
using TigerCS.Application.Modules.Collections.Services;
using TigerCS.Domain.Modules.Collections;
using TigerCS.Domain.Modules.Collections.Review;

namespace TigerCS.Application.Modules.Collections.Review;

/// <summary>
/// Approval and upload of reviewed records to Genesys outbound contact lists.
/// <list type="bullet">
/// <item>Confirm: authorizes, re-resolves the approved list and refuses it if it differs from what was shown, then stores a frozen
/// dispatch (idempotent by key; each record can be in only one live dispatch) and queues a job.</item>
/// <item>Execute (job): takes a lease, re-reads the current balances, excludes settled records, stops for review on any other change,
/// then uploads one batch per reminder type (at most 1,000 contacts each). A batch whose outcome is unknown is never re-sent;
/// it waits for reconciliation.</item>
/// </list>
/// "Uploaded to Genesys" means the contact is in the list. It never means the customer was called or contacted.
/// </summary>
public sealed class DispatchService(
    CollectionsOptions options, GenesysOutboundOptions genesys, CollectionsAuthorizationService authorization, CollectionsClock clock,
    IReviewStore store, IReviewJobScheduler scheduler, IGenesysOutboundClient client,
    ReviewQueryService query, CurrentBalanceReader balances, ILogger<DispatchService> logger,
    TigerCS.Application.Modules.IdentityAndAccess.Abstractions.IEmployeeRepository? employees = null)
{
    public const string UploadDisclaimer =
        "Uploaded to Genesys means the contact was added to the outbound contact list. It does not mean the customer was called, " +
        "messaged or reached. If a Genesys campaign using that list is active it may start dialing at any time; call and message results " +
        "arrive separately from Genesys.";

    private static readonly TimeSpan LeaseTtl = TimeSpan.FromMinutes(5);

    // ---------------------------------------------------------------- confirm

    /// <summary>The initiator recorded on a dispatch that the Auto dispatcher created on a scheduled date (no person confirmed it).</summary>
    public static readonly Guid AutoActorId = new("00000000-0000-0000-0000-00000000a070");

    /// <summary>Whether the caller holds the send right. The API checks it before it validates a request, so an unauthorized caller learns nothing about the request.</summary>
    public async Task<bool> CanSendAsync(CollectionsCaller caller, CancellationToken ct) => (await authorization.ResolveAsync(caller, ct)).CanSendReminders;

    /// <summary>A person confirms an exact, previously shown list. Authorization first: no data is read for an unauthorized caller.</summary>
    public async Task<CollectionsResult<DispatchDto>> ConfirmAsync(CollectionsCaller caller, ConfirmDispatchRequest request, CancellationToken ct)
    {
        if (!(await authorization.ResolveAsync(caller, ct)).CanSendReminders)
            return CollectionsResult<DispatchDto>.Fail(CollectionsOutcome.Forbidden);
        return await CreateAsync(caller.EmployeeId, request, ct);
    }

    /// <summary>
    /// The Auto dispatcher creates the dispatch of a campaign that is scheduled today. No person confirms it, so the CALLER (<c>AutoDispatchService</c>) must have checked the separate
    /// <c>AutoSendingEnabled</c> switch and built the selection itself; every other gate, check and revalidation below is the same as for a person's confirmation.
    /// </summary>
    public Task<CollectionsResult<DispatchDto>> ConfirmAutoAsync(ConfirmDispatchRequest request, CancellationToken ct) => CreateAsync(AutoActorId, request, ct);

    private async Task<CollectionsResult<DispatchDto>> CreateAsync(Guid initiator, ConfirmDispatchRequest request, CancellationToken ct)
    {
        if (!options.Enabled) return CollectionsResult<DispatchDto>.Fail(CollectionsOutcome.Disabled);
        if (!genesys.Enabled || !genesys.LiveCustomerDispatchEnabled)
            return CollectionsResult<DispatchDto>.Fail(CollectionsOutcome.Disabled,
                "Customer dispatch to Genesys is switched off in this environment. It stays off until the paid-after-upload suppression has been proven on a test contact list.");
        if (!genesys.SuppressionEnabled)
            return CollectionsResult<DispatchDto>.Fail(CollectionsOutcome.Disabled,
                "Customer dispatch needs the paid-after-upload suppression sweep to be enabled; otherwise a customer who pays after upload could still be called.");
        var key = request.IdempotencyKey?.Trim();
        if (string.IsNullOrEmpty(key) || key.Length > 100)
            return CollectionsResult<DispatchDto>.Fail(CollectionsOutcome.InvalidRequest, "An idempotency key of 1-100 characters is required.");
        if (!request.AcknowledgeActiveCampaignRisk)
            return CollectionsResult<DispatchDto>.Fail(CollectionsOutcome.InvalidRequest,
                "Confirm that you understand an active Genesys campaign may begin calling uploaded contacts immediately.");

        var existing = await store.FindDispatchByKeyAsync(key, ct);
        if (existing is not null) return Replay(existing, initiator, request.ExpectedFingerprint);

        // Nothing would ever pick the job up: do not create an approval that sits in Queued and holds its customers.
        if (!scheduler.IsAvailable)
            return CollectionsResult<DispatchDto>.Fail(CollectionsOutcome.Disabled,
                "Background jobs are switched off in this environment (BackgroundJobs:Enabled), so an approved send could not run. Nothing was approved.");

        var resolved = await query.ResolveAsync(request.Selection, ct);
        if (resolved.Error is not null) return CollectionsResult<DispatchDto>.Fail(CollectionsOutcome.InvalidRequest, resolved.Error);
        if (resolved.Records.Count == 0)
            return CollectionsResult<DispatchDto>.Fail(CollectionsOutcome.InvalidRequest, "No Ready records are selected.");
        // The reconciliation gate: one decision about the SOURCE per company (sign-off, procedure, automated evidence), checked here as it stands NOW and again before the upload. A customer's
        // own Ready status says the customer's data is sound; it never says the source is reconciled. A company that is not reconciled refuses its customers; it never holds the other company back.
        if (ReviewRefreshService.ClosedMessage(balances.Gate(), resolved.Records.Select(r => r.CompanyId)) is { } closed)
            return CollectionsResult<DispatchDto>.Fail(CollectionsOutcome.InvalidRequest, closed);
        var fingerprint = ReviewQueryService.Fingerprint(resolved.Records);
        if (resolved.Records.Count != request.ExpectedCount || !string.Equals(fingerprint, request.ExpectedFingerprint, StringComparison.OrdinalIgnoreCase))
            return CollectionsResult<DispatchDto>.Fail(CollectionsOutcome.ReviewRequired,
                "The records, amounts or contacts changed since this list was shown. Review the current list and confirm again.");
        if (query.IsStale(resolved.Run!))
            return CollectionsResult<DispatchDto>.Fail(CollectionsOutcome.ReviewRequired, "The review data is stale. Refresh it and review again.");
        // Defence in depth: such records are already held back as Excluded, so this can only happen through a stale or tampered list.
        if (ReviewQueryService.SharedPhones(resolved.Records).Records > 0 || resolved.Records.Any(r => !IsVoiceEligible(r.Phone)))
            return CollectionsResult<DispatchDto>.Fail(CollectionsOutcome.ReviewRequired,
                "The list contains records that share a phone number or have no callable phone. Repeated calls are blocked pending a business decision; review the current list.");
        var types = resolved.Records.Select(r => r.ReminderType).Distinct().ToList();
        var manual = CollectionsSendingModes.IsManual(request.Selection.Filter?.SendingMode);
        if (manual && types.Count != 1)
            return CollectionsResult<DispatchDto>.Fail(CollectionsOutcome.InvalidRequest, "A Manual send contains one campaign type.");
        // The destination list of every type: its own, or (Manual) the one the person chose among the existing lists that suit it. Each is checked against Genesys now (required columns, no
        // other active campaign dialling it) and again right before the upload; a list that cannot be verified is never used.
        foreach (var type in types)
        {
            string listId;
            try { listId = manual ? genesys.ManualContactListIdFor(type, request.Selection.Filter?.ContactList) : genesys.ContactListIdFor(type); }
            catch (InvalidOperationException ex) { return CollectionsResult<DispatchDto>.Fail(CollectionsOutcome.InvalidRequest, ex.Message); }
            if (await CheckDestinationAsync(type, listId, ct) is { } destinationProblem)
                return CollectionsResult<DispatchDto>.Fail(CollectionsOutcome.InvalidRequest, destinationProblem);
        }

        var now = clock.UtcNow;
        var dispatch = new CollectionsDispatch
        {
            IdempotencyKey = key, Fingerprint = fingerprint, Status = DispatchStatus.Queued, InitiatedByEmployeeId = initiator,
            InitiatedAtUtc = now, ReviewRunId = resolved.Run!.CollectionsReviewRunId, SelectionMode = request.Selection.Mode,
            FilterJson = JsonSerializer.Serialize(StoredFilter(request.Selection.Filter)), ApprovedCount = resolved.Records.Count,
            ApprovedTotalsJson = JsonSerializer.Serialize(ReviewQueryService.TotalsByCurrency(resolved.Records)
                .ToDictionary(p => p.Key, p => p.Value.ToString("0.00", CultureInfo.InvariantCulture))),
            AcknowledgedActiveCampaignRisk = true,
            Items = resolved.Records.Select(r => new CollectionsDispatchItem
            {
                RecordKey = r.RecordKey, ReminderType = r.ReminderType, CompanyId = r.CompanyId, TenantId = r.TenantId, UnitId = r.UnitId,
                UnitCode = r.UnitCode, CustomerName = r.CustomerName, Phone = r.Phone, Email = r.Email, Amount = r.RemainingAmount!.Value,
                Currency = r.Currency, DueDate = r.DueDate!.Value, Status = DispatchItemStatus.Approved, VoiceEligible = IsVoiceEligible(r.Phone)
            }).ToList()
        };
        if (!await store.TryAddDispatchAsync(dispatch, ct))
        {
            // Either the same key raced in (replay it) or a record is already in a live dispatch (a double click with another key).
            var winner = await store.FindDispatchByKeyAsync(key, ct);
            return winner is not null ? Replay(winner, initiator, request.ExpectedFingerprint)
                : CollectionsResult<DispatchDto>.Fail(CollectionsOutcome.DuplicateDispatch,
                    "One or more of these records are already in a pending or sent dispatch. Refresh the list; they now show as Already Sent.");
        }
        try { scheduler.EnqueueDispatch(dispatch.CollectionsDispatchId); }
        catch (Exception ex)
        {
            logger.LogError("Could not queue dispatch {DispatchId} ({ExceptionType}).", dispatch.CollectionsDispatchId, ex.GetType().Name);
            dispatch.Status = DispatchStatus.Failed; dispatch.StatusReason = "The background job could not be queued; nothing was sent.";
            await store.UpdateDispatchAsync(dispatch, ct);
            await store.ReleaseItemsAsync(dispatch.CollectionsDispatchId, "Dispatch could not be queued", ct);
            return CollectionsResult<DispatchDto>.Fail(CollectionsOutcome.FinanceUnavailable, "The send could not be queued. Nothing was sent; try again.");
        }
        return CollectionsResult<DispatchDto>.Ok(ToDto(dispatch), CollectionsOutcome.Accepted);
    }

    /// <summary>
    /// Checks a Genesys contact list before anything is uploaded to it. Null = it can be used; otherwise the exact reason. Read-only: it only looks.
    /// <list type="bullet">
    /// <item>The list must exist and carry every column of the contact template (Phone, CustomerName, Email Address, ReminderType, AmountDue, DueDate), or the upload would fail or drop data.</item>
    /// <item>No OTHER campaign that is on (or stopping) may dial the list: contacts added to it would be dialled at once, which would interfere with that campaign. The type\'s own stage campaign is
    /// the intended consumer of its own list and is not interference. Anything that cannot be checked is refused.</item>
    /// </list>
    /// </summary>
    private async Task<string?> CheckDestinationAsync(CampaignReminderType type, string listId, CancellationToken ct)
    {
        // The list's own label, and the one campaign that is meant to dial it: the type's stage campaign, but only when the list IS that type's own list.
        var owner = Enum.GetValues<CampaignReminderType>().FirstOrDefault(t => string.Equals(genesys.ContactListIds.GetValueOrDefault(t.ToString()), listId, StringComparison.OrdinalIgnoreCase), type);
        var label = CampaignReminderTypes.DefaultLabel(owner);
        var intended = string.Equals(owner.ToString(), type.ToString(), StringComparison.Ordinal) ? CampaignReminderTypes.StageName(type) : null;
        GenesysContactListInfo list;
        try { list = await client.GetContactListAsync(listId, ct); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            logger.LogWarning("Genesys contact list check threw {ExceptionType}.", ex.GetType().Name);
            return $"The {label} contact list could not be checked in Genesys. Nothing was uploaded.";
        }
        if (list.Outcome != GenesysUploadOutcome.Accepted)
            return $"The {label} contact list could not be checked in Genesys ({list.Error}). Nothing was uploaded.";
        var missing = GenesysContactTemplate.Columns.Where(c => !list.Columns.Contains(c, StringComparer.OrdinalIgnoreCase)).ToList();
        if (missing.Count > 0)
            return $"The {label} contact list does not have the required column(s): {string.Join(", ", missing)}. Nothing was uploaded.";
        GenesysCampaignLookupResult campaigns;
        try { campaigns = await client.FindCampaignsByContactListAsync(listId, ct); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            logger.LogWarning("Genesys campaign check by contact list threw {ExceptionType}.", ex.GetType().Name);
            return $"The campaigns that dial the {label} contact list could not be checked. Nothing was uploaded.";
        }
        if (campaigns.Outcome != GenesysUploadOutcome.Accepted)
            return $"The campaigns that dial the {label} contact list could not be checked ({campaigns.Error}). Nothing was uploaded.";
        // An active campaign dialling the list is interference unless it is the stage campaign this very send is meant for (the normal Auto / Manual path to the type's own list).
        var active = campaigns.Matches.Where(c => (string.Equals(c.Status, "on", StringComparison.OrdinalIgnoreCase) || string.Equals(c.Status, "stopping", StringComparison.OrdinalIgnoreCase))
            && !(intended is not null && string.Equals(c.Name?.Trim(), intended, StringComparison.OrdinalIgnoreCase))).ToList();
        if (active.Count > 0)
            return $"The Genesys campaign \"{string.Join("\", \"", active.Select(c => c.Name))}\" is active and dials the {label} contact list, so these customers would be dialled at once. Switch it off in Genesys, or choose the other list. Nothing was uploaded.";
        return null;
    }

    /// <summary>
    /// The filter kept with the approval. A Manual approval stores its EFFECTIVE minimum (the review default when none was given) so that the revalidation before the upload and the paid-after-upload
    /// check calculate the amount with exactly the criteria the person approved, whatever the settings are later.
    /// </summary>
    private ReviewFilter? StoredFilter(ReviewFilter? filter) =>
        filter is not null && CollectionsSendingModes.IsManual(filter.SendingMode) ? filter with { MinRemaining = query.ManualCriteria(filter).MinTotal } : filter;

    private CollectionsResult<DispatchDto> Replay(CollectionsDispatch existing, Guid initiator, string expectedFingerprint) =>
        existing.InitiatedByEmployeeId == initiator
        && string.Equals(existing.Fingerprint, expectedFingerprint, StringComparison.OrdinalIgnoreCase)
            ? CollectionsResult<DispatchDto>.Ok(ToDto(existing), CollectionsOutcome.Replayed)
            : CollectionsResult<DispatchDto>.Fail(CollectionsOutcome.IdempotencyConflict, "This idempotency key was already used for a different approval.");

    // ---------------------------------------------------------------- readiness

    /// <summary>The configuration gates Confirm enforces, plus the credentials and contact lists the upload needs. Read-only; sends nothing and calls nothing.</summary>
    public async Task<CollectionsResult<DispatchReadinessDto>> ReadinessAsync(CollectionsCaller caller, CancellationToken ct)
    {
        var permissions = await authorization.ResolveAsync(caller, ct);
        if (!permissions.CanReadFinancials)
            return CollectionsResult<DispatchReadinessDto>.Fail(CollectionsOutcome.Forbidden);
        var blockers = new List<string>();
        if (!options.Enabled) blockers.Add("Collections is switched off in this environment (Collections:Enabled).");
        if (!genesys.Enabled) blockers.Add("The Genesys outbound integration is switched off (Collections:GenesysOutbound:Enabled).");
        if (!genesys.LiveCustomerDispatchEnabled) blockers.Add("Customer dispatch to Genesys is switched off (Collections:GenesysOutbound:LiveCustomerDispatchEnabled).");
        if (!genesys.SuppressionEnabled) blockers.Add("The paid-after-upload suppression sweep is not enabled (Collections:GenesysOutbound:SuppressionEnabled); it is required before any send.");
        if (string.IsNullOrWhiteSpace(genesys.ClientId) || string.IsNullOrWhiteSpace(genesys.ClientSecret))
            blockers.Add("The Genesys OAuth client id or secret is not configured (Collections:GenesysOutbound:ClientId / ClientSecret, supplied as secrets).");
        if (!scheduler.IsAvailable) blockers.Add("Background jobs are switched off (BackgroundJobs:Enabled), so an approved send could not run.");
        foreach (var type in Enum.GetValues<CampaignReminderType>())
        {
            try { _ = genesys.ContactListIdFor(type); }
            catch (InvalidOperationException) { blockers.Add($"No valid Genesys contact list id is configured for {type} (Collections:GenesysOutbound:ContactListIds:{type})."); }
        }
        return CollectionsResult<DispatchReadinessDto>.Ok(new(blockers.Count == 0, blockers, permissions.CanSendReminders));
    }

    // ---------------------------------------------------------------- read

    public async Task<CollectionsResult<DispatchDto>> GetAsync(CollectionsCaller caller, Guid dispatchId, CancellationToken ct)
    {
        if (!(await authorization.ResolveAsync(caller, ct)).CanReadFinancials)
            return CollectionsResult<DispatchDto>.Fail(CollectionsOutcome.Forbidden);
        var dispatch = await store.GetDispatchAsync(dispatchId, ct);
        return dispatch is null ? CollectionsResult<DispatchDto>.Fail(CollectionsOutcome.NotFound) : CollectionsResult<DispatchDto>.Ok(await WithApproverAsync(ToDto(dispatch), ct));
    }

    public async Task<CollectionsResult<IReadOnlyList<DispatchDto>>> ListAsync(CollectionsCaller caller, CancellationToken ct)
    {
        if (!(await authorization.ResolveAsync(caller, ct)).CanReadFinancials)
            return CollectionsResult<IReadOnlyList<DispatchDto>>.Fail(CollectionsOutcome.Forbidden);
        var list = new List<DispatchDto>();
        foreach (var d in await store.ListDispatchesAsync(50, ct)) list.Add(await WithApproverAsync(ToDto(d), ct));
        return CollectionsResult<IReadOnlyList<DispatchDto>>.Ok(list);
    }

    private readonly Dictionary<Guid, string?> _names = [];

    /// <summary>Adds the approving employee's display name (looked up once per employee); the id stays when it cannot be resolved.</summary>
    private async Task<DispatchDto> WithApproverAsync(DispatchDto dto, CancellationToken ct)
    {
        if (dto.InitiatedByEmployeeId == AutoActorId) return dto with { ApprovedByName = "Automatic (scheduled)" };
        if (employees is null) return dto;
        if (!_names.TryGetValue(dto.InitiatedByEmployeeId, out var name))
        {
            try { name = (await employees.GetByIdAsync(dto.InitiatedByEmployeeId, ct))?.DisplayName; }
            catch (OperationCanceledException) { throw; }
            catch (Exception) { name = null; }
            _names[dto.InitiatedByEmployeeId] = name;
        }
        return dto with { ApprovedByName = name };
    }

    // ---------------------------------------------------------------- execute (background job)

    public async Task ExecuteAsync(long dispatchId, CancellationToken ct)
    {
        var owner = Guid.NewGuid();
        if (!await store.TryAcquireLeaseAsync(dispatchId, owner, clock.UtcNow, LeaseTtl, ct))
        {
            logger.LogInformation("Dispatch {DispatchId} is finished, cancelled or being processed by another worker; skipping.", dispatchId);
            return;
        }
        var dispatch = await store.GetDispatchByIdAsync(dispatchId, ct);
        if (dispatch is null) return;

        try
        {
            if (dispatch.Status == DispatchStatus.Revalidating)
            {
                // Configuration may have changed between approval and execution: a disabled integration sends nothing.
                if (!options.Enabled || !genesys.Enabled || !genesys.LiveCustomerDispatchEnabled || !genesys.SuppressionEnabled)
                { await StopAsync(dispatch, DispatchStatus.Failed, "Customer dispatch or its suppression sweep is disabled; nothing was sent.", ct); return; }
                if (!await RevalidateAsync(dispatch, owner, ct)) return;
                if (!await PlanBatchesAsync(dispatch, ct)) return;
                dispatch = (await store.GetDispatchByIdAsync(dispatchId, ct))!;
            }
            if (dispatch.Status == DispatchStatus.Sending) await SendAsync(dispatch, owner, ct);
        }
        catch (LeaseLostException)
        {
            // Another worker owns the dispatch now (this one stalled past the lease). It must not write anything more.
            logger.LogWarning("Dispatch {DispatchId} lost its lease; this worker stopped without writing further changes.", dispatchId);
        }
    }

    private sealed class LeaseLostException : Exception;

    /// <summary>
    /// Runs slow, database-free work (the PACT reads) while renewing the lease on a timer, so a read that outlasts the lease cannot let a second worker
    /// start. The timer only touches the store while the work awaits the source, and is stopped (awaited) before the caller uses the store again.
    /// If the lease cannot be renewed the work is cancelled and <see cref="LeaseLostException"/> is thrown.
    /// </summary>
    private async Task<T> WithLeaseHeartbeatAsync<T>(long dispatchId, Guid owner, Func<CancellationToken, Task<T>> work, CancellationToken ct)
    {
        using var lost = CancellationTokenSource.CreateLinkedTokenSource(ct);
        using var stop = new CancellationTokenSource();
        var interval = TimeSpan.FromSeconds(Math.Clamp(genesys.LeaseHeartbeatSeconds, 0.01, LeaseTtl.TotalSeconds / 2));
        var beat = Task.Run(async () =>
        {
            try
            {
                while (!stop.IsCancellationRequested)
                {
                    await Task.Delay(interval, stop.Token);
                    if (!await store.RenewLeaseAsync(dispatchId, owner, clock.UtcNow, LeaseTtl, CancellationToken.None)) { await lost.CancelAsync(); return; }
                }
            }
            catch (OperationCanceledException) { /* stopped */ }
        }, CancellationToken.None);
        try { return await work(lost.Token); }
        catch (OperationCanceledException) when (lost.IsCancellationRequested && !ct.IsCancellationRequested) { throw new LeaseLostException(); }
        finally
        {
            await stop.CancelAsync();
            await beat;
        }
    }

    /// <summary>Re-reads current balances. Settled records are excluded; any other change sends the whole list back for review.</summary>
    private async Task<bool> RevalidateAsync(CollectionsDispatch dispatch, Guid owner, CancellationToken ct)
    {
        var items = (await store.GetItemsAsync(dispatch.CollectionsDispatchId, ct)).Where(i => i.Status == DispatchItemStatus.Approved).ToList();
        var asOf = clock.BusinessDate;
        // Record identities and schedules are per business month: an approval from last month must be reviewed again.
        var run = await store.GetRunAsync(dispatch.ReviewRunId, ct);
        if (run is null || run.AsOfDate.Year != asOf.Year || run.AsOfDate.Month != asOf.Month)
            return await StopAsync(dispatch, DispatchStatus.ReviewRequired, "The business month changed since approval. Nothing was sent; review the current list and approve again.", ct);

        // A unit PACT has cancelled (asterisk anywhere in its code) never gets a reminder, whatever was prepared or approved earlier. Checked before any balance is read.
        var cancelledItems = items.Where(i => ReviewRecordBuilder.IsCancelledUnit(i.UnitCode)).ToList();
        foreach (var c in cancelledItems) { c.Status = DispatchItemStatus.Excluded; c.StatusReason = "Cancelled unit (code contains *): no reminder is sent"; }
        if (cancelledItems.Count > 0) items = items.Except(cancelledItems).ToList();
        if (items.Count == 0)
        {
            await store.UpdateItemsAsync(cancelledItems, ct);
            dispatch.ExcludedAtDispatchCount = cancelledItems.Count;
            dispatch.Status = DispatchStatus.Completed; dispatch.CompletedAtUtc = clock.UtcNow; dispatch.StartedAtUtc ??= clock.UtcNow;
            dispatch.StatusReason = "Every approved record belongs to a cancelled unit (code contains *); nothing was uploaded.";
            dispatch.Phase = "Finished"; dispatch.LeaseOwner = null; dispatch.LeaseExpiresAtUtc = null;
            await store.UpdateDispatchAsync(dispatch, ct);
            return false;
        }

        // The reconciliation gate again, with the configuration as it is NOW: a sign-off withdrawn or evidence expired since the approval stops the upload.
        var companies = items.Select(i => i.CompanyId).Distinct().Order().ToList();
        if (ReviewRefreshService.ClosedMessage(balances.Gate(), companies) is { } closedNow)
            return await StopAsync(dispatch, DispatchStatus.ReviewRequired, closedNow + " Nothing was sent.", ct);
        await store.SetDispatchPhaseAsync(dispatch.CollectionsDispatchId, $"Checking current balances (company {string.Join(" and ", companies)}); this can take several minutes", ct);
        CurrentBalanceReader.Result read;
        try
        {
            read = await WithLeaseHeartbeatAsync(dispatch.CollectionsDispatchId, owner, token => balances.ReadAsync(companies, [asOf], token), ct);
        }
        catch (Exception ex) when ((ex is OperationCanceledException && !ct.IsCancellationRequested) || ex is PactReceivablesSourceException or DbException)
        {
            return await StopAsync(dispatch, DispatchStatus.Failed, "Current balances could not be read, so nothing was sent. Start the send again later.", ct);
        }
        // The lease may have been lost between the last heartbeat and now; do not write on someone else's dispatch.
        if (!await store.RenewLeaseAsync(dispatch.CollectionsDispatchId, owner, clock.UtcNow, LeaseTtl, ct)) throw new LeaseLostException();
        await store.SetRevalidationMsAsync(dispatch.CollectionsDispatchId, read.ElapsedMs, ct);
        // ... and once more bound to the snapshot publish the balances were just read from: a refresh between the approval and now that has not been checked yet is not sent on.
        if (ReviewRefreshService.ClosedMessage(read.Gate, companies) is { } closedForPublish)
            return await StopAsync(dispatch, DispatchStatus.ReviewRequired, closedForPublish + " Nothing was sent.", ct);
        var current = read.ByAsOfDate[asOf];

        var settled = new List<CollectionsDispatchItem>(cancelledItems);
        var changed = 0;
        // Manual: the amount quoted is calculated from the customer's CURRENT instalments with the filter the person approved (Payment status, From / To, Minimum Total), never the stage amount,
        // and the record may be held back by the stage schedule alone (the one rule Manual waives). Auto compares the stage amount and needs the record Ready, as before.
        var manualCriteria = ManualFilters.FromStored(dispatch.FilterJson);
        var manual = manualCriteria is not null;
        foreach (var item in items)
        {
            if (!current.TryGetValue(item.RecordKey, out var now))
            {
                item.Status = DispatchItemStatus.Excluded; item.StatusReason = "Paid or settled: no qualifying balance remains";
                settled.Add(item); continue;
            }
            decimal? nowAmount = now.RemainingAmount; DateOnly? nowDue = now.DueDate;
            if (manual)
            {
                var calc = ManualFilters.CurrentAmount(now, manualCriteria!, asOf);
                if (calc.Reason == FilteredAmount.None)
                {
                    // Everything the filters selected has been paid or settled: nothing qualifies any more, so the customer is not sent.
                    item.Status = DispatchItemStatus.Excluded; item.StatusReason = "Paid or settled: no qualifying balance remains within the selected filters";
                    settled.Add(item); continue;
                }
                nowAmount = calc.Qualifies ? calc.Amount : null; nowDue = calc.EarliestDue;
            }
            var sendable = manual ? ReviewRecordBuilder.IsManuallySendable(now) : now.ValidationStatus == ReviewValidationStatus.Ready;
            var unchanged = sendable && nowAmount == item.Amount
                && nowDue == item.DueDate && now.Currency == item.Currency && now.Phone == item.Phone
                && string.Equals(now.CustomerName, item.CustomerName, StringComparison.Ordinal);
            if (!unchanged) changed++;
        }
        if (changed > 0)
        {
            await store.UpdateItemsAsync(settled, ct);
            return await StopAsync(dispatch, DispatchStatus.ReviewRequired,
                $"{changed} approved record(s) changed (amount, due date, contact or validation) after approval. Nothing was sent; review the current list and approve again.", ct,
                excluded: settled.Count);
        }
        if (settled.Count > 0) await store.UpdateItemsAsync(settled, ct);
        dispatch.ExcludedAtDispatchCount = settled.Count;
        if (settled.Count == items.Count + cancelledItems.Count)
        {
            dispatch.Status = DispatchStatus.Completed; dispatch.CompletedAtUtc = clock.UtcNow; dispatch.StartedAtUtc ??= clock.UtcNow;
            dispatch.StatusReason = "Every approved record was paid or settled before sending; nothing was uploaded.";
            dispatch.Phase = "Finished"; dispatch.LeaseOwner = null; dispatch.LeaseExpiresAtUtc = null;
            await store.UpdateDispatchAsync(dispatch, ct);
            return false;
        }
        return true;
    }

    private async Task<bool> StopAsync(CollectionsDispatch dispatch, DispatchStatus status, string reason, CancellationToken ct, int? excluded = null)
    {
        dispatch.Status = status; dispatch.StatusReason = reason; dispatch.CompletedAtUtc = clock.UtcNow; dispatch.Phase = "Finished";
        dispatch.LeaseOwner = null; dispatch.LeaseExpiresAtUtc = null;
        if (excluded is { } e) dispatch.ExcludedAtDispatchCount = e;
        await store.UpdateDispatchAsync(dispatch, ct);
        await store.ReleaseItemsAsync(dispatch.CollectionsDispatchId, reason, ct);
        return false;
    }

    /// <summary>Splits the surviving records into one batch list per reminder type, at most BatchSize per request, and stores the plan before any upload.</summary>
    private async Task<bool> PlanBatchesAsync(CollectionsDispatch dispatch, CancellationToken ct)
    {
        var all = (await store.GetItemsAsync(dispatch.CollectionsDispatchId, ct)).Where(i => i.Status == DispatchItemStatus.Approved).ToList();
        // callable is set only for approved voice-eligible contacts; anything else is never uploaded.
        var ineligible = all.Where(i => ReviewRecordBuilder.IsCancelledUnit(i.UnitCode) || !i.VoiceEligible || !IsVoiceEligible(i.Phone)).ToList();
        foreach (var item in ineligible)
        {
            item.Status = DispatchItemStatus.Excluded;
            item.StatusReason = ReviewRecordBuilder.IsCancelledUnit(item.UnitCode) ? "Cancelled unit (code contains *): no reminder is sent" : "Not voice eligible: no valid international phone number";
        }
        if (ineligible.Count > 0) await store.UpdateItemsAsync(ineligible, ct);
        var items = all.Except(ineligible).ToList();
        if (items.Count == 0)
        {
            dispatch.Status = DispatchStatus.Completed; dispatch.CompletedAtUtc = clock.UtcNow; dispatch.Phase = "Finished";
            dispatch.StatusReason = "No approved record was voice eligible; nothing was uploaded.";
            dispatch.LeaseOwner = null; dispatch.LeaseExpiresAtUtc = null;
            await store.UpdateDispatchAsync(dispatch, ct);
            return false;
        }
        // The destination list of each type: its own, or (Manual) the one the person chose at approval, which is kept in the stored filter.
        var manualSend = ManualFilters.FromStored(dispatch.FilterJson) is not null;
        var chosenList = ManualFilters.StoredContactList(dispatch.FilterJson);
        string ListFor(CampaignReminderType type) => manualSend ? genesys.ManualContactListIdFor(type, chosenList) : genesys.ContactListIdFor(type);
        try { foreach (var type in items.Select(i => i.ReminderType).Distinct()) _ = ListFor(type); }
        catch (InvalidOperationException ex)
        {
            await store.UpdateItemsAsync(all, ct);
            return await StopAsync(dispatch, DispatchStatus.Failed, $"{ex.Message} Nothing was uploaded.", ct);
        }
        var batchSize = genesys.EffectiveBatchSize;
        var batches = new List<CollectionsGenesysBatch>();
        var plan = new List<(CollectionsGenesysBatch Batch, List<CollectionsDispatchItem> Items)>();
        var sequence = 0;
        foreach (var group in items.GroupBy(i => i.ReminderType).OrderBy(g => g.Key))
        {
            var ordered = group.OrderBy(i => i.RecordKey, StringComparer.Ordinal).ToList();
            for (var offset = 0; offset < ordered.Count; offset += batchSize)
            {
                var slice = ordered.Skip(offset).Take(batchSize).ToList();
                var batch = new CollectionsGenesysBatch
                {
                    CollectionsDispatchId = dispatch.CollectionsDispatchId, ReminderType = group.Key, ContactListId = ListFor(group.Key),
                    Sequence = ++sequence, ContactCount = slice.Count, Status = GenesysBatchStatus.Pending,
                    StageName = CampaignReminderTypes.StageName(group.Key)
                };
                batches.Add(batch); plan.Add((batch, slice));
            }
        }
        await store.AddBatchesAsync(batches, ct);
        foreach (var (batch, slice) in plan)
            for (var i = 0; i < slice.Count; i++) { slice[i].CollectionsGenesysBatchId = batch.CollectionsGenesysBatchId; slice[i].BatchPosition = i; }
        await store.UpdateItemsAsync(items, ct);
        dispatch.Status = DispatchStatus.Sending; dispatch.StartedAtUtc = clock.UtcNow; dispatch.Phase = $"Uploading {batches.Count} batch(es)";
        await store.UpdateDispatchAsync(dispatch, ct);
        return true;
    }

    private async Task SendAsync(CollectionsDispatch dispatch, Guid owner, CancellationToken ct)
    {
        foreach (var planned in dispatch.Batches.OrderBy(b => b.Sequence))
        {
            if (!await store.RenewLeaseAsync(dispatch.CollectionsDispatchId, owner, clock.UtcNow, LeaseTtl, ct))
            { logger.LogWarning("Dispatch {DispatchId} lost its lease; stopping.", dispatch.CollectionsDispatchId); return; }
            await store.SetDispatchPhaseAsync(dispatch.CollectionsDispatchId, $"Uploading batch {planned.Sequence} of {dispatch.Batches.Count}", ct);
            var batch = await store.GetBatchAsync(planned.CollectionsGenesysBatchId, ct);
            if (batch is null) continue;
            if (batch.Status == GenesysBatchStatus.Submitting)
            {
                // A previous worker died between claiming and recording the answer: the outcome is unknown. Never resend.
                batch.Status = GenesysBatchStatus.UnknownOutcome; batch.Error = "The worker stopped before the Genesys response was recorded.";
                batch.CompletedAtUtc = clock.UtcNow;
                await store.UpdateBatchAsync(batch, ct);
                await MarkItemsAsync(batch, DispatchItemStatus.UnknownOutcome, batch.Error, null, ct);
                continue;
            }
            if (batch.Status != GenesysBatchStatus.Pending) continue;
            if (!await store.TryClaimBatchAsync(batch.CollectionsGenesysBatchId, clock.UtcNow, ct)) continue;
            batch = (await store.GetBatchAsync(batch.CollectionsGenesysBatchId, ct))!;
            if (!await EnsureCampaignAsync(dispatch, batch, ct)) continue;
            await SubmitAsync(batch, ct);
        }

        var final = await store.GetDispatchByIdAsync(dispatch.CollectionsDispatchId, ct);
        if (final is null) return;
        var items = await store.GetItemsAsync(final.CollectionsDispatchId, ct);
        var allUploaded = items.Where(i => i.Status != DispatchItemStatus.Excluded).All(i => i.Status == DispatchItemStatus.UploadedToGenesys);
        final.Status = allUploaded ? DispatchStatus.Completed : DispatchStatus.CompletedWithErrors;
        final.StatusReason = allUploaded ? "All approved contacts were uploaded to Genesys. No call or message has been confirmed."
            : "Some batches failed or have an unconfirmed outcome. Unconfirmed batches must be reconciled in Genesys before anything is re-sent.";
        final.CompletedAtUtc = clock.UtcNow; final.Phase = "Finished"; final.LeaseOwner = null; final.LeaseExpiresAtUtc = null;
        await store.UpdateDispatchAsync(final, ct);
    }

    /// <summary>
    /// Resolves the Genesys campaign named exactly like the batch's stage before any contact is uploaded. An existing campaign with that name is
    /// reused (never duplicated) when it dials this batch's contact list; a missing one is created when allowed. Anything uncertain (several
    /// matches, a different contact list, an unusable template, a failed or unanswered Genesys call) fails the batch with nothing uploaded; the
    /// records are released, and a later send looks the campaign up again first, so an unconfirmed creation can never produce a duplicate.
    /// Returns false when the batch was failed.
    /// </summary>
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, SemaphoreSlim> CampaignGates = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// "Look up, then create" is not atomic in Genesys, so two dispatches of the same stage working at once could both find nothing and both create the campaign. Lookup and creation
    /// therefore run one at a time per campaign name inside this process (the second waits, then finds the first one's campaign and reuses it). Several API instances would need
    /// Genesys's own unique-name rule as the backstop - it refuses a second campaign with the same name, which the batch then reports as not created.
    /// </summary>
    private async Task<bool> EnsureCampaignAsync(CollectionsDispatch dispatch, CollectionsGenesysBatch batch, CancellationToken ct)
    {
        // Right before the upload the destination is checked again: the list must still fit the upload and no OTHER active campaign may dial it. Anything that cannot be verified fails the batch with
        // nothing uploaded.
        var checkProblem = await CheckDestinationAsync(batch.ReminderType, batch.ContactListId, ct);
        if (checkProblem is not null)
        {
            logger.LogWarning("Dispatch {DispatchId} batch {BatchId}: the destination contact list failed its check before the upload.", dispatch.CollectionsDispatchId, batch.CollectionsGenesysBatchId);
            batch.Status = GenesysBatchStatus.Failed; batch.Error = Truncate(checkProblem); batch.CompletedAtUtc = clock.UtcNow;
            await store.UpdateBatchAsync(batch, ct);
            await MarkItemsAsync(batch, DispatchItemStatus.Failed, batch.Error, null, ct);
            return false;
        }
        // A Manual send to a list other than the type's own adds contacts to that EXISTING list: no campaign is created or reused for it (the stage campaign dials the stage's own list).
        if (!string.Equals(batch.ContactListId, genesys.ContactListIdFor(batch.ReminderType), StringComparison.OrdinalIgnoreCase)) return true;
        if (!genesys.Campaign.Enabled) return true;
        var gate = CampaignGates.GetOrAdd(batch.StageName ?? CampaignReminderTypes.StageName(batch.ReminderType), _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        try { return await EnsureCampaignCoreAsync(dispatch, batch, ct); }
        finally { gate.Release(); }
    }

    private async Task<bool> EnsureCampaignCoreAsync(CollectionsDispatch dispatch, CollectionsGenesysBatch batch, CancellationToken ct)
    {
        var campaign = genesys.Campaign;
        var stageName = batch.StageName ??= CampaignReminderTypes.StageName(batch.ReminderType);
        if (!campaign.Enabled) return true;

        async Task<bool> FailAsync(string reason)
        {
            logger.LogWarning("Dispatch {DispatchId} batch {BatchId}: no Genesys campaign for stage {StageName}: {Reason}",
                dispatch.CollectionsDispatchId, batch.CollectionsGenesysBatchId, stageName, reason);
            batch.Status = GenesysBatchStatus.Failed; batch.Error = Truncate(reason); batch.CompletedAtUtc = clock.UtcNow;
            await store.UpdateBatchAsync(batch, ct);
            await MarkItemsAsync(batch, DispatchItemStatus.Failed, batch.Error, null, ct);
            return false;
        }

        GenesysCampaignLookupResult lookup;
        try { lookup = await client.FindCampaignsByNameAsync(stageName, ct); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            logger.LogWarning("Genesys campaign lookup for batch {BatchId} threw {ExceptionType}.", batch.CollectionsGenesysBatchId, ex.GetType().Name);
            return await FailAsync($"The Genesys campaign \"{stageName}\" could not be looked up; nothing was uploaded.");
        }
        if (lookup.Outcome != GenesysUploadOutcome.Accepted)
            return await FailAsync($"The Genesys campaign \"{stageName}\" could not be looked up ({lookup.Error}); nothing was uploaded.");

        // Genesys treats campaign names case-insensitively for uniqueness, so any case variant counts as the same campaign.
        var matches = lookup.Matches.Where(c => string.Equals(c.Name?.Trim(), stageName, StringComparison.OrdinalIgnoreCase)).ToList();
        if (matches.Count > 1)
            return await FailAsync($"{matches.Count} Genesys campaigns are already named \"{stageName}\"; remove the duplicates or choose one by hand. Nothing was uploaded.");
        if (matches.Count == 1)
        {
            var existing = matches[0];
            if (!string.Equals(existing.Name?.Trim(), stageName, StringComparison.Ordinal))
                return await FailAsync($"A Genesys campaign named \"{existing.Name}\" already exists and differs only by letter case from \"{stageName}\". Nothing was uploaded.");
            if (!string.Equals(existing.ContactListId, batch.ContactListId, StringComparison.OrdinalIgnoreCase))
                return await FailAsync($"The Genesys campaign \"{stageName}\" already exists but dials a different contact list than this stage's list. Nothing was uploaded.");
            return await RecordCampaignAsync(batch, existing, "ReusedExisting", ct);
        }

        if (!campaign.AllowCreate)
            return await FailAsync($"No Genesys campaign named \"{stageName}\" exists and campaign creation is switched off. Nothing was uploaded.");
        var missing = campaign.MissingForCreate();
        if (missing.Count > 0)
            return await FailAsync($"No Genesys campaign named \"{stageName}\" exists and the campaign template is incomplete ({string.Join(", ", missing)}). Nothing was uploaded.");

        GenesysCampaignCreateResult created;
        try { created = await client.CreateCampaignAsync(stageName, batch.ContactListId, campaign, ct); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            logger.LogWarning("Genesys campaign creation for batch {BatchId} threw {ExceptionType}.", batch.CollectionsGenesysBatchId, ex.GetType().Name);
            created = new(GenesysUploadOutcome.Unknown, null, null, "The request ended without a usable response.");
        }
        if (created.Outcome == GenesysUploadOutcome.Accepted && created.Campaign is { Id.Length: > 0 } made)
            return await RecordCampaignAsync(batch, made, "Created", ct);
        return await FailAsync(created.Outcome == GenesysUploadOutcome.Unknown
            ? $"Genesys did not confirm creating the campaign \"{stageName}\". Nothing was uploaded; check Genesys for it before sending again (a new send reuses it if it exists)."
            : $"Genesys did not create the campaign \"{stageName}\" ({created.Error}). Nothing was uploaded.");
    }

    private async Task<bool> RecordCampaignAsync(CollectionsGenesysBatch batch, GenesysCampaignRef campaign, string action, CancellationToken ct)
    {
        batch.GenesysCampaignId = campaign.Id; batch.GenesysCampaignName = batch.StageName; batch.CampaignAction = action;
        await store.UpdateBatchAsync(batch, ct);
        logger.LogInformation("Dispatch {DispatchId} batch {BatchId}: stage {StageName} uses Genesys campaign {CampaignId} ({Action}).",
            batch.CollectionsDispatchId, batch.CollectionsGenesysBatchId, batch.StageName, campaign.Id, action);
        return true;
    }

    private async Task SubmitAsync(CollectionsGenesysBatch batch, CancellationToken ct)
    {
        var items = (await store.GetBatchItemsAsync(batch.CollectionsGenesysBatchId, ct)).OrderBy(i => i.BatchPosition).ToList();
        var payloads = items.Select(i => ToPayload(i, batch.ContactListId, genesys)).ToList();
        batch.RequestHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(payloads))));
        GenesysUploadResult result;
        try { result = await client.UploadContactsAsync(batch.ContactListId, payloads, ct); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { result = new(GenesysUploadOutcome.Unknown, null, [], "The job was cancelled during the request."); }
        catch (Exception ex)
        {
            logger.LogWarning("Genesys upload for batch {BatchId} threw {ExceptionType}.", batch.CollectionsGenesysBatchId, ex.GetType().Name);
            result = new(GenesysUploadOutcome.Unknown, null, [], "The request ended without a usable response.");
        }

        batch.HttpStatus = result.HttpStatus; batch.CompletedAtUtc = clock.UtcNow; batch.Error = Truncate(result.Error);
        batch.ReturnedContactCount = result.ContactIds.Count;
        switch (result.Outcome)
        {
            case GenesysUploadOutcome.Accepted:
                var complete = result.ContactIds.Count == items.Count;
                batch.Status = complete ? GenesysBatchStatus.Uploaded : GenesysBatchStatus.UnknownOutcome;
                if (!complete) batch.Error = "Genesys accepted the request but returned a different number of contacts; check the list.";
                var now = clock.UtcNow;
                for (var i = 0; i < items.Count; i++)
                {
                    var id = i < result.ContactIds.Count ? result.ContactIds[i] : null;
                    items[i].Status = id is null ? DispatchItemStatus.UnknownOutcome : DispatchItemStatus.UploadedToGenesys;
                    items[i].GenesysContactId = id; items[i].UploadedAtUtc = id is null ? null : now;
                    items[i].StatusReason = id is null ? batch.Error : null;
                }
                await store.UpdateItemsAsync(items, ct);
                break;
            case GenesysUploadOutcome.Rejected:
                batch.Status = GenesysBatchStatus.Failed;
                foreach (var item in items) { item.Status = DispatchItemStatus.Failed; item.StatusReason = batch.Error; }
                await store.UpdateItemsAsync(items, ct);
                break;
            default:
                batch.Status = GenesysBatchStatus.UnknownOutcome;
                foreach (var item in items) { item.Status = DispatchItemStatus.UnknownOutcome; item.StatusReason = batch.Error; }
                await store.UpdateItemsAsync(items, ct);
                break;
        }
        await store.UpdateBatchAsync(batch, ct);
    }

    private async Task MarkItemsAsync(CollectionsGenesysBatch batch, DispatchItemStatus status, string? reason, string? contactId, CancellationToken ct)
    {
        var items = await store.GetBatchItemsAsync(batch.CollectionsGenesysBatchId, ct);
        foreach (var item in items) { item.Status = status; item.StatusReason = reason; item.GenesysContactId ??= contactId; }
        await store.UpdateItemsAsync(items, ct);
    }

    /// <summary>The exact contact object posted to Genesys: the list id, the six data fields as strings, and callable (true only for approved voice-eligible contacts).</summary>
    public static GenesysContactPayload ToPayload(CollectionsDispatchItem item, string contactListId, GenesysOutboundOptions settings) => new(
        contactListId, item.Phone, item.CustomerName, item.Email, settings.LabelFor(item.ReminderType),
        MoneyNormalizer.Format(item.Amount), item.DueDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        Callable: item.VoiceEligible && IsVoiceEligible(item.Phone));

    /// <summary>Why nothing is approved or sent while the financial source is not reconciled.</summary>
    public const string NotReconciledMessage = "The financial source is not reconciled (no valid sign-off and snapshot-consistency evidence for the PACT procedure in use), so nothing can be approved or sent. Reconcile it first.";

    /// <summary>A callable contact needs a valid international (E.164) number.</summary>
    public static bool IsVoiceEligible(string? phone) => !string.IsNullOrEmpty(phone) && System.Text.RegularExpressions.Regex.IsMatch(phone, @"^\+[1-9]\d{7,14}$");

    private static string? Truncate(string? text) => text is { Length: > 400 } ? text[..400] : text;

    // ---------------------------------------------------------------- audit export

    /// <summary>The contacts of a dispatch exactly as sent to Genesys (the six contact-list columns), for comparison with the Genesys list.</summary>
    public async Task<CollectionsResult<string>> ExportContactsAsync(CollectionsCaller caller, Guid dispatchId, CancellationToken ct)
    {
        if (!(await authorization.ResolveAsync(caller, ct)).CanSendReminders)
            return CollectionsResult<string>.Fail(CollectionsOutcome.Forbidden);
        var dispatch = await store.GetDispatchAsync(dispatchId, ct);
        if (dispatch is null) return CollectionsResult<string>.Fail(CollectionsOutcome.NotFound);
        var lists = dispatch.Batches.ToDictionary(b => b.CollectionsGenesysBatchId, b => b.ContactListId);
        var sent = dispatch.Items.Where(i => i.CollectionsGenesysBatchId is not null && i.Status != DispatchItemStatus.Excluded && i.Status != DispatchItemStatus.Released)
            .OrderBy(i => i.CollectionsGenesysBatchId).ThenBy(i => i.BatchPosition)
            .Select(i => ToPayload(i, lists[i.CollectionsGenesysBatchId!.Value], genesys));
        return CollectionsResult<string>.Ok(GenesysContactTemplate.WriteCsv(sent));
    }

    // ---------------------------------------------------------------- cancel

    /// <summary>Cancels a dispatch that has not started (still Queued, for example because background jobs are off). Records are released.</summary>
    public async Task<CollectionsResult<DispatchDto>> CancelAsync(CollectionsCaller caller, Guid dispatchId, CancellationToken ct)
    {
        if (!(await authorization.ResolveAsync(caller, ct)).CanSendReminders)
            return CollectionsResult<DispatchDto>.Fail(CollectionsOutcome.Forbidden);
        var dispatch = await store.GetDispatchAsync(dispatchId, ct);
        if (dispatch is null) return CollectionsResult<DispatchDto>.Fail(CollectionsOutcome.NotFound);
        if (!await store.TryCancelQueuedAsync(dispatch.CollectionsDispatchId, "Cancelled before sending by employee " + caller.EmployeeId, clock.UtcNow, ct))
            return CollectionsResult<DispatchDto>.Fail(CollectionsOutcome.InvalidRequest, "Only a send that has not started can be cancelled.");
        await store.ReleaseItemsAsync(dispatch.CollectionsDispatchId, "Dispatch cancelled", ct);
        return CollectionsResult<DispatchDto>.Ok(ToDto((await store.GetDispatchAsync(dispatchId, ct))!));
    }

    // ---------------------------------------------------------------- reconcile

    /// <summary>
    /// Resolves a batch whose outcome is unknown, after a person checked the Genesys contact list.
    /// <c>ConfirmedUploaded</c>: the contacts are there; they stay blocked as already sent.
    /// <c>ConfirmedNotUploaded</c>: nothing was created; the records are released and may be approved again (with fresh validation).
    /// </summary>
    public async Task<CollectionsResult<DispatchDto>> ReconcileAsync(CollectionsCaller caller, Guid dispatchId, long batchId,
        ReconcileBatchRequest request, CancellationToken ct)
    {
        if (!(await authorization.ResolveAsync(caller, ct)).CanSendReminders)
            return CollectionsResult<DispatchDto>.Fail(CollectionsOutcome.Forbidden);
        if (string.IsNullOrWhiteSpace(request.Note) || request.Note.Length > 500)
            return CollectionsResult<DispatchDto>.Fail(CollectionsOutcome.InvalidRequest, "Describe what you checked in Genesys (1-500 characters).");
        var uploaded = string.Equals(request.Resolution, "ConfirmedUploaded", StringComparison.OrdinalIgnoreCase);
        if (!uploaded && !string.Equals(request.Resolution, "ConfirmedNotUploaded", StringComparison.OrdinalIgnoreCase))
            return CollectionsResult<DispatchDto>.Fail(CollectionsOutcome.InvalidRequest, "Resolution must be ConfirmedUploaded or ConfirmedNotUploaded.");
        var dispatch = await store.GetDispatchAsync(dispatchId, ct);
        var batch = await store.GetBatchAsync(batchId, ct);
        if (dispatch is null || batch is null || batch.CollectionsDispatchId != dispatch.CollectionsDispatchId)
            return CollectionsResult<DispatchDto>.Fail(CollectionsOutcome.NotFound);
        if (batch.Status != GenesysBatchStatus.UnknownOutcome)
            return CollectionsResult<DispatchDto>.Fail(CollectionsOutcome.InvalidRequest, "Only a batch with an unconfirmed outcome can be reconciled.");

        batch.Status = uploaded ? GenesysBatchStatus.Uploaded : GenesysBatchStatus.ConfirmedNotUploaded;
        batch.ReconciledByEmployeeId = caller.EmployeeId; batch.ReconciledAtUtc = clock.UtcNow; batch.ReconciliationNote = request.Note.Trim();
        await store.UpdateBatchAsync(batch, ct);
        var items = await store.GetBatchItemsAsync(batchId, ct);
        foreach (var item in items.Where(i => i.Status == DispatchItemStatus.UnknownOutcome))
        {
            item.Status = uploaded ? DispatchItemStatus.UploadedToGenesys : DispatchItemStatus.Released;
            item.StatusReason = uploaded ? "Confirmed in Genesys by a reviewer (contact id not recorded)" : "Confirmed not uploaded by a reviewer";
            if (uploaded) item.UploadedAtUtc ??= batch.CompletedAtUtc;
        }
        await store.UpdateItemsAsync(items, ct);
        return CollectionsResult<DispatchDto>.Ok(ToDto((await store.GetDispatchAsync(dispatchId, ct))!));
    }

    // ---------------------------------------------------------------- mapping

    public static DispatchDto ToDto(CollectionsDispatch d)
    {
        var totals = string.IsNullOrEmpty(d.ApprovedTotalsJson) ? new Dictionary<string, string>()
            : JsonSerializer.Deserialize<Dictionary<string, string>>(d.ApprovedTotalsJson) ?? [];
        int Count(DispatchItemStatus s) => d.Items.Count(i => i.Status == s);
        int Count2(ContactSuppressionStatus s) => d.Items.Count(i => i.SuppressionStatus == s);
        return new(d.PublicId, d.Status.ToString(), d.StatusReason, d.InitiatedByEmployeeId, d.InitiatedAtUtc, d.StartedAtUtc, d.CompletedAtUtc,
            d.ApprovedCount, Count(DispatchItemStatus.UploadedToGenesys), Count(DispatchItemStatus.Excluded), Count(DispatchItemStatus.Failed),
            Count(DispatchItemStatus.UnknownOutcome),
            totals.ToDictionary(p => p.Key, p => decimal.Parse(p.Value, CultureInfo.InvariantCulture)),
            d.Batches.OrderBy(b => b.Sequence).Select(b => new DispatchBatchDto(b.CollectionsGenesysBatchId, b.ReminderType.ToString(), b.ContactListId,
                b.Sequence, b.ContactCount, b.Status.ToString(), b.AttemptCount, b.HttpStatus, b.ReturnedContactCount, b.Error, b.StartedAtUtc,
                b.CompletedAtUtc, b.ReconciledAtUtc, b.ReconciliationNote, b.StageName, b.GenesysCampaignName, b.GenesysCampaignId, b.CampaignAction)).ToList(),
            UploadDisclaimer, d.Phase, d.RevalidationMs, Count2(ContactSuppressionStatus.Suppressed), Count2(ContactSuppressionStatus.Failed),
            d.Items.Count(i => i.Status == DispatchItemStatus.UploadedToGenesys && string.IsNullOrEmpty(i.GenesysContactId)),
            AdminEmailStatus: d.AdminEmailStatus.ToString(), AdminEmailError: d.AdminEmailError, AdminEmailAttemptedAtUtc: d.AdminEmailAttemptedAtUtc,
            AdminEmailRecipientCount: d.AdminEmailRecipientCount);
    }
}
