namespace TigerCS.Application.Modules.IdentityAndAccess.Dto;

/// <summary>One entry in the Department directory. <c>GET /api/departments</c>.</summary>
/// <param name="DepartmentId">The real database id — the only value a Department dropdown may submit as <c>DepartmentId</c>.</param>
/// <param name="Name">The Department's display name.</param>
/// <param name="Code">The stable department code (e.g. <c>CC</c> for Call Center) — what callers identify a department by, never its display name.</param>
public sealed record DepartmentDto(int DepartmentId, string Name, string? Code = null);
