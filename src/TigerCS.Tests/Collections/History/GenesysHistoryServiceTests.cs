using Microsoft.EntityFrameworkCore;
using TigerCS.Application.Modules.Collections;
using TigerCS.Application.Modules.Collections.Abstractions;
using TigerCS.Application.Modules.Collections.History;
using TigerCS.Application.Modules.Collections.Review;
using TigerCS.Application.Modules.Collections.Services;
using TigerCS.Domain.Modules.Collections.Review;
using TigerCS.Domain.Modules.IdentityAndAccess;
using TigerCS.Tests.Collections.Crm;
using TigerCS.Tests.Collections.Review;
using TigerCS.Tests.IdentityAndAccess.Fakes;
using TigerCS.Tests.Notifications.Fakes;
using static TigerCS.Tests.Collections.History.HistoryKit;

namespace TigerCS.Tests.Collections.History;

/// <summary>
/// The Genesys Campaign History over the REAL approval and upload pipeline (SQLite, scripted Genesys): what a person approved and what Genesys answered is what the history shows,
/// with no invented success, no merged units and no call outcome.
/// </summary>
public sealed class GenesysHistoryServiceTests
{
    private sealed class ThrowingUploadClient(FakeGenesysClient inner) : IGenesysOutboundClient
    {
        public Task<GenesysContactListInfo> GetContactListAsync(string id, CancellationToken ct) => inner.GetContactListAsync(id, ct);
        public Task<GenesysCampaignLookupResult> FindCampaignsByContactListAsync(string id, CancellationToken ct) => inner.FindCampaignsByContactListAsync(id, ct);
        public Task<GenesysCampaignLookupResult> FindCampaignsByNameAsync(string name, CancellationToken ct) => inner.FindCampaignsByNameAsync(name, ct);
        public Task<GenesysCampaignCreateResult> CreateCampaignAsync(string name, string id, GenesysCampaignOptions template, CancellationToken ct) => inner.CreateCampaignAsync(name, id, template, ct);
        public Task<GenesysSuppressResult> SetNotCallableAsync(string id, string contactId, GenesysContactPayload contact, CancellationToken ct) => inner.SetNotCallableAsync(id, contactId, contact, ct);
        public Task<GenesysUploadResult> UploadContactsAsync(string id, IReadOnlyList<GenesysContactPayload> contacts, CancellationToken ct) => throw new TimeoutException("simulated");
    }

    // ------------------------------------------------------------ outcomes

    [Fact]
    public async Task ASuccessfulUpload_IsUploaded_WithItsRecordsAmountListAndSubmitter()
    {
        using var h = await SeededAsync(3);
        var employees = new FakeEmployeeRepository().Add(new Employee(h.Manager.EmployeeId, "Mona Manager", true, DateTime.UtcNow));
        await SubmitAsync(h);

        var page = await ListAsync(h, employees: employees);
        var s = Assert.Single(page.Items);
        Assert.Equal(GenesysUploadStates.Uploaded, s.Status);
        Assert.Equal(("CurrentMonth", "Current Month", "Manual", "Mona Manager"), (s.ReminderType, s.ReminderLabel, s.Mode, s.SubmittedByName));
        Assert.Equal(CampaignReminderTypes.StageName(CampaignReminderType.CurrentMonth), s.StageName);
        Assert.Equal("Current Month list", s.ContactListLabel);
        Assert.Equal((3, 306m, "AED", "Submitted"), (s.UnitRecords, s.TotalAmount, s.Currency, s.FiguresBasis));
        Assert.Equal((3, 0, 0), (s.UploadedRecords, s.FailedRecords, s.UnconfirmedRecords));
        Assert.NotNull(s.SubmittedAtUtc);
        Assert.Equal("Call outcome unavailable", s.CallOutcome);
        Assert.Contains("does not mean the customers were called", s.StatusExplanation);
        Assert.Null(s.Error);
        Assert.Equal((1, 3, 306m, 1, 0), (page.Summary.Submissions, page.Summary.Records, page.Summary.TotalAmount, page.Summary.Uploaded, page.Summary.NeedAttention));
        Assert.Equal(3, page.Summary.ConfirmedRecords);
        Assert.Contains("Mona Manager", page.Options.Employees.Select(e => e.Label));
    }

