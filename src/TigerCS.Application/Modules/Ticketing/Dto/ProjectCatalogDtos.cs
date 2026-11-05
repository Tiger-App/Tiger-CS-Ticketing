namespace TigerCS.Application.Modules.Ticketing.Dto;

/// <summary>
/// One project the New Ticket wizard offers on Step 2.
/// </summary>
/// <param name="Name">The project's name as Tiger Ticketing already records it (one entry per name, compared case-insensitively).</param>
/// <param name="CrmProjectId">Tiger CRM's project id when a CRM-verified ticket has carried one for this name; null otherwise. Informational — the wizard still posts the name.</param>
public sealed record ProjectOptionDto(string Name, int? CrmProjectId);
