namespace TigerCS.Application.Modules.Ticketing;

/// <summary>
/// Settings for Take Ownership (<c>TicketClaim</c> configuration section) - a plain value in the Application layer, bound by the composition
/// root exactly like <c>GenesysOptions</c>.
///
/// <para>
/// <b><see cref="Enabled"/> (default <c>true</c>) is the operational off-switch.</b> False turns the whole feature off together: Call Center
/// agents stop seeing other departments' unassigned tickets, the details page stops offering the action, and the endpoint answers 403 - with no
/// redeploy (set <c>TicketClaim__Enabled=false</c> and recycle the API). It exists mainly for the window in which the planned one-time Call
/// Center backlog distribution runs, so a manual claim cannot land between that run's Preview and its commit (its per-row re-check would
/// abort the run). Nothing already claimed is undone.
/// </para>
/// </summary>
public sealed class TicketClaimOptions
{
    public const string SectionName = "TicketClaim";

    public bool Enabled { get; set; } = true;
}
