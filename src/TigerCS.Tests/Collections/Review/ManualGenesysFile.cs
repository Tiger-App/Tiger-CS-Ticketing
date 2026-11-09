using TigerCS.Application.Modules.Collections.Review;

namespace TigerCS.Tests.Collections.Review;

/// <summary>Reads a Genesys export file by column NAME, so a test says which column it means and survives a change of layout (the Manual file has eleven columns, Auto's six).</summary>
internal static class ManualGenesysFile
{
    /// <summary>The official Manual contact-list columns, in order: written out here on purpose, so a change to the production constant is caught by a test.</summary>
    public static readonly string[] Template = ["Phone", "Email", "CustomerName", "ReminderType", "AmountDue", "UnitNumber", "ProjectName", "TowerName", "DueDate", "DueMonthAr", "TenantId"];

    /// <summary>The value of <paramref name="column"/> in <paramref name="row"/>, where the header is the first row of <paramref name="file"/>.</summary>
    public static string Cell(IReadOnlyList<List<string>> file, IReadOnlyList<string> row, string column)
    {
        var at = file[0].IndexOf(column);
        if (at < 0) throw new InvalidOperationException($"The file has no column {column}: {string.Join(',', file[0])}");
        return row[at];
    }

    /// <summary>The data rows (everything after the header) as column-name dictionaries.</summary>
    public static List<Dictionary<string, string>> Records(string csv)
    {
        var file = GenesysContactTemplate.ParseCsv(csv);
        return file.Skip(1).Select(r => file[0].Select((name, i) => (name, value: r[i])).ToDictionary(x => x.name, x => x.value)).ToList();
    }
}
