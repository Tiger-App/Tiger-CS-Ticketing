namespace TigerCS.Web.Models;

/// <summary>The mandatory Tower + Unit block of a form that can move a ticket into Collections.</summary>
/// <param name="Id">Unique id part for the block's controls.</param>
/// <param name="Prefix">The bound input's name prefix (<c>Transfer</c> / <c>Reopen</c>), so the selects post as <c>Prefix.CollectionsTowerId</c> / <c>Prefix.CollectionsUnitId</c>.</param>
/// <param name="TriggerSelector">The department select whose value decides whether the block shows.</param>
/// <param name="CollectionsDepartmentIds">Comma-separated ids of the Collections department among the form's choices (empty = the form can never move a ticket there).</param>
/// <param name="TicketId">The ticket whose customer's towers and units are offered.</param>
public sealed record CollectionsTowerUnitFields(string Id, string Prefix, string TriggerSelector, string CollectionsDepartmentIds, long TicketId);
