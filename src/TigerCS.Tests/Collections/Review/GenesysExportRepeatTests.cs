using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using TigerCS.Application.Modules.Collections;
using TigerCS.Application.Modules.Collections.Abstractions;
using TigerCS.Application.Modules.Collections.Review;
using TigerCS.Application.Modules.Collections.Services;
using TigerCS.Domain.Audit;
using TigerCS.Domain.Modules.IdentityAndAccess;
using TigerCS.Domain.Modules.Collections.Review;
using TigerCS.Application.Abstractions;
using TigerCS.Infrastructure.Modules.Collections;
using TigerCS.Infrastructure.Persistence;
using TigerCS.Tests.IdentityAndAccess.Fakes;

namespace TigerCS.Tests.Collections.Review;

/// <summary>
/// Repeated Export for Genesys, the "not verified" label, and the Manual review export. A file export proves nothing about delivery, so it never marks anyone as sent: repeats are noticed through the
/// audit trail and confirmed by a person, and the protections that belong to a real send (already sent, contacted today, idempotency) are exactly what they were.
/// </summary>
public sealed class GenesysExportRepeatTests
{
    private static async Task<ReviewHarness> SeededAsync(Action<ReviewHarness>? configure = null)
    {
        var h = new ReviewHarness();
        h.Time.Advance(TimeSpan.FromDays(1)); h.Source.ReadAt = ReviewHarness.Now.AddDays(1);
        configure?.Invoke(h);
        h.Source.Items.AddRange([ReviewHarness.Row(1, 5000m, day: 20), ReviewHarness.Row(2, 500m, day: 20), ReviewHarness.Row(3, 700m, day: 20)]);
        await h.RefreshAsync();
        return h;
    }

    private static ReviewFilter Manual(string type = "CurrentMonth") => new(ReminderType: type, SendingMode: "Manual", MinRemaining: 0m);
    private static SelectionRequest All() => new(Manual(), ReviewQueryService.ModeAllMatching);
    private static SelectionRequest Ticked(params string[] units) =>
        new(Manual(), ReviewQueryService.ModeSelectedUnits, null, null, 1, 50, units.Select(u => ReviewQueryService.UnitKey(4, "T000" + u[^1], u)).ToList());

    private static async Task<CollectionsResult<GenesysExportDto>> ExportAsync(ReviewHarness h, SelectionRequest request, bool confirm = false, CollectionsCaller? caller = null)
    {
        using var scope = h.NewScope();
        return await scope.Export.ExportAsync(caller ?? h.Manager, request, CancellationToken.None, confirm);
    }

    private static async Task<List<AuditEntry>> AuditAsync(ReviewHarness h, string action)
    {
        using var context = h.CreateContext();
        return await context.AuditEntries.AsNoTracking().Where(a => a.Action == action).OrderBy(a => a.AuditEntryId).ToListAsync();
    }

    // ------------------------------------------------------------ every export is audited; repeats are noticed

    [Fact]
    public async Task EveryExport_IsAudited_WithOneWayFingerprintsAndNoCustomerData()
    {
        using var h = await SeededAsync();
        Assert.True((await ExportAsync(h, Ticked("TP140-1"))).IsSuccess);
        Assert.True((await ExportAsync(h, Ticked("TP140-2", "TP140-3"))).IsSuccess);        // different customers: no warning
        var audit = await AuditAsync(h, GenesysExportService.AuditAction);
        Assert.Equal(2, audit.Count);
        Assert.All(audit, a => { Assert.Equal(h.Manager.EmployeeId, a.ActorEmployeeId); Assert.Contains("Repeated=0", a.AfterValue); Assert.Contains(";Customers=", a.AfterValue); });
        foreach (var personal in new[] { "Customer 1", "Customer 2", "+97150", "example.test", "T0001", "TP140-1" }) Assert.All(audit, a => Assert.DoesNotContain(personal, a.AfterValue));
        Assert.Single(audit[0].AfterValue!.Split(";Customers=")[1].Split(','));                // one fingerprint per customer
        Assert.Equal(2, audit[1].AfterValue!.Split(";Customers=")[1].Split(',').Length);
    }

