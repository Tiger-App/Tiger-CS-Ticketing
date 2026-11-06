using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using TigerCS.Application.Abstractions;
using TigerCS.Application.Modules.Collections;
using TigerCS.Application.Modules.Collections.Abstractions;
using TigerCS.Application.Modules.Collections.Review;
using TigerCS.Application.Modules.Collections.Services;
using TigerCS.Domain.Modules.Collections.Review;
using TigerCS.Tests.IdentityAndAccess.Fakes;

namespace TigerCS.Tests.Collections.Review;

/// <summary>
/// Export for Genesys: the customers of a Manual selection as a CSV in the format of the Genesys contact list they would go into. The six columns, their order and their formats are the ones the upload
/// itself uses; the customers and filtered amounts are exactly the ones Preview shows; the export is read only and audited. The harness clock is 15 Oct 2026 (no stage is scheduled).
/// </summary>
public sealed class GenesysExportTests
{
    private const string CurrentMonthList = "79e5ae74-ea6e-4941-b76d-45ddf487d8d1", FollowUpList = "3a91c06e-47ab-4a5e-a720-004bd5cf5bba";
    private static readonly string[] Template = ["Phone", "CustomerName", "Email Address", "ReminderType", "AmountDue", "DueDate"];

    /// <summary>Customer 1 owes 5,000 due 20 Oct (Due) and 8,000 due 10 Aug (Overdue); customer 2 owes 500 due 20 Oct.</summary>
    private static PactReceivableInstalment[] Rows() =>
    [
        ReviewHarness.Row(1, 5000m, day: 20), ReviewHarness.Row(1, 8000m, day: 20) with { DueDate = new DateTime(2026, 8, 10), VoucherNumber = "INV-1B" }, ReviewHarness.Row(2, 500m, day: 20)
    ];

    private static async Task<ReviewHarness> SeededAsync(Action<ReviewHarness>? configure = null, params PactReceivableInstalment[] rows)
    {
        var h = new ReviewHarness();
        h.Time.Advance(TimeSpan.FromDays(1)); h.Source.ReadAt = ReviewHarness.Now.AddDays(1);
        configure?.Invoke(h);
        h.Source.Items.AddRange(rows.Length > 0 ? rows : Rows());
        await h.RefreshAsync();
        return h;
    }

    private static ReviewFilter Manual(string type = "CurrentMonth", string? list = null, Func<ReviewFilter, ReviewFilter>? with = null)
    {
        var filter = new ReviewFilter(ReminderType: type, SendingMode: "Manual", ContactList: list, MinRemaining: 0m);
        return with is null ? filter : with(filter);
    }

    private static SelectionRequest All(ReviewFilter filter) => new(filter, ReviewQueryService.ModeAllMatching);

    private static async Task<CollectionsResult<GenesysExportDto>> ExportAsync(ReviewHarness h, SelectionRequest request, CollectionsCaller? caller = null)
    {
        using var scope = h.NewScope();
        return await scope.Export.ExportAsync(caller ?? h.Manager, request, CancellationToken.None);
    }

    private static List<List<string>> Parse(GenesysExportDto dto) => GenesysContactTemplate.ParseCsv(dto.Csv);

    // ------------------------------------------------------------ the template

    [Fact]
    public void TheTemplate_IsExactlySixColumns_InThisOrder()
    {
        Assert.Equal(Template, GenesysContactTemplate.Columns);
        var check = GenesysContactTemplate.CheckHeader(["Phone", "CustomerName", "EmailAddress", "ReminderType", "Amount Due", "DueDate", "Notes"]);
        Assert.Equal(["Email Address", "AmountDue"], check.Missing); Assert.Equal(2, check.Misnamed.Count); Assert.Contains("found \"EmailAddress\"", check.Misnamed[0]); Assert.Equal(["Notes"], check.Unexpected);
        Assert.False(check.Fits); Assert.True(GenesysContactTemplate.CheckHeader([.. Template, "Notes"]).Fits);
    }

