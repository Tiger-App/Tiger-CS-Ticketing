using TigerCS.Application.Abstractions;
using TigerCS.Application.Authorization;
using TigerCS.Application.Modules.CustomerVerification.Abstractions;
using TigerCS.Application.Modules.IdentityAndAccess.Abstractions;
using TigerCS.Application.Modules.SlaAndEscalation.Dto;
using TigerCS.Application.Modules.Ticketing.Abstractions;
using TigerCS.Domain.Modules.IdentityAndAccess;
using TigerCS.Domain.Modules.SlaAndEscalation;
using TigerCS.Domain.Modules.Ticketing;

namespace TigerCS.Application.Modules.SlaAndEscalation.Services;

/// <summary>
/// MVP-API-Contracts.md §5.2 — records the First Human Response, the only
/// event that satisfies the First Response SLA (ISSUE-019: the automated
/// acknowledgement never does, on any channel).
///
/// <para>
/// Recording the response immediately finalizes
/// <c>TicketSlaInstances.FirstResponseBreached</c> if it landed after the
/// deadline (§5.2's own "Domain events" note), through the same
/// <see cref="SlaBreachProcessor"/> and the same idempotency key the
/// scheduled job and the sweep use — so a response arriving moments after a
/// job already flagged the breach records nothing twice.
/// </para>
/// </summary>
public sealed class SlaFirstResponseAppService(
    ITicketRepository ticketRepository,
    Abstractions.ITicketSlaInstanceRepository slaInstanceRepository,
    IUserDepartmentAssignmentRepository userDepartmentAssignmentRepository,
    FirstHumanResponseRecorder firstHumanResponseRecorder,
    ITicketingUnitOfWork unitOfWork,
    TimeProvider timeProvider)
{
    /// <summary>
    /// How far before the ticket's own creation a <see cref="FirstResponseSource.GenesysCallAnswer"/> may be placed. SLA-Architecture.md §16
    /// Example E is a call answered "a moment" before the agent finished creating its ticket; it is not a licence to place the response
    /// arbitrarily far back. Beyond this window the request is refused (400) rather than clamped, so a caller never believes a different
    /// time was stored than the one sent.
    /// </summary>
    public static readonly TimeSpan GenesysCallAnswerLeadAllowance = TimeSpan.FromHours(1);

    /// <summary>Tolerated clock skew for a response stamped slightly ahead of the server's clock. Anything later is a response that has not happened yet.</summary>
    public static readonly TimeSpan FutureTolerance = TimeSpan.FromMinutes(5);

    /// <summary>
    /// The sources a person may name through the API. <see cref="FirstResponseSource.HumanAgentMessage"/> is deliberately absent: it
    /// means "the system observed a HumanAgent transcript line" and is recorded only by the Genesys conversation-end path, so a caller
    /// cannot label a hand-entered time with the system's own provenance.
    /// </summary>
    private static readonly IReadOnlySet<FirstResponseSource> ApiRecordableSources =
        new HashSet<FirstResponseSource> { FirstResponseSource.Manual, FirstResponseSource.GenesysCallAnswer };

    public async Task<SlaOperationResult<TicketSlaSummaryResponseDto>> RecordAsync(
        Guid callerEmployeeId,
        IReadOnlyCollection<string> callerRoles,
        long ticketId,
        RecordFirstResponseRequestDto request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var ticket = await ticketRepository.GetByIdAsync(ticketId, cancellationToken);
        if (ticket is null)
        {
            return SlaOperationResult<TicketSlaSummaryResponseDto>.Failure(SlaOperationOutcome.NotFound);
        }

        // Enum.TryParse also accepts numeric text ("99") and any integer as an undefined member, so the name must be a defined one,
        // and it must be a source a person may record - not the system-only HumanAgentMessage.
        if (!Enum.TryParse<FirstResponseSource>(request.Source, ignoreCase: true, out var source)
            || !Enum.IsDefined(source)
            || !ApiRecordableSources.Contains(source))
        {
            return SlaOperationResult<TicketSlaSummaryResponseDto>.Failure(SlaOperationOutcome.InvalidRequest);
        }

        if (!await IsAuthorizedAsync(callerEmployeeId, callerRoles, ticket, cancellationToken))
        {
            return SlaOperationResult<TicketSlaSummaryResponseDto>.Failure(SlaOperationOutcome.Forbidden);
        }

        // Closed-ticket immutability, rejected before any transaction or
        // write — the same ordering TicketLifecycleAppService uses.
        if (ticket.TicketStatus == TicketStatus.Closed)
        {
            return SlaOperationResult<TicketSlaSummaryResponseDto>.Failure(SlaOperationOutcome.TicketClosed);
        }

        if (ticket.FirstHumanResponseAtUtc is not null)
        {
            return SlaOperationResult<TicketSlaSummaryResponseDto>.Failure(SlaOperationOutcome.FirstResponseAlreadyRecorded);
        }

        var nowUtc = timeProvider.GetUtcNow().UtcDateTime;
        var occurredAtUtc = request.OccurredAtUtc ?? nowUtc;

        // A response cannot have happened in the future, and a person recording it cannot place it before the ticket existed. The one
        // allowance is SLA-Architecture §16 Example E - a Genesys call answered moments before its ticket was created - and even that
        // is bounded (GenesysCallAnswerLeadAllowance). Without these bounds a caller could backdate the response to erase a breach.
        // A response that genuinely happened outside the system is still recorded exactly as sent; it is made accountable, not blocked:
        // the audit entry carries who recorded it, when it was recorded, and how far back it was placed.
        var earliestAllowedUtc = source == FirstResponseSource.GenesysCallAnswer
            ? ticket.CreatedAtUtc - GenesysCallAnswerLeadAllowance
            : ticket.CreatedAtUtc;
        if (occurredAtUtc > nowUtc + FutureTolerance || occurredAtUtc < earliestAllowedUtc)
        {
            return SlaOperationResult<TicketSlaSummaryResponseDto>.Failure(SlaOperationOutcome.InvalidRequest);
        }

        ticketRepository.SetRowVersion(ticket, request.RowVersion);
        var correlationId = Guid.NewGuid();

        await using var transaction = await unitOfWork.BeginTransactionAsync(cancellationToken);

        // The history row, the audit entry and the breach finalization all
        // live in FirstHumanResponseRecorder, which the Genesys transcript
        // path shares — ISSUE-019's measurement is written in exactly one way.
        // The already-recorded and Closed cases were rejected above with their
        // own outcomes, so a false here is that same race answered again.
        if (!await firstHumanResponseRecorder.TryRecordAsync(
                ticket, occurredAtUtc, nowUtc, callerEmployeeId, actorIsSystem: false,
                source, correlationId, cancellationToken))
        {
            return SlaOperationResult<TicketSlaSummaryResponseDto>.Failure(SlaOperationOutcome.FirstResponseAlreadyRecorded);
        }

        try
        {
            await unitOfWork.SaveChangesAsync(cancellationToken);
        }
        catch (TicketConcurrentlyModifiedException)
        {
            return SlaOperationResult<TicketSlaSummaryResponseDto>.Failure(SlaOperationOutcome.ConcurrencyConflict);
        }
        catch (DuplicateWriteException)
        {
            // A concurrent detection path claimed the same breach key first.
            return SlaOperationResult<TicketSlaSummaryResponseDto>.Failure(SlaOperationOutcome.ConcurrencyConflict);
        }

        await transaction.CommitAsync(cancellationToken);

        return SlaOperationResult<TicketSlaSummaryResponseDto>.Success(
            SlaQueryAppService.ToSummaryDto(ticket, await slaInstanceRepository.GetCurrentAsync(ticketId, cancellationToken)));
    }

    /// <summary>MVP-API-Contracts.md §5.2's "Agent and above": the ticket's current owner, a cross-department supervisory role, or a Department Head in the ticket's own department. A permission rule, so it runs under the ADR-0024 override via the gate.</summary>
    private Task<bool> IsAuthorizedAsync(
        Guid callerEmployeeId, IReadOnlyCollection<string> callerRoles, Ticket ticket, CancellationToken cancellationToken) =>
        AuthorizationGate.EvaluateAsync(callerRoles, async () =>
        {
            if (Roles.IsReadOnlyCaller(callerRoles))
            {
                return false;
            }

            if (ticket.CurrentOwnerEmployeeId == callerEmployeeId)
            {
                return true;
            }

            if (callerRoles.Any(SlaRoleSets.RecordFirstResponse.Contains))
            {
                return true;
            }

            return callerRoles.Contains(Roles.DepartmentHead)
                && await userDepartmentAssignmentRepository.ExistsAsync(callerEmployeeId, ticket.CurrentDepartmentId, cancellationToken);
        });
}

/// <summary>MVP-API-Contracts.md §5.2's <c>Source</c> enum. <see cref="GenesysCallAnswer"/> is part of the approved DTO; no Genesys adapter ships in this increment, so nothing populates it automatically yet.</summary>
public enum FirstResponseSource : byte
{
    Manual = 1,
    GenesysCallAnswer = 2,

    /// <summary>
    /// The first genuinely human message of a conversation was stored — an
    /// <see cref="Domain.Modules.Ticketing.InteractionMessageSender.HumanAgent"/>
    /// transcript line. Observed by the system rather than recorded by a
    /// person, and deliberately never reached by a
    /// <see cref="Domain.Modules.Ticketing.InteractionMessageSender.VirtualAgent"/>
    /// line: an AI reply is not a first human response (ISSUE-019).
    /// </summary>
    HumanAgentMessage = 3
}
