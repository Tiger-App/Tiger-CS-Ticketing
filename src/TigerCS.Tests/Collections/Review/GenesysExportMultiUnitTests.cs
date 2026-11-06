using TigerCS.Application.Modules.Collections.Abstractions;
using TigerCS.Application.Modules.Collections.Services;
using TigerCS.Application.Modules.Collections.Review;
using TigerCS.Domain.Modules.Collections.Review;

namespace TigerCS.Tests.Collections.Review;

/// <summary>
/// One customer who owns several units. Each unit is its own record with its own amount and due date: units are never merged. A phone number that several units share is held back in full, so
/// one number never becomes two rows of the Genesys file. Numbers that differ give one row per unit, each with that unit's amount and due date. The harness clock is 15 Oct 2026, a day on which
/// no stage is scheduled, so every record is a Manual candidate.
/// </summary>
public sealed class GenesysExportMultiUnitTests
{
    private static PactReceivableInstalment UnitOne(string mobile) => ReviewHarness.Row(1, 5000m, day: 20, mobile: mobile);

    private static PactReceivableInstalment UnitTwo(string mobile) =>
        ReviewHarness.Row(1, 700m, day: 25, mobile: mobile) with { UnitId = 2001, UnitCode = "TP140-9", VoucherNumber = "INV-1-9" };

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

    [Fact]
    public async Task OneNumberOnTwoUnits_IsHeldBackInFull_AndNeverBecomesTwoRows()
    {
        using var h = await SeededAsync(UnitOne("971500000001"), UnitTwo("971500000001"));
        using (var scope = h.NewScope())
        {
            var page = (await scope.Query.QueryAsync(h.Manager, Manual(), 1, 50, CancellationToken.None)).Value!;
            Assert.Equal(2, page.Items.Count);
            Assert.DoesNotContain(page.Items, i => i.ValidationStatus == "Ready");
            Assert.All(page.Items, i => Assert.Contains(i.Reasons, r => r.Code == ReviewReasons.ManualSharedPhone));
        }
        var refused = await ExportAsync(h);
        Assert.False(refused.IsSuccess); Assert.Null(refused.Value); Assert.Contains("No Ready customers", refused.Detail);
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
    }
}
