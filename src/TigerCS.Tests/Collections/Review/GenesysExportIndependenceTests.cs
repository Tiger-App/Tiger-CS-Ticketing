using System.Globalization;
using Microsoft.EntityFrameworkCore;
using TigerCS.Application.Modules.Collections.Abstractions;
using TigerCS.Application.Modules.Collections.Review;
using TigerCS.Application.Modules.Collections.Services;
using TigerCS.Domain.Modules.Collections.Review;

namespace TigerCS.Tests.Collections.Review;

/// <summary>
/// Export for Genesys is independent of sending eligibility: in both modes it holds every customer matching the filters, whatever the status of the record or of the data, and what is
/// unverified is labelled instead of refused. The send path keeps every rule. The harness clock is 15 Oct 2026 (no stage is scheduled).
/// </summary>
public sealed class GenesysExportIndependenceTests
{
    private static async Task<ReviewHarness> SeededAsync(Action<ReviewHarness>? configure = null, params PactReceivableInstalment[] rows)
    {
        var h = new ReviewHarness();
        h.Time.Advance(TimeSpan.FromDays(1)); h.Source.ReadAt = ReviewHarness.Now.AddDays(1);
        configure?.Invoke(h);
        h.Source.Items.AddRange(rows);
        await h.RefreshAsync();
        return h;
    }

    /// <summary>Customer 1 is Ready; customer 2's payment status cannot be confirmed (no plan amount: PaymentStatusUnknown, Needs review); customer 3 has no callable number.</summary>
    private static PactReceivableInstalment[] Mixed() =>
    [
        ReviewHarness.Row(1, 5000m, day: 20), ReviewHarness.Row(2, 700m, day: 20) with { PlanAmount = null }, ReviewHarness.Row(3, 900m, day: 20, mobile: "123")
    ];

    private static ReviewFilter Filter(string mode, string type = "CurrentMonth", Func<ReviewFilter, ReviewFilter>? with = null)
    {
        var filter = new ReviewFilter(ReminderType: type, SendingMode: mode, MinRemaining: 0m);
        return with is null ? filter : with(filter);
    }

    private static SelectionRequest All(ReviewFilter filter) => new(filter, ReviewQueryService.ModeAllMatching);

    private static async Task<CollectionsResult<GenesysExportDto>> ExportAsync(ReviewHarness h, SelectionRequest request, IGenesysOutboundClient? client = null)
    {
        using var scope = h.NewScope(client);
        return await scope.Export.ExportAsync(h.Manager, request, CancellationToken.None);
    }

    private static async Task<string> AuditAsync(ReviewHarness h)
    {
        using var context = h.CreateContext();
        return (await context.AuditEntries.AsNoTracking().Where(a => a.Action == GenesysExportService.AuditAction).OrderBy(a => a.AuditEntryId).LastAsync()).AfterValue!;
    }

    private static List<List<string>> Rows(GenesysExportDto dto) => GenesysContactTemplate.ParseCsv(dto.Csv);

    // ------------------------------------------------------------ every status, both modes

    [Theory]
    [InlineData("Manual")]
    [InlineData("Auto")]
    public async Task InBothModes_ReadyAndNeedsReviewCustomersAreInTheFile_WithTheAmountsOfThePreview_AndUnverifiedDataIsLabelled(string mode)
    {
        using var h = await SeededAsync(null, Mixed());
        var dto = (await ExportAsync(h, All(Filter(mode)))).Value!;
        var rows = Rows(dto);
        Assert.Equal(2, dto.RowCount); Assert.Equal(["Customer 1", "Customer 2"], rows.Skip(1).Select(r => r[1]));
        Assert.Equal(["5000.00", "700.00"], rows.Skip(1).Select(r => r[4]));
        Assert.Equal(new Dictionary<string, int> { ["Ready"] = 1, ["NeedsReview"] = 1 }, dto.IncludedByStatus);
        Assert.False(dto.DataVerified); Assert.Equal(["PaymentStatusUnknown:1"], dto.UnverifiedReasons);
        Assert.Contains(dto.Notices, n => n.Contains("1 unit(s) have an unconfirmed payment status (PaymentStatusUnknown)") && n.Contains("not classified as unpaid"));
        Assert.Contains(dto.Notices, n => n.Contains("1 of the 2 customer unit(s) in this file are held back from sending (Needs review 1)"));
        Assert.Equal([(GenesysExportRows.NoCallablePhone, 1)], dto.Exclusions!.Select(e => (e.Code, e.Units)));
        Assert.EndsWith($" ({GenesysExportService.NotVerifiedLabel}) ({GenesysExportService.UnverifiedDataLabel}).csv", dto.FileName);
        var audit = await AuditAsync(h);
        Assert.Contains($"Mode={mode}", audit); Assert.Contains("IncludedByStatus=Ready:1,NeedsReview:1", audit); Assert.Contains("Unverified=PaymentStatusUnknown:1", audit);
        Assert.Contains("Excluded=1;Exclusions=NoCallablePhone:1", audit); Assert.Contains("Total=AED 5700.00", audit);

        // Preview parity: the Manual page lists the same customers with the same amounts, including the one that cannot be sent.
        using var scope = h.NewScope();
        var page = (await scope.Query.QueryAsync(h.Manager, Filter("Manual"), 1, 50, CancellationToken.None)).Value!;
        Assert.Equal(3, page.Counts.Total); Assert.Equal(1, page.Counts.Ready);
        Assert.Equal(page.Items.Where(i => i.Phone.Length > 0).Select(i => i.RemainingAmount!.Value.ToString("0.00", CultureInfo.InvariantCulture)), rows.Skip(1).Select(r => r[4]));
        // Send eligibility is unchanged: only the Ready customer can be approved.
        var summary = (await scope.Query.SummarizeAsync(h.Manager, All(Filter("Manual")), CancellationToken.None)).Value!;
        Assert.Equal(1, summary.Count); Assert.Equal("Customer 1", summary.Contacts.Single().CustomerName);
    }

