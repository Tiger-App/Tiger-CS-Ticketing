using Microsoft.Extensions.Logging.Abstractions;
using TigerCS.Application.Modules.Collections;
using TigerCS.Application.Modules.Collections.Abstractions;
using TigerCS.Application.Modules.Collections.Review;
using TigerCS.Application.Modules.Collections.Services;
using TigerCS.Domain.Modules.Collections;
using TigerCS.Domain.Modules.Collections.Review;
using TigerCS.Tests.IdentityAndAccess.Fakes;

namespace TigerCS.Tests.Collections.Review;

/// <summary>
/// Special case units (<c>SpecialCaseUnitCodes</c>) are handled outside the normal Collections campaigns: never listed, previewed, approved, exported or sent by Manual or Auto, whatever the UI
/// shows and whether the data was stored before or after they were configured, and also for a previously prepared approval. Only the listed units are excluded (not the customer's others), and the
/// financial source is untouched (Receivables still lists them). Everything runs against the fakes: nothing reaches Genesys.
/// </summary>
public sealed class SpecialCaseUnitTests
{
    // Unit 1 is the special one. Unit 2 is another customer. Unit 3 belongs to the SAME customer as unit 1 but is not listed.
    private static PactReceivableInstalment[] Rows() =>
    [
        ReviewHarness.Row(1, 500m), ReviewHarness.Row(2, 700m),
        ReviewHarness.Row(1, 300m, day: 25) with { UnitCode = "TP140-9", UnitId = 1009, VoucherNumber = "INV-9" }
    ];

    private static async Task<ReviewHarness> SeededAsync(Action<ReviewHarness>? configure = null)
    {
        var h = new ReviewHarness();                       // 14 Oct 2026: Current Month is scheduled
        configure?.Invoke(h);
        h.Source.Items.AddRange(Rows());
        await h.RefreshAsync();
        return h;
    }

    private static ReviewFilter Manual() => new(ReminderType: "CurrentMonth", SendingMode: "Manual", MinRemaining: 0m);
    private static ReviewFilter Auto() => new(ReminderType: "CurrentMonth", SendingMode: "Auto", MinRemaining: 0m);
    private static SelectionRequest All(ReviewFilter f) => new(f, ReviewQueryService.ModeAllMatching);

    private static async Task<string[]> ManualUnitsAsync(ReviewHarness h)
    {
        using var scope = h.NewScope();
        var page = (await scope.Query.QueryAsync(h.Manager, Manual(), 1, 100, CancellationToken.None)).Value!;
        return page.Items.Select(i => i.UnitCode).Order().ToArray();
    }

    // ------------------------------------------------------------ the unit code comparison

    [Theory]
    [InlineData("TP140-1", "TP140-1", true)]
    [InlineData("tp140-1", "TP140-1", true)]       // case
    [InlineData("  TP140-1  ", "TP140-1", true)]   // spaces
    [InlineData("TP140-1", "140-1", true)]         // with and without the TP prefix
    [InlineData("140-1", "TP140-1", true)]
    [InlineData("TP140-1", "TP140-10", false)]     // never a prefix match
    [InlineData("TP140-1", "TP141-1", false)]
    [InlineData("TP136-C-402", "tp136-c-402", true)]
    public void UnitCodesAreComparedInOneNormalizedForm(string configured, string unit, bool expected)
    {
        var special = new SpecialCaseUnits([configured]);
        Assert.Equal(expected, special.IsSpecial(unit));
    }

    [Fact]
    public void NothingConfiguredExcludesNothing_AndBlankEntriesAreIgnored()
    {
        Assert.False(SpecialCaseUnits.None.IsSpecial("TP140-1"));
        var blanks = new SpecialCaseUnits(["", "  ", null]);
        Assert.False(blanks.Any); Assert.False(blanks.IsSpecial("")); Assert.False(blanks.IsSpecial(null));
    }

    // ------------------------------------------------------------ Manual and Auto lists

    [Fact]
    public async Task Manual_ListsTheNonSpecialUnits_AndTheSameCustomersOtherUnitStaysVisible()
    {
        using var configured = await SeededAsync(h => h.Sql.SpecialCaseUnitCodes = ["TP140-1"]);
        Assert.Equal(["TP140-2", "TP140-9"], await ManualUnitsAsync(configured));                   // unit 1 is gone; unit 9 (same customer, not listed) and unit 2 stay
        using var none = await SeededAsync();
        Assert.Equal(["TP140-1", "TP140-2", "TP140-9"], await ManualUnitsAsync(none));              // not configured: nothing changes
    }

    [Fact]
    public async Task Manual_AlsoExcludesWhatWasStoredBeforeTheUnitWasConfigured()
    {
        using var h = await SeededAsync();                                                           // stored with unit 1 in it
        Assert.Contains("TP140-1", await ManualUnitsAsync(h));
        h.Sql.SpecialCaseUnitCodes = ["tp140-1 "];                                                    // configured afterwards, in another form of the code
        Assert.DoesNotContain("TP140-1", await ManualUnitsAsync(h));
        using var scope = h.NewScope();
        var summary = (await scope.Query.SummarizeAsync(h.Manager, All(Manual()), CancellationToken.None)).Value!;
        Assert.Equal(2, summary.Count); Assert.DoesNotContain(summary.Contacts, c => c.UnitCode == "TP140-1");
    }