    [Fact]
    public async Task AFailedUpload_IsFailed_WithTheErrorOnTheSubmissionAndEveryRecord()
    {
        using var h = await SeededAsync(3);
        h.Client.Script.Enqueue(GenesysUploadOutcome.Rejected);
        var id = await SubmitAsync(h);

        var s = Assert.Single((await ListAsync(h)).Items);
        Assert.Equal(GenesysUploadStates.Failed, s.Status);
        Assert.Equal((0, 3, 0), (s.UploadedRecords, s.FailedRecords, s.UnconfirmedRecords));
        Assert.Contains("rejected the request (HTTP 400)", s.Error);
        Assert.Contains("Nothing was added to the contact list", s.StatusExplanation);
        var records = await RecordsAsync(h, PublicId(h, id));
        Assert.All(records.Items, r => { Assert.Equal("Failed", r.Result); Assert.Contains("rejected the request", r.Error); Assert.Null(r.GenesysContactId); });
        var detail = await DetailAsync(h, PublicId(h, id));
        var batch = Assert.Single(detail.Batches);
        Assert.Equal(("Failed", 400), (batch.StatusLabel, batch.HttpStatus));
    }

    [Fact]
    public async Task ATimeout_IsUnknown_NeverUploaded_AndSaysSo()
    {
        using var h = await SeededAsync(3);
        h.Client.Script.Enqueue(GenesysUploadOutcome.Unknown);
        var id = await SubmitAsync(h);

        var page = await ListAsync(h);
        var s = Assert.Single(page.Items);
        Assert.Equal(GenesysUploadStates.Unknown, s.Status);
        Assert.NotEqual(GenesysUploadStates.Uploaded, s.Status);
        Assert.Equal((0, 0, 3), (s.UploadedRecords, s.FailedRecords, s.UnconfirmedRecords));
        Assert.Contains("uncertain", s.StatusExplanation);
        Assert.Contains("timed out", s.Error);
        Assert.Equal(0, page.Summary.Uploaded);
        Assert.Equal(1, page.Summary.NeedAttention);
        Assert.Equal(0, page.Summary.ConfirmedRecords);                                                  // nothing is counted as confirmed
        Assert.All((await RecordsAsync(h, PublicId(h, id))).Items, r => Assert.Equal("Unknown", r.Result));
    }

    [Fact]
    public async Task AnExceptionFromTheClient_IsUnknown_NotASuccess()
    {
        using var h = await SeededAsync(2);
        await SubmitAsync(h, client: new ThrowingUploadClient(h.Client));

        var s = Assert.Single((await ListAsync(h)).Items);
        Assert.Equal(GenesysUploadStates.Unknown, s.Status);
        Assert.Equal(2, s.UnconfirmedRecords);
    }

    [Fact]
    public async Task AccessedButShortAnswer_IsPartialByRecord_NotUploaded()
    {
        using var h = await SeededAsync(4);
        h.Client.IdCounts.Enqueue(3);                                                                    // Genesys accepted the request but returned 3 contact ids for 4 contacts
        var id = await SubmitAsync(h);

        var s = Assert.Single((await ListAsync(h)).Items);
        Assert.Equal(GenesysUploadStates.Partial, s.Status);
        Assert.Equal((3, 0, 1), (s.UploadedRecords, s.FailedRecords, s.UnconfirmedRecords));
        var records = await RecordsAsync(h, PublicId(h, id));
        Assert.Equal(3, records.Items.Count(r => r.Result == "Uploaded"));
        Assert.Single(records.Items, r => r.Result == "Unknown");
    }

    [Fact]
    public async Task APartialResult_IsReportedOnlyWhenSomeBatchesAreConfirmedAndOthersAreNot()
    {
        using var h = await SeededAsync(3, x => x.Genesys.BatchSize = 2);                                // two requests: 2 + 1 contacts
        h.Client.Script.Enqueue(GenesysUploadOutcome.Accepted);
        h.Client.Script.Enqueue(GenesysUploadOutcome.Rejected);
        var id = await SubmitAsync(h);

        var page = await ListAsync(h);
        var s = Assert.Single(page.Items);
        Assert.Equal(GenesysUploadStates.Partial, s.Status);
        Assert.Equal((2, 1, 0), (s.UploadedRecords, s.FailedRecords, s.UnconfirmedRecords));
        Assert.Contains("2 of 3", s.StatusExplanation);
        Assert.Equal(3, s.UnitRecords);
        Assert.Equal(1, page.Summary.NeedAttention);
        Assert.Equal(2, page.Summary.ConfirmedRecords);
        Assert.Equal(2, (await DetailAsync(h, PublicId(h, id))).Batches.Count);
        Assert.Single((await ListAsync(h, new GenesysHistoryFilter(Status: "Partial"))).Items);
        Assert.Empty((await ListAsync(h, new GenesysHistoryFilter(Status: "Uploaded"))).Items);
    }

