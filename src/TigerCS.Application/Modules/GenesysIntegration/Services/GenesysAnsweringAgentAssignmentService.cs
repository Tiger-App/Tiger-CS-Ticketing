using TigerCS.Application.Abstractions;
using TigerCS.Application.Modules.CustomerVerification.Abstractions;
using TigerCS.Application.Modules.GenesysIntegration.Dto;
using TigerCS.Application.Modules.Ticketing.Abstractions;
using TigerCS.Application.Modules.Ticketing.Services;
using TigerCS.Domain.Modules.Ticketing;

namespace TigerCS.Application.Modules.GenesysIntegration.Services;

/// <summary>
/// <b>Who answered owns the ticket.</b> The Genesys-facing half of the "answering agent" rule: it turns a Genesys User ID that Genesys
/// reported as <i>connected</i> to an interaction into a decision on the ticket, in its own transaction. The decision itself —
/// ownership, Call Center routing, the manual-assignment flag — lives in <see cref="TicketAutoAssignmentService.ApplyAnsweringAgentAsync"/>,
/// beside Round Robin, so there is one place that decides who owns a ticket.
///
/// <para>
/// <b>What counts as answered.</b> Only an id Genesys sent as the CONNECTED agent: the agent on the ticket-creating inquiry of a call that
/// was picked up, and <c>routing.agentId</c> on an update. A ringing, offered, alerting or queue-only agent never reaches this class, and a
/// handoff <i>assignment</i> (<c>handoff.assignedAgentId</c>) is not an answer either — Genesys naming who is to take the work is not that
/// person taking it. Those keep the existing behaviour: a human handoff nobody has accepted is Round Robin assigned.
/// </para>
///
/// <para>
/// <b>Safe to repeat and to race.</b> A redelivered event finds the agent already owning the ticket and writes nothing. A competing writer
/// (another delivery, a person assigning at the same instant) trips the ticket's row version or the unique "one current assignment"
/// index; that is reported as <see cref="AnswerAssignmentOutcome.ConcurrencyConflict"/> with nothing applied, never as two owners.
/// </para>
/// </summary>
public sealed class GenesysAnsweringAgentAssignmentService(
    GenesysAgentResolutionAppService agentResolution,
    TicketAutoAssignmentService autoAssignment,
    ITicketAgentHandoffRepository handoffRepository,
    ITicketingUnitOfWork unitOfWork,
    TimeProvider timeProvider)
{
    /// <summary>Maps the resolver's answer onto the Ticketing-side description of the answering agent. Pure; never invents a mapping.</summary>
    public static AnsweringAgent ToAnsweringAgent(string genesysUserId, GenesysAgentResolutionResult resolution)
    {
        ArgumentNullException.ThrowIfNull(resolution);

        var id = genesysUserId?.Trim() ?? string.Empty;
        return resolution.IsResolved
            ? new AnsweringAgent(resolution.Agent!.GenesysUserId, resolution.Agent.UserId)
            : new AnsweringAgent(id, null, resolution.Outcome switch
            {
                GenesysAgentResolutionOutcome.Inactive => AnsweringAgentIssue.Inactive,
                GenesysAgentResolutionOutcome.Invalid => AnsweringAgentIssue.InvalidId,
                GenesysAgentResolutionOutcome.Ambiguous => AnsweringAgentIssue.AmbiguousMapping,
                GenesysAgentResolutionOutcome.ServiceAccount => AnsweringAgentIssue.ServiceAccount,
                _ => AnsweringAgentIssue.NotMapped
            });
    }

    /// <summary>Resolves a connected agent's Genesys User ID server-side (<c>AspNetUsers.GenesysUserId</c>) — the only key; names and emails never match.</summary>
    public async Task<AnsweringAgent> ResolveAsync(string genesysUserId, CancellationToken cancellationToken = default) =>
        ToAnsweringAgent(genesysUserId, await agentResolution.ResolveByGenesysUserIdAsync(genesysUserId, cancellationToken));

    /// <summary>
    /// A Genesys update reported <paramref name="genesysAgentId"/> as connected to the interaction. Applies the answering-agent rule to the
    /// ticket and saves it in one transaction.
    /// </summary>
    public async Task<AnswerAssignmentResult> ApplyConnectedAgentAsync(
        Guid callerEmployeeId, Ticket ticket, TicketInteraction interaction, string genesysAgentId, string source,
        CancellationToken cancellationToken = default, bool firstResponseIsFromTheAnsweringAgent = false)
    {
        ArgumentNullException.ThrowIfNull(ticket);
        ArgumentNullException.ThrowIfNull(interaction);

        if (string.IsNullOrWhiteSpace(genesysAgentId))
        {
            return new AnswerAssignmentResult(AnswerAssignmentOutcome.NotApplicable, Detail: "No connected agent.");
        }

        var agent = await ResolveAsync(genesysAgentId, cancellationToken);

        // An unusable agent flags/queues the ticket only when a person is evidently handling it already: it sits in the Call Center, the
        // conversation needs human follow-up, human work is open, or it is a VOICE call (a call connected to an agent is answered by a
        // person), or the id maps to a real Ticketing user. Otherwise (an AI chat that merely names an unmapped id - a bot) nothing moves.
        // A MAPPED id (even a deactivated or locked one) is a known person, never a bot, so it counts too; only an id that maps to nobody
        // needs one of the other signals before the ticket is touched.
        var knownPerson = agent.Issue is not (AnsweringAgentIssue.NotMapped or AnsweringAgentIssue.InvalidId);
        var humanManaged = knownPerson
            || await autoAssignment.IsCallCenterDepartmentAsync(ticket.CurrentDepartmentId, cancellationToken)
            || interaction.ChannelId == WellKnownChannels.Phone
            || interaction.RequiresHumanFollowUp
            || await handoffRepository.GetOpenByInteractionIdAsync(interaction.TicketInteractionId, cancellationToken) is not null;

        await using var transaction = await unitOfWork.BeginTransactionAsync(cancellationToken);

        var result = await autoAssignment.ApplyAnsweringAgentAsync(
            ticket, agent, source, timeProvider.GetUtcNow().UtcDateTime, Guid.NewGuid(), callerEmployeeId,
            routeToCallCenterIfAgentUnavailable: humanManaged, cancellationToken,
            firstResponseIsFromTheAnsweringAgent: firstResponseIsFromTheAnsweringAgent);

        if (!result.Wrote)
        {
            return result;
        }

        try
        {
            await unitOfWork.SaveChangesAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is TicketConcurrentlyModifiedException or DuplicateWriteException)
        {
            // Another delivery or a person got there first. The winner stands; this event can be redelivered safely.
            return new AnswerAssignmentResult(AnswerAssignmentOutcome.ConcurrencyConflict, Detail: "The ticket or its assignment changed at the same moment.");
        }

        await transaction.CommitAsync(cancellationToken);
        return result;
    }
}
