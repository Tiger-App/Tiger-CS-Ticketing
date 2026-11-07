using Microsoft.EntityFrameworkCore;
using TigerCS.Application.Modules.Collections.History;
using TigerCS.Application.Modules.Collections.Review;
using TigerCS.Domain.Modules.Collections.Review;
using TigerCS.Infrastructure.Modules.Collections;
using TigerCS.Tests.Collections.Review;
using static TigerCS.Tests.Collections.History.HistoryKit;

namespace TigerCS.Tests.Collections.History;

/// <summary>Server-side filtering, paging and the single status rule, over submissions seeded directly so every combination can be covered.</summary>
public sealed class GenesysHistoryQueryTests
{
    private static readonly SeedItem Up = new(DispatchItemStatus.UploadedToGenesys), Fail = new(DispatchItemStatus.Failed, Reason: "rejected"), Unk = new(DispatchItemStatus.UnknownOutcome, Reason: "timeout"),
        Wait = new(DispatchItemStatus.Approved), Excl = new(DispatchItemStatus.Excluded, InBatch: false, Reason: "paid"), Rel = new(DispatchItemStatus.Released, InBatch: false), Unplanned = new(DispatchItemStatus.Approved, InBatch: false);

    private static readonly Guid Alice = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001"), Bob = Guid.Parse("bbbbbbbb-0000-0000-0000-000000000002");

    // ------------------------------------------------------------ one rule, two places

    [Fact]
    public async Task TheStatusFilterInSql_AgreesWithTheLabelShown_ForEveryCombination()
    {
        using var h = new ReviewHarness();
        var combos = new (string Name, SeedItem[] Items, bool Batch)[]
        {
            ("all uploaded", [Up, Up], true), ("all failed", [Fail, Fail], true), ("all unknown", [Unk, Unk], true), ("up+fail", [Up, Fail], true), ("up+unk", [Up, Unk], true),
            ("fail+unk", [Fail, Unk], true), ("up+fail+unk", [Up, Fail, Unk], true), ("up+waiting", [Up, Wait], true), ("only waiting", [Wait], true), ("up+excluded", [Up, Excl], true),
            ("only excluded", [Excl], true), ("only released", [Rel], true), ("up+released", [Up, Rel], true), ("unplanned", [Unplanned, Unplanned], false), ("fail+excluded", [Fail, Excl], true)
        };
        var ids = new Dictionary<Guid, string>();
        using (var context = h.CreateContext())
            foreach (var status in Enum.GetValues<DispatchStatus>())
                foreach (var (name, items, batch) in combos)
                    ids[(await SeedAsync(context, status, items, withBatch: batch, batchStatus: GenesysBatchStatus.Uploaded)).PublicId] = $"{status} / {name}";

        using var read = h.CreateContext();
        var store = new GenesysHistoryStore(read);
        var everything = await store.ListSubmissionsAsync(new GenesysHistorySpec(null, null, null, null, null, null, null, null), 0, 1000, default);
        Assert.Equal(ids.Count, everything.TotalCount);
        var expected = everything.Items.ToDictionary(r => r.PublicId, r => GenesysUploadStateRules.Classify(r.Status, r.Counts));

        var seen = new HashSet<Guid>();
        foreach (var state in GenesysUploadStates.All)
        {
            var filtered = await store.ListSubmissionsAsync(new GenesysHistorySpec(null, null, null, state, null, null, null, null), 0, 1000, default);
            var wanted = expected.Where(e => e.Value == state).Select(e => e.Key).Order().ToList();
            Assert.True(wanted.SequenceEqual(filtered.Items.Select(r => r.PublicId).Order()),
                $"{state}: SQL filter {string.Join(", ", filtered.Items.Select(r => ids[r.PublicId]))} != rule {string.Join(", ", wanted.Select(w => ids[w]))}");
            Assert.Equal(wanted.Count, filtered.TotalCount);
            foreach (var id in wanted) Assert.True(seen.Add(id), "a submission has exactly one state");
        }
        Assert.Equal(ids.Count, seen.Count);
        foreach (var state in GenesysUploadStates.All) Assert.Contains(expected.Values, v => v == state);   // the matrix reaches every state
    }

    [Theory]
    [InlineData(DispatchStatus.Queued, GenesysUploadStates.Pending)]
    [InlineData(DispatchStatus.Revalidating, GenesysUploadStates.Pending)]
    [InlineData(DispatchStatus.Sending, GenesysUploadStates.Uploading)]
    public async Task AnUnfinishedSubmission_IsPendingOrUploading_WithItsApprovedFigures_NotSuccess(DispatchStatus dispatch, string expected)
    {
        using var h = new ReviewHarness();
        using (var context = h.CreateContext()) await SeedAsync(context, dispatch, [Unplanned, Unplanned, Unplanned], withBatch: false);
        var s = Assert.Single((await ListAsync(h)).Items);
        Assert.Equal(expected, s.Status);
        Assert.Equal(("Approved", 3, 300m, null), (s.FiguresBasis, s.UnitRecords, s.TotalAmount, s.SubmittedAtUtc));   // nothing submitted yet: approved figures, labelled so
        Assert.Contains(expected == GenesysUploadStates.Pending ? "not started" : "in progress", s.StatusExplanation);
        Assert.Null(s.ContactListLabel);
    }