    // ------------------------------------------------------------ duplicate phones

    [Fact]
    public async Task UnitsSharingAPhoneNumber_StayDistinctRows_NeverMergedOrCountedOnce()
    {
        using var h = await SeededAsync(3, row: n => ReviewHarness.Row(n, 100m * n, mobile: "971500000099"));
        var id = await SubmitAsync(h);
        Assert.Equal(3, Assert.Single(h.Client.Calls).Contacts.Count);                                   // three calls to the same number: one per unit

        var s = Assert.Single((await ListAsync(h)).Items);
        Assert.Equal((GenesysUploadStates.Uploaded, 3, 600m), (s.Status, s.UnitRecords, s.TotalAmount));  // 100 + 200 + 300, not one record
        var detail = await DetailAsync(h, PublicId(h, id));
        Assert.Equal(3, detail.SharedPhoneRecords);
        var records = await RecordsAsync(h, PublicId(h, id));
        Assert.Equal(3, records.TotalCount);
        Assert.Equal(3, records.Items.Select(r => r.UnitCode).Distinct().Count());
        Assert.Equal([100m, 200m, 300m], records.Items.Select(r => r.Amount).Order().ToArray());
        Assert.All(records.Items, r => { Assert.Equal("+971500000099", r.Phone); Assert.True(r.SharedPhone); });
    }

    [Fact]
    public async Task APhoneUsedOnce_IsNotFlaggedShared()
    {
        using var h = await SeededAsync(2);
        var id = await SubmitAsync(h);
        Assert.All((await RecordsAsync(h, PublicId(h, id))).Items, r => Assert.False(r.SharedPhone));
        Assert.Equal(0, (await DetailAsync(h, PublicId(h, id))).SharedPhoneRecords);
    }

    // ------------------------------------------------------------ manual and auto

    [Fact]
    public async Task ManualAndAutoSubmissions_AreTold_ApartByWhoStartedThem_AndFilterByMode()
    {
        using var h = await SeededAsync(2, x => x.Campaign.AutoSendingEnabled = true);
        using (var scope = h.NewScope())
        {
            var run = await scope.Auto.RunAsync(CancellationToken.None);
            Assert.True(run.Ran, run.Detail);
            await scope.Dispatch.ExecuteAsync(h.Scheduler.Dispatches.Last(), CancellationToken.None);
        }
        h.Source.Items.AddRange([ReviewHarness.Row(3, 103m), ReviewHarness.Row(4, 104m)]);
        await h.RefreshAsync();
        var manualSelection = new SelectionRequest(new ReviewFilter(ReminderType: "CurrentMonth", SendingMode: "Manual", MinRemaining: 0m), ReviewQueryService.ModeAllMatching);
        await SubmitAsync(h, "manual-1", manualSelection);

        var all = await ListAsync(h);
        Assert.Equal(2, all.TotalCount);
        var auto = Assert.Single((await ListAsync(h, new GenesysHistoryFilter(Mode: "Auto"))).Items);
        var manual = Assert.Single((await ListAsync(h, new GenesysHistoryFilter(Mode: "Manual"))).Items);
        Assert.Equal(("Auto", "Automatic (scheduler)", 2), (auto.Mode, auto.SubmittedByName, auto.UnitRecords));
        Assert.Equal(("Manual", h.Manager.EmployeeId, 2), (manual.Mode, manual.SubmittedById, manual.UnitRecords));
        Assert.All(all.Items, i => Assert.Equal(GenesysUploadStates.Uploaded, i.Status));
        Assert.Single((await ListAsync(h, new GenesysHistoryFilter(EmployeeId: h.Manager.EmployeeId))).Items);
        Assert.DoesNotContain(all.Options.Employees, e => e.Label == "Automatic (scheduler)");           // Auto is a mode, not an employee
    }

    // ------------------------------------------------------------ counts and amounts

