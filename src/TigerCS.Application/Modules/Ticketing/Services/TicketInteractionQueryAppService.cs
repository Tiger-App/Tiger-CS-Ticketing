using TigerCS.Application.Modules.GenesysIntegration.Abstractions;
using TigerCS.Application.Modules.Ticketing.Abstractions;
using TigerCS.Application.Modules.Ticketing.Dto;
using TigerCS.Domain.Modules.Ticketing;

namespace TigerCS.Application.Modules.Ticketing.Services;

/// <summary>
/// The read model behind Ticket Details' Conversation History: every
/// interaction a ticket accumulated — the originating Genesys call or chat
/// and any later ones — in chronological order, each with its transcript.
///
/// <para>
/// <b>Same visibility rule as the ticket itself.</b> Authorization is
/// delegated to <see cref="TicketQueryAppService.CanViewDepartmentAsync"/>,
/// so a conversation is readable exactly when the ticket it belongs to is —
/// this endpoint can never become a side door onto a department's
/// conversations.
/// </para>
///
/// <para>
/// <b>One query for the transcripts, not one per interaction.</b> Every
/// interaction's messages are fetched in a single batched read
/// (<see cref="IGenesysConversationRepository.ListMessagesForInteractionsAsync"/>),
/// the same no-N+1 discipline the customer-history read follows.
/// </para>
/// </summary>
public sealed class TicketInteractionQueryAppService(
    ITicketRepository ticketRepository,
    ITicketInteractionRepository interactionRepository,
    IGenesysConversationRepository conversationRepository,
    IChannelRepository channelRepository,
    ITicketAgentHandoffRepository agentHandoffRepository,
    TicketQueryAppService ticketQueryAppService)
{
    public async Task<TicketQueryResultDto<TicketInteractionHistoryDto>> GetForTicketAsync(
        Guid callerEmployeeId,
        IReadOnlyCollection<string> callerRoles,
        long ticketId,
        CancellationToken cancellationToken = default)
    {
        var ticket = await ticketRepository.GetByIdAsync(ticketId, cancellationToken);
        if (ticket is null)
        {
            return TicketQueryResultDto<TicketInteractionHistoryDto>.Failure(TicketQueryOutcome.NotFound);
        }

        if (!await ticketQueryAppService.CanViewTicketAsync(callerEmployeeId, callerRoles, ticket, cancellationToken))
        {
            return TicketQueryResultDto<TicketInteractionHistoryDto>.Failure(TicketQueryOutcome.Forbidden);
        }

        var interactions = await interactionRepository.ListByTicketIdAsync(ticketId, cancellationToken);
        if (interactions.Count == 0)
        {
            return TicketQueryResultDto<TicketInteractionHistoryDto>.Success(
                new TicketInteractionHistoryDto(ticketId, []));
        }

        // A Genesys conversation can carry several tickets (one per distinct service request), and Genesys sends the conversation's
        // messages against whichever ticket it happens to hold. The transcript belongs to the CONVERSATION, so every ticket of it shows the
        // whole conversation: the messages stored under any sibling interaction are read alongside this ticket's own, in one batched query.
        var conversationIds = interactions
            .Select(i => i.GenesysConversationId)
            .OfType<string>()
            .Distinct(StringComparer.Ordinal)
            .ToList();
        var siblingIdsByConversation = await conversationRepository.ListInteractionIdsByConversationIdsAsync(conversationIds, cancellationToken);

        var interactionIdsToRead = interactions
            .Select(i => i.TicketInteractionId)
            .Concat(siblingIdsByConversation.Values.SelectMany(ids => ids))
            .Distinct()
            .ToList();
        var storedMessages = await conversationRepository.ListMessagesForInteractionsAsync(interactionIdsToRead, cancellationToken);
        var messagesByInteraction = interactions.ToDictionary(
            i => i.TicketInteractionId,
            i => MergeConversationMessages(i, siblingIdsByConversation, storedMessages));

        // Channel names come from configuration, resolved by id, so a channel
        // deactivated since the interaction happened still displays its name.
        var channelNames = new Dictionary<byte, string?>();
        foreach (var channelId in interactions.Select(i => i.ChannelId).Distinct())
        {
            channelNames[channelId] = (await channelRepository.GetByIdAsync(channelId, cancellationToken))?.Name;
        }

        // The pending human work raised from these interactions, so an agent
        // opening the ticket sees WHY a human was asked for beside the
        // transcript that led to it — the context that matters most when a
        // virtual agent handed over. One query for the whole ticket, never
        // one per interaction. Only the latest per interaction is shown; the
        // full history lives on the work item itself.
        var handoffsByInteraction = (await agentHandoffRepository.ListByTicketIdAsync(ticketId, cancellationToken))
            .GroupBy(h => h.TicketInteractionId)
            .ToDictionary(g => g.Key, g => g.First());

        var items = interactions
            .Select(interaction => ToDto(
                interaction,
                channelNames.GetValueOrDefault(interaction.ChannelId),
                messagesByInteraction.TryGetValue(interaction.TicketInteractionId, out var messages) ? messages : [],
                storedMessages.TryGetValue(interaction.TicketInteractionId, out var ownMessages) ? ownMessages : [],
                handoffsByInteraction.GetValueOrDefault(interaction.TicketInteractionId)))
            // Chronological: Genesys' own start time where it supplied one,
            // otherwise the moment Ticketing recorded the interaction.
            .OrderBy(i => i.StartedAtUtc)
            .ThenBy(i => i.TicketInteractionId)
            .ToList();

        return TicketQueryResultDto<TicketInteractionHistoryDto>.Success(
            new TicketInteractionHistoryDto(ticketId, items));
    }

    /// <summary>
    /// The messages of one interaction's conversation: its own plus every sibling interaction's (same Genesys conversation, other tickets),
    /// one line once — the same message delivered against two tickets is shown once, recognised by Genesys' message id, else by
    /// sender + time + body — in the order they were said.
    /// </summary>
    private static IReadOnlyList<TicketInteractionMessage> MergeConversationMessages(
        TicketInteraction interaction,
        IReadOnlyDictionary<string, IReadOnlyList<long>> siblingIdsByConversation,
        IReadOnlyDictionary<long, IReadOnlyList<TicketInteractionMessage>> storedMessages)
    {
        var ids = interaction.GenesysConversationId is { } conversationId
            && siblingIdsByConversation.TryGetValue(conversationId, out var siblings) && siblings.Count > 1
                ? siblings.OrderBy(id => id == interaction.TicketInteractionId ? 0 : 1).ThenBy(id => id).ToList()
                : [interaction.TicketInteractionId];

        var merged = new List<TicketInteractionMessage>();
        foreach (var id in ids)
        {
            if (!storedMessages.TryGetValue(id, out var messages))
            {
                continue;
            }

            foreach (var message in messages.OrderBy(m => m.Sequence))
            {
                var duplicate = merged.Any(m =>
                    message.ExternalMessageId is { } externalId && m.ExternalMessageId is { } otherId
                        ? string.Equals(externalId, otherId, StringComparison.Ordinal)
                        : m.Sender == message.Sender && m.SentAtUtc == message.SentAtUtc && string.Equals(m.Body, message.Body, StringComparison.Ordinal));
                if (!duplicate)
                {
                    merged.Add(message);
                }
            }
        }

        // A single interaction keeps its stored order untouched; a merged conversation is ordered by when things were said.
        return ids.Count == 1
            ? merged
            : merged.OrderBy(m => m.SentAtUtc).ThenBy(m => m.TicketInteractionId).ThenBy(m => m.Sequence).ToList();
    }

    private static TicketInteractionDto ToDto(
        TicketInteraction interaction, string? channelName, IReadOnlyList<TicketInteractionMessage> messages,
        IReadOnlyList<TicketInteractionMessage> ownMessages, TicketAgentHandoff? handoff) => new(
        interaction.TicketInteractionId,
        interaction.IsOriginatingInteraction,
        interaction.Source.ToString(),
        interaction.ChannelId,
        channelName,
        interaction.CustomerPhone,
        interaction.CustomerName,
        interaction.CustomerEmail,
        interaction.Direction,
        interaction.GenesysConversationId,
        interaction.GenesysQueueId,
        interaction.GenesysQueueName,
        interaction.GenesysAgentName,
        interaction.GenesysAgentId,
        interaction.InteractionStartedAtUtc ?? interaction.CreatedAtUtc,
        interaction.EndedAtUtc,
        interaction.EndReason,
        interaction.IsEnded ? "Ended" : "Active",
        // Single-interaction conversations keep their stored sequence number; a merged conversation is renumbered 1..n in display order.
        messages.Select((m, index) => new TicketInteractionMessageDto(
                messages.Any(x => x.TicketInteractionId != interaction.TicketInteractionId) ? index + 1 : m.Sequence,
                m.Sender.ToString(), m.SenderName, m.SentAtUtc, m.Body))
            .ToList(),
        handoff is null
            ? null
            : new AgentHandoffSummaryDto(
                handoff.TicketAgentHandoffId,
                handoff.Status.ToString(),
                handoff.Mode?.ToString(),
                handoff.RequestReason,
                handoff.RequestedAtUtc,
                handoff.AssignedEmployeeId,
                handoff.GenesysAgentId,
                handoff.ResolvedAtUtc,
                handoff.ResolutionNote,
                handoff.Trigger?.ToString()),
        // Who is handling THIS ticket's interaction is judged on its own messages only: a person answering the conversation under a
        // sibling ticket does not make this ticket human-handled.
        DescribeHandlingState(interaction, ownMessages, handoff),
        HumanAccepted: HumanAccepted(interaction, handoff),
        HumanResponded: ownMessages.Any(m => m.Sender == InteractionMessageSender.HumanAgent));

    /// <summary>A person actually took the interaction (not merely was assigned the ticket): a handoff taken, a recorded handler, or the answering agent of a call.</summary>
    private static bool HumanAccepted(TicketInteraction interaction, TicketAgentHandoff? handoff) =>
        handoff is { Status: AgentHandoffStatus.Assigned or AgentHandoffStatus.InProgress or AgentHandoffStatus.Completed }
        || interaction.HandledByUserId is not null
        || (interaction.ChannelId == WellKnownChannels.Phone && !string.IsNullOrWhiteSpace(interaction.GenesysAgentId));

    /// <summary>
    /// Who is handling the interaction, from facts only: a person took the
    /// requested work, a person's line is in the transcript, a Ticketing user is
    /// recorded as handler, or — on the phone, where a person always answers —
    /// Genesys named an agent. A conversation with no human signal is the AI's,
    /// and an outstanding explicit handoff request reads as "waiting", never as
    /// a human being present.
    /// </summary>
    private static string? DescribeHandlingState(
        TicketInteraction interaction, IReadOnlyList<TicketInteractionMessage> messages, TicketAgentHandoff? handoff)
    {
        if (interaction.Source != InteractionContextSource.Genesys)
        {
            return null;
        }

        var humanTookIt =
            handoff is { Status: AgentHandoffStatus.Assigned or AgentHandoffStatus.InProgress or AgentHandoffStatus.Completed }
            || interaction.HandledByUserId is not null
            || messages.Any(m => m.Sender == InteractionMessageSender.HumanAgent)
            || (interaction.ChannelId == WellKnownChannels.Phone && !string.IsNullOrWhiteSpace(interaction.GenesysAgentId));
        if (humanTookIt)
        {
            return "Human";
        }

        if (handoff is { Status: AgentHandoffStatus.WaitingForAgent })
        {
            return "WaitingForHuman";
        }

        // A person is needed (explicit follow-up) but nobody has accepted or replied: not "AI handling", whether or not an employee was assigned.
        if (interaction.RequiresHumanFollowUp)
        {
            return "HumanFollowUp";
        }

        // The AI conversation is over and nobody asked for a person: it is no longer "being handled".
        return interaction.IsEnded ? "AiEnded" : "Ai";
    }
}
