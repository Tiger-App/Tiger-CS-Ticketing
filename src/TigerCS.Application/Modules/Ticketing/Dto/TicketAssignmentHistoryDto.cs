namespace TigerCS.Application.Modules.Ticketing.Dto;

/// <summary>One assignment event of a Collections ticket. <c>Type</c> is <c>Automatic</c> or <c>Manual</c>; <c>ChangedByName</c> is "System" for automatic routing.</summary>
public sealed record TicketAssignmentHistoryItemDto(
    long Id, DateTime ChangedAtUtc, string Type,
    Guid? PreviousEmployeeId, string? PreviousEmployeeName, Guid NewEmployeeId, string NewEmployeeName,
    int DepartmentId, string DepartmentName,
    string? TowerCode, string? TowerName, string? UnitNumber,
    Guid? ChangedByEmployeeId, string ChangedByName, string? Reason, string? Source = null, DateTime? SourceEventAtUtc = null, string? GenesysConversationId = null);