    [Fact]
    public async Task ARecordPaidBeforeSending_IsNotCountedAsSubmitted_ButStaysVisibleWithItsReason()
    {
        using var h = await SeededAsync(3);
        var (_, confirm) = await h.PrepareAsync();
        using (var scope = h.NewScope())
        {
            Assert.Equal(CollectionsOutcome.Accepted, (await scope.Dispatch.ConfirmAsync(h.Manager, confirm, CancellationToken.None)).Outcome);
            h.Source.Items.RemoveAll(r => r.TenantId == "T0002");                                       // paid after approval
            await scope.Dispatch.ExecuteAsync(h.Scheduler.Dispatches[0], CancellationToken.None);
        }
        var id = PublicId(h, h.Scheduler.Dispatches[0]);

        var s = Assert.Single((await ListAsync(h)).Items);
        Assert.Equal((GenesysUploadStates.Uploaded, 2, 204m, 1), (s.Status, s.UnitRecords, s.TotalAmount, s.ExcludedRecords));   // 101 + 103; the paid one is not in the figures
        var excluded = Assert.Single((await RecordsAsync(h, id, "NotSent")).Items);
        Assert.Equal(("Customer 2", "Excluded before upload"), (excluded.CustomerName, excluded.Result));
        Assert.Contains("Paid or settled", excluded.Error);
        Assert.Equal(3, (await RecordsAsync(h, id)).TotalCount);                                         // nothing disappears from the record list
        Assert.Equal(3, (await DetailAsync(h, id)).ApprovedRecords);
    }

    [Fact]
    public async Task RecordRows_CarryEveryDetailTheScreenShows_AndNeverACallResult()
    {
        using var h = await SeededAsync(2);
        var id = await SubmitAsync(h);

        var records = await RecordsAsync(h, PublicId(h, id));
        var r = records.Items.Single(x => x.CustomerName == "Customer 1");
        Assert.Equal(("TP140-1", "+971500000001", "c1@example.test", "CurrentMonth", 101m, "AED", new DateOnly(2026, 10, 20)), (r.UnitCode, r.Phone, r.Email, r.ReminderType, r.Amount, r.Currency, r.DueDate));
        Assert.Equal("Uploaded", r.Result);
        Assert.NotNull(r.GenesysContactId);
        Assert.NotNull(r.SubmittedAtUtc);
        Assert.NotNull(r.UploadedAtUtc);
        Assert.Null(r.Error);
        Assert.All(records.Items, x => Assert.Equal("Call outcome unavailable", x.CallOutcome));
        var forbidden = new[] { "Called", "Contacted", "Delivered", "Reached" };
        Assert.All(records.Items, x => Assert.DoesNotContain(x.Result, forbidden));
        Assert.Contains("call outcome is unavailable", records.CallOutcomeNote);
    }

    [Fact]
    public async Task TheCrmCustomerId_IsTheUnitsCurrentCrmLink_AndAbsentWhenCrmIsNotAvailable()
    {
        var store = new FakeCrmOwnerStore { LastSuccessUtc = ReviewHarness.Now.AddMinutes(-20) };
        var links = new CollectionsUnitLinkService(store, new CollectionsCrmOwnersOptions { Enabled = true }, new FakeTimeProvider(ReviewHarness.Now),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<CollectionsUnitLinkService>.Instance);
        await store.ReplaceAsync([.. Enumerable.Range(1, 2).Select(n => new CrmOwnerRow($"TP140-{n}", "TP140", n.ToString(), n * 10, 8, 7000 + n, 5000 + n, 7, "Crm Customer " + n, "", "", "", "", "Blue Wave"))], default, null);
        using var h = await SeededAsync(2);
        var id = await SubmitAsync(h);

        var withCrm = await RecordsAsync(h, PublicId(h, id), links: links);
        Assert.Equal([7001, 7002], withCrm.Items.Select(r => r.CrmCustomerId).Order().Select(x => x!.Value).ToArray());
        Assert.All((await RecordsAsync(h, PublicId(h, id))).Items, r => Assert.Null(r.CrmCustomerId));   // no CRM feed: shown as not available, never guessed
    }

    // ------------------------------------------------------------ downloaded files are not uploads