    [Fact]
    public async Task ASendStoppedBeforeUpload_IsNotSent_WithItsReason_NotFailedOrUploaded()
    {
        using var h = new ReviewHarness();
        using (var context = h.CreateContext())
            await SeedAsync(context, DispatchStatus.ReviewRequired, [Rel, Rel], withBatch: false, statusReason: "2 approved record(s) changed after approval. Nothing was sent.");
        var s = Assert.Single((await ListAsync(h)).Items);
        Assert.Equal(GenesysUploadStates.NotSent, s.Status);
        Assert.Equal("Not sent", s.StatusLabel);
        Assert.Contains("Nothing was sent", s.Error);
        Assert.Contains("nothing was submitted", s.StatusExplanation);
        Assert.Equal(0, (await ListAsync(h)).Summary.NeedAttention);
    }

    [Fact]
    public async Task OtherCurrencies_AreNeverAddedIntoTheAedTotal()
    {
        using var h = new ReviewHarness();
        using (var context = h.CreateContext()) await SeedAsync(context, DispatchStatus.Completed, [Up with { Amount = 100m }, Up with { Amount = 50m, Currency = "SAR" }]);
        var s = Assert.Single((await ListAsync(h)).Items);
        Assert.Equal((2, 100m, 1), (s.UnitRecords, s.TotalAmount, s.OtherCurrencyRecords));
    }

    // ------------------------------------------------------------ filters

    [Fact]
    public async Task Filters_NarrowByStageListEmployeeSearchAndStatus()
    {
        using var h = new ReviewHarness();
        var other = "c9d1e9f0-1111-4222-8333-444455556666";
        using (var context = h.CreateContext())
        {
            await SeedAsync(context, DispatchStatus.Completed, [Up with { Unit = "ALPHA", Customer = "Zed Zayed" }], by: Alice, type: CampaignReminderType.Overdue, campaignId: "camp-overdue");
            await SeedAsync(context, DispatchStatus.Completed, [Fail, Fail], by: Bob, type: CampaignReminderType.FollowUp, listId: other, contactIdPrefix: "x-");
            await SeedAsync(context, DispatchStatus.CompletedWithErrors, [Up with { Unit = "BRAVO" }, Fail], by: Alice, type: CampaignReminderType.CurrentMonth, contactIdPrefix: "exact-");
        }
        async Task<int> CountAsync(GenesysHistoryFilter f) => (await ListAsync(h, f)).TotalCount;

        Assert.Equal(3, await CountAsync(new()));
        Assert.Equal(1, await CountAsync(new(Stage: "Overdue")));
        Assert.Equal(1, await CountAsync(new(Stage: "followup")));
        Assert.Equal(2, await CountAsync(new(EmployeeId: Alice)));
        Assert.Equal(1, await CountAsync(new(EmployeeId: Bob)));
        Assert.Equal(1, await CountAsync(new(List: "Overdue")));                                          // a configured list, by its reminder type name
        Assert.Equal(1, await CountAsync(new(List: other)));                                              // or by the list id
        Assert.Equal(1, await CountAsync(new(Status: "Failed")));
        Assert.Equal(1, await CountAsync(new(Status: "partial")));
        Assert.Equal(1, await CountAsync(new(Status: "Uploaded")));
        Assert.Equal(1, await CountAsync(new(Search: "ALPHA")));                                          // unit code
        Assert.Equal(1, await CountAsync(new(Search: "Zayed")));                                          // customer name
        Assert.Equal(1, await CountAsync(new(Search: "camp-overdue")));                                   // an exact Genesys campaign id
        Assert.Equal(0, await CountAsync(new(Search: "no-such-thing")));
        Assert.Equal(1, await CountAsync(new(Stage: "CurrentMonth", EmployeeId: Alice, Status: "Partial")));   // filters combine
        Assert.Equal(0, await CountAsync(new(Stage: "Overdue", EmployeeId: Bob)));
        var filtered = await ListAsync(h, new GenesysHistoryFilter(EmployeeId: Alice));
        Assert.Equal(2, filtered.Summary.Submissions);                                                    // the summary follows the filters
        Assert.Equal(3, (await ListAsync(h)).Options.Statuses.Count(s => s.Value is "Pending" or "Uploading" or "NotSent"));
    }

