using System.Globalization;
using System.Net.Mail;
using System.Text;
using System.Text.RegularExpressions;
using TigerCS.Domain.Modules.Collections;
using TigerCS.Domain.Modules.Collections.Review;

namespace TigerCS.Application.Modules.Collections.Review;

public sealed record TemplateIssue(int Row, string Column, string Message);

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

    private static readonly Regex E164 = new(@"^\+[1-9]\d{7,14}$", RegexOptions.Compiled);
    private static readonly Regex Amount = new(@"^\d+\.\d{2}$", RegexOptions.Compiled);

    public static IReadOnlyList<string> ExpectedReminderTypes(GenesysOutboundOptions options) =>
        Enum.GetValues<CampaignReminderType>().Select(options.LabelFor).ToList();

    public static string WriteCsv(IEnumerable<GenesysContactPayload> contacts)
    {
        var text = new StringBuilder();
        text.Append(string.Join(',', Columns.Select(Quote))).Append("\r\n");
        foreach (var c in contacts)
            text.Append(string.Join(',', new[] { c.Phone, c.CustomerName, c.EmailAddress, c.ReminderType, c.AmountDue, c.DueDate }.Select(v => Quote(Neutralize(v))))).Append("\r\n");
        return text.ToString();
    }

    // A name or phone beginning with = + - @ would be executed by a spreadsheet. The phone's own leading + is the one legitimate case.
    private static string Neutralize(string value) =>
        value.Length > 0 && value[0] is '=' or '-' or '@' ? "'" + value : value;

    private static string Quote(string value) => "\"" + value.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";

    public static TemplateVerification Verify(string csv, IReadOnlyCollection<string> expectedReminderTypes)
    {
        var records = ParseCsv(csv);
        if (records.Count == 0)
            return new([], Columns, [], [], [], [], 0, [new(0, "", "The file is empty.")]);
        var header = records[0].Select(h => h.Trim().TrimStart('﻿')).ToList();
        var missing = Columns.Where(c => !header.Contains(c, StringComparer.Ordinal)).ToList();
        // A column that matches only ignoring case/spacing is a misnamed column (e.g. "EmailAddress", "Amount Due"), reported separately.
        static string Key(string v) => new string(v.Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();
        var misnamed = missing.Where(m => header.Any(h => Key(h) == Key(m))).Select(m => $"{m} (found \"{header.First(h => Key(h) == Key(m))}\")").ToList();
        var unexpected = header.Where(h => !Columns.Contains(h, StringComparer.Ordinal) && !missing.Any(m => Key(m) == Key(h))).ToList();

        var index = Columns.ToDictionary(c => c, c => header.FindIndex(h => h == c));
        var issues = new List<TemplateIssue>();
        var values = new SortedSet<string>(StringComparer.Ordinal);
        for (var row = 1; row < records.Count; row++)
        {
            string Cell(string column) => index[column] is var i && i >= 0 && i < records[row].Count ? records[row][i] : "";
            void Check(string column, bool ok, string message) { if (index[column] >= 0 && !ok) issues.Add(new(row, column, message)); }
            Check("Phone", E164.IsMatch(Cell("Phone")), $"\"{Cell("Phone")}\" is not an international (E.164) number");
            Check("CustomerName", Cell("CustomerName").Trim().Length > 0, "empty");
            var email = Cell("Email Address");
            Check("Email Address", email.Length == 0 || (MailAddress.TryCreate(email, out var mail) && mail.Address == email), $"\"{email}\" is not an email address");
            if (index["ReminderType"] >= 0) values.Add(Cell("ReminderType"));
            Check("AmountDue", Amount.IsMatch(Cell("AmountDue")), $"\"{Cell("AmountDue")}\" is not a plain number with exactly two decimals");
            Check("DueDate", DateTime.TryParseExact(Cell("DueDate"), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _), $"\"{Cell("DueDate")}\" is not yyyy-MM-dd");
        }
        var unknown = values.Where(v => !expectedReminderTypes.Contains(v, StringComparer.Ordinal)).ToList();
        return new(header, missing, unexpected, misnamed, values.ToList(), unknown, records.Count - 1, issues);
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