    [Fact]
    public async Task ADownloadedCsv_IsInTheExportHistory_NeverInTheSubmissions_OrTheirStatistics()
    {
        using var h = await SeededAsync(3);
        using (var scope = h.NewScope())
        {
            var export = await scope.Export.ExportAsync(h.Manager, h.All(new ReviewFilter(ReminderType: "CurrentMonth")), CancellationToken.None);
            Assert.True(export.IsSuccess, export.Detail);
        }
        var before = await ListAsync(h);
        Assert.Equal(0, before.TotalCount);
        Assert.Equal((0, 0, 0m, 0, 0, 0), (before.Summary.Submissions, before.Summary.Records, before.Summary.TotalAmount, before.Summary.Uploaded, before.Summary.NeedAttention, before.Summary.Pending));
        Assert.Empty(h.Client.Calls);                                                                    // a download uploads nothing

        using (var context = h.CreateContext())
        {
            var exports = (await Service(h, context).ExportsAsync(h.Manager, null, null, 1, 25, CancellationToken.None)).Value!;
            var file = Assert.Single(exports.Items);
            Assert.Equal(("CurrentMonth", "Current Month", 3, 3, 306m, "AED"), (file.ReminderType, file.ReminderLabel, file.Rows, file.Units, file.TotalAmount, file.Currency));
            Assert.Equal(h.Manager.EmployeeId, file.ExportedById);
            Assert.NotNull(file.FileFingerprint);
            Assert.Contains("not an upload", exports.Notice);
        }

        await SubmitAsync(h);                                                                            // now a real upload
        var after = await ListAsync(h);
        Assert.Equal((1, 3, 306m), (after.Summary.Submissions, after.Summary.Records, after.Summary.TotalAmount));   // the earlier download adds nothing
        using var verify = h.CreateContext();
        Assert.Equal(1, (await Service(h, verify).ExportsAsync(h.Manager, null, null, 1, 25, CancellationToken.None)).Value!.TotalCount);
    }

    [Fact]
    public void TheExportAuditText_IsParsedWithoutTheCustomerFingerprints()
    {
        var parsed = GenesysHistoryService.ParseExport(
            "Campaign=Overdue;ContactList=FollowUp (3a91c06e-47ab-4a5e-a720-004bd5cf5bba);Rows=12;Units=14;Excluded=2;Exclusions=NoPhone:2;Total=AED 1234.50;IncludedByStatus=Ready:10;DataVerified=False;" +
            "Unverified=Stale (Financial data not verified);SchemaVerified=True;Mode=Manual;Population=Filtered;Filter={\"Project\":\"A;B\"};CustomerSearch=False;Selection=AllMatching;Skipped=0;Repeated=3;" +
            "RepeatCheck=Checked;Sha256=ABCDEF0123456789ABCDEF;Customers=aaaaaaaaaaaa,bbbbbbbbbbbb");
        Assert.Equal(("Overdue", "FollowUp", 12, 14, 2, 1234.50m, "AED"), (parsed.Campaign, parsed.ContactList, parsed.Rows, parsed.Units, parsed.Excluded, parsed.Total, parsed.Currency));
        Assert.Equal((false, true, "Manual", 3, "ABCDEF0123456789ABCDEF"), (parsed.DataVerified, parsed.SchemaVerified, parsed.Mode, parsed.Repeated, parsed.Sha));
        var empty = GenesysHistoryService.ParseExport(null);
        Assert.Null(empty.Campaign); Assert.Null(empty.Rows); Assert.Null(empty.Total);
        Assert.Null(GenesysHistoryService.ParseExport("garbage;text").Campaign);
    }

    // ------------------------------------------------------------ durability

    [Fact]
    public async Task History_StaysReadable_AfterTheSnapshotAndReviewDataChangeAndOldRunsAreRemoved()
    {
        using var h = await SeededAsync(3, x => x.Review.RunsToKeep = 1);
        var id = await SubmitAsync(h);
        var publicId = PublicId(h, id);
        var before = await RecordsAsync(h, publicId);
        var submissionBefore = Assert.Single((await ListAsync(h)).Items);

        h.Source.Items.Clear();                                                                          // the units are paid / gone from the snapshot
        h.Source.Items.Add(ReviewHarness.Row(9, 999m, name: "Someone Else"));
        await h.RefreshAsync();
        await h.RefreshAsync();                                                                          // the run the dispatch was approved from is pruned

        using (var context = h.CreateContext())
        {
            var approvedFrom = await context.CollectionsDispatches.Select(d => d.ReviewRunId).SingleAsync();
            Assert.Equal(0, await context.CollectionsReviewRecords.CountAsync(r => r.CollectionsReviewRunId == approvedFrom));   // the review records the approval was made from are gone
            Assert.True(await context.CollectionsDispatchItems.CountAsync() == 3);                                           // the frozen copies are not
        }
        var after = await RecordsAsync(h, publicId);
        Assert.Equal(before.Items.Select(r => (r.CustomerName, r.UnitCode, r.Amount, r.DueDate, r.Phone, r.Result, r.GenesysContactId)),
            after.Items.Select(r => (r.CustomerName, r.UnitCode, r.Amount, r.DueDate, r.Phone, r.Result, r.GenesysContactId)));
        Assert.Equal(submissionBefore with { Companies = null }, Assert.Single((await ListAsync(h)).Items) with { Companies = null });
    }

