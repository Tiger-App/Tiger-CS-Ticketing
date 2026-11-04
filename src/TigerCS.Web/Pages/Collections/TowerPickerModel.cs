using TigerCS.Application.Modules.Collections.Dto;

namespace TigerCS.Web.Pages.Collections;

public sealed record TowerPickerModel(string Id, IReadOnlyList<CollectionsTowerDto> Towers, int? SelectedTowerId, bool LoadFailed = false,
    string Label = "Tower", string AllText = "All towers");

/// <summary>The searchable multi-select Project / Tower filter (checkboxes named <c>towerId</c>). No selection = <see cref="AllText"/>.</summary>
public sealed record TowerMultiPickerModel(string Id, IReadOnlyList<CollectionsTowerDto> Towers, IReadOnlyList<int> SelectedTowerIds, bool LoadFailed = false,
    string Label = "Project / Tower", string AllText = "All projects");