    [Fact]
    public async Task ASpecialUnitIsNeverStoredAsAReviewRecord_WhenItIsConfiguredBeforeTheRefresh()
    {
        using var h = await SeededAsync(x => x.Sql.SpecialCaseUnitCodes = ["TP140-1"]);
        using var context = h.CreateContext();
        Assert.DoesNotContain(context.CollectionsReviewRecords, r => r.UnitCode == "TP140-1");
        Assert.Contains(context.CollectionsReviewRecords, r => r.UnitCode == "TP140-9");
    }

    [Fact]
    public async Task Auto_ExcludesTheSpecialUnit()
    {
        using var h = await SeededAsync(x => x.Sql.SpecialCaseUnitCodes = ["TP140-1"]);
        using var scope = h.NewScope();
        var summary = (await scope.Query.SummarizeAsync(h.Manager, All(Auto()), CancellationToken.None)).Value!;
        Assert.DoesNotContain(summary.Contacts, c => c.UnitCode == "TP140-1");
        Assert.Contains(summary.Contacts, c => c.UnitCode == "TP140-2");
    }

    [Fact]
    public async Task ADirectApiSelectionOfTheSpecialUnitFindsNothing_ThereIsNothingToApprove()
    {
        using var h = await SeededAsync(x => x.Sql.SpecialCaseUnitCodes = ["TP140-1"]);
        using var scope = h.NewScope();
        var request = new SelectionRequest(Manual(), ReviewQueryService.ModeSelectedUnits, null, null, 1, 50, [ReviewQueryService.UnitKey(4, "T0001", "TP140-1")]);
        var summary = await scope.Query.SummarizeAsync(h.Manager, request, CancellationToken.None);
        Assert.True(!summary.IsSuccess || summary.Value!.Count == 0);
    }

    // ------------------------------------------------------------ Campaigns preview

    [Fact]
    public async Task TheCampaignsPreview_ExcludesTheSpecialUnit_ButNotTheOthers()
    {
        using var h = await SeededAsync(x => x.Sql.SpecialCaseUnitCodes = ["TP140-1"]);
        var campaigns = new CollectionsCampaignAppService(h.Options, h.Campaign, h.Sql, new(h.Options, new FakeDepartmentRepository()), new(h.Options, h.Time), h.Source,
            NullLogger<CollectionsCampaignAppService>.Instance);
        var preview = (await campaigns.PreviewAsync(h.Manager, "CurrentMonthReminder", minTotal: 0m, paymentStatus: "Both")).Value!;
        Assert.DoesNotContain(preview.Items, i => i.UnitCode == "TP140-1");
        Assert.Contains(preview.Items, i => i.UnitCode == "TP140-2"); Assert.Contains(preview.Items, i => i.UnitCode == "TP140-9");
    }

    // ------------------------------------------------------------ Export and Send

    [Fact]
    public async Task ExportForGenesys_LeavesTheSpecialUnitOutOfTheFile()
    {
        using var h = await SeededAsync();
        h.Sql.SpecialCaseUnitCodes = ["TP140-1"];                                                    // stored before, configured after
        using var scope = h.NewScope();
        var file = (await scope.Export.ExportAsync(h.Manager, All(Manual()), CancellationToken.None)).Value!;
        Assert.Equal(2, file.RowCount);
        Assert.DoesNotContain("500.00", file.Csv);                                                   // unit 1's amount is not in the file
        Assert.Contains("700.00", file.Csv);
    }

    [Fact]
    public async Task SendToGenesys_NeverUploadsTheSpecialUnit_NotEvenFromAnApprovalPreparedBeforeItWasConfigured()
    {
        using var h = await SeededAsync();
        var (summary, confirm) = await h.PrepareAsync(All(Manual()), "special");                    // prepared and confirmed while unit 1 was still allowed
        Assert.Equal(3, summary.Count);
        using (var scope = h.NewScope()) Assert.Equal(CollectionsOutcome.Accepted, (await scope.Dispatch.ConfirmAsync(h.Manager, confirm, CancellationToken.None)).Outcome);
        h.Sql.SpecialCaseUnitCodes = ["TP140-1"];                                                    // configured before the upload
        using (var scope = h.NewScope()) await scope.Dispatch.ExecuteAsync(h.Scheduler.Dispatches.Single(), CancellationToken.None);
        var upload = Assert.Single(h.Client.Calls);
        Assert.Equal(2, upload.Contacts.Count); Assert.DoesNotContain(upload.Contacts, c => c.AmountDue == "500.00");   // the other customer and the same customer's unit 9 are sent
        using var context = h.CreateContext();
        var item = Assert.Single(context.CollectionsDispatchItems, i => i.UnitCode == "TP140-1");
        Assert.Equal(DispatchItemStatus.Excluded, item.Status); Assert.Contains("Special case", item.StatusReason);
    }

    [Fact]
    public async Task ANewApprovalNeverContainsTheSpecialUnit()
    {
        using var h = await SeededAsync(x => x.Sql.SpecialCaseUnitCodes = ["TP140-1"]);
        var (summary, confirm) = await h.PrepareAsync(All(Manual()), "new");
        Assert.Equal(2, summary.Count);
        using (var scope = h.NewScope()) Assert.Equal(CollectionsOutcome.Accepted, (await scope.Dispatch.ConfirmAsync(h.Manager, confirm, CancellationToken.None)).Outcome);
        using var context = h.CreateContext();
        Assert.DoesNotContain(context.CollectionsDispatchItems, i => i.UnitCode == "TP140-1");
        Assert.Empty(h.Client.Calls);                                                                // approving never sends
    }
}
