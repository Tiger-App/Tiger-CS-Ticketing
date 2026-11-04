using System.Globalization;
using System.IO.Compression;
using System.Text;
using TigerCS.Application.Modules.Collections.Dto;
using TigerCS.Domain.Modules.Collections;

namespace TigerCS.Application.Modules.Collections.Services;

/// <summary>
/// The employee-facing Campaigns review workbook (.xlsx): ONE row per customer/unit with only
/// CompanyId, TenantId, CustomerName, Tower, UnitNumber, TotalAmount, DueAmount, OverdueAmount.
/// It is a pure presentation of the already-validated preview items (same filters, same snapshot amounts);
/// it recalculates nothing. Audit / eligibility fields (RecordId, CycleKey, ReadAtUtc, Status, Reason, channel flags ...)
/// stay on the backend items and in the audit CSV. Written with System.IO.Compression only (no spreadsheet dependency).
/// </summary>
public static class CollectionsCampaignReviewWorkbook
{
    public const string ContentType = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";

    public static readonly IReadOnlyList<string> Headers =
        ["CompanyId", "TenantId", "CustomerName", "Tower", "UnitNumber", "TotalAmount", "DueAmount", "OverdueAmount"];

    private static readonly double[] Widths = [11, 16, 34, 24, 14, 16, 16, 16];

    /// <summary>The tower name where CRM/PACT holds a real one, else the tower code (never a made-up name).</summary>
    public static string TowerLabel(CollectionsCampaignContactDto c) =>
        CollectionsTowerName.Display(c.TowerName, c.TowerNumber ?? CollectionsTowerNumber.Parse(c.UnitCode), "");

    /// <summary>
    /// The apartment number only: drops the project prefix (<c>TP124-1001</c> -> <c>1001</c>, <c>TP136-C-402</c> -> <c>C-402</c>).
    /// A code without the tower segment is only stripped of a leading "TP".
    /// </summary>
    public static string UnitNumber(string? unitCode)
    {
        var value = unitCode?.Trim() ?? "";
        if (value.Length == 0) return "";
        var hadPrefix = value.StartsWith("TP", StringComparison.OrdinalIgnoreCase);
        if (hadPrefix) value = value[2..].TrimStart();
        var hyphen = value.IndexOf('-');
        // Only drop the tower segment when the code really is "<tower>-<unit>" (project-prefixed or numeric tower).
        if (hyphen > 0 && hyphen < value.Length - 1 && (hadPrefix || value[..hyphen].All(char.IsAsciiDigit)))
            value = value[(hyphen + 1)..];
        return value.Trim();
    }

