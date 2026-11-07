using Microsoft.EntityFrameworkCore;
using TigerCS.Application.Modules.Collections.Abstractions;
using TigerCS.Application.Modules.Collections.Services;
using TigerCS.Application.Modules.Collections.Review;
using TigerCS.Domain.Modules.Collections.Review;

namespace TigerCS.Tests.Collections.Review;

/// <summary>
/// One customer who owns several units, and phone numbers. The SEND rule is unchanged: a number on several units is held back from sending (<c>ManualSharedPhone</c>). The FILE is one row per
/// unit, exactly as Preview lists them: a phone number never merges, adds up, de-duplicates or drops a record. A unit is left out only when a mandatory field is genuinely missing, and it is
/// then named. The harness clock is 15 Oct 2026.
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
    public async Task OneNumberOnTwoUnitsOfOneCustomer_IsTwoRows_EachWithItsOwnAmountAndDueDate_WhileSendingStaysHeldBack()
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
        Assert.Equal(2, dto.RowCount); Assert.Equal(2, dto.UnitCount); Assert.Equal(0, dto.ExcludedUnits);
        Assert.Equal(new[] { "+971500000001", "Customer 1", "5000.00", "2026-10-20" }, new[] { rows[1][0], rows[1][1], rows[1][4], rows[1][5] });
        Assert.Equal(new[] { "+971500000001", "Customer 1", "700.00", "2026-10-25" }, new[] { rows[2][0], rows[2][1], rows[2][4], rows[2][5] });
        Assert.Equal(5700m, dto.TotalAmount);
        Assert.DoesNotContain(dto.Notices, n => n.Contains("combined"));
        Assert.Contains(dto.Notices, n => n.Contains("2 of the 2 customer unit(s) in this file are held back from sending (Held back 2)"));
        var audit = await AuditAsync(h);
        Assert.Contains("Rows=2;Units=2;Excluded=0", audit); Assert.Contains("Total=AED 5700.00", audit); Assert.Contains("IncludedByStatus=Excluded:2", audit);
        Assert.Equal(3, audit.Split(";Customers=")[1].Split(',').Distinct().Count());        // two unit fingerprints and the one number, recorded once
    }

    [Fact]
    public async Task TwoNumbers_OnTwoUnits_AreTwoRows_EachWithItsOwnAmountAndDueDate()
    {
        using var h = await SeededAsync(UnitOne("971500000001"), UnitTwo("971500000002"));
        var dto = (await ExportAsync(h)).Value!;
        var rows = GenesysContactTemplate.ParseCsv(dto.Csv);
        Assert.Equal(3, rows.Count); Assert.Equal(2, dto.RowCount);
        var byPhone = rows.Skip(1).ToDictionary(r => r[0], StringComparer.Ordinal);
        Assert.Equal(new[] { "Customer 1", "5000.00", "2026-10-20" }, new[] { byPhone["+971500000001"][1], byPhone["+971500000001"][4], byPhone["+971500000001"][5] });
        Assert.Equal(new[] { "Customer 1", "700.00", "2026-10-25" }, new[] { byPhone["+971500000002"][1], byPhone["+971500000002"][4], byPhone["+971500000002"][5] });
        Assert.Equal(5700m, dto.TotalAmount);
    }

    [Fact]
    public async Task AnAmbiguousAllocationBetweenTheUnits_StillExportsEachUnitWithItsOwnAmount_Labelled()
    {
        // The same due date under two units of one tenant (UnitAllocationNeedsReview): each unit is written with its own stored amount, nothing is added up, and the file says the units need review.
        using var h = await SeededAsync(UnitOne("971500000001", day: 20), UnitTwo("971500000001", day: 20), ReviewHarness.Row(2, 300m, day: 20));
        var dto = (await ExportAsync(h)).Value!;
        Assert.Equal(3, dto.RowCount); Assert.Equal(0, dto.ExcludedUnits);
        Assert.Equal(["5000.00", "700.00", "300.00"], GenesysContactTemplate.ParseCsv(dto.Csv).Skip(1).Select(r => r[4]));
        Assert.False(dto.DataVerified); Assert.Contains(dto.UnverifiedReasons!, r => r.StartsWith(ReviewReasons.UnitAllocationNeedsReview + ":", StringComparison.Ordinal));
        Assert.Contains("Excluded=0;Exclusions=;", await AuditAsync(h));
    }

    [Fact]
    public async Task OneNumberOnUnitsOfDifferentCustomers_IsOneRowEach_NeverMergedAndNeverDropped()
    {
        using var h = await SeededAsync(ReviewHarness.Row(1, mobile: "971500000009"), ReviewHarness.Row(2, mobile: "971500000009"), ReviewHarness.Row(3));
        var dto = (await ExportAsync(h)).Value!;
        var rows = GenesysContactTemplate.ParseCsv(dto.Csv);
        Assert.Equal(3, dto.RowCount); Assert.Empty(dto.Exclusions!);
        Assert.Equal(["Customer 1", "Customer 2", "Customer 3"], rows.Skip(1).Select(r => r[1]));
        Assert.Equal(["+971500000009", "+971500000009", "+971500000003"], rows.Skip(1).Select(r => r[0]));
        Assert.Equal(1500m, dto.TotalAmount);
        Assert.Contains("Rows=3;Units=3;Excluded=0;Exclusions=", await AuditAsync(h));
    }

    [Fact]
    public void TheRowBuilder_IsDeterministic_OneRowPerUnit_NamesWhatItLeavesOut_AndNeverInventsAValue()
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
            Unit("E", "U6", "+971500000006", 3m, null),
            Unit("F", "U7", "+971500000001", 4m, new(2026, 10, 2)),                       // a different customer on A's number: its own row
            Unit("G", "U8", "+971500000008", 1.2345m, new(2026, 10, 3), ReviewReasons.AmountPrecisionNeedsReview)
        };
        var first = GenesysExportRows.Build(candidates); var again = GenesysExportRows.Build(candidates.Reverse().ToArray());
        Assert.Equal(first.Rows.Select(r => (r.Phone, r.Amount, r.DueDate)), again.Rows.Select(r => (r.Phone, r.Amount, r.DueDate)));
        Assert.Equal([("+971500000001", 5m, new DateOnly(2026, 10, 20)), ("+971500000001", 7m, new DateOnly(2026, 10, 5)), ("+971500000002", 10m, new DateOnly(2026, 10, 25)), ("+971500000001", 4m, new DateOnly(2026, 10, 2))],
            first.Rows.Select(r => (r.Phone, r.Amount, r.DueDate)));
        Assert.All(first.Rows, r => Assert.Single(r.Units));
        Assert.Equal([(GenesysExportRows.AmountPrecisionUnresolved, 1), (GenesysExportRows.AmountUnavailable, 2), (GenesysExportRows.NoCallablePhone, 1)], first.Exclusions.Select(e => (e.Code, e.Units)));
        Assert.Equal([("U4", GenesysExportRows.NoCallablePhone), ("U5", GenesysExportRows.AmountUnavailable), ("U6", GenesysExportRows.AmountUnavailable), ("U8", GenesysExportRows.AmountPrecisionUnresolved)],
            first.ExcludedRecords.Select(e => (e.UnitCode, e.Code)));
    }

    /// <summary>The reported case: Preview lists 37 customer units, the file must hold 37 rows. Same customer / several units, different customers / one number, same customer and number / several units.</summary>
    [Fact]
    public async Task ThirtySevenPreviewRecords_AreThirtySevenRows_WhateverThePhoneNumbers()
    {
        var rows = new List<PactReceivableInstalment>();
        for (var n = 1; n <= 20; n++) rows.Add(ReviewHarness.Row(n, 100m * n, day: 20));                                      // 20 customers, own numbers
        for (var n = 21; n <= 25; n++) rows.Add(ReviewHarness.Row(n, 50m, day: 21, mobile: "971500000777"));                 // 5 different customers on ONE number
        for (var u = 0; u < 6; u++) rows.Add(ReviewHarness.Row(26, 10m + u, day: 22) with { UnitId = 3000 + u, UnitCode = $"TP140-26-{u}", VoucherNumber = $"INV-26-{u}" });   // 1 customer, 6 units, one number
        for (var u = 0; u < 6; u++) rows.Add(ReviewHarness.Row(27, 20m + u, day: 23 + u, mobile: $"97150000090{u}") with { UnitId = 4000 + u, UnitCode = $"TP140-27-{u}", VoucherNumber = $"INV-27-{u}" });   // 1 customer, 6 units, 6 numbers
        Assert.Equal(37, rows.Count);
        using var h = await SeededAsync([.. rows]);
        using (var scope = h.NewScope())
        {
            var page = (await scope.Query.QueryAsync(h.Manager, Manual(), 1, 100, CancellationToken.None)).Value!;
            Assert.Equal(37, page.TotalCount);
            var dto = (await scope.Export.ExportAsync(h.Manager, new SelectionRequest(Manual(), ReviewQueryService.ModeAllMatching), CancellationToken.None)).Value!;
            var csv = GenesysContactTemplate.ParseCsv(dto.Csv);
            Assert.Equal(37, dto.RowCount); Assert.Equal(37, dto.UnitCount); Assert.Equal(37, csv.Count - 1); Assert.Empty(dto.Exclusions!);
            // Compared by unit identity, not by number: every Preview record is in the file once, with its own amount and date.
            var previewUnits = page.Items.Select(i => (i.UnitCode, i.RemainingAmount, i.DueDate)).OrderBy(x => x.UnitCode, StringComparer.Ordinal).ToList();
            var exported = (await ExportedUnitsAsync(scope, dto)).OrderBy(x => x.UnitCode, StringComparer.Ordinal).ToList();
            Assert.Equal(previewUnits.Select(p => p.UnitCode), exported.Select(e => e.UnitCode));
            Assert.Equal(previewUnits.Select(p => p.RemainingAmount!.Value.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture)), exported.Select(e => e.Amount));
            Assert.Equal(previewUnits.Select(p => p.DueDate!.Value.ToString("yyyy-MM-dd")), exported.Select(e => e.DueDate));
            Assert.Equal(6, csv.Skip(1).Count(r => r[1] == "Customer 26" && r[0] == "+971500000026"));                        // same customer, same number, six units: six rows
            Assert.Equal(5, csv.Skip(1).Count(r => r[0] == "+971500000777"));                                                 // five different customers on one number: five rows
            Assert.Equal(["Phone", "CustomerName", "Email Address", "ReminderType", "AmountDue", "DueDate"], csv[0]);          // the six columns, unchanged
        }
    }

    /// <summary>The file's rows tied back to their unit by the stable identity the audit keeps (the unit fingerprint), not by the phone number.</summary>
    private static async Task<List<(string UnitCode, string Amount, string DueDate)>> ExportedUnitsAsync(ReviewHarness.Scope scope, GenesysExportDto dto)
    {
        var resolved = await scope.Query.ResolveForExportAsync(new SelectionRequest(Manual(), ReviewQueryService.ModeAllMatching), CancellationToken.None);
        var rows = GenesysExportRows.Build(resolved.Candidates).Rows;
        var csv = GenesysContactTemplate.ParseCsv(dto.Csv).Skip(1).ToList();
        Assert.Equal(rows.Count, csv.Count);
        return rows.Select((r, i) => (r.First.UnitCode, csv[i][4], csv[i][5])).ToList();
    }
}
