using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using TigerCS.Application.Modules.Collections;
using TigerCS.Application.Modules.Collections.History;
using TigerCS.Application.Modules.Collections.Abstractions;
using TigerCS.Application.Modules.Collections.Review;
using TigerCS.Application.Modules.Collections.Services;
using TigerCS.Application.Modules.IdentityAndAccess.Abstractions;
using TigerCS.Domain.Modules.Collections.Review;
using TigerCS.Infrastructure.Modules.Collections;
using TigerCS.Infrastructure.Persistence;
using TigerCS.Tests.Collections.Review;
using TigerCS.Tests.IdentityAndAccess.Fakes;

namespace TigerCS.Tests.Collections.History;

/// <summary>One item of a seeded submission: its upload status and whether it was assigned to a Genesys batch.</summary>
internal sealed record SeedItem(DispatchItemStatus Status, bool InBatch = true, decimal Amount = 100m, string Phone = "+971500000001", string Unit = "U1",
    string Currency = "AED", string? Reason = null, string Customer = "Customer");

internal static class HistoryKit
{
    public static GenesysHistoryService Service(ReviewHarness h, TigerCsDbContext context, IEmployeeRepository? employees = null, CollectionsUnitLinkService? links = null) =>
        new(h.Options, h.Genesys, new CollectionsAuthorizationService(h.Options, new FakeDepartmentRepository()), new GenesysHistoryStore(context),
            NullLogger<GenesysHistoryService>.Instance, links, employees);

    public static async Task<ReviewHarness> SeededAsync(int count = 3, Action<ReviewHarness>? configure = null, Func<int, PactReceivableInstalment>? row = null)
    {
        var h = new ReviewHarness();
        configure?.Invoke(h);
        for (var n = 1; n <= count; n++) h.Source.Items.Add(row?.Invoke(n) ?? ReviewHarness.Row(n, 100m + n));
        await h.RefreshAsync();
        return h;
    }

    /// <summary>Approves (as the CS manager) and runs the upload of the current Ready CurrentMonth list through the real pipeline; returns the dispatch id.</summary>
    public static async Task<long> SubmitAsync(ReviewHarness h, string key = "k1", SelectionRequest? selection = null, IGenesysOutboundClient? client = null)
    {
        var (_, confirm) = await h.PrepareAsync(selection, key);
        using var scope = h.NewScope(client);
        var accepted = await scope.Dispatch.ConfirmAsync(h.Manager, confirm, CancellationToken.None);
        Assert.Equal(CollectionsOutcome.Accepted, accepted.Outcome);
        var id = h.Scheduler.Dispatches.Last();
        await scope.Dispatch.ExecuteAsync(id, CancellationToken.None);
        return id;
    }

    public static Guid PublicId(ReviewHarness h, long dispatchId)
    {
        using var context = h.CreateContext();
        return context.CollectionsDispatches.Where(d => d.CollectionsDispatchId == dispatchId).Select(d => d.PublicId).Single();
    }

    public static async Task<GenesysHistoryPageDto> ListAsync(ReviewHarness h, GenesysHistoryFilter? filter = null, int page = 1, int pageSize = 25, CollectionsCaller? caller = null,
        IEmployeeRepository? employees = null)
    {
        using var context = h.CreateContext();
        var result = await Service(h, context, employees).ListAsync(caller ?? h.Manager, filter ?? new GenesysHistoryFilter(), page, pageSize, CancellationToken.None);
        Assert.True(result.IsSuccess, result.Detail);
        return result.Value!;
    }

    public static async Task<GenesysRecordsPageDto> RecordsAsync(ReviewHarness h, Guid id, string? result = null, string? search = null, int page = 1, int pageSize = 50, CollectionsUnitLinkService? links = null)
    {
        using var context = h.CreateContext();
        var records = await Service(h, context, null, links).RecordsAsync(h.Manager, id, result, search, page, pageSize, CancellationToken.None);
        Assert.True(records.IsSuccess, records.Detail);
        return records.Value!;
    }

