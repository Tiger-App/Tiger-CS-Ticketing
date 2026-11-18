using TigerCS.Application.Abstractions;
using TigerCS.Application.Modules.Ticketing.Abstractions;
using TigerCS.Application.Modules.Ticketing.Dto;
using TigerCS.Domain.Modules.IdentityAndAccess;
using TigerCS.Domain.Modules.Ticketing;

namespace TigerCS.Application.Modules.Ticketing.Services;

/// <summary>
/// Internal ticket notes (this increment's item 5, MVP-API-Contracts.md
/// §4.1/§4.2). Customer-facing comments and attachments are out of scope.
/// Visibility/authorization mirrors ticket detail's own department-scoped
/// view rule (<see cref="TicketQueryAppService"/>) — a note is only ever
/// meaningful in the context of a ticket the caller may already view.
/// </summary>
public sealed class TicketNoteAppService(
    ITicketRepository ticketRepository,
    ITicketNoteRepository ticketNoteRepository,
    TicketQueryAppService ticketQueryAppService,
    ITicketingUnitOfWork unitOfWork,
    IAuditEntryWriter auditWriter,
    TimeProvider timeProvider,
    TigerCS.Application.Modules.IdentityAndAccess.Abstractions.IEmployeeRepository? employeeRepository = null)
{
    public async Task<NoteResult> AddNoteAsync(
        Guid callerEmployeeId,
        IReadOnlyCollection<string> callerRoles,
        long ticketId,
        CreateNoteRequestDto request,
        CancellationToken cancellationToken = default)
    {
        var ticket = await ticketRepository.GetByIdAsync(ticketId, cancellationToken);
        if (ticket is null)
        {
            return NoteResult.Failure(NoteOutcome.TicketNotFound);
        }

        // Read-only roles (Chairman/CEO, Reporting User) may read notes but never write one.
        if (Roles.IsReadOnlyCaller(callerRoles))
        {
            return NoteResult.Failure(NoteOutcome.Forbidden);
        }

        if (!await ticketQueryAppService.CanModifyTicketAsync(callerEmployeeId, callerRoles, ticket, cancellationToken))
        {
            return NoteResult.Failure(NoteOutcome.Forbidden);
        }

        var now = timeProvider.GetUtcNow().UtcDateTime;
        var note = new TicketNote(ticketId, request.NoteText, callerEmployeeId, now);

        await using var transaction = await unitOfWork.BeginTransactionAsync(cancellationToken);

        await ticketNoteRepository.AddAsync(note, cancellationToken);

        await auditWriter.WriteAsync(
            callerEmployeeId, "AddNote", "Ticket", ticketId.ToString(),
            beforeValue: null, afterValue: null, Guid.NewGuid(), cancellationToken);

        await unitOfWork.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return NoteResult.Success(new TicketNoteResponseDto(note.TicketNoteId, ticketId, note.NoteText, callerEmployeeId, now));
    }

    public async Task<TicketQueryResultDto<TicketNoteListResultDto>> ListNotesAsync(
        Guid callerEmployeeId,
        IReadOnlyCollection<string> callerRoles,
        long ticketId,
        int page,
        int pageSize,
        CancellationToken cancellationToken = default)
    {
        var ticket = await ticketRepository.GetByIdAsync(ticketId, cancellationToken);
        if (ticket is null)
        {
            return TicketQueryResultDto<TicketNoteListResultDto>.Failure(TicketQueryOutcome.NotFound);
        }

        if (!await ticketQueryAppService.CanViewTicketAsync(callerEmployeeId, callerRoles, ticket, cancellationToken))
        {
            return TicketQueryResultDto<TicketNoteListResultDto>.Failure(TicketQueryOutcome.Forbidden);
        }

        page = page < 1 ? 1 : page;
        pageSize = pageSize is < 1 or > 200 ? 50 : pageSize;

        var notes = await ticketNoteRepository.ListByTicketIdAsync(ticketId, page, pageSize, cancellationToken);
        var totalCount = await ticketNoteRepository.CountByTicketIdAsync(ticketId, cancellationToken);

        var names = new Dictionary<Guid, string?>();
        foreach (var authorId in notes.Select(n => n.AuthorEmployeeId).Distinct())
        {
            names[authorId] = employeeRepository is null ? null : (await employeeRepository.GetByIdAsync(authorId, cancellationToken))?.DisplayName;
        }

        var items = notes
            .Select(n => new TicketNoteResponseDto(n.TicketNoteId, n.TicketId, n.NoteText, n.AuthorEmployeeId, n.CreatedAtUtc, names.GetValueOrDefault(n.AuthorEmployeeId)))
            .ToList();

        return TicketQueryResultDto<TicketNoteListResultDto>.Success(new TicketNoteListResultDto(items, totalCount, page, pageSize));
    }
}
