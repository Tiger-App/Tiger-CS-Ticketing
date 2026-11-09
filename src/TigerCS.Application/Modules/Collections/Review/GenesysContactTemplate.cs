using System.Globalization;
using System.Net.Mail;
using System.Text;
using System.Text.RegularExpressions;
using TigerCS.Domain.Modules.Collections;
using TigerCS.Domain.Modules.Collections.Review;

namespace TigerCS.Application.Modules.Collections.Review;

public sealed record TemplateIssue(int Row, string Column, string Message);

/// <summary>How a list's actual column names compare with the six template columns.</summary>
public sealed record TemplateHeaderCheck(IReadOnlyList<string> Missing, IReadOnlyList<string> Misnamed, IReadOnlyList<string> Unexpected)
{
    /// <summary>Every template column is present with its exact name (extra columns are allowed here; they are reported separately).</summary>
    public bool Fits => Missing.Count == 0 && Misnamed.Count == 0;
}

public sealed record TemplateVerification(
    IReadOnlyList<string> Columns, IReadOnlyList<string> MissingColumns, IReadOnlyList<string> UnexpectedColumns,
    IReadOnlyList<string> MisnamedColumns, IReadOnlyList<string> ReminderTypeValues, IReadOnlyList<string> UnknownReminderTypes,
    int RowCount, IReadOnlyList<TemplateIssue> Issues)
{
    public bool IsMatch => MissingColumns.Count == 0 && MisnamedColumns.Count == 0 && UnknownReminderTypes.Count == 0 && Issues.Count == 0;
}

/// <summary>
/// The Genesys outbound contact-list shape: exactly six data columns, every value a string. This type writes that shape and checks any
/// CSV (a Genesys list export or template, or a file produced here) against it, reporting the exact ReminderType values found.
/// </summary>
public static class GenesysContactTemplate
{
    /// <summary>The six data columns, in Genesys order. <c>Email Address</c> has a space; the others do not.</summary>
    public static readonly IReadOnlyList<string> Columns = ["Phone", "CustomerName", "Email Address", "ReminderType", "AmountDue", "DueDate"];

    /// <summary>
    /// The official Genesys template of the dedicated Manual contact list: eleven columns, in this order and spelling. The e-mail column is <c>Email</c> here (Auto's is <c>Email Address</c>).
    /// Contact details come from CRM, financial values from PACT; <c>TenantId</c> is the PACT tenant, never a CRM CustomerId.
    /// </summary>
    public static readonly IReadOnlyList<string> ManualColumns =
        ["Phone", "Email", "CustomerName", "ReminderType", "AmountDue", "UnitNumber", "ProjectName", "TowerName", "DueDate", "DueMonthAr", "TenantId"];

    /// <summary>The template of a list: the eleven Manual columns or the six Auto columns.</summary>
    public static IReadOnlyList<string> ColumnsFor(bool manual) => manual ? ManualColumns : Columns;

    private static readonly string[] ArabicMonths =
        ["يناير", "فبراير", "مارس", "أبريل", "مايو", "يونيو", "يوليو", "أغسطس", "سبتمبر", "أكتوبر", "نوفمبر", "ديسمبر"];

    /// <summary>The Arabic name of a date's month (calendar months, not culture data, so it never depends on the server's locale).</summary>
    public static string ArabicMonthName(DateOnly date) => ArabicMonths[date.Month - 1];

