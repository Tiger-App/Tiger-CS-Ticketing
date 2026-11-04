using System.Globalization;
using Microsoft.Extensions.Primitives;
using TigerCS.Application.Modules.Collections.Dto;

namespace TigerCS.Web.Pages.Collections;

public sealed record TowerPickerModel(string Id, IReadOnlyList<CollectionsTowerDto> Towers, int? SelectedTowerId, bool LoadFailed = false,
    string Label = "Tower", string AllText = "All towers");

/// <summary>The searchable multi-select Project / Tower filter (checkboxes named <c>towerId</c>). No selection = <see cref="AllText"/>.</summary>
public sealed record TowerMultiPickerModel(string Id, IReadOnlyList<CollectionsTowerDto> Towers, IReadOnlyList<int> SelectedTowerIds, bool LoadFailed = false,
    string Label = "Project / Tower", string AllText = "All towers");

/// <summary>The one place the Project / Tower selection of a request is read and normalised, shared by Campaigns and Receivables so both pages treat the same URL the same way.</summary>
public static class TowerSelection
{
    /// <summary>Distinct, valid tower ids from repeated <c>towerId</c> values (an old URL carries one; a comma list is also read); blanks and junk are ignored. Empty = All towers.</summary>
    public static IReadOnlyList<int> Parse(StringValues values) =>
        values.SelectMany(v => (v ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
              .Select(v => int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out var id) && id > 0 ? id : 0)
              .Where(id => id > 0).Distinct().Order().ToList();

    /// <summary>What is sent to the API: null for All towers (nothing chosen, or every listed tower chosen - the unrestricted, fastest read).</summary>
    public static IReadOnlyList<int>? Effective(IReadOnlyList<int> selected, IReadOnlyList<CollectionsTowerDto> catalogue) =>
        selected.Count == 0 || (catalogue.Count > 0 && catalogue.All(t => selected.Contains(t.TowerId))) ? null : selected;
}
