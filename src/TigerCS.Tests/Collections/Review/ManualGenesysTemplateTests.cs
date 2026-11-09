using TigerCS.Application.Modules.Collections;
using TigerCS.Application.Modules.Collections.Abstractions;
using TigerCS.Application.Modules.Collections.Review;
using TigerCS.Application.Modules.Collections.Services;
using TigerCS.Domain.Modules.Collections;
using TigerCS.Domain.Modules.Collections.Review;

namespace TigerCS.Tests.Collections.Review;

/// <summary>
/// The Manual contact list follows the official eleven-column Genesys template: Phone, Email, CustomerName, ReminderType, AmountDue, UnitNumber, ProjectName, TowerName, DueDate, DueMonthAr, TenantId.
/// Manual Export for Genesys and Manual Send to Genesys produce the same payload; Auto keeps its six columns and its routing. Everything here runs against fakes: nothing is uploaded to Genesys.
/// </summary>
public sealed class ManualGenesysTemplateTests
{
    private const string CurrentMonthList = "79e5ae74-ea6e-4941-b76d-45ddf487d8d1", ManualList = ReviewHarness.ManualListId;

    private static readonly string[] AutoTemplate = ["Phone", "CustomerName", "Email Address", "ReminderType", "AmountDue", "DueDate"];

    private static async Task<ReviewHarness> SeededAsync(Action<ReviewHarness>? configure = null, params PactReceivableInstalment[] rows)
    {
        var h = new ReviewHarness();
        h.Time.Advance(TimeSpan.FromDays(1)); h.Source.ReadAt = ReviewHarness.Now.AddDays(1);
        configure?.Invoke(h);
        h.Source.Items.AddRange(rows.Length > 0 ? rows : [ReviewHarness.Row(1, 5000m, day: 20) with { TowerId = 7, TowerNumber = "140", TowerName = "Blue Wave" }]);
        await h.RefreshAsync();
        return h;
    }

    private static Task<ReviewHarness> WithGenesysAsync(Action<ReviewHarness>? more = null, params PactReceivableInstalment[] rows) =>
        SeededAsync(h => { h.Genesys.ClientId = "client"; h.Genesys.ClientSecret = "secret"; more?.Invoke(h); }, rows);

    private static ReviewFilter Filter(string mode = "Manual") => new(ReminderType: "CurrentMonth", SendingMode: mode, MinRemaining: 0m);
    private static SelectionRequest All(string mode = "Manual") => new(Filter(mode), ReviewQueryService.ModeAllMatching);

    private static async Task<CollectionsResult<GenesysExportDto>> ExportAsync(ReviewHarness h, string mode = "Manual")
    {
        using var scope = h.NewScope();
        return await scope.Export.ExportAsync(h.Manager, All(mode), CancellationToken.None);
    }

    /// <summary>Approves and executes a real send against the harness's fake Genesys client (recorded, never contacted).</summary>
    private static async Task SendAsync(ReviewHarness h, string mode = "Manual", string key = "k1")
    {
        var (_, confirm) = await h.PrepareAsync(All(mode), key);
        using var scope = h.NewScope();
        Assert.Equal(CollectionsOutcome.Accepted, (await scope.Dispatch.ConfirmAsync(h.Manager, confirm, CancellationToken.None)).Outcome);
        await scope.Dispatch.ExecuteAsync(h.Scheduler.Dispatches.Single(), CancellationToken.None);
    }

    // ------------------------------------------------------------ the exact headers

    [Fact]
    public void TheManualTemplate_IsExactlyTheElevenOfficialColumns_InThisOrderAndSpelling()
    {
        Assert.Equal(ManualGenesysFile.Template, GenesysContactTemplate.ManualColumns);
        Assert.Equal(11, GenesysContactTemplate.ManualColumns.Count);
        Assert.Contains("Email", GenesysContactTemplate.ManualColumns); Assert.DoesNotContain("Email Address", GenesysContactTemplate.ManualColumns);
        Assert.Equal(GenesysContactTemplate.ManualColumns, GenesysContactTemplate.ColumnsFor(manual: true));
    }