    /// <summary>The value of every column of a payload, by the column name of its list's template (Manual or Auto). Exactly what an upload sends.</summary>
    public static IReadOnlyDictionary<string, string> ValuesOf(GenesysContactPayload c) => c.Manual is { } m
        ? new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["Phone"] = c.Phone, ["Email"] = c.EmailAddress, ["CustomerName"] = c.CustomerName, ["ReminderType"] = c.ReminderType, ["AmountDue"] = c.AmountDue,
            ["UnitNumber"] = m.UnitNumber, ["ProjectName"] = m.ProjectName, ["TowerName"] = m.TowerName, ["DueDate"] = c.DueDate, ["DueMonthAr"] = m.DueMonthAr, ["TenantId"] = m.TenantId
        }
        : new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["Phone"] = c.Phone, ["CustomerName"] = c.CustomerName, ["Email Address"] = c.EmailAddress, ["ReminderType"] = c.ReminderType, ["AmountDue"] = c.AmountDue, ["DueDate"] = c.DueDate
        };

    private static readonly Regex E164 = new(@"^\+[1-9]\d{7,14}$", RegexOptions.Compiled);
    private static readonly Regex Amount = new(@"^\d+\.\d{2}$", RegexOptions.Compiled);

    public static IReadOnlyList<string> ExpectedReminderTypes(GenesysOutboundOptions options) =>
        Enum.GetValues<CampaignReminderType>().Select(options.LabelFor).ToList();

    /// <summary>The file of payloads that all belong to one list: the eleven Manual columns when they are Manual payloads, else the six Auto columns. Nothing else is written.</summary>
    public static string WriteCsv(IEnumerable<GenesysContactPayload> contacts)
    {
        var list = contacts.ToList();
        var columns = ColumnsFor(list.Count > 0 && list.All(c => c.Manual is not null));
        var text = new StringBuilder();
        text.Append(string.Join(',', columns.Select(Quote))).Append("\r\n");
        foreach (var c in list)
        {
            var values = ValuesOf(c);
            text.Append(string.Join(',', columns.Select(name => Quote(Neutralize(values[name], name == "Phone"))))).Append("\r\n");
        }
        return text.ToString();
    }

    // A value beginning with = + - @ (or a tab or carriage return, which a spreadsheet also reads as the start of a formula) would be executed. The phone's own leading + is the one legitimate
    // case: in any other column a leading + is neutralized too.
    private static string Neutralize(string value, bool isPhone) =>
        value.Length > 0 && (value[0] is '=' or '-' or '@' or '\t' or '\r' || (value[0] == '+' && !isPhone)) ? "'" + value : value;

    private static string Quote(string value) => "\"" + value.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";

    public static TemplateVerification Verify(string csv, IReadOnlyCollection<string> expectedReminderTypes, bool manual = false)
    {
        var template = ColumnsFor(manual);
        var records = ParseCsv(csv);
        if (records.Count == 0)
            return new([], template, [], [], [], [], 0, [new(0, "", "The file is empty.")]);
        var header = records[0].Select(h => h.Trim().TrimStart('﻿')).ToList();
        var check = CheckHeader(header, manual);
        var missing = check.Missing; var misnamed = check.Misnamed; var unexpected = check.Unexpected;

        var index = template.ToDictionary(c => c, c => header.FindIndex(h => h == c));
        var issues = new List<TemplateIssue>();
        var values = new SortedSet<string>(StringComparer.Ordinal);
        for (var row = 1; row < records.Count; row++)
        {
            string Cell(string column) => index[column] is var i && i >= 0 && i < records[row].Count ? records[row][i] : "";
            void Check(string column, bool ok, string message) { if (index[column] >= 0 && !ok) issues.Add(new(row, column, message)); }
            Check("Phone", E164.IsMatch(Cell("Phone")), $"\"{Cell("Phone")}\" is not an international (E.164) number");
            Check("CustomerName", Cell("CustomerName").Trim().Length > 0, "empty");
            var emailColumn = manual ? "Email" : "Email Address";
            var email = Cell(emailColumn);
            Check(emailColumn, email.Length == 0 || (MailAddress.TryCreate(email, out var mail) && mail.Address == email), $"\"{email}\" is not an email address");
            if (index["ReminderType"] >= 0) values.Add(Cell("ReminderType"));
            Check("AmountDue", Amount.IsMatch(Cell("AmountDue")), $"\"{Cell("AmountDue")}\" is not a plain number with exactly two decimals");
            var dueOk = DateOnly.TryParseExact(Cell("DueDate"), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var due);
            Check("DueDate", dueOk, $"\"{Cell("DueDate")}\" is not yyyy-MM-dd");
            if (manual)
            {
                // The identity values are the unit's and tenant's own; a file without them cannot be matched to a unit. Project and tower may legitimately be empty (never invented).
                Check("UnitNumber", Cell("UnitNumber").Trim().Length > 0, "empty");
                Check("TenantId", Cell("TenantId").Trim().Length > 0, "empty");
                Check("DueMonthAr", dueOk && Cell("DueMonthAr") == ArabicMonthName(due), $"\"{Cell("DueMonthAr")}\" is not the Arabic month of the due date");
            }
        }
        var unknown = values.Where(v => !expectedReminderTypes.Contains(v, StringComparer.Ordinal)).ToList();
        return new(header, missing, unexpected, misnamed, values.ToList(), unknown, records.Count - 1, issues);
    }

    /// <summary>
    /// Compares a list's column names with the template. A column that matches only ignoring case or spacing (for example <c>EmailAddress</c>, <c>Amount Due</c>) is reported as misnamed with the name
    /// that was found, never silently accepted: an import maps columns by their exact names.
    /// </summary>
    public static TemplateHeaderCheck CheckHeader(IReadOnlyList<string> header, bool manual = false)
    {
        var template = ColumnsFor(manual);
        var missing = template.Where(c => !header.Contains(c, StringComparer.Ordinal)).ToList();
        static string Key(string v) => new string(v.Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();
        var misnamed = missing.Where(m => header.Any(h => Key(h) == Key(m))).Select(m => $"{m} (found \"{header.First(h => Key(h) == Key(m))}\")").ToList();
        // Manual only: Auto's "Email Address" is the one wrong name this list is most likely to carry; it is named so the fix is obvious.
        if (manual && missing.Contains("Email") && header.Contains("Email Address", StringComparer.Ordinal)) misnamed.Add("Email (found \"Email Address\")");
        var unexpected = header.Where(h => !template.Contains(h, StringComparer.Ordinal) && !missing.Any(m => Key(m) == Key(h))).ToList();
        return new(missing, misnamed, unexpected);
    }

    /// <summary>
    /// The import file for a list whose actual columns are <paramref name="columns"/>: the template columns of the payloads' list in template order, then any further column the list has (left empty,
    /// in the list's own order), because a list rejects or mis-maps a file that lacks one of its columns. Each value is the exact string the upload sends.
    /// A Manual file is exactly the eleven Manual columns: its upload sends exactly those, so the file is the same payload.
    /// </summary>
    public static string WriteCsv(IEnumerable<GenesysContactPayload> contacts, IReadOnlyList<string> columns)
    {
        var list = contacts.ToList();
        var manual = list.Count > 0 && list.All(c => c.Manual is not null);
        var template = ColumnsFor(manual);
        var header = manual ? template.ToList() : template.Concat(columns.Where(c => !template.Contains(c, StringComparer.Ordinal)).Distinct(StringComparer.Ordinal)).ToList();
        var text = new StringBuilder();
        // The list's own column names come from Genesys: neutralized like any cell (the template names start with a letter, so they are unchanged).
        text.Append(string.Join(',', header.Select(h => Quote(Neutralize(h, isPhone: false))))).Append("\r\n");
        foreach (var c in list)
        {
            var values = ValuesOf(c);
            text.Append(string.Join(',', header.Select(h => Quote(Neutralize(values.TryGetValue(h, out var v) ? v : "", h == "Phone"))))).Append("\r\n");
        }
        return text.ToString();
    }

    /// <summary>RFC 4180 CSV: quoted fields, doubled quotes, embedded commas and newlines.</summary>
    public static List<List<string>> ParseCsv(string text)
    {
        var rows = new List<List<string>>();
        var row = new List<string>();
        var field = new StringBuilder();
        var quoted = false;
        for (var i = 0; i < text.Length; i++)
        {
            var ch = text[i];
            if (quoted)
            {
                if (ch == '"' && i + 1 < text.Length && text[i + 1] == '"') { field.Append('"'); i++; }
                else if (ch == '"') quoted = false;
                else field.Append(ch);
            }
            else if (ch == '"') quoted = true;
            else if (ch == ',') { row.Add(field.ToString()); field.Clear(); }
            else if (ch is '\r' or '\n')
            {
                if (ch == '\r' && i + 1 < text.Length && text[i + 1] == '\n') i++;
                row.Add(field.ToString()); field.Clear();
                if (row.Count > 1 || row[0].Length > 0) rows.Add(row);
                row = [];
            }
            else field.Append(ch);
        }
        if (field.Length > 0 || row.Count > 0) { row.Add(field.ToString()); rows.Add(row); }
        return rows;
    }

    /// <summary>
    /// How the pre-existing campaign "Genesys CSV" export (<c>CollectionsCampaignCsv</c>, 22 columns) maps onto the contact-list columns.
    /// It is an internal staff file, not the contact-list template: names differ and Stage values are not ReminderType labels.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, string> LegacyExportColumnMap = new Dictionary<string, string>
    {
        ["Phone"] = "Phone", ["CustomerName"] = "CustomerName", ["Email"] = "Email Address", ["Stage"] = "ReminderType",
        ["Amount"] = "AmountDue", ["DueDate"] = "DueDate"
    };

    public static string LegacyStageToReminderType(string stage, GenesysOutboundOptions options) =>
        options.LabelFor(CampaignReminderTypes.FromStage(Enum.Parse<CollectionsCampaignStage>(stage)));
}
