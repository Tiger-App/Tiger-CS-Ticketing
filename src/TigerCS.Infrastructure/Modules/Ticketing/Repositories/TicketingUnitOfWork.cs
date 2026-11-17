using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using TigerCS.Application.Modules.CustomerVerification.Abstractions;
using TigerCS.Application.Modules.Ticketing.Abstractions;
using TigerCS.Domain.Modules.CustomerVerification;
using TigerCS.Domain.Modules.Ticketing;
using TigerCS.Infrastructure.Persistence;

namespace TigerCS.Infrastructure.Modules.Ticketing.Repositories;

/// <summary>Mirrors CustomerVerificationUnitOfWork's translation exactly — see its remarks for the SQL-Server-only detection rationale and the InMemory-provider caveat. Additionally translates a lost optimistic-concurrency race on VerificationSession consumption or on a Ticket's own RowVersion (see ITicketingUnitOfWork's remarks and TigerCsDbContext's supplemental concurrency-token configuration).</summary>
public sealed class TicketingUnitOfWork(TigerCsDbContext dbContext) : ITicketingUnitOfWork
{
    /// <summary>
    /// Starts a database transaction - or JOINS the one already running on this context. A caller that must make several application services atomic (the
    /// Queueing integration: intake + ticket + queue state + audit + inbox row) opens the outer transaction; every service it calls then begins "its own"
    /// transaction as before and receives a non-owning scope: its <c>CommitAsync</c> does nothing (only the owner commits) and disposing it without committing
    /// does nothing either (the owner's disposal rolls everything back). A <see cref="ITicketingTransaction.RollbackAsync"/> on a joined scope rolls the
    /// whole transaction back, so a participant can never be "rolled back" while the owner commits the rest. With no transaction running the behaviour is exactly
    /// what it always was, so every existing caller is unaffected.
    /// </summary>
    public async Task<ITicketingTransaction> BeginTransactionAsync(CancellationToken cancellationToken = default)
    {
        if (dbContext.Database.CurrentTransaction is { } ambient)
        {
            return new JoinedTicketingTransaction(ambient);
        }

        return new EfTicketingTransaction(await dbContext.Database.BeginTransactionAsync(cancellationToken));
    }

    public async Task SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException ex)
        {
            // EF Core's own client-side detection that an UPDATE affected
            // zero rows because a concurrency-token value had already
            // changed. Two independent tokens exist in this module's model:
            // VerificationSessions.Status (single-use session consumption)
            // and Tickets.RowVersion (assignment/transfer/status-change/
            // resolve/close/reconciliation) — inspect which entity type
            // actually lost the race so the caller gets the right, specific
            // exception rather than a misleading one.
            if (ex.Entries.Any(e => e.Entity is VerificationSession))
            {
                throw new VerificationSessionConcurrentlyConsumedException(ex);
            }

            throw new TicketConcurrentlyModifiedException(ex);
        }
        catch (DbUpdateException ex) when (IsUniqueConstraintViolation(ex))
        {
            throw new DuplicateWriteException(ex);
        }
        catch (DbUpdateException ex) when (IsDeadlockVictim(ex))
        {
            // SQL Server chose this transaction as the deadlock victim and has already rolled it back whole: nothing was written. Two requests
            // wrote the same ticket at the same moment - the same situation as a lost row-version race, so it gets the same answer (409, reload
            // and retry) instead of an unhandled 500.
            throw new TicketConcurrentlyModifiedException(ex);
        }
    }

    private static bool IsDeadlockVictim(DbUpdateException ex)
    {
        for (var inner = ex.InnerException; inner is not null; inner = inner.InnerException)
        {
            if (inner is SqlException { Number: 1205 })
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsUniqueConstraintViolation(DbUpdateException ex)
    {
        for (var inner = ex.InnerException; inner is not null; inner = inner.InnerException)
        {
            if (inner is SqlException { Number: 2601 or 2627 })
            {
                return true;
            }
        }

        return false;
    }

    private sealed class JoinedTicketingTransaction(IDbContextTransaction owner) : ITicketingTransaction
    {
        public Task CommitAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task RollbackAsync(CancellationToken cancellationToken = default) => owner.RollbackAsync(cancellationToken);

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class EfTicketingTransaction(IDbContextTransaction inner) : ITicketingTransaction
    {
        public Task CommitAsync(CancellationToken cancellationToken = default) => inner.CommitAsync(cancellationToken);

        public Task RollbackAsync(CancellationToken cancellationToken = default) => inner.RollbackAsync(cancellationToken);

        public ValueTask DisposeAsync() => inner.DisposeAsync();
    }
}