    [Fact]
    public async Task ExportingTheSameCustomersAgain_IsHeldUntilConfirmed_AndNothingIsReleasedOrRecordedMeanwhile()
    {
        using var h = await SeededAsync();
        Assert.True((await ExportAsync(h, All())).IsSuccess);
        var again = await ExportAsync(h, All());
        Assert.Equal(CollectionsOutcome.RepeatExport, again.Outcome); Assert.Null(again.Value);
        Assert.Contains("3 of the 3 customers", again.Detail); Assert.Contains("already exported", again.Detail); Assert.Contains("does not mark anyone as sent", again.Detail);
        Assert.Single(await AuditAsync(h, GenesysExportService.AuditAction));                // the held attempt wrote no export record
    }

    [Fact]
    public async Task AConfirmedRepeat_ReleasesTheFile_AndIsAuditedAsARepeat()
    {
        using var h = await SeededAsync();
        await ExportAsync(h, All());
        var dto = (await ExportAsync(h, All(), confirm: true)).Value!;
        Assert.Equal(3, dto.RepeatedCustomers); Assert.NotNull(dto.LastExportedAtUtc); Assert.Contains(dto.Notices, n => n.Contains("exported before"));
        Assert.Equal(3, dto.RowCount);
        var last = (await AuditAsync(h, GenesysExportService.AuditAction)).Last();
        Assert.Contains("Repeated=3;RepeatConfirmed=true", last.AfterValue);
    }

    [Fact]
    public async Task OnlyTheCustomersExportedBefore_AreCounted_AndOtherCustomersAreNotWarnedAbout()
    {
        using var h = await SeededAsync();
        await ExportAsync(h, Ticked("TP140-1"));
        var mixed = await ExportAsync(h, All());
        Assert.Equal(CollectionsOutcome.RepeatExport, mixed.Outcome); Assert.Contains("1 of the 3 customers", mixed.Detail);
        Assert.True((await ExportAsync(h, Ticked("TP140-2", "TP140-3"))).IsSuccess);           // none of them was exported
    }

    [Fact]
    public async Task ARepeatIsNoticedForAnotherContactListAndAnotherUser_Too()
    {
        using var h = await SeededAsync();
        await ExportAsync(h, All());
        using var scope = h.NewScope();
        var otherList = await scope.Export.ExportAsync(h.Manager, new SelectionRequest(Manual() with { ContactList = "FollowUp" }, ReviewQueryService.ModeAllMatching), CancellationToken.None);
        Assert.Equal(CollectionsOutcome.RepeatExport, otherList.Outcome);
        var otherUser = new CollectionsCaller(Guid.NewGuid(), [Roles.CsManager], []);
        Assert.Equal(CollectionsOutcome.RepeatExport, (await ExportAsync(h, All(), caller: otherUser)).Outcome);
    }

    [Fact]
    public async Task ARepeat_IsNoticedAcrossCampaignTypes_BecauseItIsTheSameCustomer()
    {
        // The same customers under another campaign type are a different record, but the same people: the second file would reach them twice.
        using var h = await SeededAsync();
        Assert.True((await ExportAsync(h, All())).IsSuccess);
        var other = await ExportAsync(h, new SelectionRequest(Manual("FollowUp"), ReviewQueryService.ModeAllMatching));
        Assert.Equal(CollectionsOutcome.RepeatExport, other.Outcome); Assert.Contains("3 of the 3 customers", other.Detail);
    }

    [Fact]
    public async Task IfTheHistoryIsTooLongToCheckInFull_NoFileIsMade()
    {
        // An earlier export beyond what is read could repeat a customer, so the export stops rather than guessing.
        using var h = await SeededAsync();
        var result = await ExportWithHistoryAsync(h, new FullHistory());
        Assert.Equal(CollectionsOutcome.FinanceUnavailable, result.Outcome); Assert.Null(result.Value); Assert.Contains("cannot cover them all", result.Detail);
    }

