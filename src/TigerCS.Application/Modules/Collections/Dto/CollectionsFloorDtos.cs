namespace TigerCS.Application.Modules.Collections.Dto;

/// <summary>One entry of the Floor dropdown: the floor number, its label ("Floor 5", "Ground floor") and how many apartment units of the chosen towers are on it.</summary>
public sealed record CollectionsFloorDto(int Floor, string Label, int UnitCount);

/// <summary>The floors available for the chosen towers (all towers when <c>TowerIds</c> is empty). Only apartment units contribute; the floor comes from the unit number.</summary>
public sealed record CollectionsFloorsDto(IReadOnlyList<CollectionsFloorDto> Floors, IReadOnlyList<int> TowerIds);
