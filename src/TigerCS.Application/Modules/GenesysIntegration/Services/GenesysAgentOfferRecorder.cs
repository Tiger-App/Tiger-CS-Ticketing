using TigerCS.Application.Modules.Ticketing.Abstractions;
using TigerCS.Application.Modules.Ticketing.Services;
using TigerCS.Domain.Modules.Ticketing;

namespace TigerCS.Application.Modules.GenesysIntegration.Services;

/// <summary>
/// <b>The log of agents Genesys OFFERED a call to</b> (VOICE only). A Genesys event that names an agent without reporting that agent <i>connected</i> - the handoff
/// request / assignment naming the agent, an update or end event whose top-level <c>agentId</c> is only assigned / ringing - is an <b>offer</b>, never an answer. Each
/// distinct offer is appended to the ticket's event log as <see cref="WorkflowEventType.GenesysAgentOffered"/>; the LATEST one is "the last agent the call was offered
/// to", which the voice reconciler uses when the call ended and nobody answered. It is read from these events - never inferred from the current ticket owner.
///
/// <para>
/// Idempotent: the same agent named again right after itself writes nothing (a redelivery). A different agent, or the same agent after another one, is a new offer. Runs
/// in the caller's unit of work (the caller saves).
/// </para>
/// </summary>
public sealed class GenesysAgentOfferRecorder(ITicketWorkflowEventRepository workflowEventRepository, TimeProvider timeProvider)
{
    /// <summary>Records an offer. Returns whether an event was written.</summary>
    /// <param name="interaction">The Genesys interaction the agent was named on (only a Phone interaction records offers).</param>
    /// <param name="genesysAgentId">The Genesys user id named.</param>
    /// <param name="source">Where the offer came from (handoff request, handoff assignment, update, end) - for the audit trail.</param>
    /// <param name="offeredAtUtc">When Genesys says it offered the call; the receipt time when it did not say. Never in the future.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task<bool> RecordAsync(
        TicketInteraction interaction, string? genesysAgentId, string source, DateTime? offeredAtUtc, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(interaction);

        if (interaction.ChannelId != WellKnownChannels.Phone || string.IsNullOrWhiteSpace(genesysAgentId))
        {
            return false;
        }

        var agentId = genesysAgentId.Trim();
        var now = timeProvider.GetUtcNow().UtcDateTime;
        var at = offeredAtUtc is { } reported && reported <= now ? DateTime.SpecifyKind(reported, DateTimeKind.Utc) : now;

        var latest = await workflowEventRepository.GetLatestAsync(interaction.TicketId, [WorkflowEventType.GenesysAgentOffered], cancellationToken);
        if (OfferedAgent.TryParse(latest?.Note) is { } last && string.Equals(last.GenesysUserId, agentId, StringComparison.OrdinalIgnoreCase))
        {
            return false;   // the same agent named again: a redelivery, not a new offer
        }

        await workflowEventRepository.AddAsync(
            new TicketWorkflowEvent(
                interaction.TicketId, WorkflowEventType.GenesysAgentOffered, now, actorEmployeeId: null, ticketApprovalId: null,
                note: $"Genesys offered the call to agent {agentId}. {OfferedAgent.Format(agentId, at, source)}",
                correlationId: Guid.NewGuid()),
            cancellationToken);
        return true;
    }
}
