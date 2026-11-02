namespace TigerCS.Domain.Modules.IdentityAndAccess.Permissions;

/// <summary>The kind of operation a permission controls. Only the actions that exist for a module are defined for it.</summary>
public enum PermissionAction
{
    View,
    Create,
    Edit,
    Delete,
    Export,
    Assign,
    Approve,
    Send,
    Reopen,
    Close,
    Resolve,
    Transfer,
    Escalate,
    Manage
}

/// <summary>
/// One grantable permission: a module (the grouping shown in the UI), the
/// action, and the stable key persisted in <c>UserPermissionOverrides</c> and
/// named on endpoints (<c>[RequirePermission]</c>). Keys are
/// <c>"&lt;Area&gt;.&lt;Action&gt;"</c> and never change once released: they are
/// stored in the database and in audit history.
/// </summary>
/// <param name="Key">Stable persisted key.</param>
/// <param name="Module">Grouping shown in the UI (<see cref="PermissionModules"/>).</param>
/// <param name="Action">What the permission controls.</param>
/// <param name="Name">Short display name.</param>
/// <param name="Description">One-line explanation shown beside the toggle.</param>
/// <param name="IsReserved">
/// A reserved ("administrator-only") permission can never be <i>granted</i>
/// to a user whose roles do not already carry it. These are the permissions
/// that would let the holder reset an administrator's password, create an
/// administrator, change roles or change permissions - i.e. escalate. They
/// can still be <i>denied</i>.
/// </param>
public sealed record PermissionDefinition(
    string Key,
    string Module,
    PermissionAction Action,
    string Name,
    string Description,
    bool IsReserved = false)
{
    /// <summary>True for permissions that change data (everything except <see cref="PermissionAction.View"/>); a read-only caller is never granted these.</summary>
    public bool IsMutating => Action != PermissionAction.View;
}

/// <summary>Module names, in display order.</summary>
public static class PermissionModules
{
    public const string Tickets = "Tickets";
    public const string Customers = "Customers";
    public const string Collections = "Collections / Receivables";
    public const string Campaigns = "Campaigns";
    public const string Reports = "Reports";
    public const string Dashboard = "Dashboard";
    public const string Interactions = "Pending Interactions";
    public const string Administration = "Administration";

    public static readonly IReadOnlyList<string> Ordered =
    [Tickets, Customers, Collections, Campaigns, Reports, Dashboard, Interactions, Administration];
}

/// <summary>
/// The permission keys, as constants so endpoints, tests and the role
/// baseline refer to them without string literals. The catalog is a fixed,
/// code-defined list (like <see cref="Roles"/>): adding a permission is a
/// code change and needs no migration, because only the <i>overrides</i> are
/// stored.
/// </summary>
public static class Permissions
{
    public const string TicketsView = "Tickets.View";
    public const string TicketsCreate = "Tickets.Create";
    public const string TicketsEdit = "Tickets.Edit";
    public const string TicketsAssign = "Tickets.Assign";
    public const string TicketsApprove = "Tickets.Approve";
    public const string TicketsResolve = "Tickets.Resolve";
    public const string TicketsClose = "Tickets.Close";
    public const string TicketsTransfer = "Tickets.Transfer";
    public const string TicketsReopen = "Tickets.Reopen";
    public const string TicketsEscalate = "Tickets.Escalate";

    public const string CustomersView = "Customers.View";
    public const string CustomersSend = "Customers.Send";

    public const string CollectionsView = "Collections.View";
    public const string CollectionsEdit = "Collections.Edit";
    public const string CollectionsSend = "Collections.Send";

    public const string CampaignsView = "Campaigns.View";
    public const string CampaignsExport = "Campaigns.Export";

    public const string ReportsView = "Reports.View";

    public const string DashboardView = "Dashboard.View";

    public const string InteractionsView = "Interactions.View";
    public const string InteractionsEdit = "Interactions.Edit";

    public const string AdminUsersView = "Administration.Users.View";
    public const string AdminUsersCreate = "Administration.Users.Create";
    public const string AdminUsersEdit = "Administration.Users.Edit";
    public const string AdminRolesAssign = "Administration.Roles.Assign";
    public const string AdminConfigurationView = "Administration.Configuration.View";
    public const string AdminConfigurationEdit = "Administration.Configuration.Edit";
    public const string AdminPermissionsManage = "Administration.Permissions.Manage";
}