    [Fact]
    public async Task TheFile_HasTheSixTemplateColumns_InOrder_AndEveryValueIsExactlyWhatTheUploadSends()
    {
        using var h = await SeededAsync();
        var dto = (await ExportAsync(h, All(Manual()))).Value!;
        var rows = Parse(dto);
        Assert.Equal(Template, rows[0]); Assert.Equal(2, dto.RowCount); Assert.Equal(3, rows.Count); Assert.All(rows, r => Assert.Equal(6, r.Count));
        Assert.Equal(Template, dto.Columns);
        var check = GenesysContactTemplate.Verify(dto.Csv, GenesysContactTemplate.ExpectedReminderTypes(h.Genesys));
        Assert.True(check.IsMatch, string.Join("; ", check.Issues.Select(i => i.Message))); Assert.Empty(check.UnexpectedColumns);
        Assert.Equal(["Current Month"], check.ReminderTypeValues);

        // The same selection, really uploaded: the payloads the upload sends are the rows of the file, value for value.
        var (_, confirm) = await h.PrepareAsync(All(Manual()), "compare");
        using (var scope = h.NewScope())
        {
            Assert.Equal(CollectionsOutcome.Accepted, (await scope.Dispatch.ConfirmAsync(h.Manager, confirm, CancellationToken.None)).Outcome);
            await scope.Dispatch.ExecuteAsync(h.Scheduler.Dispatches.Single(), CancellationToken.None);
        }
        var uploaded = h.Client.Calls.Single().Contacts.OrderBy(c => c.Phone).Select(c => new[] { c.Phone, c.CustomerName, c.EmailAddress, c.ReminderType, c.AmountDue, c.DueDate }).ToList();
        var exported = rows.Skip(1).OrderBy(r => r[0]).Select(r => r.ToArray()).ToList();
        Assert.Equal(uploaded, exported);
        Assert.Matches(@"^\+[1-9]\d{7,14}$", rows[1][0]); Assert.Matches(@"^\d+\.\d{2}$", rows[1][4]); Assert.Matches(@"^\d{4}-\d{2}-\d{2}$", rows[1][5]);
    }

    [Fact]
    public async Task OnlyTheTemplateFieldsLeaveTheSystem_NothingElseAboutACustomer()
    {
        using var h = await SeededAsync();
        var dto = (await ExportAsync(h, All(Manual()))).Value!;
        var header = Parse(dto)[0];
        Assert.DoesNotContain(header, c => c is "TenantId" or "UnitCode" or "CompanyId" or "RecordKey" or "ProjectCode");
        foreach (var secret in new[] { "T0001", "TP140-1", "RK", h.Manager.EmployeeId.ToString() }) Assert.DoesNotContain(secret, dto.Csv);
    }

    [Fact]
    public async Task ACustomerNameThatLooksLikeAFormula_IsNeutralizedForSpreadsheets()
    {
        using var h = await SeededAsync(null, ReviewHarness.Row(1, name: "=HYPERLINK(\"x\")"), ReviewHarness.Row(2, name: "+Ali"));
        var dto = (await ExportAsync(h, All(Manual()))).Value!;
        var names = Parse(dto).Skip(1).Select(r => r[1]).Order().ToList();
        Assert.Contains("'=HYPERLINK(\"x\")", names); Assert.Contains("'+Ali", names);                 // = and a leading + are neutralized in a name; the phone keeps its own +
        Assert.All(Parse(dto).Skip(1), r => Assert.StartsWith("+", r[0]));
    }

    // ------------------------------------------------------------ amounts follow the filters, exactly as in Preview

