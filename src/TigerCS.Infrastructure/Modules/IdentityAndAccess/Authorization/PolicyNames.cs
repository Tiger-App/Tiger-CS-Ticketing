namespace TigerCS.Infrastructure.Modules.IdentityAndAccess.Authorization;

/// <summary>
/// The policy catalog requested for this increment. Role-set membership for
/// the tiered policies below is the literal, minimal reading of
/// Solution-Analysis.md §4's permission matrix and is not otherwise
/// exhaustively specified in the approved documents beyond that matrix —
/// flagged here, and in the PR description, as an assumption to confirm
/// before ticket-level endpoints (which will consume these same policies)
/// are built in the next increment.
///
/// <para>
/// <b>Every policy below also admits System Administrator</b>, without any
/// of them naming the role: <see cref="SystemAdministratorOverrideHandler"/>
/// satisfies their requirements centrally (ADR-0024 — confirmed management
/// decision superseding Solution-Analysis.md §4.1's previous exclusion). The
/// role lists below therefore continue to state what the permission matrix
/// grants each role; they are not the whole authorization answer, and a
/// policy added here needs no change to include the override.
/// </para>
/// </summary>
public static class PolicyNames
{
    /// <summary>Any authenticated, active staff member — no role restriction.</summary>
    public const string AuthenticatedStaff = "AuthenticatedStaff";

    /// <summary>The Queueing System integration: the request must authenticate with the integration's own API key scheme. A staff JWT never satisfies it.</summary>
    public const string QueueingIntegration = "QueueingIntegration";

    /// <summary>Resource-scoped: caller must be assigned to the target department, or hold a cross-department role.</summary>
    public const string DepartmentScoped = "DepartmentScoped";

    /// <summary>CS Supervisor, CS Manager, or General Manager (Chairman/CEO is read-only and excluded).</summary>
    public const string SupervisorOrAbove = "SupervisorOrAbove";

    /// <summary>Department Head, CS Manager, or General Manager (Chairman/CEO is read-only and excluded).</summary>
    public const string DepartmentHeadOrAbove = "DepartmentHeadOrAbove";

    /// <summary>CS Manager or General Manager. Chairman/CEO is read-only and not admitted (use <see cref="ReportsRead"/> for report reads).</summary>
    public const string CsManagerOrGeneralManager = "CsManagerOrGeneralManager";

    /// <summary>Read access to reports: CS Manager, General Manager, Chairman/CEO.</summary>
    public const string ReportsRead = "ReportsRead";

    /// <summary>System Administrator only.</summary>
    public const string SystemAdministrator = "SystemAdministrator";

    /// <summary>
    /// CS Agent or CS Supervisor only. Scoped to exactly the two roles
    /// Solution-Analysis.md §4.1's Permission Matrix grants ticket-Create
    /// (✔) to — customer/requester verification is Tiger CS Ticketing
    /// business logic (not CRM business logic — see
    /// CrmUnitLookupAppService's and VerificationSessionAppService's
    /// remarks) and exists solely to gate ticket creation (FR-CH-01/
    /// FR-VER-02's "verify-then-create sequence"), so no role that cannot
    /// create a ticket has a documented need to perform it. This was a real
    /// conflict between that matrix (which denies Create to Department
    /// Head/CS Manager/GM/Chairman/SysAdmin) and MVP-API-Contracts.md §0's
    /// broader "Agent and above" role vocabulary — resolved by explicit
    /// confirmation rather than guessed; see the
    /// implementation/mvp-crm-verification PR history.
    ///
    /// <para>
    /// System Administrator now also passes this policy, via the central
    /// override rather than by being added to the role list above — the
    /// confirmed management decision in ADR-0024 supersedes the exclusion
    /// this remark records.
    /// </para>
    /// </summary>
    public const string CustomerVerification = "CustomerVerification";
}
