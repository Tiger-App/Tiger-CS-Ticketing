using Microsoft.EntityFrameworkCore;
using TigerCS.Application.Modules.Collections.Abstractions;
using TigerCS.Application.Modules.Collections.Services;
using TigerCS.Application.Modules.Collections.Review;
using TigerCS.Domain.Modules.Collections.Review;

namespace TigerCS.Tests.Collections.Review;

/// <summary>
/// One customer who owns several units, and phone numbers. The SEND rule is unchanged: a number on several units is held back from sending (<c>ManualSharedPhone</c>). The FILE handles it
/// deliberately: several units of the same customer on one number are one row with the amounts added up (each unit has its own instalments, so nothing is counted twice) and the earliest due
/// date; an ambiguous allocation between the units, or different customers on one number, is left out and counted, never merged by guesswork. The harness clock is 15 Oct 2026.
/// </summary>
public sealed class GenesysExportMultiUnitTests
{
    private static PactReceivableInstalment UnitOne(string mobile, int day = 20) => ReviewHarness.Row(1, 5000m, day: day, mobile: mobile);

    private static PactReceivableInstalment UnitTwo(string mobile, int day = 25) =>
        ReviewHarness.Row(1, 700m, day: day, mobile: mobile) with { UnitId = 2001, UnitCode = "TP140-9", VoucherNumber = "INV-1-9" };

    private static async Task<ReviewHarness> SeededAsync(params PactReceivableInstalment[] rows)
    {
        var h = new ReviewHarness();
        h.Time.Advance(TimeSpan.FromDays(1)); h.Source.ReadAt = ReviewHarness.Now.AddDays(1);
        h.Source.Items.AddRange(rows);
        await h.RefreshAsync();
        return h;
    }

    private static ReviewFilter Manual() => new(ReminderType: "CurrentMonth", SendingMode: "Manual", MinRemaining: 0m);

    private static async Task<CollectionsResult<GenesysExportDto>> ExportAsync(ReviewHarness h)
    {
        using var scope = h.NewScope();
        return await scope.Export.ExportAsync(h.Manager, new SelectionRequest(Manual(), ReviewQueryService.ModeAllMatching), CancellationToken.None);
    }

    private static async Task<string> AuditAsync(ReviewHarness h)
    {
        using var context = h.CreateContext();
        return (await context.AuditEntries.AsNoTracking().Where(a => a.Action == GenesysExportService.AuditAction).SingleAsync()).AfterValue!;
    }

    [Fact]
    public async Task OneNumberOnTwoUnitsOfOneCustomer_IsOneRow_WithTheAmountsAddedUp_AndTheEarliestDueDate_WhileSendingStaysHeldBack()
    {
        using var h = await SeededAsync(UnitOne("971500000001"), UnitTwo("971500000001"));
        using (var scope = h.NewScope())
        {
            // The send rule is untouched: the page holds both units back, and nothing can be approved.
            var page = (await scope.Query.QueryAsync(h.Manager, Manual(), 1, 50, CancellationToken.None)).Value!;
            Assert.Equal(2, page.Items.Count); Assert.DoesNotContain(page.Items, i => i.ValidationStatus == "Ready");
            Assert.All(page.Items, i => Assert.Contains(i.Reasons, r => r.Code == ReviewReasons.ManualSharedPhone));
            Assert.Equal(0, (await scope.Query.SummarizeAsync(h.Manager, new SelectionRequest(Manual(), ReviewQueryService.ModeAllMatching), CancellationToken.None)).Value!.Count);
        }
        var dto = (await ExportAsync(h)).Value!;
        var rows = GenesysContactTemplate.ParseCsv(dto.Csv);
        Assert.Equal(1, dto.RowCount); Assert.Equal(2, dto.UnitCount); Assert.Equal(1, dto.CombinedRows); Assert.Equal(2, dto.CombinedUnits); Assert.Equal(0, dto.ExcludedUnits);
        Assert.Equal(new[] { "+971500000001", "Customer 1", "5700.00", "2026-10-20" }, new[] { rows[1][0], rows[1][1], rows[1][4], rows[1][5] });
        Assert.Equal(5700m, dto.TotalAmount);
        Assert.Contains(dto.Notices, n => n.Contains("2 unit(s) of the same customer on one number were combined into 1 row(s)"));
        Assert.Contains(dto.Notices, n => n.Contains("2 of the 2 customer unit(s) in this file are held back from sending (Held back 2)"));
        var audit = await AuditAsync(h);
        Assert.Contains("Rows=1;Units=2;CombinedRows=1;CombinedUnits=2;Excluded=0", audit); Assert.Contains("Total=AED 5700.00", audit); Assert.Contains("IncludedByStatus=Excluded:2", audit);
        Assert.Equal(3, audit.Split(";Customers=")[1].Split(',').Distinct().Count());        // two unit fingerprints and the one number, recorded once
    }