    [Fact]
    public void TheAutoTemplate_IsUnchanged_SixColumns_WithEmailAddress()
    {
        Assert.Equal(AutoTemplate, GenesysContactTemplate.Columns);
        Assert.Equal(AutoTemplate, GenesysContactTemplate.ColumnsFor(manual: false));
    }

    [Fact]
    public async Task TheManualExportFile_HasExactlyTheElevenHeaders_AndEveryRowHasElevenCells()
    {
        using var h = await SeededAsync();
        var dto = (await ExportAsync(h)).Value!;
        var file = GenesysContactTemplate.ParseCsv(dto.Csv);
        Assert.Equal(ManualGenesysFile.Template, file[0]);
        Assert.Equal(ManualGenesysFile.Template, dto.Columns);
        Assert.All(file, r => Assert.Equal(11, r.Count));
        Assert.StartsWith("\"Phone\",\"Email\",\"CustomerName\",\"ReminderType\",\"AmountDue\",\"UnitNumber\",\"ProjectName\",\"TowerName\",\"DueDate\",\"DueMonthAr\",\"TenantId\"\r\n", dto.Csv);
    }

    // ------------------------------------------------------------ the mappings

    [Fact]
    public async Task EveryColumn_IsMappedFromItsOwnSource_AndTenantIdIsThePactTenant()
    {
        using var h = await SeededAsync();
        var row = Assert.Single(ManualGenesysFile.Records((await ExportAsync(h)).Value!.Csv));
        Assert.Equal("+971500000001", row["Phone"]);                    // contact details: CRM first, PACT completing (here PACT only)
        Assert.Equal("c1@example.test", row["Email"]);
        Assert.Equal("Customer 1", row["CustomerName"]);
        Assert.Equal("Current Month", row["ReminderType"]);
        Assert.Equal("5000.00", row["AmountDue"]);                      // financial values: PACT
        Assert.Equal("2026-10-20", row["DueDate"]);
        Assert.Equal("أكتوبر", row["DueMonthAr"]);                      // October, from the due date
        Assert.Equal("TP140-1", row["UnitNumber"]);                     // the unit as the Manual page lists it
        Assert.Equal("TP140", row["ProjectName"]);                      // the project as the review data holds it
        Assert.Equal("Blue Wave", row["TowerName"]);                    // the tower's real name
        Assert.Equal("T0001", row["TenantId"]);                         // the PACT tenant, not a CRM CustomerId (which is a number such as 1001)
    }

    [Fact]
    public async Task ATowerWithoutARealName_IsLeftEmpty_ItsNumberIsNotInventedAsAName()
    {
        using var h = await SeededAsync(null,
            ReviewHarness.Row(1, 500m, day: 20) with { TowerId = 7, TowerNumber = "119", TowerName = "TP119" },        // a name that only repeats the number
            ReviewHarness.Row(2, 500m, day: 20));                                                                        // no tower data at all
        var rows = ManualGenesysFile.Records((await ExportAsync(h)).Value!.Csv);
        Assert.Equal(2, rows.Count);
        Assert.All(rows, r => Assert.Equal("", r["TowerName"]));
        Assert.All(rows, r => Assert.NotEqual("", r["UnitNumber"]));        // identity values are always present
        Assert.All(rows, r => Assert.NotEqual("", r["TenantId"]));
    }

    [Fact]
    public async Task TheTenantId_NeverBecomesACustomerId()
    {
        using var h = await SeededAsync(null, ReviewHarness.Row(7, 300m, day: 20));            // PACT tenant "T0007", PACT unit id 1007
        var row = Assert.Single(ManualGenesysFile.Records((await ExportAsync(h)).Value!.Csv));
        Assert.Equal("T0007", row["TenantId"]);
        Assert.DoesNotContain("1007", row.Values);                                              // no unit / customer id leaks into any column
        Assert.DoesNotContain("CustomerId", ManualGenesysFile.Template);
    }

