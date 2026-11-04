namespace TigerCS.Application.Modules.Ticketing.Dto;

/// <summary>
/// Classify an Unclassified ticket — the agent has read the inquiry and is
/// recording what the customer actually wants.
/// </summary>
/// <param name="CategoryId">
/// Required. The real Ticket Category the agent selected. Must be active and
/// must belong to the ticket's current department — a ticket is never filed
/// under another department's category.
/// </param>
/// <param name="PriorityId">
/// Required. The real priority. This is what selects the ticket's SLA policy,
/// which is exactly why it is set here rather than guessed at creation.
/// </param>
/// <param name="RequestTypeId">
/// Optional. The configured Request Type, when the agent can name it. Must be
/// an active request type of the same department, and its workflow must have
/// a Published version — the ticket is pinned to that version, exactly as it
/// would have been had the request type been known at creation.
/// </param>
/// <param name="RowVersion">Required. The <c>rowVersion</c> from the ticket you read. A stale value is answered with 409.</param>
/// <param name="CollectionsTowerId">Required when the category routes the ticket to Collections (unless it already carries a verified tower and unit): the tower.</param>
/// <param name="CollectionsUnitRef">Required with the tower: the unit of that tower this request is about (<c>crm:{id}</c> or <c>pact:{id}</c>).</param>
public sealed record ClassifyTicketRequestDto(
    int CategoryId,
    byte PriorityId,
    int? RequestTypeId,
    byte[] RowVersion,
    int? CollectionsTowerId = null,
    string? CollectionsUnitRef = null);