    [Theory]
    [InlineData("Manual")]
    [InlineData("Auto")]
    public async Task InBothModes_AFailedReconciliation_IsLabelled_NotABlock_AndSendingStaysRefused(string mode)
    {
        using var h = await SeededAsync(x => x.Campaign.FinancialSourceValidated = false, ReviewHarness.Row(1, 5000m, day: 20));
        var dto = (await ExportAsync(h, All(Filter(mode)))).Value!;
        Assert.Equal(1, dto.RowCount); Assert.False(dto.DataVerified); Assert.Contains(ReviewReasons.SourceReconciliationRequired, dto.UnverifiedReasons!);
        Assert.Contains("DataVerified=False", await AuditAsync(h));
        using var scope = h.NewScope();
        Assert.Equal(0, (await scope.Query.SummarizeAsync(h.Manager, All(Filter("Manual")), CancellationToken.None)).Value!.Count);   // nothing can be sent
    }

    [Fact]
    public async Task AHeldBackLegalNotice_IsInTheFile_CountedAsHeldBack_AndStillCannotBeSent()
    {
        var notice = ReviewHarness.Row(2, 2000m) with { DueDate = new DateTime(2026, 9, 10) };
        using var h = await SeededAsync(x => x.Campaign.LegalNoticeExportEnabled = false, notice);
        var dto = (await ExportAsync(h, All(Filter("Manual", "LegalNotice", f => f with { ContactList = "LegalNotice" })))).Value!;
        Assert.Equal(1, dto.RowCount); Assert.Equal(1, dto.IncludedByStatus!["Excluded"]); Assert.Equal("Legal Notice", Rows(dto)[1][3]);
        Assert.True(dto.DataVerified);                                                           // a policy hold is not unverified data; it is said separately
        Assert.Contains(dto.Notices, n => n.Contains("held back from sending (Held back 1)"));
        using var scope = h.NewScope();
        Assert.Equal(0, (await scope.Query.SummarizeAsync(h.Manager, All(Filter("Manual", "LegalNotice")), CancellationToken.None)).Value!.Count);
    }

    [Fact]
    public async Task ARecordWhoseAmountCannotBeCalculated_IsCountedOut_NoValueIsInvented()
    {
        // Customer 2: two different instalments on one due date (AmbiguousInstalments, the calculation refuses to sum them). Customer 3: stored instalments missing (data written before the column).
        var rows = new[]
        {
            ReviewHarness.Row(1, 5000m, day: 20), ReviewHarness.Row(2, 700m, day: 20), ReviewHarness.Row(2, 300m, day: 20) with { VoucherNumber = "INV-2B" }, ReviewHarness.Row(3, 900m, day: 20)
        };
        using var h = await SeededAsync(null, rows);
        using (var context = h.CreateContext())
            await context.Database.ExecuteSqlInterpolatedAsync($"UPDATE \"CollectionsReviewRecords\" SET \"InstalmentsJson\" = NULL WHERE \"TenantId\" = {"T0003"}");
        var dto = (await ExportAsync(h, All(Filter("Manual")))).Value!;
        Assert.Equal(1, dto.RowCount); Assert.Equal("Customer 1", Rows(dto)[1][1]); Assert.Equal(5000m, dto.TotalAmount);
        Assert.Equal([(GenesysExportRows.AmountUnavailable, 2)], dto.Exclusions!.Select(e => (e.Code, e.Units)));
        Assert.Contains(dto.Notices, n => n.Contains("2 of the 3 matching customer unit(s) are not in the file") && n.Contains("no value was invented"));
    }