    [Theory]
    [InlineData(1, "يناير"), InlineData(2, "فبراير"), InlineData(3, "مارس"), InlineData(4, "أبريل"), InlineData(5, "مايو"), InlineData(6, "يونيو"),
     InlineData(7, "يوليو"), InlineData(8, "أغسطس"), InlineData(9, "سبتمبر"), InlineData(10, "أكتوبر"), InlineData(11, "نوفمبر"), InlineData(12, "ديسمبر")]
    public void DueMonthAr_IsTheArabicNameOfTheDueDatesMonth_Whatever_TheServersCulture(int month, string expected)
    {
        var original = System.Globalization.CultureInfo.CurrentCulture;
        try
        {
            System.Globalization.CultureInfo.CurrentCulture = new System.Globalization.CultureInfo("de-DE");
            Assert.Equal(expected, GenesysContactTemplate.ArabicMonthName(new DateOnly(2026, month, 28)));
        }
        finally { System.Globalization.CultureInfo.CurrentCulture = original; }
    }

    // ------------------------------------------------------------ one row per unit, even when phone numbers repeat

    [Fact]
    public async Task DuplicatePhoneNumbers_KeepOneRowPerUnit_EachWithItsOwnUnitProjectAmountAndDate()
    {
        using var h = await SeededAsync(null,
            ReviewHarness.Row(1, 5000m, day: 20, mobile: "971500000099"),
            ReviewHarness.Row(2, 700m, day: 25, mobile: "971500000099"),                                                       // another customer, the same number
            ReviewHarness.Row(1, 300m, day: 22, mobile: "971500000099") with { UnitId = 2001, UnitCode = "TP140-1B", VoucherNumber = "INV-1B" });   // the same customer, a second unit
        var dto = (await ExportAsync(h)).Value!;
        var rows = ManualGenesysFile.Records(dto.Csv);
        Assert.Equal(3, dto.RowCount); Assert.Equal(3, rows.Count); Assert.Empty(dto.Exclusions!);
        Assert.All(rows, r => Assert.Equal("+971500000099", r["Phone"]));                                                      // the number repeats; nothing is merged or dropped
        Assert.Equal(3, rows.Select(r => r["UnitNumber"]).Distinct().Count());
        Assert.Equal(["300.00", "5000.00", "700.00"], rows.Select(r => r["AmountDue"]).Order());
        Assert.Equal(["T0001", "T0001", "T0002"], rows.Select(r => r["TenantId"]).Order());
    }

    [Fact]
    public async Task TheSendKeepsOneContactPerUnit_ForRepeatedNumbers_WithTheSamePayloadAsTheExport()
    {
        using var h = await SeededAsync(null,
            ReviewHarness.Row(1, 5000m, day: 20, mobile: "971500000099"),
            ReviewHarness.Row(2, 700m, day: 25, mobile: "971500000099"));
        var exported = ManualGenesysFile.Records((await ExportAsync(h)).Value!.Csv);
        await SendAsync(h);
        var call = Assert.Single(h.Client.Calls);
        Assert.Equal(2, call.Contacts.Count);
        Assert.All(call.Contacts, c => Assert.Equal("+971500000099", c.Phone));
        Assert.Equal(2, call.Contacts.Select(c => c.Manual!.UnitNumber).Distinct().Count());
        AssertSameAsFile(call.Contacts, exported);
    }

    // ------------------------------------------------------------ Manual export == Manual send

    [Fact]
    public async Task TheManualExport_IsTheSamePayload_AsTheManualSend_ValueForValue()
    {
        using var h = await SeededAsync(null,
            ReviewHarness.Row(1, 5000m, day: 20) with { TowerId = 7, TowerNumber = "140", TowerName = "Blue Wave" },
            ReviewHarness.Row(2, 700m, day: 25),
            ReviewHarness.Row(3, 900m, day: 18, name: "=Ali, \"Q\""));
        var exported = ManualGenesysFile.Records((await ExportAsync(h)).Value!.Csv);
        await SendAsync(h);
        var call = Assert.Single(h.Client.Calls);
        Assert.Equal(ManualList, call.ContactListId);
        Assert.All(call.Contacts, c => { Assert.Equal(ManualList, c.ContactListId); Assert.NotNull(c.Manual); });
        AssertSameAsFile(call.Contacts, exported);
        // The upload sends exactly the template's keys, with Email (not "Email Address").
        Assert.All(call.Contacts, c => Assert.Equal(ManualGenesysFile.Template.Order(), GenesysContactTemplate.ValuesOf(c).Keys.Order()));
    }