/// <summary>The complete, ordered permission catalog.</summary>
public static class PermissionCatalog
{
    public static readonly IReadOnlyList<PermissionDefinition> All =
    [
        new(Permissions.TicketsView, PermissionModules.Tickets, PermissionAction.View, "View tickets", "Open the ticket list, ticket details, history and notes."),
        new(Permissions.TicketsCreate, PermissionModules.Tickets, PermissionAction.Create, "Create tickets", "Create a ticket from a verified customer interaction."),
        new(Permissions.TicketsEdit, PermissionModules.Tickets, PermissionAction.Edit, "Edit tickets", "Classify, add notes, change status and resolve."),
        new(Permissions.TicketsAssign, PermissionModules.Tickets, PermissionAction.Assign, "Assign tickets", "Assign or reassign a ticket to an employee (within the department scope the role allows)."),
        new(Permissions.TicketsTransfer, PermissionModules.Tickets, PermissionAction.Transfer, "Transfer tickets", "Move a ticket to another department. Never bypasses ticket visibility, status or department workflow settings."),
        new(Permissions.TicketsApprove, PermissionModules.Tickets, PermissionAction.Approve, "Approve requests", "Decide ticket approvals and priority-downgrade requests."),
        new(Permissions.TicketsResolve, PermissionModules.Tickets, PermissionAction.Resolve, "Resolve tickets", "Resolve a ticket. The employee a ticket is assigned to can always resolve it unless this is denied; a grant never bypasses ticket visibility, status or workflow rules."),
        new(Permissions.TicketsClose, PermissionModules.Tickets, PermissionAction.Close, "Close tickets", "Close a resolved ticket. The assigned employee can always close their own ticket unless this is denied; a grant never bypasses ticket visibility, status or workflow rules."),
        new(Permissions.TicketsReopen, PermissionModules.Tickets, PermissionAction.Reopen, "Reopen tickets", "Reopen a closed ticket (a reason is mandatory)."),
        new(Permissions.TicketsEscalate, PermissionModules.Tickets, PermissionAction.Escalate, "Escalate tickets", "Raise SLA escalations and record first responses."),

        new(Permissions.CustomersView, PermissionModules.Customers, PermissionAction.View, "View customers", "Search customers, open customer profiles, history and documents."),
        new(Permissions.CustomersSend, PermissionModules.Customers, PermissionAction.Send, "Send customer documents", "Send customer documents by e-mail / SMS."),

        new(Permissions.CollectionsView, PermissionModules.Collections, PermissionAction.View, "View collections", "Receivables, instalments, payment summaries and review runs."),
        new(Permissions.CollectionsEdit, PermissionModules.Collections, PermissionAction.Edit, "Run collections jobs", "Refresh review runs, reconciliation, suppression sweep, cancel dispatches."),
        new(Permissions.CollectionsSend, PermissionModules.Collections, PermissionAction.Send, "Send collections dispatches", "Create collections dispatches and retry admin e-mails."),

        new(Permissions.CampaignsView, PermissionModules.Campaigns, PermissionAction.View, "View campaigns", "Campaign preview."),
        new(Permissions.CampaignsExport, PermissionModules.Campaigns, PermissionAction.Export, "Export campaigns", "Export a campaign list."),

        new(Permissions.ReportsView, PermissionModules.Reports, PermissionAction.View, "View reports", "Team performance and executive reports."),

        new(Permissions.DashboardView, PermissionModules.Dashboard, PermissionAction.View, "View dashboard", "Dashboard KPIs and overview."),

        new(Permissions.InteractionsView, PermissionModules.Interactions, PermissionAction.View, "View pending interactions", "List customer interactions waiting for a human."),
        new(Permissions.InteractionsEdit, PermissionModules.Interactions, PermissionAction.Edit, "Handle pending interactions", "Start, complete or cancel a pending interaction."),

        new(Permissions.AdminUsersView, PermissionModules.Administration, PermissionAction.View, "View users", "Open Administration → Users."),
        new(Permissions.AdminUsersCreate, PermissionModules.Administration, PermissionAction.Create, "Create users", "Create employee accounts.", IsReserved: true),
        new(Permissions.AdminUsersEdit, PermissionModules.Administration, PermissionAction.Edit, "Edit users", "Edit profile, activation, departments, password reset and unlock.", IsReserved: true),
        new(Permissions.AdminRolesAssign, PermissionModules.Administration, PermissionAction.Assign, "Assign roles", "Change the roles of an employee.", IsReserved: true),
        new(Permissions.AdminConfigurationView, PermissionModules.Administration, PermissionAction.View, "View configuration", "Departments, request types, channels, workflows, SLA and Genesys routing."),
        new(Permissions.AdminConfigurationEdit, PermissionModules.Administration, PermissionAction.Edit, "Edit configuration", "Change departments, request types, channels, workflows and routing."),
        new(Permissions.AdminPermissionsManage, PermissionModules.Administration, PermissionAction.Manage, "Manage permissions", "Open Manage Permissions and change per-user permission overrides.", IsReserved: true)
    ];

    private static readonly Dictionary<string, PermissionDefinition> ByKey =
        All.ToDictionary(p => p.Key, StringComparer.Ordinal);

    public static bool IsKnown(string key) => ByKey.ContainsKey(key);

    public static PermissionDefinition? Find(string key) => ByKey.GetValueOrDefault(key);
}
