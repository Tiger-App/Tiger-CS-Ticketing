namespace TigerCS.Application.Modules.GenesysIntegration.Dto;

/// <summary>
/// The read model <see cref="Abstractions.IGenesysAgentMappingRepository"/>
/// answers with: the Ticketing user one Genesys User ID is mapped to, and
/// whether that user is currently a valid, active employee under the
/// application's existing rule (an active <c>Employees</c> row —
/// the same rule <c>ActiveEmployeeRequirement</c> applies to every request).
/// </summary>
/// <param name="UserId">The Ticketing user id (<c>AspNetUsers.Id</c> / <c>Employees.EmployeeId</c>).</param>
/// <param name="GenesysUserId">The mapped Genesys User ID, as stored.</param>
/// <param name="GenesysEmail">The informational Genesys email, as stored. Never used for matching.</param>
/// <param name="UserName">The Ticketing account's user name.</param>
/// <param name="DisplayName">The employee's display name, where an Employee row exists.</param>
/// <param name="IsActive">True when the user has an Employee row that is not deactivated.</param>
/// <param name="IsAmbiguous">True when more than one Ticketing user carries this Genesys User ID (only possible without the unique index); such a mapping is never resolved.</param>
public sealed record GenesysMappedAgent(
    Guid UserId,
    string GenesysUserId,
    string? GenesysEmail,
    string? UserName,
    string? DisplayName,
    bool IsActive,
    bool IsAmbiguous = false);

/// <summary>How resolving a Genesys User ID to a Ticketing user landed.</summary>
public enum GenesysAgentResolutionOutcome
{
    /// <summary>The Genesys User ID is mapped to an active Ticketing user; <see cref="GenesysAgentResolutionResult.Agent"/> is populated.</summary>
    Success,

    /// <summary>The Genesys User ID was missing, blank, or longer than the mapping column allows — a validation failure, not a mapping gap.</summary>
    Invalid,

    /// <summary>No Ticketing user carries this Genesys User ID. Deliberately distinct from "user not found": the user may well exist and simply not be mapped yet. Never provisions one.</summary>
    NotMapped,

    /// <summary>A Ticketing user is mapped, but is deactivated (or has no Employee row) and so cannot act.</summary>
    Inactive,

    /// <summary>
    /// More than one Ticketing user carries this Genesys User ID. The unique index makes that impossible in a correctly migrated database; if it ever happens
    /// the id is never resolved to either user (no guess), the event is flagged, and the data must be fixed.
    /// </summary>
    Ambiguous,

    /// <summary>The Genesys User ID is mapped to a service / integration identity (e.g. the Genesys integration account). It is never a handling agent.</summary>
    ServiceAccount,

    /// <summary>Resolved only through the e-mail-form <c>senderId</c> fallback: the matched account is locked out, so it is never used as a handling agent.</summary>
    LockedOut,

    /// <summary>Resolved only through the e-mail-form <c>senderId</c> fallback: the matched user does not hold the CS Agent role, so the e-mail alone is not enough to treat it as the answering agent.</summary>
    NotCsAgent
}

/// <summary>
/// The result of resolving one Genesys agent. On <see cref="GenesysAgentResolutionOutcome.Success"/>
/// the Ticketing user, roles and department memberships are populated — the
/// "Genesys User ID → AspNetUsers.GenesysUserId → Ticketing user → id / roles /
/// departments" chain, in one answer.
/// </summary>
public sealed record GenesysAgentResolutionResult(
    GenesysAgentResolutionOutcome Outcome,
    GenesysMappedAgent? Agent = null,
    IReadOnlyCollection<string>? Roles = null,
    IReadOnlyCollection<int>? DepartmentIds = null,
    string? Detail = null)
{
    public static GenesysAgentResolutionResult Failure(GenesysAgentResolutionOutcome outcome, string? detail = null) =>
        new(outcome, Detail: detail);

    /// <summary>True only for <see cref="GenesysAgentResolutionOutcome.Success"/>; the convenience every caller that records ownership checks.</summary>
    public bool IsResolved => Outcome == GenesysAgentResolutionOutcome.Success && Agent is not null;
}

/// <summary>
/// The normalized Genesys agent context — what a Genesys agent's session
/// (the Ticketing screen opened from Genesys) hands over to identify itself.
/// </summary>
/// <param name="GenesysUserId">Required. The immutable Genesys User ID of the agent. The <b>only</b> value agent resolution uses.</param>
/// <param name="AgentEmail">The agent's email as Genesys knows it. Informational only — recorded on the audit trail, never trusted as identity.</param>
/// <param name="ConversationId">The Genesys conversation the agent is working, when there is one. Resolves to the interaction whose ownership is recorded.</param>
public sealed record GenesysAgentContextDto(
    string? GenesysUserId,
    string? AgentEmail = null,
    string? ConversationId = null);

/// <summary>How an agent-context request landed.</summary>
public enum GenesysAgentContextOutcome
{
    /// <summary>The agent resolved to a Ticketing user; when a conversation was supplied, its interaction now records that user as the handler.</summary>
    Resolved,

    IntegrationDisabled,

    /// <summary>genesysUserId was missing or blank — a validation failure.</summary>
    AgentIdRequired,

    /// <summary>The Genesys User ID is not mapped to any Ticketing user. Nothing was created; the mapping must be set up administratively.</summary>
    AgentNotMapped,

    /// <summary>The mapped Ticketing user is deactivated and cannot act.</summary>
    AgentInactive,

    /// <summary>A conversation id was supplied but no interaction exists for it.</summary>
    ConversationNotFound
}

/// <summary>The answer to an agent-context request: who the agent is in Ticketing, and — when a conversation was named — which ticket and interaction it belongs to.</summary>
public sealed record GenesysAgentContextResult(
    GenesysAgentContextOutcome Outcome,
    GenesysMappedAgent? Agent = null,
    IReadOnlyCollection<string>? Roles = null,
    IReadOnlyCollection<int>? DepartmentIds = null,
    long? TicketId = null,
    string? TicketNumber = null,
    long? TicketInteractionId = null,
    Guid? HandledByUserId = null,
    string? Detail = null)
{
    public static GenesysAgentContextResult Failure(GenesysAgentContextOutcome outcome, string? detail = null) =>
        new(outcome, Detail: detail);
}