    private static void AssertSameAsFile(IReadOnlyList<GenesysContactPayload> uploaded, List<Dictionary<string, string>> file)
    {
        Assert.Equal(file.Count, uploaded.Count);
        foreach (var contact in uploaded)
        {
            var values = GenesysContactTemplate.ValuesOf(contact);
            // The CSV neutralizes a leading = - @ (formula injection); the upload keeps the raw text. Compare after the same neutralization.
            var match = Assert.Single(file, r => r["UnitNumber"] == values["UnitNumber"]);
            foreach (var column in ManualGenesysFile.Template)
            {
                var expected = values[column] is { Length: > 0 } v && (v[0] is '=' or '-' or '@') ? "'" + v : values[column];
                Assert.Equal(expected, match[column]);
            }
        }
    }

    [Fact]
    public async Task TheDispatchAuditExport_OfAManualSend_IsTheElevenColumnFile()
    {
        using var h = await SeededAsync();
        await SendAsync(h);
        using var scope = h.NewScope();
        var dispatchId = h.Scheduler.Dispatches.Single();
        var exported = await scope.Dispatch.ExportContactsAsync(h.Manager, await DispatchPublicIdAsync(h, dispatchId), CancellationToken.None);
        Assert.True(exported.IsSuccess);
        Assert.Equal(ManualGenesysFile.Template, GenesysContactTemplate.ParseCsv(exported.Value!)[0]);
    }

    private static async Task<Guid> DispatchPublicIdAsync(ReviewHarness h, long dispatchId)
    {
        using var context = h.CreateContext();
        return (await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions.SingleAsync(context.Set<CollectionsDispatch>(), d => d.CollectionsDispatchId == dispatchId)).PublicId;
    }

    // ------------------------------------------------------------ Manual versus Auto routing

    [Fact]
    public async Task Manual_GoesToTheManualList_WithElevenColumns_WhileAutoStaysOnItsStageList_WithSix()
    {
        using var h = await WithGenesysAsync();
        var manual = (await ExportAsync(h, "Manual")).Value!;
        Assert.Equal(ManualList, manual.ContactListId); Assert.Equal(ManualGenesysFile.Template, GenesysContactTemplate.ParseCsv(manual.Csv)[0]);

        var auto = (await ExportAsync(h, "Auto")).Value!;
        Assert.Equal(CurrentMonthList, auto.ContactListId); Assert.Equal(AutoTemplate, GenesysContactTemplate.ParseCsv(auto.Csv)[0]);
        Assert.Equal(AutoTemplate, auto.Columns);
        Assert.Contains(ManualList, h.Client.ListChecks); Assert.Contains(CurrentMonthList, h.Client.ListChecks);    // each list was read once for its own export

        // Same customer, same values, two shapes.
        var m = Assert.Single(ManualGenesysFile.Records(manual.Csv)); var a = Assert.Single(ManualGenesysFile.Records(auto.Csv));
        Assert.Equal((a["Phone"], a["CustomerName"], a["Email Address"], a["AmountDue"], a["DueDate"]), (m["Phone"], m["CustomerName"], m["Email"], m["AmountDue"], m["DueDate"]));
        Assert.DoesNotContain("UnitNumber", a.Keys); Assert.DoesNotContain("TenantId", a.Keys);
    }