    // ------------------------------------------------------------ access control

    [Fact]
    public async Task AccessFollowsTheCollectionsRights_ReadersSeeSubmissions_OnlySendersSeeDownloads_OthersSeeNothing()
    {
        using var h = await SeededAsync(2);
        var id = await SubmitAsync(h);
        var publicId = PublicId(h, id);
        using var context = h.CreateContext();
        var service = Service(h, context);
        var f = new GenesysHistoryFilter();

        Assert.True((await service.ListAsync(h.Agent, f, 1, 25, default)).IsSuccess);                  // CS agent: financial reader
        Assert.True((await service.GetAsync(h.Agent, publicId, default)).IsSuccess);
        Assert.True((await service.RecordsAsync(h.Agent, publicId, null, null, 1, 25, default)).IsSuccess);
        Assert.Equal(CollectionsOutcome.Forbidden, (await service.ExportsAsync(h.Agent, null, null, 1, 25, default)).Outcome);
        Assert.True((await service.ExportsAsync(h.Manager, null, null, 1, 25, default)).IsSuccess);

        Assert.Equal(CollectionsOutcome.Forbidden, (await service.ListAsync(h.Reporting, f, 1, 25, default)).Outcome);
        Assert.Equal(CollectionsOutcome.Forbidden, (await service.GetAsync(h.Reporting, publicId, default)).Outcome);
        Assert.Equal(CollectionsOutcome.Forbidden, (await service.RecordsAsync(h.Reporting, publicId, null, null, 1, 25, default)).Outcome);
        Assert.Equal(CollectionsOutcome.Forbidden, (await service.ExportsAsync(h.Reporting, null, null, 1, 25, default)).Outcome);
        Assert.Equal(CollectionsOutcome.NotFound, (await service.GetAsync(h.Manager, Guid.NewGuid(), default)).Outcome);
        Assert.Equal(CollectionsOutcome.NotFound, (await service.RecordsAsync(h.Manager, Guid.NewGuid(), null, null, 1, 25, default)).Outcome);
    }

    [Fact]
    public async Task InvalidFilters_AreRefused_NotIgnored()
    {
        using var h = new ReviewHarness();
        using var context = h.CreateContext();
        var service = Service(h, context);
        foreach (var bad in new[] { new GenesysHistoryFilter(Status: "Called"), new GenesysHistoryFilter(Mode: "Robot"), new GenesysHistoryFilter(Stage: "Nope"), new GenesysHistoryFilter(List: "not-a-list"),
                     new GenesysHistoryFilter(From: new DateOnly(2026, 10, 5), To: new DateOnly(2026, 10, 1)) })
            Assert.Equal(CollectionsOutcome.InvalidRequest, (await service.ListAsync(h.Manager, bad, 1, 25, default)).Outcome);
        Assert.Equal(CollectionsOutcome.InvalidRequest, (await service.RecordsAsync(h.Manager, Guid.NewGuid(), "Delivered", null, 1, 25, default)).Outcome);
        Assert.Equal(CollectionsOutcome.InvalidRequest, (await service.ExportsAsync(h.Manager, new DateOnly(2026, 10, 5), new DateOnly(2026, 10, 1), 1, 25, default)).Outcome);
    }

    // ------------------------------------------------------------ read only

