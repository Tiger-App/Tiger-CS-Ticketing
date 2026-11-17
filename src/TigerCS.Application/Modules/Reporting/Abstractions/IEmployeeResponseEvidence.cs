namespace TigerCS.Application.Modules.Reporting.Abstractions;

/// <summary>
/// What Genesys itself said happened to ONE call offer made to ONE agent. It is a statement about that offer only: that a different agent
/// went on to answer the call says nothing about whether this agent responded to theirs.
/// </summary>
public enum GenesysOfferOutcome
{
    /// <summary>Genesys reports the offered agent answered / accepted this offer.</summary>
    Answered = 1,

    /// <summary>Genesys explicitly confirms the offered agent did not respond to this offer (the offer timed out unanswered). Only Genesys can say this.</summary>
    NoAnswerConfirmed = 2,

    /// <summary>Genesys explicitly confirms the offered agent declined this offer. Shown separately; not performance-scored until a policy is agreed.</summary>
    Declined = 4,

    /// <summary>Genesys explicitly confirms the offer was redirected/transferred away to another target. Shown separately; not performance-scored until a policy is agreed.</summary>
    Redirected = 5,

    /// <summary>Genesys explicitly confirms the caller abandoned while this offer was alerting. Shown separately; not performance-scored until a policy is agreed.</summary>
    CallerAbandoned = 6,

    /// <summary>The offer's result is not known or not confirmed — still alerting, declined/redirected/cancelled for a reason the business has not ruled on, or simply not reported.</summary>
    Unverified = 3
}

/// <summary>
/// One Genesys call offer to one agent, as Genesys reported it. <b>TigerCS does not receive or store these today</b> (the contract carries only
/// the connected agent and connect time), so no implementation backs this yet — see <c>docs/Employee-Response-Time-Reporting.md</c> for the
/// integration needed.
/// </summary>
/// <param name="TicketId">The ticket the offered conversation belongs to.</param>
/// <param name="GenesysConversationId">The Genesys conversation.</param>
/// <param name="OfferedAtUtc">When Genesys offered / alerted the call to THIS agent — the actual offer event for the receiving agent. Null when the event was not received.</param>
/// <param name="Outcome">What Genesys says happened to this offer.</param>
/// <param name="AnsweredAtUtc">When this agent answered this offer (Genesys' connected time for this agent); null unless <see cref="Outcome"/> is Answered.</param>
public sealed record GenesysOfferRecord(
    long TicketId,
    string? GenesysConversationId,
    DateTime? OfferedAtUtc,
    GenesysOfferOutcome Outcome,
    DateTime? AnsweredAtUtc);

/// <summary>Where Genesys call offers made to an employee come from. Resolution of the receiving agent is by <c>AspNetUsers.GenesysUserId</c>, done by the implementation.</summary>
public interface IGenesysOfferHistory
{
    /// <summary>Offers made to <paramref name="employeeId"/> whose offer time falls in [<paramref name="rangeStartUtc"/>, <paramref name="rangeEndUtc"/>).</summary>
    Task<IReadOnlyList<GenesysOfferRecord>> GetOffersAsync(
        Guid employeeId, DateTime rangeStartUtc, DateTime rangeEndUtc, CancellationToken cancellationToken = default);
}

/// <summary>What the historical roster says about an employee at one instant.</summary>
public enum ShiftVerdict
{
    /// <summary>The employee had a verified shift covering the instant and was not on approved leave.</summary>
    WithinShift = 1,

    /// <summary>The employee had no shift covering the instant (verified: the roster for that time exists).</summary>
    OutsideShift = 2,

    /// <summary>The employee was on approved leave at the instant.</summary>
    OnApprovedLeave = 3,

    /// <summary>No historical shift / leave data exists for that employee and instant. The current presence of the employee is never a substitute.</summary>
    Unknown = 4
}

/// <summary>
/// Historical shift and approved-leave records. <b>TigerCS holds no such data today</b> (no shift, roster or leave table exists; the business
/// calendar is company-wide working hours, not a per-employee shift), so the registered implementation answers
/// <see cref="ShiftVerdict.Unknown"/> for everything.
/// </summary>
public interface IEmployeeShiftHistory
{
    Task<ShiftVerdict> GetVerdictAsync(Guid employeeId, DateTime atUtc, CancellationToken cancellationToken = default);
}

/// <summary>Default <see cref="IGenesysOfferHistory"/> while no offer data is received: no offers, so nothing can be measured or confirmed.</summary>
public sealed class NoGenesysOfferHistory : IGenesysOfferHistory
{
    public Task<IReadOnlyList<GenesysOfferRecord>> GetOffersAsync(
        Guid employeeId, DateTime rangeStartUtc, DateTime rangeEndUtc, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<GenesysOfferRecord>>([]);
}

/// <summary>Default <see cref="IEmployeeShiftHistory"/> while no shift / leave data exists: every instant is <see cref="ShiftVerdict.Unknown"/>.</summary>
public sealed class NoEmployeeShiftHistory : IEmployeeShiftHistory
{
    public Task<ShiftVerdict> GetVerdictAsync(Guid employeeId, DateTime atUtc, CancellationToken cancellationToken = default) =>
        Task.FromResult(ShiftVerdict.Unknown);
}