    [Fact]
    public async Task ABalanceWithUnresolvedDecimals_IsNeverRoundedIntoTheFile_ItIsCountedOut()
    {
        // 50.125 is not rounding noise: the stored value is the raw one, and "550.13" would be an invented figure. The unit stays out, with its reason; the clean customer is exported.
        using var h = await SeededAsync(null, ReviewHarness.Row(1, 500m, day: 20), ReviewHarness.Row(1, 50.125m, day: 10) with { DueDate = new DateTime(2026, 8, 10), VoucherNumber = "INV-1-X" },
            ReviewHarness.Row(2, 300m, day: 20));
        var dto = (await ExportAsync(h, All(Filter("Manual")))).Value!;
        Assert.Equal(1, dto.RowCount); Assert.Equal("Customer 2", Rows(dto)[1][1]); Assert.DoesNotContain("550.13", dto.Csv); Assert.DoesNotContain("550.125", dto.Csv);
        Assert.Equal([(GenesysExportRows.AmountPrecisionUnresolved, 1)], dto.Exclusions!.Select(e => (e.Code, e.Units)));
        Assert.Contains("Exclusions=AmountPrecisionUnresolved:1", await AuditAsync(h));
    }

    [Fact]
    public async Task InvalidOrMissingPhones_AreCountedOut_NeverSubstituted()
    {
        using var h = await SeededAsync(null, ReviewHarness.Row(1), ReviewHarness.Row(2, mobile: "123"), ReviewHarness.Row(3, mobile: ""));
        var dto = (await ExportAsync(h, All(Filter("Manual")))).Value!;
        Assert.Equal(1, dto.RowCount); Assert.Equal([(GenesysExportRows.NoCallablePhone, 2)], dto.Exclusions!.Select(e => (e.Code, e.Units)));
        Assert.DoesNotContain("Customer 2", dto.Csv); Assert.DoesNotContain("Customer 3", dto.Csv);
        Assert.True(GenesysContactTemplate.Verify(dto.Csv, GenesysContactTemplate.ExpectedReminderTypes(h.Genesys)).IsMatch);
        using var none = await SeededAsync(null, ReviewHarness.Row(2, mobile: "123"));
        var refused = await ExportAsync(none, All(Filter("Manual")));
        Assert.Equal(CollectionsOutcome.InvalidRequest, refused.Outcome); Assert.Contains("NoCallablePhone", refused.Detail); Assert.Contains("Nothing was exported", refused.Detail);
    }

    [Theory]
    [InlineData("Manual", "Due", null, null, "5000.00")]
    [InlineData("Auto", "Due", null, null, "5000.00")]
    [InlineData("Manual", "Overdue", null, null, "8000.00")]
    [InlineData("Auto", "Overdue", null, null, "8000.00")]
    [InlineData("Manual", null, "2026-08-01", "2026-10-31", "13000.00")]
    [InlineData("Auto", null, "2026-08-01", "2026-08-31", "8000.00")]
    public async Task DueOverdueAndBoth_WithCustomRanges_GiveTheSameAmountInBothModes(string mode, string? status, string? from, string? to, string expected)
    {
        using var h = await SeededAsync(null, ReviewHarness.Row(1, 5000m, day: 20), ReviewHarness.Row(1, 8000m, day: 20) with { DueDate = new DateTime(2026, 8, 10), VoucherNumber = "INV-1B" });
        var filter = Filter(mode, with: f => f with { DueStatus = status, DueFrom = from is null ? null : DateOnly.Parse(from), DueTo = to is null ? null : DateOnly.Parse(to) });
        var dto = (await ExportAsync(h, All(filter))).Value!;
        Assert.Equal(expected, Assert.Single(Rows(dto).Skip(1))[4]);
        Assert.Equal(decimal.Parse(expected, CultureInfo.InvariantCulture), dto.TotalAmount);
    }

    [Theory]
    [InlineData("Manual", 200, 2)]
    [InlineData("Auto", 200, 2)]
    [InlineData("Manual", 0, 3)]
    [InlineData("Auto", 850, 1)]
    public async Task TheMinimumTotal_AppliesTheSameWayInBothModes(string mode, int minimum, int expectedRows)
    {
        using var h = await SeededAsync(null, ReviewHarness.Row(1, 900m, day: 20), ReviewHarness.Row(2, 150m, day: 20), ReviewHarness.Row(3, 700m, day: 20));
        Assert.Equal(expectedRows, (await ExportAsync(h, All(Filter(mode, with: f => f with { MinRemaining = minimum })))).Value!.RowCount);
    }

    // ------------------------------------------------------------ Genesys login refused: the template, labelled; a send still needs Genesys

