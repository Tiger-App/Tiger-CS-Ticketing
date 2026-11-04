using TigerCS.Application.Modules.Collections.Dto;

namespace TigerCS.Application.Modules.Collections.TowerAssignments;

/// <summary>One tower held by a collector for the month. <c>Status</c> is <c>Draft</c> or <c>Active</c>.</summary>
public sealed record TowerAssignmentRowDto(int TowerId, string TowerCode, string TowerName, int CompanyId, string Status);

/// <summary>A Collections employee and the towers they hold for the month (none for a collector who has not been given any yet).</summary>
public sealed record CollectorTowersDto(Guid EmployeeId, string EmployeeName, bool IsActiveMember, IReadOnlyList<TowerAssignmentRowDto> Towers);

public sealed record TowerAssignmentMonthDto(
    int Year, int Month, bool CanManage,
    IReadOnlyList<CollectorTowersDto> Collectors,
    IReadOnlyList<CollectionsTowerDto> Towers,
    IReadOnlyList<int> UnassignedTowerIds,
    int DraftCount);

/// <param name="Year">Calendar year of the assignment month.</param>
/// <param name="Month">Calendar month (1-12).</param>
/// <param name="TowerIds">The complete set the employee should hold this month. Towers they hold now that are missing here are removed.</param>
/// <param name="Activate">True: the employee's towers become Active (in force); false: they are saved as Draft.</param>
/// <param name="MoveFromOthers">True: a tower another employee holds this month is moved to this employee. False (default): that is reported as a conflict and nothing is changed.</param>
public sealed record SaveEmployeeTowersRequest(int Year, int Month, IReadOnlyList<int> TowerIds, bool Activate = false, bool MoveFromOthers = false);

/// <param name="Year">Calendar year of the assignment month.</param>
/// <param name="Month">Calendar month (1-12).</param>
/// <param name="EmployeeId">Null activates every draft of the month; set, only that employee's.</param>
public sealed record ActivateTowerAssignmentsRequest(int Year, int Month, Guid? EmployeeId = null);

/// <summary>Copies last month's assignments into this month as Drafts for towers that have none yet.</summary>
public sealed record CopyPreviousMonthRequest(int Year, int Month);

public sealed record CopyPreviousMonthResultDto(int Copied, int SkippedAlreadyAssigned, int SkippedUnavailable);

public sealed record TowerAssignmentHistoryDto(
    long Id, int TowerId, string TowerCode, string TowerName, int Year, int Month,
    Guid? PreviousEmployeeId, string? PreviousEmployeeName, Guid? NewEmployeeId, string? NewEmployeeName,
    string Action, Guid ChangedByEmployeeId, string ChangedByName, DateTime ChangedAtUtc);