    [Fact]
    public async Task TheDateRange_UsesDubaiDays_InclusiveOfBothEnds()
    {
        using var h = new ReviewHarness();
        using (var context = h.CreateContext())
        {
            await SeedAsync(context, DispatchStatus.Completed, [Up], initiatedUtc: new DateTime(2026, 10, 13, 19, 59, 59, DateTimeKind.Utc));   // 13 Oct 23:59:59 Dubai
            await SeedAsync(context, DispatchStatus.Completed, [Up], initiatedUtc: new DateTime(2026, 10, 13, 20, 0, 0, DateTimeKind.Utc));       // 14 Oct 00:00:00 Dubai
            await SeedAsync(context, DispatchStatus.Completed, [Up], initiatedUtc: new DateTime(2026, 10, 14, 19, 59, 59, DateTimeKind.Utc));     // 14 Oct 23:59:59 Dubai
            await SeedAsync(context, DispatchStatus.Completed, [Up], initiatedUtc: new DateTime(2026, 10, 14, 20, 0, 0, DateTimeKind.Utc));       // 15 Oct 00:00:00 Dubai
        }
        async Task<int> CountAsync(DateOnly? from, DateOnly? to) => (await ListAsync(h, new GenesysHistoryFilter(From: from, To: to))).TotalCount;
        var d14 = new DateOnly(2026, 10, 14);
        Assert.Equal(2, await CountAsync(d14, d14));                                                      // exactly the Dubai day 14 Oct
        Assert.Equal(3, await CountAsync(d14, null));
        Assert.Equal(3, await CountAsync(null, d14));
        Assert.Equal(4, await CountAsync(new DateOnly(2026, 10, 13), new DateOnly(2026, 10, 15)));
        Assert.Equal(0, await CountAsync(new DateOnly(2026, 10, 16), null));
    }

    // ------------------------------------------------------------ paging

    [Fact]
    public async Task Paging_IsServerSide_NewestFirst_AndTheSummaryCoversEveryPage()
    {
        using var h = new ReviewHarness();
        var start = new DateTime(2026, 9, 1, 8, 0, 0, DateTimeKind.Utc);
        using (var context = h.CreateContext())
            for (var i = 0; i < 60; i++)
                await SeedAsync(context, DispatchStatus.Completed, [Up with { Amount = 10m }, Up with { Amount = 20m }], initiatedUtc: start.AddHours(i));

        var first = await ListAsync(h, pageSize: 25);
        var second = await ListAsync(h, page: 2, pageSize: 25);
        var third = await ListAsync(h, page: 3, pageSize: 25);
        Assert.Equal((60, 25, 25, 10), (first.TotalCount, first.Items.Count, second.Items.Count, third.Items.Count));
        Assert.Equal(start.AddHours(59), first.Items[0].ApprovedAtUtc);
        Assert.Equal(start.AddHours(34), second.Items[0].ApprovedAtUtc);
        Assert.Equal(start.AddHours(0), third.Items[^1].ApprovedAtUtc);
        Assert.Empty(first.Items.Select(i => i.SubmissionId).Intersect(second.Items.Select(i => i.SubmissionId)));
        Assert.All(new[] { first, second, third }, p => Assert.Equal((60, 120, 1800m, 60), (p.Summary.Submissions, p.Summary.Records, p.Summary.TotalAmount, p.Summary.Uploaded)));   // not just the visible page
        Assert.Empty((await ListAsync(h, page: 4, pageSize: 25)).Items);
        Assert.Equal(100, (await ListAsync(h, pageSize: 5000)).PageSize);                                  // the page size is bounded
        Assert.Equal(25, (await ListAsync(h, pageSize: 0)).PageSize);
        Assert.Equal(1, (await ListAsync(h, page: -3)).Page);
    }

    [Fact]
    public async Task TheRecordList_IsPaged_FilteredAndSearchable()
    {
        using var h = new ReviewHarness();
        CollectionsDispatch dispatch;
        using (var context = h.CreateContext())
            dispatch = await SeedAsync(context, DispatchStatus.CompletedWithErrors, [.. Enumerable.Repeat(Up, 7), Fail, Fail, Unk, Excl]);

        var first = await RecordsAsync(h, dispatch.PublicId, pageSize: 4);
        var third = await RecordsAsync(h, dispatch.PublicId, page: 3, pageSize: 4);
        Assert.Equal((11, 4, 3), (first.TotalCount, first.Items.Count, third.Items.Count));
        Assert.Equal(7, (await RecordsAsync(h, dispatch.PublicId, "Uploaded")).TotalCount);
        Assert.Equal(2, (await RecordsAsync(h, dispatch.PublicId, "Failed")).TotalCount);
        Assert.Equal(1, (await RecordsAsync(h, dispatch.PublicId, "Unknown")).TotalCount);
        Assert.Equal(1, (await RecordsAsync(h, dispatch.PublicId, "NotSent")).TotalCount);
        Assert.Equal(1, (await RecordsAsync(h, dispatch.PublicId, search: "U1-9")).TotalCount);
        var detail = await DetailAsync(h, dispatch.PublicId);
        Assert.Equal((GenesysUploadStates.Partial, 10, 11), (detail.Submission.Status, detail.Submission.UnitRecords, detail.ApprovedRecords));
    }
}