    [Fact]
    public async Task TheRepeatCheck_CanBeSwitchedOff_AndAnUnreadableHistoryStopsTheExport()
    {
        using var off = await SeededAsync(x => x.Review.ExportRepeatLookbackDays = 0);
        await ExportAsync(off, All());
        Assert.True((await ExportAsync(off, All())).IsSuccess);                               // 0 = no check

        using var h = await SeededAsync();
        using var context = h.CreateContext();
        using var scope = h.NewScope();
        var broken = new GenesysExportService(h.Options, h.Genesys, new CollectionsAuthorizationService(h.Options, new FakeDepartmentRepository()), new CollectionsClock(h.Options, h.Time),
            scope.Query, h.Client, new TigerCS.Infrastructure.Audit.AuditEntryWriter(context, h.Time), new TigerCS.Infrastructure.Modules.Collections.CollectionsUnitOfWork(context), h.Review,
            new ThrowingHistory(), new ReviewHarness.FreeExportLock(), NullLogger<GenesysExportService>.Instance);
        var result = await broken.ExportAsync(h.Manager, All(), CancellationToken.None);
        Assert.Equal(CollectionsOutcome.FinanceUnavailable, result.Outcome); Assert.Null(result.Value); Assert.Contains("no file was made", result.Detail);
        Assert.Empty(await AuditAsync(h, GenesysExportService.AuditAction));
    }

    private sealed class ThrowingHistory : IGenesysExportHistory
    {
        public Task<IReadOnlyList<GenesysExportAuditRecord>> ListExportsAsync(string action, DateTime sinceUtc, int take, CancellationToken ct) => throw new InvalidOperationException("audit unavailable");
    }

    /// <summary>Answers with as many entries as asked: the window is full.</summary>
    private sealed class FullHistory : IGenesysExportHistory
    {
        public Task<IReadOnlyList<GenesysExportAuditRecord>> ListExportsAsync(string action, DateTime sinceUtc, int take, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<GenesysExportAuditRecord>>(Enumerable.Range(0, take).Select(_ => new GenesysExportAuditRecord(sinceUtc, null, "Filter={}")).ToList());
    }

    private static async Task<CollectionsResult<GenesysExportDto>> ExportWithHistoryAsync(ReviewHarness h, IGenesysExportHistory history)
    {
        using var context = h.CreateContext();
        using var scope = h.NewScope();
        var service = new GenesysExportService(h.Options, h.Genesys, new CollectionsAuthorizationService(h.Options, new FakeDepartmentRepository()), new CollectionsClock(h.Options, h.Time),
            scope.Query, h.Client, new TigerCS.Infrastructure.Audit.AuditEntryWriter(context, h.Time), new TigerCS.Infrastructure.Modules.Collections.CollectionsUnitOfWork(context), h.Review,
            history, new ReviewHarness.FreeExportLock(), NullLogger<GenesysExportService>.Instance);
        return await service.ExportAsync(h.Manager, All(), CancellationToken.None);
    }

    // ------------------------------------------------------------ an export is not a send

    [Fact]
    public async Task AnExport_NeverMarksAnyoneAsSent_AndTheSendProtectionsAreUntouched()
    {
        using var h = await SeededAsync();
        await ExportAsync(h, All());
        await ExportAsync(h, All(), confirm: true);
        using (var scope = h.NewScope())
        {
            Assert.Empty(await scope.Store.ListDispatchesAsync(10, CancellationToken.None));     // no dispatch, no batch
            var page = (await scope.Query.QueryAsync(h.Manager, Manual(), 1, 50, CancellationToken.None)).Value!;
            Assert.Equal(3, page.Counts.Ready); Assert.All(page.Items, i => { Assert.Equal("Ready", i.ValidationStatus); Assert.Null(i.PreviousDispatchStatus); });
        }
        Assert.Empty(h.Client.Calls);                                                           // nothing was uploaded

        // A real send of the exported customers still works, once. It is then the send that protects against a duplicate.
        var (_, confirm) = await h.PrepareAsync(All(), "after-export");
        using (var scope = h.NewScope())
        {
            Assert.Equal(CollectionsOutcome.Accepted, (await scope.Dispatch.ConfirmAsync(h.Manager, confirm, CancellationToken.None)).Outcome);
            await scope.Dispatch.ExecuteAsync(h.Scheduler.Dispatches.Single(), CancellationToken.None);
        }
        Assert.Equal(3, h.Client.Calls.Single().Contacts.Count);
        var afterSend = await ExportAsync(h, All(), confirm: true);                            // even a confirmed repeat cannot export customers who are now sent
        Assert.False(afterSend.IsSuccess); Assert.Contains("No Ready customers", afterSend.Detail);
        using var again = h.NewScope();
        var second = await again.Dispatch.ConfirmAsync(h.Manager, confirm with { IdempotencyKey = "second-try" }, CancellationToken.None);
        Assert.False(second.IsSuccess);                                                         // already sent: duplicate-send protection
    }