    [Fact]
    public async Task TwoNumbers_OnTwoUnits_AreTwoRows_EachWithItsOwnAmountAndDueDate()
    {
        using var h = await SeededAsync(UnitOne("971500000001"), UnitTwo("971500000002"));
        var dto = (await ExportAsync(h)).Value!;
        var rows = GenesysContactTemplate.ParseCsv(dto.Csv);
        Assert.Equal(3, rows.Count); Assert.Equal(2, dto.RowCount); Assert.Equal(0, dto.CombinedRows);
        var byPhone = rows.Skip(1).ToDictionary(r => r[0], StringComparer.Ordinal);
        Assert.Equal(new[] { "Customer 1", "5000.00", "2026-10-20" }, new[] { byPhone["+971500000001"][1], byPhone["+971500000001"][4], byPhone["+971500000001"][5] });
        Assert.Equal(new[] { "Customer 1", "700.00", "2026-10-25" }, new[] { byPhone["+971500000002"][1], byPhone["+971500000002"][4], byPhone["+971500000002"][5] });
        Assert.Equal(5700m, dto.TotalAmount);
    }

    [Fact]
    public async Task AnAmbiguousAllocationBetweenTheUnits_IsNotAddedUp_TheUnitsAreCountedOut()
    {
        // The same due date under two units of one tenant: the source may have allocated one instalment to both (UnitAllocationNeedsReview). Adding them up could count it twice, so neither is exported.
        using var h = await SeededAsync(UnitOne("971500000001", day: 20), UnitTwo("971500000001", day: 20), ReviewHarness.Row(2, 300m, day: 20));
        var dto = (await ExportAsync(h)).Value!;
        Assert.Equal(1, dto.RowCount); Assert.Equal("Customer 2", GenesysContactTemplate.ParseCsv(dto.Csv)[1][1]);
        var exclusion = Assert.Single(dto.Exclusions!);
        Assert.Equal((GenesysExportRows.UnitAllocationAmbiguous, 2), (exclusion.Code, exclusion.Units));
        Assert.Contains("Exclusions=UnitAllocationAmbiguous:2", await AuditAsync(h));
    }

    [Fact]
    public async Task OneNumberOnUnitsOfDifferentCustomers_IsNeverMerged_AndNeverDialledForTheWrongPerson()
    {
        using var h = await SeededAsync(ReviewHarness.Row(1, mobile: "971500000009"), ReviewHarness.Row(2, mobile: "971500000009"), ReviewHarness.Row(3));
        var dto = (await ExportAsync(h)).Value!;
        Assert.Equal(1, dto.RowCount); Assert.Equal("Customer 3", GenesysContactTemplate.ParseCsv(dto.Csv)[1][1]);
        Assert.Equal([(GenesysExportRows.SharedPhoneDifferentCustomers, 2)], dto.Exclusions!.Select(e => (e.Code, e.Units)));
        Assert.Equal(500m, dto.TotalAmount);                                                            // the total is the total of the rows that are in the file
        Assert.Contains("Rows=1;Units=1;CombinedRows=0;CombinedUnits=0;Excluded=2;Exclusions=SharedPhoneDifferentCustomers:2", await AuditAsync(h));
    }

    [Fact]
    public void TheRowBuilder_IsDeterministic_AndNeverInventsAValue()
    {
        static ReviewQueryService.ExportCandidate Unit(string tenant, string unit, string phone, decimal? amount, DateOnly? due, params string[] reasons) =>
            new(new CollectionsReviewRecord { CompanyId = 4, TenantId = tenant, UnitId = 1, UnitCode = unit, CustomerName = "N " + tenant, Phone = phone, Email = "", ReminderType = CampaignReminderType.CurrentMonth,
                RemainingAmount = amount, DueDate = due, Currency = "AED", RecordKey = tenant + unit }, ReviewValidationStatus.Ready, reasons);
        var candidates = new[]
        {
            Unit("B", "U2", "+971500000002", 10m, new(2026, 10, 25)),
            Unit("A", "U1", "+971500000001", 5m, new(2026, 10, 20)),
            Unit("A", "U3", "+971500000001", 7m, new(2026, 10, 5)),
            Unit("C", "U4", "", 9m, new(2026, 10, 1)),
            Unit("D", "U5", "+971500000005", null, new(2026, 10, 1)),
            Unit("E", "U6", "+971500000006", 3m, null)
        };
        var first = GenesysExportRows.Build(candidates); var again = GenesysExportRows.Build(candidates.Reverse().ToArray());
        Assert.Equal(first.Rows.Select(r => (r.Phone, r.Amount, r.DueDate)), again.Rows.Select(r => (r.Phone, r.Amount, r.DueDate)));
        Assert.Equal([("+971500000001", 12m, new DateOnly(2026, 10, 5)), ("+971500000002", 10m, new DateOnly(2026, 10, 25))], first.Rows.Select(r => (r.Phone, r.Amount, r.DueDate)));
        Assert.Equal([(GenesysExportRows.AmountUnavailable, 2), (GenesysExportRows.NoCallablePhone, 1)], first.Exclusions.Select(e => (e.Code, e.Units)));
        Assert.Equal((1, 2), (first.CombinedRows, first.CombinedUnits));
    }
}