    [Theory]
    [InlineData("Both", null, null, 13000)]
    [InlineData("Due", null, null, 5000)]
    [InlineData("Overdue", null, null, 8000)]
    [InlineData("Both", "2026-10-01", "2026-10-31", 5000)]
    [InlineData("Both", "2026-08-01", "2026-08-31", 8000)]
    [InlineData("Due", "2026-08-01", "2026-08-31", null)]
    [InlineData("Overdue", "2026-10-01", "2026-10-31", null)]
    public async Task TheAmounts_AreTheFilteredAmountsOfThePreview_AndZeroCustomersAreNotExported(string status, string? from, string? to, int? expectedCustomer1)
    {
        using var h = await SeededAsync(null, ReviewHarness.Row(1, 5000m, day: 20), ReviewHarness.Row(1, 8000m, day: 20) with { DueDate = new DateTime(2026, 8, 10), VoucherNumber = "INV-1B" });
        var filter = Manual(with: f => f with { DueStatus = status == "Both" ? null : status, DueFrom = from is null ? null : DateOnly.Parse(from), DueTo = to is null ? null : DateOnly.Parse(to) });
        using var scope = h.NewScope();
        var preview = (await scope.Query.QueryAsync(h.Manager, filter, 1, 50, CancellationToken.None)).Value!;
        var result = await scope.Export.ExportAsync(h.Manager, All(filter), CancellationToken.None);
        if (expectedCustomer1 is null)
        {
            Assert.Empty(preview.Items); Assert.False(result.IsSuccess); Assert.Contains("No Ready customers", result.Detail);
            return;
        }
        var row = Assert.Single(Parse(result.Value!).Skip(1));
        Assert.Equal(MoneyNormalizer.Format(preview.Items.Single().RemainingAmount!.Value), row[4]);   // exactly the amount Preview shows
        Assert.Equal(MoneyNormalizer.Format(expectedCustomer1.Value), row[4]);
        Assert.Equal(preview.ReadyTotalAmount, result.Value!.TotalAmount);
        Assert.Equal(preview.Items.Single().DueDate!.Value.ToString("yyyy-MM-dd"), row[5]);
    }

    [Fact]
    public async Task TheMinimumTotal_AndTheTower_NarrowTheExportLikeThePreview()
    {
        var rows = new[] { ReviewHarness.Row(1, 900m, day: 20), ReviewHarness.Row(2, 150m, day: 20), ReviewHarness.Row(3, 700m, day: 20) with { TowerId = 7, TowerNumber = "124", TowerName = "Tower 124" } };
        using var h = await SeededAsync(null, rows);
        Assert.Equal(3, (await ExportAsync(h, All(Manual()))).Value!.RowCount);
        Assert.Equal(2, (await ExportAsync(h, All(Manual(with: f => f with { MinRemaining = 200m })))).Value!.RowCount);
        Assert.Equal(1, (await ExportAsync(h, All(Manual(with: f => f with { TowerId = 7 })))).Value!.RowCount);
    }

    // ------------------------------------------------------------ selection and the safety rules

    [Fact]
    public async Task OnlyTheTickedCustomers_OrAllEligible_AreExported_AndHeldBackOnesNever()
    {
        // Customers 3 and 4 share a number: both are held back and can never be exported, ticked or not.
        var rows = new[] { ReviewHarness.Row(1), ReviewHarness.Row(2), ReviewHarness.Row(3, mobile: "971500000009"), ReviewHarness.Row(4, mobile: "971500000009") };
        using var h = await SeededAsync(null, rows);
        Assert.Equal(2, (await ExportAsync(h, All(Manual()))).Value!.RowCount);                         // all eligible
        var one = new SelectionRequest(Manual(), ReviewQueryService.ModeSelectedUnits, null, null, 1, 50, [ReviewQueryService.UnitKey(4, "T0001", "TP140-1")]);
        var ticked = (await ExportAsync(h, one)).Value!;                                               // only the ticked one
        Assert.Equal(1, ticked.RowCount); Assert.Equal("Customer 1", Parse(ticked)[1][1]);
        var heldBack = new SelectionRequest(Manual(), ReviewQueryService.ModeSelectedUnits, null, null, 1, 50, [ReviewQueryService.UnitKey(4, "T0003", "TP140-3"), ReviewQueryService.UnitKey(4, "T0004", "TP140-4")]);
        Assert.False((await ExportAsync(h, heldBack)).IsSuccess);                                      // nothing eligible among them
        Assert.DoesNotContain("Customer 3", (await ExportAsync(h, All(Manual()))).Value!.Csv);
        var excluding = (await ExportAsync(h, All(Manual()) with { ExcludedKeys = [await KeyOf(h, "TP140-2")] })).Value!;
        Assert.Equal(1, excluding.RowCount);
    }

    private static async Task<string> KeyOf(ReviewHarness h, string unit)
    {
        using var scope = h.NewScope();
        return (await scope.Query.QueryAsync(h.Manager, Manual(), 1, 50, CancellationToken.None)).Value!.Items.Single(i => i.UnitCode == unit).RecordKey;
    }