    [Fact]
    public async Task ExportingDoesNotUseUpTheDayForARealSend()
    {
        using var h = await SeededAsync();
        await ExportAsync(h, All());
        await ExportAsync(h, All(), confirm: true);
        var (summary, confirm) = await h.PrepareAsync(All(), "send-after-exports");
        Assert.Equal(3, summary.Count);                                                         // contact frequency (ContactedToday) only counts real uploads
        using var scope = h.NewScope();
        Assert.True((await scope.Dispatch.ConfirmAsync(h.Manager, confirm, CancellationToken.None)).IsSuccess);
    }

    [Fact]
    public async Task TheLookBackIsNinetyDays_SoAnExportFromTwoMonthsAgoIsStillARepeat_AndAnOlderOneIsNot()
    {
        // A monthly reminder cycle contacts the same customers again the next month: the look-back must span that, not stop at 30 days.
        using var h = await SeededAsync();
        Assert.True((await ExportAsync(h, All())).IsSuccess);
        await AgeExportsAsync(h, days: 60);
        Assert.Equal(CollectionsOutcome.RepeatExport, (await ExportAsync(h, All())).Outcome);
        await AgeExportsAsync(h, days: 100);
        Assert.True((await ExportAsync(h, All())).IsSuccess);
    }

    /// <summary>Moves the recorded export audit entries back in time, as if they had been made that many days ago.</summary>
    private static async Task AgeExportsAsync(ReviewHarness h, int days)
    {
        using var context = h.CreateContext();
        var when = h.Time.GetUtcNow().UtcDateTime.AddDays(-days);
        await context.Database.ExecuteSqlInterpolatedAsync($"UPDATE \"AuditEntries\" SET \"OccurredAtUtc\" = {when} WHERE \"Action\" = {GenesysExportService.AuditAction}");
    }

    [Fact]
    public async Task TheAuditIsWrittenWhileTheExportLockIsHeld_AndTheLockIsReleasedAfterwards()
    {
        using var h = await SeededAsync();
        using var context = h.CreateContext();
        using var scope = h.NewScope();
        var gate = new RecordingLock();
        var audit = new HeldWhenAudited(new TigerCS.Infrastructure.Audit.AuditEntryWriter(context, h.Time), gate);
        var service = Service(h, scope.Query, context, gate, audit);
        Assert.True((await service.ExportAsync(h.Manager, All(), CancellationToken.None)).IsSuccess);
        Assert.Equal(new[] { true }, audit.HeldAtEachWrite);                  // the repeat check and the audit happen inside the lock
        Assert.False(gate.Held);                                              // and the lock is released once the audit is written
    }

    [Fact]
    public async Task WhileAnotherExportIsRecording_ANewExportCannotGetIn_AndNothingIsReleased()
    {
        using var h = await SeededAsync();
        using var context = h.CreateContext();
        using var scope = h.NewScope();
        var result = await Service(h, scope.Query, context, new BusyLock()).ExportAsync(h.Manager, All(), CancellationToken.None);
        Assert.Equal(CollectionsOutcome.FinanceUnavailable, result.Outcome); Assert.Null(result.Value); Assert.Contains("Try again", result.Detail);
        Assert.Empty(await AuditAsync(h, GenesysExportService.AuditAction));                 // no audit row, so no file
    }