    [Fact]
    public async Task AutoPayloads_NeverCarryTheManualFields_AndSendNoMoreThanTheSixKeys()
    {
        using var h = await SeededAsync();
        using var scope = h.NewScope();
        var resolved = await scope.Query.ResolveForExportAsync(All("Auto"), CancellationToken.None);
        var row = GenesysExportRows.Build(resolved.Candidates).Rows.Single();
        var item = new CollectionsDispatchItem { RecordKey = row.First.RecordKey, ReminderType = row.ReminderType, TenantId = "T0001", UnitCode = "TP140-1", ProjectName = "TP140", TowerName = "Blue Wave",
            CustomerName = row.CustomerName, Phone = row.Phone, Email = row.Email, Amount = row.Amount, DueDate = row.DueDate, VoiceEligible = true };
        var auto = DispatchService.ToPayload(item, CurrentMonthList, h.Genesys);
        Assert.Null(auto.Manual);
        Assert.Equal(AutoTemplate.Order(), GenesysContactTemplate.ValuesOf(auto).Keys.Order());
        var manual = DispatchService.ToPayload(item, ManualList, h.Genesys, manual: true);
        Assert.Equal(("TP140-1", "TP140", "Blue Wave", "أكتوبر", "T0001"), (manual.Manual!.UnitNumber, manual.Manual.ProjectName, manual.Manual.TowerName, manual.Manual.DueMonthAr, manual.Manual.TenantId));
        Assert.True(DispatchService.IsManualListId(ManualList, h.Genesys)); Assert.False(DispatchService.IsManualListId(CurrentMonthList, h.Genesys));
    }

    // ------------------------------------------------------------ schema validation

    [Fact]
    public void TheManualHeaderCheck_RequiresAllElevenColumns_AndNamesAMisnamedEmail()
    {
        Assert.True(GenesysContactTemplate.CheckHeader([.. ManualGenesysFile.Template], manual: true).Fits);
        Assert.True(GenesysContactTemplate.CheckHeader([.. ManualGenesysFile.Template, "Notes"], manual: true).Fits);                 // extra list columns are tolerated

        var sixOnly = GenesysContactTemplate.CheckHeader([.. AutoTemplate], manual: true);                                              // the old Manual shape
        Assert.False(sixOnly.Fits);
        Assert.Equal(["Email", "UnitNumber", "ProjectName", "TowerName", "DueMonthAr", "TenantId"], sixOnly.Missing);
        Assert.Contains("Email (found \"Email Address\")", sixOnly.Misnamed);

        Assert.False(GenesysContactTemplate.CheckHeader(ManualGenesysFile.Template.Where(c => c != "TenantId").ToList(), manual: true).Fits);
        Assert.False(GenesysContactTemplate.CheckHeader(ManualGenesysFile.Template.Select(c => c == "DueMonthAr" ? "DueMonthAR" : c).ToList(), manual: true).Fits);   // spelling is exact
        // Auto's check is not affected by the Manual template.
        Assert.True(GenesysContactTemplate.CheckHeader([.. AutoTemplate]).Fits);
        Assert.False(GenesysContactTemplate.CheckHeader([.. ManualGenesysFile.Template]).Fits);
    }

    [Fact]
    public async Task AManualSend_IsRefused_WhenTheManualListLacksAColumn_OrKeepsTheOldEmailAddress_NothingUploaded()
    {
        foreach (var columns in new[]
        {
            AutoTemplate,                                                                                                    // the old six-column Manual list
            ManualGenesysFile.Template.Where(c => c != "TenantId").ToArray(),                                                // one of the eleven missing
            ManualGenesysFile.Template.Select(c => c == "Email" ? "Email Address" : c).ToArray()                             // Auto's e-mail name on the Manual list
        })
        {
            using var h = await SeededAsync(x => x.Client.ListColumns[ManualList] = columns);
            var (_, confirm) = await h.PrepareAsync(All(), "refused");
            using var scope = h.NewScope();
            var result = await scope.Dispatch.ConfirmAsync(h.Manager, confirm, CancellationToken.None);
            Assert.Equal(CollectionsOutcome.InvalidRequest, result.Outcome);
            Assert.Contains("does not have the required column(s)", result.Detail);
            Assert.Empty(h.Client.Calls); Assert.Empty(h.Scheduler.Dispatches);                                              // nothing was uploaded, nothing queued
        }
    }