    [Fact]
    public async Task WhenTheGenesysLoginIsRefused_TheExportUsesTheTemplate_LabelledNotVerified_AndASendStaysRefused()
    {
        using var h = await SeededAsync(x => { x.Genesys.ClientId = "client"; x.Genesys.ClientSecret = "secret"; }, ReviewHarness.Row(1, 5000m, day: 20));
        var refused = new LoginRefusedClient();
        var dto = (await ExportAsync(h, All(Filter("Manual")), refused)).Value!;
        Assert.Equal(1, dto.RowCount); Assert.False(dto.SchemaVerified);
        Assert.Equal(["Phone", "CustomerName", "Email Address", "ReminderType", "AmountDue", "DueDate"], Rows(dto)[0]);
        Assert.StartsWith(GenesysExportService.NotVerifiedLabel, dto.Notices[0]);
        Assert.Contains("could not be read in Genesys (The Genesys login service refused the credentials (HTTP 400, invalid_client))", dto.Notices[0]);
        Assert.Contains($"({GenesysExportService.NotVerifiedLabel})", dto.FileName);
        Assert.Contains($"SchemaVerified=False ({GenesysExportService.NotVerifiedLabel})", await AuditAsync(h));
        Assert.True(dto.DataVerified);
        // The real send is still dependent on Genesys: the list cannot be checked, so nothing is approved.
        var (_, confirm) = await h.PrepareAsync(All(Filter("Manual")), "login-refused");
        using var scope = h.NewScope(refused);
        var send = await scope.Dispatch.ConfirmAsync(h.Manager, confirm, CancellationToken.None);
        Assert.Equal(CollectionsOutcome.InvalidRequest, send.Outcome); Assert.Contains("could not be checked in Genesys", send.Detail);
        Assert.Empty(h.Scheduler.Dispatches);
    }

    private sealed class LoginRefusedClient : IGenesysOutboundClient
    {
        private static readonly GenesysContactListInfo Refused = new(GenesysUploadOutcome.Rejected, null, [], "The Genesys login service refused the credentials (HTTP 400, invalid_client).");
        public Task<GenesysContactListInfo> GetContactListAsync(string contactListId, CancellationToken ct) => Task.FromResult(Refused);
        public Task<GenesysUploadResult> UploadContactsAsync(string contactListId, IReadOnlyList<GenesysContactPayload> contacts, CancellationToken ct) => throw new NotSupportedException();
        public Task<GenesysSuppressResult> SetNotCallableAsync(string contactListId, string contactId, GenesysContactPayload contact, CancellationToken ct) => throw new NotSupportedException();
        public Task<GenesysCampaignLookupResult> FindCampaignsByNameAsync(string name, CancellationToken ct) => throw new NotSupportedException();
        public Task<GenesysCampaignCreateResult> CreateCampaignAsync(string name, string contactListId, GenesysCampaignOptions template, CancellationToken ct) => throw new NotSupportedException();
    }

    // ------------------------------------------------------------ the file's own shape

    [Fact]
    public async Task NamesWithQuotesCommasAndLineBreaks_SurviveAsOneCell_AndTheFileStillVerifies()
    {
        using var h = await SeededAsync(null, ReviewHarness.Row(1, name: "O'Brien, \"Tiger\" Holdings\nLtd"), ReviewHarness.Row(2, name: "Plain Name"));
        var dto = (await ExportAsync(h, All(Filter("Manual")))).Value!;
        var rows = Rows(dto);
        Assert.Equal(3, rows.Count); Assert.All(rows, r => Assert.Equal(6, r.Count));
        Assert.Equal("O'Brien, \"Tiger\" Holdings\nLtd", rows[1][1]);
        Assert.True(GenesysContactTemplate.Verify(dto.Csv, GenesysContactTemplate.ExpectedReminderTypes(h.Genesys)).IsMatch);
        Assert.Equal(2, dto.RowCount);
    }

    [Fact]
    public async Task TheExport_IsBoundedByTheSelectionLimit_AndRefusedBeyondIt_NeverCutShort()
    {
        using var h = await SeededAsync(x => x.Review.MaxSelection = 2, ReviewHarness.Row(1), ReviewHarness.Row(2), ReviewHarness.Row(3));
        var refused = await ExportAsync(h, All(Filter("Manual")));
        Assert.Equal(CollectionsOutcome.InvalidRequest, refused.Outcome); Assert.Contains("More than 2 customers match", refused.Detail);
        Assert.Equal(1, (await ExportAsync(h, All(Filter("Manual", with: f => f with { Customer = "Customer 1" })))).Value!.RowCount);   // narrowed, it fits
        using var context = h.CreateContext();
        Assert.Empty(await context.AuditEntries.Where(a => a.Action == GenesysExportService.AuditAction && a.AfterValue!.Contains("Rows=3")).ToListAsync());
    }
}