    private static GenesysExportService Service(ReviewHarness h, ReviewQueryService query, TigerCsDbContext context, IGenesysExportLock gate, IAuditEntryWriter? audit = null) =>
        new(h.Options, h.Genesys, new CollectionsAuthorizationService(h.Options, new FakeDepartmentRepository()), new CollectionsClock(h.Options, h.Time), query, h.Client,
            audit ?? new TigerCS.Infrastructure.Audit.AuditEntryWriter(context, h.Time), new TigerCS.Infrastructure.Modules.Collections.CollectionsUnitOfWork(context), h.Review,
            new ReviewStore(context, h.RetryPolicy), gate, NullLogger<GenesysExportService>.Instance);

    /// <summary>Records whether the lock is held at each audit write.</summary>
    private sealed class HeldWhenAudited(IAuditEntryWriter inner, RecordingLock gate) : IAuditEntryWriter
    {
        public List<bool> HeldAtEachWrite { get; } = [];

        public Task WriteAsync(Guid? actorEmployeeId, string action, string entityType, string? entityId, string? beforeValue, string? afterValue, Guid correlationId, CancellationToken cancellationToken = default)
        {
            HeldAtEachWrite.Add(gate.Held);
            return inner.WriteAsync(actorEmployeeId, action, entityType, entityId, beforeValue, afterValue, correlationId, cancellationToken);
        }
    }

    private sealed class RecordingLock : IGenesysExportLock
    {
        public bool Held { get; private set; }

        public Task<IAsyncDisposable?> TryAcquireAsync(TimeSpan wait, CancellationToken ct)
        {
            Held = true;
            return Task.FromResult<IAsyncDisposable?>(new Release(this));
        }

        private sealed class Release(RecordingLock owner) : IAsyncDisposable
        {
            public ValueTask DisposeAsync()
            {
                owner.Held = false;
                return ValueTask.CompletedTask;
            }
        }
    }

    /// <summary>A lock another export holds for the whole wait.</summary>
    private sealed class BusyLock : IGenesysExportLock
    {
        public Task<IAsyncDisposable?> TryAcquireAsync(TimeSpan wait, CancellationToken ct) => Task.FromResult<IAsyncDisposable?>(null);
    }

    // ------------------------------------------------------------ the label when the schema was not verified

    [Fact]
    public async Task WithoutSchemaVerification_TheFileIsLabelled_TemplateNotVerifiedAgainstGenesys_ButStaysTheSixColumnTemplate()
    {
        using var h = await SeededAsync();                                                       // no OAuth credentials in the harness
        var dto = (await ExportAsync(h, All())).Value!;
        Assert.False(dto.SchemaVerified);
        Assert.StartsWith("Template not verified against Genesys", dto.Notices[0]);
        Assert.EndsWith("(Template not verified against Genesys).csv", dto.FileName);
        Assert.Equal(["Phone", "CustomerName", "Email Address", "ReminderType", "AmountDue", "DueDate"], GenesysContactTemplate.ParseCsv(dto.Csv)[0]);
        Assert.DoesNotContain("not verified", dto.Csv, StringComparison.OrdinalIgnoreCase);       // the file itself is import-ready
        Assert.True(GenesysContactTemplate.Verify(dto.Csv, GenesysContactTemplate.ExpectedReminderTypes(h.Genesys)).IsMatch);
        Assert.Contains("Template not verified against Genesys", (await AuditAsync(h, GenesysExportService.AuditAction)).Single().AfterValue);
    }