    public static async Task<GenesysSubmissionDetailDto> DetailAsync(ReviewHarness h, Guid id)
    {
        using var context = h.CreateContext();
        var detail = await Service(h, context).GetAsync(h.Manager, id, CancellationToken.None);
        Assert.True(detail.IsSuccess, detail.Detail);
        return detail.Value!;
    }

    /// <summary>Seeds a submission directly (for filter, paging and status-matrix tests that need many varied rows).</summary>
    public static async Task<CollectionsDispatch> SeedAsync(TigerCsDbContext context, DispatchStatus status, IReadOnlyList<SeedItem> items, DateTime? initiatedUtc = null, Guid? by = null,
        CampaignReminderType type = CampaignReminderType.CurrentMonth, string? listId = null, bool withBatch = true, GenesysBatchStatus batchStatus = GenesysBatchStatus.Uploaded,
        string? statusReason = null, string? batchError = null, string? campaignId = null, string? contactIdPrefix = null)
    {
        var when = initiatedUtc ?? new DateTime(2026, 10, 14, 8, 0, 0, DateTimeKind.Utc);
        var dispatch = new CollectionsDispatch
        {
            IdempotencyKey = "seed-" + Guid.NewGuid().ToString("N"), Fingerprint = new string('a', 64), Status = status, InitiatedByEmployeeId = by ?? Guid.Parse("6f1d2a40-8f0e-4c7b-9b57-0a1f3c2d4e5f"),
            InitiatedAtUtc = when, ReviewRunId = 1, SelectionMode = "AllMatching", FilterJson = "{}", ApprovedCount = items.Count,
            ApprovedTotalsJson = "{\"AED\":\"" + items.Where(i => i.Currency == "AED").Sum(i => i.Amount).ToString("0.00", System.Globalization.CultureInfo.InvariantCulture) + "\"}",
            StartedAtUtc = withBatch ? when.AddSeconds(5) : null, CompletedAtUtc = status is DispatchStatus.Queued or DispatchStatus.Revalidating or DispatchStatus.Sending ? null : when.AddSeconds(30),
            StatusReason = statusReason
        };
        CollectionsGenesysBatch? batch = null;
        if (withBatch)
        {
            batch = new CollectionsGenesysBatch
            {
                ReminderType = type, ContactListId = listId ?? new GenesysOutboundOptions().ContactListIds[type.ToString()], Sequence = 1, ContactCount = items.Count(i => i.InBatch),
                Status = batchStatus, StageName = CampaignReminderTypes.StageName(type), StartedAtUtc = when.AddSeconds(6), CompletedAtUtc = when.AddSeconds(20), Error = batchError, GenesysCampaignId = campaignId
            };
            dispatch.Batches.Add(batch);
        }
        var n = 0;
        foreach (var item in items)
        {
            dispatch.Items.Add(new CollectionsDispatchItem
            {
                RecordKey = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Guid.NewGuid().ToString()))).ToLowerInvariant(), ReminderType = type, CompanyId = 4, TenantId = "T" + (++n),
                UnitCode = item.Unit + "-" + n, CustomerName = item.Customer + " " + n, Phone = item.Phone, Email = "c" + n + "@example.test", Amount = item.Amount, Currency = item.Currency,
                DueDate = new DateOnly(2026, 10, 20), Status = item.Status, StatusReason = item.Reason, VoiceEligible = true, BatchPosition = item.InBatch ? n : null,
                GenesysContactId = item.Status == DispatchItemStatus.UploadedToGenesys ? (contactIdPrefix ?? "contact-") + Guid.NewGuid().ToString("N")[..8] : null,
                UploadedAtUtc = item.Status == DispatchItemStatus.UploadedToGenesys ? when.AddSeconds(20) : null
            });
        }
        context.CollectionsDispatches.Add(dispatch);
        await context.SaveChangesAsync();
        if (batch is not null)
        {
            foreach (var item in dispatch.Items.Where((_, i) => items[i].InBatch)) item.CollectionsGenesysBatchId = batch.CollectionsGenesysBatchId;
            await context.SaveChangesAsync();
        }
        return dispatch;
    }
}
