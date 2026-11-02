using static TigerCS.Domain.Modules.IdentityAndAccess.Permissions.Permissions;

namespace TigerCS.Domain.Modules.IdentityAndAccess.Permissions;

/// <summary>
/// The permissions each fixed role carries <i>before</i> any per-user
/// override - the baseline "Effective permissions" starts from. It restates
/// what the existing role policies and the permission matrix
/// (Solution-Analysis.md section 4.1, <c>PolicyNames</c>) already allow; it
/// does not widen any role. System Administrator holds every permission
/// (ADR-0024). The read-only roles (<see cref="Roles.ReadOnly"/>) hold view
/// permissions only.
///
/// <para>
/// A user holding several roles gets the union of their baselines. The
/// baseline is a description of entitlement; the existing role policies and
/// the application services' business rules (department scope, status
/// rules, ownership) keep deciding what a request may do on top of it.
/// </para>
/// </summary>
public static class RolePermissionDefaults
{
    private static readonly IReadOnlyDictionary<string, IReadOnlySet<string>> Map =
        new Dictionary<string, IReadOnlySet<string>>(StringComparer.Ordinal)
        {
            [Roles.CsAgent] = Set(
                TicketsView, TicketsCreate, TicketsEdit, TicketsReopen, TicketsEscalate,
                CustomersView, CustomersSend, CollectionsView, CampaignsView, DashboardView,
                InteractionsView, InteractionsEdit),
            [Roles.CsSupervisor] = Set(
                TicketsView, TicketsCreate, TicketsEdit, TicketsAssign, TicketsApprove, TicketsClose, TicketsReopen, TicketsEscalate,
                CustomersView, CustomersSend, CollectionsView, CampaignsView, DashboardView,
                InteractionsView, InteractionsEdit),
            [Roles.DepartmentEmployee] = Set(
                TicketsView, TicketsEdit, TicketsEscalate, CustomersView, DashboardView),
            [Roles.DepartmentHead] = Set(
                TicketsView, TicketsEdit, TicketsAssign, TicketsApprove, TicketsClose, TicketsEscalate,
                CustomersView, DashboardView),
            [Roles.CsManager] = Set(
                TicketsView, TicketsEdit, TicketsAssign, TicketsApprove, TicketsClose, TicketsReopen, TicketsEscalate,
                CustomersView, CustomersSend, CollectionsView, CollectionsEdit, CollectionsSend,
                CampaignsView, CampaignsExport, ReportsView, DashboardView, InteractionsView, InteractionsEdit),
            [Roles.GeneralManager] = Set(
                TicketsView, TicketsEdit, TicketsAssign, TicketsApprove, TicketsClose, TicketsReopen, TicketsEscalate,
                CustomersView, CollectionsView, CampaignsView, CampaignsExport, ReportsView, DashboardView),
            [Roles.ChairmanCeo] = Set(
                TicketsView, CustomersView, CollectionsView, CampaignsView, ReportsView, DashboardView),
            [Roles.ReportingUser] = Set(ReportsView, DashboardView),
            [Roles.SystemAdministrator] = Set([.. PermissionCatalog.All.Select(p => p.Key)])
        };

    private static IReadOnlySet<string> Set(params string[] keys) =>
        new HashSet<string>(keys, StringComparer.Ordinal);

    /// <summary>The baseline of one role; empty for an unknown role name.</summary>
    public static IReadOnlySet<string> ForRole(string role) =>
        Map.TryGetValue(role, out var set) ? set : new HashSet<string>(StringComparer.Ordinal);

    /// <summary>The union of the baselines of the given roles.</summary>
    public static IReadOnlySet<string> ForRoles(IEnumerable<string> roles)
    {
        var union = new HashSet<string>(StringComparer.Ordinal);
        foreach (var role in roles)
        {
            union.UnionWith(ForRole(role));
        }

        return union;
    }
}
