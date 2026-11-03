using TigerCS.Application.Abstractions;
using TigerCS.Application.Modules.CustomerVerification.Abstractions;
using TigerCS.Application.Modules.GenesysIntegration.Dto;
using TigerCS.Application.Modules.Ticketing.Abstractions;
using TigerCS.Domain.Modules.Ticketing;

namespace TigerCS.Application.Modules.GenesysIntegration.Services;

/// <summary>
/// Records <b>every</b> Ticketing user that actually handles a Genesys interaction — the fact CS Agent ticket visibility rests on.
///
/// <para>
/// <c>TicketInteraction.HandledByUserId</c> is "the first resolved handler, never overwritten", so a call transferred from agent A to agent
/// B only ever names A. That is right for "who took it first" and wrong for "who handled it", which is what lets B see the ticket. This
/// recorder appends one <see cref="TicketInteractionHandler"/> per (interaction, user) as Genesys reports each agent connecting, and
/// leaves <c>HandledByUserId</c> alone.
/// </para>
///
/// <para>
/// <b>Callers must only pass an agent id Genesys reported as connected/handling</b> — see <see cref="TicketInteractionHandler"/> for what
/// does not count. The id is resolved here, server-side, through <c>AspNetUsers.GenesysUserId</c>; an id that does not map to an active
/// user (a bot, an unmapped or deactivated agent) records nothing and is not an error.
/// </para>
/// </summary>
public sealed class GenesysInteractionHandlerRecorder(
    GenesysAgentResolutionAppService agentResolution,
    ITicketInteractionHandlerRepository handlerRepository,
    ITicketingUnitOfWork unitOfWork,
    IAuditEntryWriter auditWriter,
    TimeProvider timeProvider)
{
    /// <summary>
    /// Resolves <paramref name="genesysAgentId"/> and, when it maps to an active user not yet recorded on the interaction, records it in its
    /// own transaction with an audit entry. Returns true when a new handler was recorded.
    /// </summary>
    public async Task<bool> RecordAsync(
        Guid callerEmployeeId, TicketInteraction interaction, string? genesysAgentId,
        InteractionHandlerSource source, DateTime? connectedAtUtc, CancellationToken cancellationToken = default,
        bool identityIsTranscriptSenderId = false)
    {
        ArgumentNullException.ThrowIfNull(interaction);

        if (string.IsNullOrWhiteSpace(genesysAgentId))
        {
            return false;
        }

        // A HumanAgent transcript senderId is the GUID, or (strict, unique-CS-Agent fallback) an e-mail; every other agent id is the GUID only.
        var resolution = identityIsTranscriptSenderId
            ? await agentResolution.ResolveSenderIdAsync(genesysAgentId, cancellationToken)
            : await agentResolution.ResolveByGenesysUserIdAsync(genesysAgentId, cancellationToken);
        if (!resolution.IsResolved)
        {
            return false;
        }

        var handler = await StageAsync(interaction, resolution.Agent!, source, connectedAtUtc, cancellationToken);
        if (handler is null)
        {
            return false;
        }

        try
        {
            await using var transaction = await unitOfWork.BeginTransactionAsync(cancellationToken);
            await WriteAuditAsync(callerEmployeeId, interaction, handler, cancellationToken);
            await unitOfWork.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return true;
        }
        catch (DuplicateWriteException)
        {
            // A concurrent delivery recorded the same (interaction, user) first: the fact is stored, which is all that matters.
            handlerRepository.Discard(handler);
            return false;
        }
    }

    /// <summary>
    /// Stages the handler on the caller's unit of work without saving (the caller saves with its own change). Null when the agent is already
    /// recorded on this interaction. <paramref name="agent"/> must be a successfully resolved mapping.
    /// </summary>
    public async Task<TicketInteractionHandler?> StageAsync(
        TicketInteraction interaction, GenesysMappedAgent agent,
        InteractionHandlerSource source, DateTime? connectedAtUtc, CancellationToken cancellationToken = default)
    {
        var handler = TicketInteractionHandler.Create(
            interaction.TicketInteractionId,
            interaction.TicketId,
            agent.UserId,
            agent.GenesysUserId,
            source,
            connectedAtUtc,
            timeProvider.GetUtcNow().UtcDateTime);

        return await handlerRepository.AddIfAbsentAsync(handler, cancellationToken) ? handler : null;
    }

    private Task WriteAuditAsync(
        Guid callerEmployeeId, TicketInteraction interaction, TicketInteractionHandler handler, CancellationToken cancellationToken) =>
        auditWriter.WriteAsync(
            callerEmployeeId,
            GenesysAuditActions.InteractionHandlerAdded,
            nameof(TicketInteraction),
            interaction.TicketInteractionId.ToString(),
            beforeValue: null,
            afterValue:
                $"ConversationId={interaction.GenesysConversationId ?? "(none)"};TicketId={interaction.TicketId};"
                + $"GenesysUserId={handler.GenesysAgentUserId};HandledByUserId={handler.HandledByUserId};Source={handler.Source};"
                + $"ConnectedAtUtc={handler.ConnectedAtUtc?.ToString("O") ?? "(none)"}",
            correlationId: Guid.NewGuid(),
            cancellationToken);
}