    public static byte[] Write(CollectionsCampaignPreviewDto report)
    {
        var rows = report.Items;
        using var buffer = new MemoryStream();
        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            Add(zip, "[Content_Types].xml",
                """<?xml version="1.0" encoding="UTF-8" standalone="yes"?><Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types"><Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/><Default Extension="xml" ContentType="application/xml"/><Override PartName="/xl/workbook.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml"/><Override PartName="/xl/worksheets/sheet1.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml"/><Override PartName="/xl/styles.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.styles+xml"/></Types>""");
            Add(zip, "_rels/.rels",
                """<?xml version="1.0" encoding="UTF-8" standalone="yes"?><Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships"><Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument" Target="xl/workbook.xml"/></Relationships>""");
            var lastRow = Math.Max(1, rows.Count + 1);
            Add(zip, "xl/workbook.xml",
                $"""<?xml version="1.0" encoding="UTF-8" standalone="yes"?><workbook xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main" xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships"><sheets><sheet name="Campaign Review" sheetId="1" r:id="rId1"/></sheets><definedNames><definedName name="_xlnm._FilterDatabase" localSheetId="0" hidden="1">'Campaign Review'!$A$1:$H${lastRow}</definedName></definedNames></workbook>""");
            Add(zip, "xl/_rels/workbook.xml.rels",
                """<?xml version="1.0" encoding="UTF-8" standalone="yes"?><Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships"><Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet" Target="worksheets/sheet1.xml"/><Relationship Id="rId2" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/styles" Target="styles.xml"/></Relationships>""");
            // Style 0 = default, 1 = bold header on a grey fill, 2 = number with two decimals and a thousands separator (#,##0.00).
            Add(zip, "xl/styles.xml",
                """<?xml version="1.0" encoding="UTF-8" standalone="yes"?><styleSheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main"><fonts count="2"><font><sz val="11"/><name val="Calibri"/></font><font><b/><sz val="11"/><name val="Calibri"/></font></fonts><fills count="3"><fill><patternFill patternType="none"/></fill><fill><patternFill patternType="gray125"/></fill><fill><patternFill patternType="solid"><fgColor rgb="FFD9E1F2"/><bgColor indexed="64"/></patternFill></fill></fills><borders count="1"><border><left/><right/><top/><bottom/><diagonal/></border></borders><cellStyleXfs count="1"><xf numFmtId="0" fontId="0" fillId="0" borderId="0"/></cellStyleXfs><cellXfs count="3"><xf numFmtId="0" fontId="0" fillId="0" borderId="0" xfId="0"/><xf numFmtId="0" fontId="1" fillId="2" borderId="0" xfId="0" applyFont="1" applyFill="1" applyAlignment="1"><alignment vertical="center"/></xf><xf numFmtId="4" fontId="0" fillId="0" borderId="0" xfId="0" applyNumberFormat="1"/></cellXfs><cellStyles count="1"><cellStyle name="Normal" xfId="0" builtinId="0"/></cellStyles></styleSheet>""");
            Add(zip, "xl/worksheets/sheet1.xml", Sheet(rows, lastRow));
        }
        return buffer.ToArray();
    }

    private static string Sheet(IReadOnlyList<CollectionsCampaignContactDto> rows, int lastRow)
    {
        var xml = new StringBuilder();
        xml.Append("""<?xml version="1.0" encoding="UTF-8" standalone="yes"?><worksheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main">""");
        xml.Append($"<dimension ref=\"A1:H{lastRow}\"/>");
        xml.Append("""<sheetViews><sheetView workbookViewId="0"><pane ySplit="1" topLeftCell="A2" activePane="bottomLeft" state="frozen"/><selection pane="bottomLeft" activeCell="A2" sqref="A2"/></sheetView></sheetViews><sheetFormatPr defaultRowHeight="15"/>""");
        xml.Append("<cols>");
        for (var i = 0; i < Widths.Length; i++)
            xml.Append(CultureInfo.InvariantCulture, $"<col min=\"{i + 1}\" max=\"{i + 1}\" width=\"{Widths[i]}\" customWidth=\"1\"/>");
        xml.Append("</cols><sheetData>");

        xml.Append("<row r=\"1\">");
        for (var i = 0; i < Headers.Count; i++) xml.Append(Text(Column(i) + "1", Headers[i], style: 1));
        xml.Append("</row>");

        var r = 2;
        foreach (var c in rows)
        {
            xml.Append(CultureInfo.InvariantCulture, $"<row r=\"{r}\">");
            xml.Append(Number($"A{r}", c.CompanyId, style: 0, decimals: 0));
            xml.Append(Text($"B{r}", c.TenantId));
            xml.Append(Text($"C{r}", c.CustomerName));
            xml.Append(Text($"D{r}", TowerLabel(c)));
            xml.Append(Text($"E{r}", UnitNumber(c.UnitCode)));
            xml.Append(Number($"F{r}", c.TotalAmount, style: 2));
            xml.Append(Number($"G{r}", c.DueAmount, style: 2));
            xml.Append(Number($"H{r}", c.OverdueAmount, style: 2));
            xml.Append("</row>");
            r++;
        }
        xml.Append("</sheetData>");
        xml.Append(CultureInfo.InvariantCulture, $"<autoFilter ref=\"A1:H{lastRow}\"/>");
        xml.Append("</worksheet>");
        return xml.ToString();
    }

    private static string Column(int index) => ((char)('A' + index)).ToString();

    private static string Text(string cell, string? value, int style = 0) =>
        $"<c r=\"{cell}\" t=\"inlineStr\"{(style == 0 ? "" : $" s=\"{style}\"")}><is><t xml:space=\"preserve\">{Escape(value ?? "")}</t></is></c>";

    private static string Number(string cell, decimal value, int style, int decimals = 2) =>
        $"<c r=\"{cell}\"{(style == 0 ? "" : $" s=\"{style}\"")}><v>{Math.Round(value, decimals, MidpointRounding.AwayFromZero).ToString(decimals == 0 ? "0" : "0.00", CultureInfo.InvariantCulture)}</v></c>";

    private static string Escape(string value)
    {
        var text = new StringBuilder(value.Length);
        foreach (var ch in value)
        {
            if (ch is '\t' or '\n' or '\r' || (ch >= 0x20 && ch != 0xFFFE && ch != 0xFFFF && !char.IsSurrogate(ch)) || char.IsSurrogate(ch))
                text.Append(ch switch { '&' => "&amp;", '<' => "&lt;", '>' => "&gt;", '"' => "&quot;", _ => ch.ToString() });
        }
        return text.ToString();
    }

    private static void Add(ZipArchive zip, string path, string content)
    {
        var entry = zip.CreateEntry(path, CompressionLevel.Optimal);
        using var stream = entry.Open();
        var bytes = new UTF8Encoding(false).GetBytes(content);
        stream.Write(bytes, 0, bytes.Length);
    }
}
