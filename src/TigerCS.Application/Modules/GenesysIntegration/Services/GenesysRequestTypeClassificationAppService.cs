using TigerCS.Application.Modules.GenesysIntegration.Abstractions;
using TigerCS.Application.Modules.GenesysIntegration.Dto;
using TigerCS.Application.Modules.Ticketing.Abstractions;
using TigerCS.Application.Modules.Ticketing.Services;
using TigerCS.Domain.Modules.Ticketing;

namespace TigerCS.Application.Modules.GenesysIntegration.Services;

/// <summary>
/// The one place a Genesys-supplied request type is validated and applied to a
/// ticket — at initial ingestion and when the bot identifies it later — plus
/// the "nobody could classify this" path into the existing human follow-up
/// queue. Everything it does to the ticket is delegated to
/// <see cref="TicketRequestTypeRoutingService"/> (transfer semantics,
/// automatic assignment, SLA policy) and <see cref="GenesysAgentHandoffAppService"/>
/// (the human follow-up queue); nothing here is a second implementation.
///
/// <para>
/// <b>Idempotent and conservative.</b> Re-sending the request type a ticket
/// already has changes nothing; an invalid one is refused before any write.
/// Classification never touches the first-human-response measurement.
/// </para>
///
/// <para>
/// <b>The AI may refine its own classification — until a person has worked
/// the ticket.</b> A <i>different</i> request type replaces the current one
/// (history kept: <c>RequestTypeReclassified</c> workflow event + audit entry)
/// only while <see cref="GetReclassificationBlockAsync"/> finds nothing in the way: the ticket is Open, nobody
/// classified it by hand (no Category), no human has responded or accepted the
/// interaction, and no approval was raised under the old type. Otherwise the
/// change is refused with <see cref="GenesysClassificationOutcome.RequestTypeConflict"/>
/// and the ticket is left exactly as it was.
/// </para>
///
/// <para>
/// <b>A missing classification is not a human request.</b> A ticket without a
/// request type stays Unclassified and is picked up by the existing manual
/// classification workflow (Classify Ticket / the Unclassified filters); no
/// handoff row is written for it. Human work exists only when Genesys signals
/// it explicitly through the handoff contract.
/// </para>
/// </summary>
public sealed class GenesysRequestTypeClassificationAppService(
    ITicketRepository ticketRepository,
    ITicketApprovalRepository approvalRepository,
    IGenesysConversationRepository conversationRepository,
    ITicketAgentHandoffRepository handoffRepository,
    TicketRequestTypeRoutingService routingService,
    GenesysDefaultPriorityResolver defaultPriorityResolver,
    GenesysAgentHandoffAppService agentHandoffAppService,
    ITicketingUnitOfWork unitOfWork,
    TimeProvider timeProvider)
{
    /// <summary>
    /// The handoff reason prefix older versions of this service wrote on human work raised only because a ticket was unclassified. New tickets
    /// never get such work; the prefix is kept so a later classification still stands down any that already exists.
    /// </summary>
    public const string AwaitingClassificationReason = "Awaiting classification";

    /// <summary>
    /// Read-only check of an explicitly supplied request type, used BEFORE ingestion writes anything: an invalid one (unknown id, unknown or
    /// ambiguous name, inactive, or one that cannot route) is a client error, never a ticket in the human queue.
    /// </summary>
    public Task<RequestTypeResolution> ValidateAsync(GenesysRequestTypeDto requestType, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(requestType);
        return routingService.ResolveAsync(requestType.RequestTypeId, requestType.Name, cancellationToken);
    }

    /// <summary>
    /// Null when a validated request type pairs with exactly one active category of its department (so classifying by it yields a complete
    /// Category + Request Type), otherwise why it does not. A request type that cannot complete a classification takes the default
    /// classification instead of leaving the ticket half classified.
    /// </summary>
    public async Task<string?> GetCategoryPairingGapAsync(RequestTypeResolution resolution, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(resolution);
        if (!resolution.IsValid || resolution.RequestType is null)
        {
            return resolution.Detail ?? "the supplied request type cannot be used.";
        }

        return await routingService.ResolveCategoryIdAsync(resolution.RequestType, cancellationToken) is null
            ? $"request type '{resolution.RequestType.Name}' has no unique matching category in its department."
            : null;
    }

    public async Task<GenesysClassificationResult> ClassifyAsync(
        Guid callerEmployeeId,
        long ticketId,
        string conversationId,
        GenesysRequestTypeDto requestType,
        string source,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(requestType);

        var ticket = await ticketRepository.GetByIdAsync(ticketId, cancellationToken);
        if (ticket is null)
        {
            return GenesysClassificationResult.Failure(GenesysClassificationOutcome.TicketNotFound);
        }

        var resolution = await routingService.ResolveAsync(requestType.RequestTypeId, requestType.Name, cancellationToken);
        if (!resolution.IsValid)
        {
            return GenesysClassificationResult.Failure(GenesysClassificationOutcome.RequestTypeInvalid, resolution.Detail);
        }

        var resolved = resolution.RequestType!;

        var reclassify = false;
        if (ticket.RequestTypeId is { } existing)
        {
            if (existing == resolved.RequestTypeId)
            {
                return new GenesysClassificationResult(
                    GenesysClassificationOutcome.AlreadyClassified, resolved.RequestTypeId, resolved.Name, ticket.CurrentDepartmentId);
            }

            if (await GetReclassificationBlockAsync(ticket, cancellationToken) is { } block)
            {
                return GenesysClassificationResult.Failure(
                    GenesysClassificationOutcome.RequestTypeConflict,
                    $"Ticket {ticket.TicketNumber} is classified as request type {existing} and was not changed to {resolved.RequestTypeId}: {block}");
            }

            reclassify = true;
        }

        if (ticket.TicketStatus == TicketStatus.Closed)
        {
            return GenesysClassificationResult.Failure(GenesysClassificationOutcome.TicketClosed);
        }

        // Only a ticket that predates default priorities needs the default
        // here; a ticket that already has a priority is never touched.
        var defaultPriority = ticket.PriorityId is null
            ? await defaultPriorityResolver.ResolveAsync(cancellationToken)
            : null;

        var now = timeProvider.GetUtcNow().UtcDateTime;
        var correlationId = Guid.NewGuid();

        RequestTypeRoutingResult routed;
        await using (var transaction = await unitOfWork.BeginTransactionAsync(cancellationToken))
        {
            try
            {
                routed = await routingService.ApplyAsync(
                    ticket, resolution, callerEmployeeId, source, defaultPriority, now, correlationId, cancellationToken, reclassify);
                await unitOfWork.SaveChangesAsync(cancellationToken);
            }
            catch (TicketConcurrentlyModifiedException)
            {
                return GenesysClassificationResult.Failure(
                    GenesysClassificationOutcome.ConcurrencyConflict,
                    "The ticket was changed by someone else while it was being classified. Nothing was applied — retry.");
            }

            await transaction.CommitAsync(cancellationToken);
        }

        await StandDownAwaitingClassificationWorkAsync(callerEmployeeId, ticketId, conversationId, routed.RequestTypeName, cancellationToken);

        return new GenesysClassificationResult(
            GenesysClassificationOutcome.Classified, routed.RequestTypeId, routed.RequestTypeName, routed.DepartmentId,
            routed.Transferred, routed.AssignedEmployeeId, routed.AssignmentOutcome, routed.SlaOutcome);
    }

    /// <summary>
    /// The request type is missing: the ticket stays <b>Unclassified</b> and is left to the existing manual classification workflow.
    /// <b>No handoff is raised</b> — a missing classification says nothing about whether the customer needs a person, and recording a
    /// human request for it is what put "Waiting for agent" on conversations the bot was still handling.
    /// </summary>
    public static GenesysClassificationResult AwaitClassification(string reason) =>
        new(GenesysClassificationOutcome.AwaitingClassification, Detail: reason);

    /// <summary>
    /// Why the AI may not replace this ticket's request type, or null when it may. The AI refines its own work only: once a person has
    /// classified, answered, accepted or started an approval on the ticket, the classification is theirs.
    /// </summary>
    internal async Task<string?> GetReclassificationBlockAsync(Ticket ticket, CancellationToken cancellationToken)
    {
        if (ticket.TicketStatus != TicketStatus.Open)
        {
            return $"the ticket is {ticket.TicketStatus}.";
        }

        // A category the system paired with the current request type is the AI's own work; any other category was chosen by a person.
        if (ticket.CategoryId is { } category
            && !(ticket.RequestTypeId is { } currentTypeId
                 && await routingService.ResolveCategoryForRequestTypeIdAsync(currentTypeId, cancellationToken) == category))
        {
            return "a person classified it.";
        }

        if (ticket.FirstHumanResponseAtUtc is not null)
        {
            return "a person has already responded to the customer.";
        }

        if ((await approvalRepository.ListByTicketIdAsync(ticket.TicketId, cancellationToken)).Count > 0)
        {
            return "an approval was already raised under the current request type.";
        }

        var handoffs = await handoffRepository.ListByTicketIdAsync(ticket.TicketId, cancellationToken);
        if (handoffs.Any(h => h.Status is AgentHandoffStatus.Assigned or AgentHandoffStatus.InProgress or AgentHandoffStatus.Completed))
        {
            return "a human agent has taken the interaction.";
        }

        return null;
    }

    private async Task StandDownAwaitingClassificationWorkAsync(
        Guid callerEmployeeId, long ticketId, string conversationId, string requestTypeName, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(conversationId)
            || await conversationRepository.GetByConversationAndTicketAsync(conversationId.Trim(), ticketId, cancellationToken) is not { } interaction
            || await handoffRepository.GetOpenByInteractionIdAsync(interaction.TicketInteractionId, cancellationToken) is not { } open
            || open.RequestReason?.StartsWith(AwaitingClassificationReason, StringComparison.Ordinal) != true)
        {
            return;
        }

        // Only the work raised BECAUSE the ticket was unclassified is stood
        // down; a customer's own request for a human is left outstanding.
        await agentHandoffAppService.CancelAsync(
            callerEmployeeId,
            new GenesysHandoffCancellationDto(conversationId.Trim(), $"Request type '{requestTypeName}' was classified.", ticketId),
            cancellationToken);
    }
}