    [Fact]
    public async Task WhenTheSchemaWasVerified_ThereIsNoSuchLabel()
    {
        using var h = await SeededAsync(x => { x.Genesys.ClientId = "client"; x.Genesys.ClientSecret = "secret"; });
        var dto = (await ExportAsync(h, All())).Value!;
        Assert.True(dto.SchemaVerified); Assert.Empty(dto.Notices); Assert.DoesNotContain("not verified", dto.FileName, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Template not verified", (await AuditAsync(h, GenesysExportService.AuditAction)).Single().AfterValue);
    }

    // ------------------------------------------------------------ Manual "Export review CSV"

    private static async Task<CollectionsResult<ReviewExportDto>> ReviewAsync(ReviewHarness h, ReviewFilter? filter, CollectionsCaller? caller = null)
    {
        using var scope = h.NewScope();
        return await scope.ReviewExport.ExportAsync(caller ?? h.Manager, filter, CancellationToken.None);
    }

    [Fact]
    public async Task ManualReviewExport_HoldsEveryCustomerOfTheList_WithTheFilteredAmountStatusAndReason()
    {
        using var h = await SeededAsync();
        h.Source.Items.Add(ReviewHarness.Row(4, 300m, day: 20, mobile: "971500000009")); h.Source.Items.Add(ReviewHarness.Row(5, 400m, day: 20, mobile: "971500000009"));
        await h.RefreshAsync();
        var dto = (await ReviewAsync(h, Manual())).Value!;
        var rows = GenesysContactTemplate.ParseCsv(dto.Csv);
        Assert.Equal("Customer", rows[0][0]); Assert.Equal(5, dto.RowCount); Assert.Equal(3, dto.ReadyCount); Assert.Equal(6, rows.Count);
        var status = rows[0].IndexOf("Status"); var reason = rows[0].IndexOf("Reasons"); var amount = rows[0].IndexOf("Amount");
        Assert.Equal(2, rows.Skip(1).Count(r => r[status] != "Ready" && r[reason].Length > 0));       // held back: the exact reason is in the file
        using var scope = h.NewScope();
        var preview = (await scope.Query.QueryAsync(h.Manager, Manual(), 1, 50, CancellationToken.None)).Value!;
        Assert.Equal(preview.Items.Select(i => i.RemainingAmount!.Value.ToString("0.00")).Order(), rows.Skip(1).Select(r => r[amount]).Order());   // the amounts the page shows
    }

    [Fact]
    public async Task ManualReviewExport_FollowsTheFilters_NeedsTheSendRight_AndIsAudited()
    {
        using var h = await SeededAsync();
        Assert.Equal(2, (await ReviewAsync(h, Manual() with { MinRemaining = 600m })).Value!.RowCount);                    // Minimum Total narrows it like the page
        Assert.Equal(CollectionsOutcome.Forbidden, (await ReviewAsync(h, Manual(), h.Agent)).Outcome);
        Assert.Equal(CollectionsOutcome.Forbidden, (await ReviewAsync(h, Manual(), h.Reporting)).Outcome);
        Assert.Equal(CollectionsOutcome.InvalidRequest, (await ReviewAsync(h, new ReviewFilter(ReminderType: "CurrentMonth", SendingMode: "Auto"))).Outcome);
        var audit = (await AuditAsync(h, ManualReviewExportService.AuditAction)).Single();
        Assert.Contains("Rows=2", audit.AfterValue); Assert.Contains("Sha256=", audit.AfterValue);
        foreach (var personal in new[] { "Customer 1", "+97150", "example.test" }) Assert.DoesNotContain(personal, audit.AfterValue);
    }

    [Fact]
    public async Task ManualReviewExport_NeedsApprovalData_AndNeutralizesSpreadsheetFormulas()
    {
        using var empty = new ReviewHarness();
        Assert.Equal(CollectionsOutcome.ReviewRequired, (await ReviewAsync(empty, Manual())).Outcome);

        using var h = await SeededAsync();
        h.Source.Items.Clear(); h.Source.Items.Add(ReviewHarness.Row(1, 100m, day: 20, name: "=HYPERLINK(\"x\")"));
        await h.RefreshAsync();
        var csv = (await ReviewAsync(h, Manual())).Value!.Csv;
        Assert.DoesNotContain(",\"=HYPERLINK", csv); Assert.Contains("\"'=HYPERLINK", csv);
        Assert.Empty(await AuditAsync(h, GenesysExportService.AuditAction));                      // not a Genesys export
    }
}
