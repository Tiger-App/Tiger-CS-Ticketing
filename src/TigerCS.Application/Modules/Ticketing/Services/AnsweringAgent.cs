namespace TigerCS.Application.Modules.Ticketing.Services;

/// <summary>
/// Why the agent Genesys reported as having ANSWERED an interaction cannot be given the ticket. <see cref="None"/> means they can. Anything
/// else leaves the ticket in the Call Center queue, flagged for manual assignment — never handed to somebody else (see
/// <see cref="TicketAutoAssignmentService.ApplyAnsweringAgentAsync"/>).
/// </summary>
public enum AnsweringAgentIssue
{
    None = 0,

    /// <summary>The Genesys User ID matches no <c>AspNetUsers.GenesysUserId</c>.</summary>
    NotMapped,

    /// <summary>The mapped Ticketing user is deactivated.</summary>
    Inactive,

    /// <summary>The mapped Ticketing account is locked out.</summary>
    LockedOut,

    /// <summary>The mapped user is a service / integration identity, not a person.</summary>
    ServiceAccount,

    /// <summary>The mapped user is not a member of the Call Center department, so they cannot own a Call Center ticket.</summary>
    NotCallCenterMember,

    /// <summary>The mapped user holds no operational role (read-only, supervisory or administrative only).</summary>
    NoOperationalRole,

    /// <summary>The id Genesys sent is blank or malformed.</summary>
    InvalidId,

    /// <summary>The mapped user has an operational role but not <c>CS Agent</c> (a Department Employee / Head, say): only CS Agents own Call Center tickets.</summary>
    NotCsAgent
}

/// <summary>
/// The agent Genesys reported as <b>connected</b> (answered / accepted — never one that is merely ringing, offered or queued), as far as
/// TigerCS could resolve it. <see cref="EmployeeId"/> is null whenever resolution already failed (<see cref="Issue"/> says why).
/// Built server-side from the Genesys User ID; never taken from a client-supplied employee id.
/// </summary>
public sealed record AnsweringAgent(string GenesysUserId, Guid? EmployeeId, AnsweringAgentIssue Issue = AnsweringAgentIssue.None);

public enum AnswerAssignmentOutcome
{
    /// <summary>The ticket was given directly to the answering agent (routed to the Call Center first when it was elsewhere).</summary>
    AssignedToAnsweringAgent,

    /// <summary>The answering agent already owns the ticket: nothing was written (a redelivered event).</summary>
    AlreadyOwnedByAnsweringAgent,

    /// <summary>A person already owns the ticket (not an automatic pre-assignment): human ownership is never overwritten.</summary>
    HumanOwnerKept,

    /// <summary>The answering agent cannot take the ticket: it is in the Call Center queue, unowned, flagged for manual assignment.</summary>
    QueuedForManualAssignment,

    /// <summary>The same unusable answering agent was already flagged on this ticket: nothing new was written.</summary>
    AlreadyFlagged,

    /// <summary>The rule does not apply: the ticket is closed or resolved, a person transferred it, there is no Call Center, or the agent is unusable on a ticket that is not human-managed.</summary>
    NotApplicable,

    /// <summary>A concurrent writer changed the ticket or its assignment first; nothing was applied and a redelivery is safe.</summary>
    ConcurrencyConflict
}

/// <summary>The result of one answering-agent decision. <c>Wrote</c> is true when the call changed the ticket, its assignment or its history (the caller must save).</summary>
public sealed record AnswerAssignmentResult(
    AnswerAssignmentOutcome Outcome,
    Guid? AssignedEmployeeId = null,
    AnsweringAgentIssue Issue = AnsweringAgentIssue.None,
    string? Detail = null,
    bool Wrote = false);
