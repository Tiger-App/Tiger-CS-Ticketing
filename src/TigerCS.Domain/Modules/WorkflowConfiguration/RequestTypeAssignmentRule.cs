namespace TigerCS.Domain.Modules.WorkflowConfiguration;

/// <summary>
/// The configured assignment behavior for one <see cref="RequestType"/>
/// (Workflow/Automation phase 2): when a ticket of this type is created with
/// its department and request type known, the assignment automation resolves
/// this rule instead of asking the CS agent to know every department's
/// staff. At most one rule per request type; a request type with no rule (or
/// an inactive one) falls back to <see cref="AssignmentMode.DepartmentQueue"/>.
///
/// <para>
/// <b>References existing employees only.</b> <see cref="PrimaryEmployeeId"/>
/// and the team members are ids from the existing Employee/department
/// membership model — no employee directory data is duplicated here, and the
/// automation re-validates department membership at assignment time (a
/// configured employee who left the department degrades safely to the
/// department queue rather than producing an invalid assignment).
/// </para>
///
/// <para>
/// <b>Accountability stays single-owner.</b> The existing assignment model
/// has one current owner per ticket, and this phase keeps it: a
/// <see cref="AssignmentMode.Team"/> rule names the primary assignee (the
/// owner) plus further members as configured participants — never several
/// ambiguous owners.
/// </para>
///
/// <para>
/// <b>Extensibility (reviewed pre-phase-3, deliberately not implemented):</b>
/// today exactly one rule per request type exists (unique index on
/// RequestTypeId) and the resolver takes only the request type. If the
/// business later needs conditional routing — e.g. by Project or Priority —
/// the path is purely additive: nullable condition columns (ProjectId,
/// PriorityId, …meaning "matches any" when null) plus a specificity/order
/// column on this same table, the unique index relaxed to cover the
/// condition columns, and the repository lookup widened to a resolve-by-
/// context that picks the most specific active match with the
/// unconditional rule as its fallback. No column here changes meaning, no
/// data migrates destructively, and <see cref="AssignmentMode"/> values
/// stay additive — which is why no speculative condition columns are added
/// now.
/// </para>
/// </summary>
public class RequestTypeAssignmentRule
{
    public int RequestTypeAssignmentRuleId { get; private set; }
    public int RequestTypeId { get; private set; }
    public AssignmentMode Mode { get; private set; }

    /// <summary>The single accountable assignee for <see cref="AssignmentMode.SpecificEmployee"/>/<see cref="AssignmentMode.Team"/>; null for <see cref="AssignmentMode.DepartmentQueue"/>.</summary>
    public Guid? PrimaryEmployeeId { get; private set; }

    /// <summary>Display label for a team rule (e.g. "AC Team"); configuration text only, never an identity.</summary>
    public string? TeamName { get; private set; }

    public bool IsActive { get; private set; }

    private readonly List<RequestTypeAssignmentRuleMember> _members = [];

    /// <summary>The further team members (excluding the primary) for <see cref="AssignmentMode.Team"/>; empty for the other modes.</summary>
    public IReadOnlyList<RequestTypeAssignmentRuleMember> Members => _members.AsReadOnly();

    private RequestTypeAssignmentRule() { }

    private RequestTypeAssignmentRule(int requestTypeId, AssignmentMode mode, Guid? primaryEmployeeId, string? teamName, bool isActive)
    {
        RequestTypeId = requestTypeId;
        Mode = mode;
        PrimaryEmployeeId = primaryEmployeeId;
        TeamName = teamName;
        IsActive = isActive;
    }

    public void Deactivate() => IsActive = false;

    public void Activate() => IsActive = true;

    /// <summary>An explicit department-queue rule — equivalent to having no rule at all, but recordable so the choice is visible configuration rather than an absence.</summary>
    public static RequestTypeAssignmentRule ForDepartmentQueue(int requestTypeId, bool isActive = true) =>
        new(requestTypeId, AssignmentMode.DepartmentQueue, primaryEmployeeId: null, teamName: null, isActive);

    /// <summary>Round Robin across the ticket's department's eligible employees; no employee is configured.</summary>
    public static RequestTypeAssignmentRule ForRoundRobin(int requestTypeId, bool isActive = true) =>
        new(requestTypeId, AssignmentMode.RoundRobin, primaryEmployeeId: null, teamName: null, isActive);

    public static RequestTypeAssignmentRule ForSpecificEmployee(int requestTypeId, Guid primaryEmployeeId, bool isActive = true)
    {
        if (primaryEmployeeId == Guid.Empty)
        {
            throw new ArgumentException("PrimaryEmployeeId is required for a specific-employee rule.", nameof(primaryEmployeeId));
        }

        return new RequestTypeAssignmentRule(requestTypeId, AssignmentMode.SpecificEmployee, primaryEmployeeId, teamName: null, isActive);
    }

    /// <summary>A team rule: one primary (accountable owner) plus at least one further member.</summary>
    public static RequestTypeAssignmentRule ForTeam(
        int requestTypeId, Guid primaryEmployeeId, IReadOnlyCollection<Guid> memberEmployeeIds, string? teamName, bool isActive = true)
    {
        if (primaryEmployeeId == Guid.Empty)
        {
            throw new ArgumentException("PrimaryEmployeeId is required for a team rule — ownership stays unambiguous.", nameof(primaryEmployeeId));
        }

        ArgumentNullException.ThrowIfNull(memberEmployeeIds);

        var distinctMembers = memberEmployeeIds.Distinct().Where(id => id != primaryEmployeeId).ToList();
        if (distinctMembers.Count == 0)
        {
            throw new ArgumentException(
                "A team rule needs at least one member besides the primary — otherwise configure a specific-employee rule.",
                nameof(memberEmployeeIds));
        }

        if (distinctMembers.Any(id => id == Guid.Empty))
        {
            throw new ArgumentException("Team member ids must be real employee ids.", nameof(memberEmployeeIds));
        }

        var rule = new RequestTypeAssignmentRule(requestTypeId, AssignmentMode.Team, primaryEmployeeId, teamName, isActive);
        foreach (var memberId in distinctMembers)
        {
            rule._members.Add(new RequestTypeAssignmentRuleMember(rule, memberId));
        }

        return rule;
    }
}

/// <summary>One further (non-primary) member of a team assignment rule — an existing employee id, never duplicated directory data.</summary>
public class RequestTypeAssignmentRuleMember
{
    public int RequestTypeAssignmentRuleMemberId { get; private set; }
    public int RequestTypeAssignmentRuleId { get; private set; }
    public Guid EmployeeId { get; private set; }

    private RequestTypeAssignmentRuleMember() { }

    internal RequestTypeAssignmentRuleMember(RequestTypeAssignmentRule rule, Guid employeeId)
    {
        Rule = rule;
        EmployeeId = employeeId;
    }

    /// <summary>Navigation back to the owning rule — set by the rule's own factory, never independently.</summary>
    public RequestTypeAssignmentRule? Rule { get; private set; }
}