    [Fact]
    public async Task AlreadySentAndContactedToday_AreNotExported_AndNeitherIsAnUnreconciledSource()
    {
        using var h = await SeededAsync();
        var (_, confirm) = await h.PrepareAsync(All(Manual("CurrentMonth")), "first");
        using (var scope = h.NewScope()) await scope.Dispatch.ConfirmAsync(h.Manager, confirm, CancellationToken.None);
        Assert.False((await ExportAsync(h, All(Manual("CurrentMonth")))).IsSuccess);                  // already in a live dispatch
        Assert.False((await ExportAsync(h, All(Manual("FollowUp")))).IsSuccess);                       // one contact per customer per day

        using var unreconciled = await SeededAsync(x => x.Campaign.FinancialSourceValidated = false);
        var refused = await ExportAsync(unreconciled, All(Manual()));
        Assert.False(refused.IsSuccess); Assert.Contains("No Ready customers", refused.Detail);
    }

    [Fact]
    public async Task ItNeedsTheSendRight_ManualMode_AndCurrentData()
    {
        using var h = await SeededAsync();
        Assert.Equal(CollectionsOutcome.Forbidden, (await ExportAsync(h, All(Manual()), h.Agent)).Outcome);
        Assert.Equal(CollectionsOutcome.Forbidden, (await ExportAsync(h, All(Manual()), h.Reporting)).Outcome);
        var auto = await ExportAsync(h, All(new ReviewFilter(ReminderType: "CurrentMonth", SendingMode: "Auto")));
        Assert.Equal(CollectionsOutcome.InvalidRequest, auto.Outcome); Assert.Contains("Manual action", auto.Detail);
        Assert.Equal(CollectionsOutcome.InvalidRequest, (await ExportAsync(h, All(new ReviewFilter(SendingMode: "Manual")))).Outcome);          // no campaign type
        h.Time.Advance(TimeSpan.FromDays(1));                                                         // the data was built yesterday
        var stale = await ExportAsync(h, All(Manual()));
        Assert.Equal(CollectionsOutcome.InvalidRequest, stale.Outcome); Assert.Contains("built for", stale.Detail);
    }

    // ------------------------------------------------------------ the contact list

    [Theory]
    [InlineData("CurrentMonth", null, "CurrentMonth", CurrentMonthList)]
    [InlineData("CurrentMonth", "FollowUp", "FollowUp", FollowUpList)]
    [InlineData("FollowUp", "CurrentMonth", "CurrentMonth", CurrentMonthList)]
    [InlineData("FollowUp", null, "FollowUp", FollowUpList)]
    public async Task TheFile_IsForTheChosenExistingList_AndCarriesTheCampaignsReminderType(string type, string? chosen, string listName, string listId)
    {
        using var h = await SeededAsync();
        var dto = (await ExportAsync(h, All(Manual(type, chosen)))).Value!;
        Assert.Equal(listId, dto.ContactListId); Assert.StartsWith($"genesys-{type}-to-{listName}-20261015-", dto.FileName); Assert.EndsWith(".csv", dto.FileName);
        Assert.Equal(CampaignReminderTypes.StageName(Enum.Parse<CampaignReminderType>(type)), dto.Campaign);
        Assert.All(Parse(dto).Skip(1), r => Assert.Equal(CampaignReminderTypes.DefaultLabel(Enum.Parse<CampaignReminderType>(type)), r[3]));
    }

    [Theory]
    [InlineData("CurrentMonth", "LegalNotice")]
    [InlineData("CurrentMonth", "Nonsense")]
    [InlineData("LegalNotice", "CurrentMonth")]
    public async Task AListThatDoesNotSuitTheCampaign_IsRefused(string type, string list)
    {
        using var h = await SeededAsync(null, ReviewHarness.Row(1), ReviewHarness.Row(2, 2000m) with { DueDate = new DateTime(2026, 9, 10) });
        var refused = await ExportAsync(h, All(Manual(type, list)));
        Assert.Equal(CollectionsOutcome.InvalidRequest, refused.Outcome);
    }

    // ------------------------------------------------------------ the list's real schema

    private static Task<ReviewHarness> WithGenesysAsync(Action<ReviewHarness>? more = null) =>
        SeededAsync(h => { h.Genesys.ClientId = "client"; h.Genesys.ClientSecret = "secret"; more?.Invoke(h); });

