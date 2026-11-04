using System.IO.Compression;
using TigerCS.Application.Modules.Collections.Dto;
using TigerCS.Application.Modules.Collections.Services;

namespace TigerCS.Tests.Collections.Services;

public sealed class CollectionsCampaignReviewWorkbookTests
{
    private static CollectionsCampaignContactDto Row(string unit, string? towerNumber, string? towerName, decimal due, decimal overdue, string name = "A & B <Ltd>") =>
        new("rec-secret", "key", 1, "T-001", name, "+971500000000", "a@b.c", 5, unit, "", due + overdue, "AED", null,
            "CurrentMonthReminder", "cycle-secret", "Ready", "Qualifies", towerNumber, towerName, due, overdue);

    private static CollectionsCampaignPreviewDto Report(params CollectionsCampaignContactDto[] items) =>
        new(new DateOnly(2026, 11, 4), new DateOnly(2026, 11, 4), DateTime.UtcNow, "Snapshot", "CurrentMonthReminder", "cycle-secret",
            [], true, true, true, true, items.Length, items.Length, 0, 1, 25, items);

    private static string Entry(byte[] xlsx, string path)
    {
        using var zip = new ZipArchive(new MemoryStream(xlsx), ZipArchiveMode.Read);
        using var reader = new StreamReader(zip.GetEntry(path)!.Open());
        return reader.ReadToEnd();
    }

    [Theory]
    [InlineData("TP124-1001", "1001")]
    [InlineData("TP136-C-402", "C-402")]
    [InlineData("124-1001", "1001")]
    [InlineData("1001", "1001")]
    [InlineData("TP1001", "1001")]
    [InlineData("", "")]
    public void UnitNumber_DropsTheProjectPrefix(string code, string expected) =>
        Assert.Equal(expected, CollectionsCampaignReviewWorkbook.UnitNumber(code));

    [Fact]
    public void Tower_UsesTheNameElseTheCode()
    {
        Assert.Equal("Tiger Heights", CollectionsCampaignReviewWorkbook.TowerLabel(Row("TP124-1001", "124", "Tiger Heights", 1, 0)));
        Assert.Equal("124", CollectionsCampaignReviewWorkbook.TowerLabel(Row("TP124-1001", "124", "TP124", 1, 0)));
        Assert.Equal("124", CollectionsCampaignReviewWorkbook.TowerLabel(Row("TP124-1001", null, null, 1, 0)));
    }

    [Fact]
    public void Workbook_HasOnlyTheEightColumns_AndNoTechnicalFields()
    {
        var xlsx = CollectionsCampaignReviewWorkbook.Write(Report(Row("TP124-1001", "124", "Tiger Heights", 100.5m, 20m)));
        var sheet = Entry(xlsx, "xl/worksheets/sheet1.xml");
        foreach (var header in new[] { "CompanyId", "TenantId", "CustomerName", "Tower", "UnitNumber", "TotalAmount", "DueAmount", "OverdueAmount" })
            Assert.Contains($">{header}<", sheet);
        Assert.Equal(8, System.Text.RegularExpressions.Regex.Matches(sheet, "<c r=\"[A-Z]1\"").Count);
        foreach (var technical in new[] { "RecordId", "CycleKey", "ReadAtUtc", "Reason", "rec-secret", "cycle-secret", "Eligible", "Qualifies", "+971500000000" })
            Assert.DoesNotContain(technical, sheet);
        Assert.Contains("A &amp; B &lt;Ltd&gt;", sheet);
        Assert.Contains("<v>120.50</v>", sheet);   // Total = Due + Overdue
        Assert.Contains("<v>100.50</v>", sheet);
        Assert.Contains("<v>20.00</v>", sheet);
        Assert.Contains("state=\"frozen\"", sheet);
        Assert.Contains("<autoFilter ref=\"A1:H2\"/>", sheet);
        Assert.Contains(">1001<", sheet);
        Assert.DoesNotContain("TP124", sheet);
    }

    [Fact]
    public void Workbook_WithNoRows_StillHasAHeaderRow()
    {
        var sheet = Entry(CollectionsCampaignReviewWorkbook.Write(Report()), "xl/worksheets/sheet1.xml");
        Assert.Contains("<autoFilter ref=\"A1:H1\"/>", sheet);
    }
}