    [Fact]
    public async Task ReadingTheHistory_ChangesNothing_AndNeverCallsGenesysOrQueuesAnything()
    {
        using var h = await SeededAsync(2);
        var id = await SubmitAsync(h);
        var publicId = PublicId(h, id);
        var uploads = h.Client.Calls.Count; var lookups = h.Client.CampaignLookups.Count; var listChecks = h.Client.ListChecks.Count; var campaignChecks = h.Client.CampaignChecks.Count;
        var queued = h.Scheduler.Dispatches.Count; var refreshes = h.Scheduler.Refreshes.Count; var sweeps = h.Scheduler.Sweeps;
        async Task<int[]> RowCountsAsync()
        {
            using var c = h.CreateContext();
            return [await c.CollectionsDispatches.CountAsync(), await c.CollectionsDispatchItems.CountAsync(), await c.CollectionsGenesysBatches.CountAsync(), await c.AuditEntries.CountAsync(), await c.CollectionsReviewRuns.CountAsync()];
        }
        var rowsBefore = await RowCountsAsync();
        string StateOf(IEnumerable<CollectionsDispatchItem> items) => string.Join("|", items.OrderBy(i => i.CollectionsDispatchItemId).Select(i => $"{i.Status}:{i.GenesysContactId}:{i.StatusReason}"));
        string itemsBefore; using (var c = h.CreateContext()) itemsBefore = StateOf(c.CollectionsDispatchItems.AsNoTracking().ToList());

        for (var i = 0; i < 2; i++)
        {
            await ListAsync(h);
            await ListAsync(h, new GenesysHistoryFilter(Status: "Failed", Search: "Customer"));
            await DetailAsync(h, publicId);
            await RecordsAsync(h, publicId);
            using var c = h.CreateContext();
            await Service(h, c).ExportsAsync(h.Manager, null, null, 1, 25, default);
        }

        Assert.Equal((uploads, lookups, listChecks, campaignChecks), (h.Client.Calls.Count, h.Client.CampaignLookups.Count, h.Client.ListChecks.Count, h.Client.CampaignChecks.Count));
        Assert.Equal((queued, refreshes, sweeps), (h.Scheduler.Dispatches.Count, h.Scheduler.Refreshes.Count, h.Scheduler.Sweeps));
        Assert.Equal(rowsBefore, await RowCountsAsync());
        using var after = h.CreateContext();
        Assert.Equal(itemsBefore, StateOf(after.CollectionsDispatchItems.AsNoTracking().ToList()));
    }

    [Fact]
    public void TheHistoryCannotUploadOrResend_ByConstruction()
    {
        var forbidden = new[] { typeof(IGenesysOutboundClient), typeof(IReviewJobScheduler), typeof(IReviewStore), typeof(DispatchService), typeof(ReviewRefreshService), typeof(AutoDispatchService) };
        foreach (var type in new[] { typeof(GenesysHistoryService), typeof(TigerCS.Infrastructure.Modules.Collections.GenesysHistoryStore) })
            foreach (var parameter in type.GetConstructors().Single().GetParameters())
                Assert.DoesNotContain(parameter.ParameterType, forbidden);
        foreach (var method in typeof(IGenesysHistoryStore).GetMethods())
            Assert.DoesNotMatch("^(Add|Update|Delete|Set|Save|Try|Release|Enqueue|Upload|Resend|Retry|Cancel|Reconcile)", method.Name);

        var controller = typeof(TigerCS.Api.Controllers.CollectionsGenesysHistoryController);
        var actions = controller.GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.DeclaredOnly);
        Assert.NotEmpty(actions);
        // Every route is a GET except the manual Sync from Genesys, which only READS from Genesys and writes the local sync tables (see GenesysCampaignSyncServiceTests).
        var posts = actions.Where(m => m.GetCustomAttributes(typeof(Microsoft.AspNetCore.Mvc.HttpPostAttribute), false).Length > 0).ToList();
        Assert.Equal(["Sync"], posts.Select(m => m.Name));
        Assert.All(actions.Except(posts), m => Assert.NotNull(m.GetCustomAttributes(typeof(Microsoft.AspNetCore.Mvc.HttpGetAttribute), false).SingleOrDefault()));
        Assert.DoesNotContain(typeof(IReviewStore), typeof(TigerCS.Application.Modules.Collections.History.GenesysCampaignSyncService).GetConstructors().Single().GetParameters().Select(p => p.ParameterType));
        Assert.DoesNotContain(typeof(DispatchService), typeof(TigerCS.Application.Modules.Collections.History.GenesysCampaignSyncService).GetConstructors().Single().GetParameters().Select(p => p.ParameterType));
        Assert.DoesNotContain(typeof(IReviewJobScheduler), typeof(TigerCS.Application.Modules.Collections.History.GenesysCampaignSyncService).GetConstructors().Single().GetParameters().Select(p => p.ParameterType));
    }
}