    [Theory]
    [InlineData("CurrentMonth", CurrentMonthList)]
    [InlineData("FollowUp", FollowUpList)]
    public async Task WithGenesysConfigured_TheListsRealColumnsAreReadAndChecked(string list, string listId)
    {
        using var h = await WithGenesysAsync();
        var dto = (await ExportAsync(h, All(Manual("CurrentMonth", list)))).Value!;
        Assert.True(dto.SchemaVerified); Assert.Empty(dto.Notices); Assert.Contains(listId, h.Client.ListChecks);
        Assert.Equal(Template, Parse(dto)[0]);
    }

    [Fact]
    public async Task ExtraColumnsOfTheList_AreKeptEmpty_InTheListsOwnOrder_AfterTheSixTemplateColumns()
    {
        using var h = await WithGenesysAsync(x => x.Client.ListColumns[CurrentMonthList] = ["DueDate", "Notes", "Phone", "AmountDue", "CustomerName", "Email Address", "Region", "ReminderType"]);
        var dto = (await ExportAsync(h, All(Manual()))).Value!;
        Assert.Equal([.. Template, "Notes", "Region"], Parse(dto)[0]);
        Assert.All(Parse(dto).Skip(1), r => { Assert.Equal(8, r.Count); Assert.Equal("", r[6]); Assert.Equal("", r[7]); Assert.NotEqual("", r[0]); });
        Assert.True(GenesysContactTemplate.Verify(dto.Csv, GenesysContactTemplate.ExpectedReminderTypes(h.Genesys)).IsMatch);
    }

    [Fact]
    public async Task AListThatLacksOrMisnamesAColumn_RefusesTheExport_NamingTheColumn()
    {
        using var missing = await WithGenesysAsync(x => x.Client.ListColumns[CurrentMonthList] = ["Phone", "CustomerName", "Email Address", "ReminderType", "DueDate"]);
        var refused = await ExportAsync(missing, All(Manual()));
        Assert.Equal(CollectionsOutcome.InvalidRequest, refused.Outcome); Assert.Contains("missing column AmountDue", refused.Detail); Assert.Contains("Nothing was exported", refused.Detail);

        using var misnamed = await WithGenesysAsync(x => x.Client.ListColumns[FollowUpList] = ["Phone", "CustomerName", "EmailAddress", "ReminderType", "AmountDue", "DueDate"]);
        var wrong = await ExportAsync(misnamed, All(Manual("CurrentMonth", "FollowUp")));
        Assert.Equal(CollectionsOutcome.InvalidRequest, wrong.Outcome); Assert.Contains("misnamed column Email Address (found \"EmailAddress\")", wrong.Detail);
        Assert.True((await ExportAsync(misnamed, All(Manual("CurrentMonth", "CurrentMonth")))).IsSuccess);   // the other list is fine
    }

    [Fact]
    public async Task AListThatCannotBeRead_RefusesTheExport_ItIsNeverGuessed()
    {
        using var h = await WithGenesysAsync();
        h.Client.ListCheckScript.Enqueue(GenesysUploadOutcome.Unknown);
        var refused = await ExportAsync(h, All(Manual()));
        Assert.Equal(CollectionsOutcome.InvalidRequest, refused.Outcome); Assert.Contains("could not be read in Genesys", refused.Detail);
        using var throwing = await SeededAsync(x => { x.Genesys.ClientId = "c"; x.Genesys.ClientSecret = "s"; });
        using var scope = throwing.NewScope(new ThrowingListClient());
        var thrown = await scope.Export.ExportAsync(throwing.Manager, All(Manual()), CancellationToken.None);
        Assert.Equal(CollectionsOutcome.InvalidRequest, thrown.Outcome);
    }

    private sealed class ThrowingListClient : IGenesysOutboundClient
    {
        public Task<GenesysContactListInfo> GetContactListAsync(string contactListId, CancellationToken ct) => throw new TimeoutException();
        public Task<GenesysUploadResult> UploadContactsAsync(string contactListId, IReadOnlyList<GenesysContactPayload> contacts, CancellationToken ct) => throw new NotSupportedException();
        public Task<GenesysSuppressResult> SetNotCallableAsync(string contactListId, string contactId, GenesysContactPayload contact, CancellationToken ct) => throw new NotSupportedException();
        public Task<GenesysCampaignLookupResult> FindCampaignsByNameAsync(string name, CancellationToken ct) => throw new NotSupportedException();
        public Task<GenesysCampaignCreateResult> CreateCampaignAsync(string name, string contactListId, GenesysCampaignOptions template, CancellationToken ct) => throw new NotSupportedException();
    }