    [Fact]
    public async Task AManualExport_IsRefused_ForAListThatWasReadAndDoesNotFit()
    {
        using var h = await WithGenesysAsync(x => x.Client.ListColumns[ManualList] = AutoTemplate);
        var refused = await ExportAsync(h);
        Assert.Equal(CollectionsOutcome.InvalidRequest, refused.Outcome);
        Assert.Contains("does not match the template", refused.Detail); Assert.Contains("Nothing was exported", refused.Detail);
        Assert.Contains("misnamed column Email (found \"Email Address\")", refused.Detail);
        Assert.Contains("missing column TenantId", refused.Detail);
    }

    [Fact]
    public async Task AnAutoList_IsStillCheckedAgainstTheSixColumns_AndNeedsNoManualColumn()
    {
        using var h = await WithGenesysAsync(x => x.Client.ListColumns[CurrentMonthList] = AutoTemplate);
        var dto = (await ExportAsync(h, "Auto")).Value!;
        Assert.True(dto.SchemaVerified); Assert.Equal(AutoTemplate, GenesysContactTemplate.ParseCsv(dto.Csv)[0]);
        using var short_ = await WithGenesysAsync(x => x.Client.ListColumns[CurrentMonthList] = AutoTemplate.Where(c => c != "AmountDue").ToArray());
        Assert.Equal(CollectionsOutcome.InvalidRequest, (await ExportAsync(short_, "Auto")).Outcome);
    }

    [Fact]
    public async Task TheFileCheck_CatchesAWrongMonthOrAMissingIdentity_BeforeAFileIsReleased()
    {
        using var h = await SeededAsync();
        var csv = (await ExportAsync(h)).Value!.Csv;
        var types = GenesysContactTemplate.ExpectedReminderTypes(h.Genesys);
        Assert.True(GenesysContactTemplate.Verify(csv, types, manual: true).IsMatch);
        Assert.False(GenesysContactTemplate.Verify(csv.Replace("أكتوبر", "نوفمبر"), types, manual: true).IsMatch);                        // the month must follow the due date
        Assert.False(GenesysContactTemplate.Verify(csv.Replace("\"T0001\"", "\"\""), types, manual: true).IsMatch);                      // a TenantId is required
        Assert.False(GenesysContactTemplate.Verify(csv.Replace("\"TP140-1\"", "\"\""), types, manual: true).IsMatch);                    // so is a UnitNumber
        Assert.False(GenesysContactTemplate.Verify(csv, types, manual: false).IsMatch);                                                  // and it is not an Auto file
    }

    // ------------------------------------------------------------ Manual stays safe

    [Fact]
    public async Task TheManualSend_StillRefusesAnUnauthorizedCaller_AndTheExportStillNeedsTheSendRight()
    {
        using var h = await SeededAsync();
        using var scope = h.NewScope();
        Assert.Equal(CollectionsOutcome.Forbidden, (await scope.Export.ExportAsync(h.Agent, All(), CancellationToken.None)).Outcome);
        var (_, confirm) = await h.PrepareAsync(All(), "unauthorized");
        Assert.Equal(CollectionsOutcome.Forbidden, (await scope.Dispatch.ConfirmAsync(h.Agent, confirm, CancellationToken.None)).Outcome);
        Assert.Empty(h.Client.Calls); Assert.Empty(h.Scheduler.Dispatches);
    }

    [Fact]
    public async Task TheManualSend_IsStillIdempotent_AndAnExportNeverUploadsAnything()
    {
        using var h = await SeededAsync();
        await ExportAsync(h); await ExportAsync(h);
        Assert.Empty(h.Client.Calls);                                                                                         // exporting uploads nothing
        var (_, confirm) = await h.PrepareAsync(All(), "same-key");
        using (var scope = h.NewScope())
        {
            var first = await scope.Dispatch.ConfirmAsync(h.Manager, confirm, CancellationToken.None);
            var again = await scope.Dispatch.ConfirmAsync(h.Manager, confirm, CancellationToken.None);
            Assert.Equal(CollectionsOutcome.Accepted, first.Outcome); Assert.Equal(first.Value!.DispatchId, again.Value!.DispatchId);   // a replay, not a second dispatch
        }
        Assert.Single(h.Scheduler.Dispatches);
    }
}