    [Fact]
    public async Task WithoutTheGenesysIntegration_TheDocumentedTemplateIsUsed_AndTheFileSaysItWasNotVerified()
    {
        using var h = await SeededAsync();                                                            // the harness has no OAuth credentials
        var dto = (await ExportAsync(h, All(Manual()))).Value!;
        Assert.False(dto.SchemaVerified); Assert.Single(dto.Notices); Assert.Contains("were not checked in Genesys", dto.Notices[0]);
        Assert.Empty(h.Client.ListChecks);                                                            // no call to Genesys was made
        Assert.Equal(Template, Parse(dto)[0]);
    }

    // ------------------------------------------------------------ read only, and audited

    [Fact]
    public async Task ItIsReadOnly_NothingIsUploaded_QueuedOrMarkedAsSent()
    {
        using var h = await SeededAsync();
        using var scope = h.NewScope();
        var before = (await scope.Query.SummarizeAsync(h.Manager, All(Manual()), CancellationToken.None)).Value!;
        Assert.True((await scope.Export.ExportAsync(h.Manager, All(Manual()), CancellationToken.None)).IsSuccess);
        Assert.Empty(h.Scheduler.Dispatches); Assert.Empty(h.Client.Calls); Assert.Empty(h.Client.CampaignLookups);
        using var context = h.CreateContext();
        Assert.Empty(context.CollectionsDispatches); Assert.Empty(context.CollectionsDispatchItems);
        var after = (await scope.Query.SummarizeAsync(h.Manager, All(Manual()), CancellationToken.None)).Value!;
        Assert.Equal(before.Fingerprint, after.Fingerprint);                                          // the same customers are still eligible: an export is not a send
    }

    [Fact]
    public async Task EveryExportIsAudited_WithoutAnyCustomerData()
    {
        using var h = await WithGenesysAsync();
        var dto = (await ExportAsync(h, All(Manual("CurrentMonth", "FollowUp", f => f with { DueStatus = "Due" })))).Value!;
        using var context = h.CreateContext();
        var entry = Assert.Single(await context.AuditEntries.ToListAsync());
        Assert.Equal((GenesysExportService.AuditAction, GenesysExportService.AuditEntityType, (Guid?)h.Manager.EmployeeId), (entry.Action, entry.EntityType, entry.ActorEmployeeId));
        Assert.Equal(Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(dto.Csv)))[..16], entry.EntityId);
        var after = entry.AfterValue!;
        Assert.Contains("Campaign=CurrentMonth", after); Assert.Contains($"ContactList=FollowUp ({FollowUpList})", after); Assert.Contains("Rows=2", after);
        Assert.Contains("Total=AED 5500.00", after); Assert.Contains("SchemaVerified=True", after); Assert.Contains("Mode=Manual", after); Assert.Contains("\"DueStatus\":\"Due\"", after); Assert.Contains(dto.Sha256, after);
        foreach (var personal in new[] { "Customer 1", "Customer 2", "+97150", "example.test" }) Assert.DoesNotContain(personal, after);
    }

    [Fact]
    public async Task IfTheAuditCannotBeWritten_NoFileIsReleased()
    {
        using var h = await SeededAsync();
        using var context = h.CreateContext();
        var auth = new CollectionsAuthorizationService(h.Options, new FakeDepartmentRepository());
        var clock = new CollectionsClock(h.Options, h.Time);
        using var scope = h.NewScope();
        var broken = new GenesysExportService(h.Options, h.Genesys, auth, clock, scope.Query, h.Client, new ThrowingAudit(), new TigerCS.Infrastructure.Modules.Collections.CollectionsUnitOfWork(context), NullLogger<GenesysExportService>.Instance);
        var result = await broken.ExportAsync(h.Manager, All(Manual()), CancellationToken.None);
        Assert.Equal(CollectionsOutcome.FinanceUnavailable, result.Outcome); Assert.Null(result.Value); Assert.Contains("no file was released", result.Detail);
    }

    private sealed class ThrowingAudit : IAuditEntryWriter
    {
        public Task WriteAsync(Guid? actorEmployeeId, string action, string entityType, string? entityId, string? beforeValue, string? afterValue, Guid correlationId, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("audit store unavailable");
    }
}
