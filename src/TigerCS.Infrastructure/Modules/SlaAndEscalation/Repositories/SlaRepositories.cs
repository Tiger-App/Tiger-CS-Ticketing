using Microsoft.EntityFrameworkCore;
using TigerCS.Application.Modules.SlaAndEscalation.Abstractions;
using TigerCS.Application.Modules.SlaAndEscalation.Dto;
using TigerCS.Domain.Modules.SlaAndEscalation;
using TigerCS.Domain.Modules.Ticketing;
using TigerCS.Infrastructure.Persistence;

namespace TigerCS.Infrastructure.Modules.SlaAndEscalation.Repositories;

public sealed class SlaPolicyRepository(TigerCsDbContext dbContext) : ISlaPolicyRepository
{
    public Task<SlaPolicy?> GetByPriorityIdAsync(byte priorityId, CancellationToken cancellationToken = default) =>
        dbContext.SlaPolicies.FirstOrDefaultAsync(p => p.PriorityId == priorityId && p.IsActive, cancellationToken);

    public async Task<IReadOnlyList<SlaPolicy>> ListAsync(CancellationToken cancellationToken = default) =>
        await dbContext.SlaPolicies.AsNoTracking().OrderBy(p => p.PriorityId).ToListAsync(cancellationToken);
}

/// <summary>
/// Loads the active calendar, its working days and its holidays, and resolves
/// them into the immutable snapshot the due-date calculation consumes.
///
/// <para>
/// <b>The time zone is resolved here, not in the calculation.</b> Looking up
/// a <see cref="TimeZoneInfo"/> touches the host's zone database, which is an
/// infrastructure concern; keeping it out of
/// <c>SlaDueDateCalculator</c> is what lets that calculation stay a pure,
/// environment-independent function (ADR-0010).
/// </para>
/// </summary>
public sealed class BusinessCalendarRepository(TigerCsDbContext dbContext) : IBusinessCalendarRepository
{
    public async Task<BusinessCalendarSnapshot?> GetActiveSnapshotAsync(CancellationToken cancellationToken = default)
    {
        var calendar = await dbContext.BusinessCalendars
            .Where(c => c.IsActive)
            .OrderByDescending(c => c.EffectiveFromUtc)
            .FirstOrDefaultAsync(cancellationToken);

        if (calendar is null)
        {
            return null;
        }

        var workingDays = await dbContext.BusinessCalendarWorkingDays
            .Where(d => d.BusinessCalendarId == calendar.BusinessCalendarId && d.IsWorkingDay)
            .Select(d => d.DayOfWeek)
            .ToListAsync(cancellationToken);

        var holidays = await dbContext.Holidays
            .Where(h => h.BusinessCalendarId == calendar.BusinessCalendarId)
            .Select(h => h.HolidayDate)
            .ToListAsync(cancellationToken);

        return new BusinessCalendarSnapshot(
            ResolveTimeZone(calendar.TimeZone),
            calendar.BusinessDayStartLocal,
            calendar.BusinessDayEndLocal,
            workingDays.Select(d => (DayOfWeek)d),
            holidays);
    }

    public async Task<SlaCalendarDto?> GetActiveDescriptionAsync(CancellationToken cancellationToken = default)
    {
        var calendar = await dbContext.BusinessCalendars
            .AsNoTracking()
            .Where(c => c.IsActive)
            .OrderByDescending(c => c.EffectiveFromUtc)
            .FirstOrDefaultAsync(cancellationToken);

        if (calendar is null)
        {
            return null;
        }

        var workingDays = await dbContext.BusinessCalendarWorkingDays
            .Where(d => d.BusinessCalendarId == calendar.BusinessCalendarId && d.IsWorkingDay)
            .Select(d => d.DayOfWeek)
            .ToListAsync(cancellationToken);

        var holidays = await dbContext.Holidays
            .Where(h => h.BusinessCalendarId == calendar.BusinessCalendarId)
            .Select(h => h.HolidayDate)
            .OrderBy(h => h)
            .ToListAsync(cancellationToken);

        var today = TigerCS.Domain.Common.UaeCalendar.Today(DateTime.UtcNow);   // holidays are dates on the UAE calendar
        return new SlaCalendarDto(
            calendar.Name,
            calendar.BusinessDayStartLocal.ToString("HH:mm"),
            calendar.BusinessDayEndLocal.ToString("HH:mm"),
            calendar.TimeZone,
            workingDays.OrderBy(d => d).Select(d => ((DayOfWeek)d).ToString()).ToList(),
            holidays.Count,
            holidays.Where(h => h >= today).Take(10).ToList());
    }

    /// <summary>
    /// Fails loudly on an unknown zone rather than falling back to UTC. A
    /// silent UTC fallback would shift every non-Critical business-hours
    /// deadline by the zone's offset — four hours for the approved
    /// Asia/Dubai calendar — and produce due dates that look plausible and
    /// are wrong, which is the worst possible failure mode for a
    /// contractually load-bearing calculation (NFR-UAE-01).
    /// </summary>
    private static TimeZoneInfo ResolveTimeZone(string timeZoneId)
    {
        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(timeZoneId);
        }
        catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            throw new InvalidOperationException(
                $"BusinessCalendar time zone '{timeZoneId}' could not be resolved on this host. SLA due dates cannot be computed "
                + "without it, and falling back to UTC would silently shift every business-hours deadline (NFR-UAE-01).",
                ex);
        }
    }
}

public sealed class TicketSlaInstanceRepository(TigerCsDbContext dbContext) : ITicketSlaInstanceRepository
{
    public Task<TicketSlaInstance?> GetCurrentAsync(long ticketId, CancellationToken cancellationToken = default) =>
        dbContext.TicketSlaInstances
            .FirstOrDefaultAsync(i => i.TicketId == ticketId && i.PeriodEndAtUtc == null, cancellationToken);

    public async Task<IReadOnlyList<TicketSlaInstance>> ListByTicketIdAsync(
        long ticketId, CancellationToken cancellationToken = default) =>
        await dbContext.TicketSlaInstances
            .Where(i => i.TicketId == ticketId)
            .OrderBy(i => i.PeriodStartAtUtc)
            .ThenBy(i => i.TicketSlaInstanceId)
            .ToListAsync(cancellationToken);

    public async Task AddAsync(TicketSlaInstance instance, CancellationToken cancellationToken = default) =>
        await dbContext.TicketSlaInstances.AddAsync(instance, cancellationToken);

    /// <summary>
    /// SLA-Architecture.md §14's sweep candidates. Narrowed in the database
    /// rather than in memory: current periods only, this clock not already
    /// flagged, deadline reached, the ticket's clock actually running, and
    /// the deadline's own achievement event still absent — so a ticket that
    /// responded or resolved on time is never re-examined.
    /// </summary>
    public async Task<IReadOnlyList<long>> ListTicketIdsWithDeadlineDueAsync(
        SlaDeadlineType deadlineType, DateTime asOfUtc, int maxResults, CancellationToken cancellationToken = default)
    {
        var current = dbContext.TicketSlaInstances.Where(i => i.PeriodEndAtUtc == null);

        current = deadlineType == SlaDeadlineType.FirstResponse
            ? current.Where(i => !i.FirstResponseBreached && i.FirstResponseDueAtUtc <= asOfUtc)
            : current.Where(i => !i.ResolutionBreached && i.ResolutionDueAtUtc <= asOfUtc);

        var query =
            from instance in current
            join ticket in dbContext.Tickets on instance.TicketId equals ticket.TicketId

            // A Closed ticket's SLA outcome is settled and immutable; a
            // ticket whose clock is not running (a provisional one awaiting
            // reconciliation) has no deadline to miss.
            //
            // Pause (ISSUE-018): only the Resolution clock pauses, so a
            // ticket with an open pause is skipped for Resolution — its held
            // deadline is not running out — but is still a First Response
            // candidate (that clock never pauses). The open-pause row, not
            // SlaState, is the test: SlaState turns Breached when the First
            // Response clock breaches mid-pause, and the Resolution clock is
            // still paused then.
            where ticket.TicketStatus != TicketStatus.Closed
                  && (deadlineType == SlaDeadlineType.FirstResponse
                      ? ticket.FirstHumanResponseAtUtc == null
                      : ticket.TicketStatus != TicketStatus.Resolved
                        && !dbContext.TicketSlaPausePeriods.Any(p => p.TicketId == ticket.TicketId && p.ResumedAtUtc == null))
            orderby instance.TicketSlaInstanceId
            select instance.TicketId;

        return await query.Take(maxResults).ToListAsync(cancellationToken);
    }
}

public sealed class TicketSlaPausePeriodRepository(TigerCsDbContext dbContext) : ITicketSlaPausePeriodRepository
{
    public Task<TicketSlaPausePeriod?> GetOpenAsync(long ticketId, CancellationToken cancellationToken = default) =>
        dbContext.TicketSlaPausePeriods.FirstOrDefaultAsync(p => p.TicketId == ticketId && p.ResumedAtUtc == null, cancellationToken);

    public async Task<IReadOnlyList<TicketSlaPausePeriod>> ListByInstanceIdAsync(
        long ticketSlaInstanceId, CancellationToken cancellationToken = default) =>
        await dbContext.TicketSlaPausePeriods
            .Where(p => p.TicketSlaInstanceId == ticketSlaInstanceId)
            .OrderBy(p => p.StartedAtUtc)
            .ThenBy(p => p.TicketSlaPausePeriodId)
            .ToListAsync(cancellationToken);

    public async Task AddAsync(TicketSlaPausePeriod pause, CancellationToken cancellationToken = default) =>
        await dbContext.TicketSlaPausePeriods.AddAsync(pause, cancellationToken);
}

public sealed class TicketEscalationRepository(TigerCsDbContext dbContext) : ITicketEscalationRepository
{
    public async Task AddAsync(TicketEscalation escalation, CancellationToken cancellationToken = default) =>
        await dbContext.TicketEscalations.AddAsync(escalation, cancellationToken);

    public async Task<IReadOnlyList<TicketEscalation>> ListByTicketIdAsync(
        long ticketId, CancellationToken cancellationToken = default) =>
        await dbContext.TicketEscalations
            .Where(e => e.TicketId == ticketId)
            .OrderByDescending(e => e.RaisedAtUtc)
            .ThenByDescending(e => e.TicketEscalationId)
            .ToListAsync(cancellationToken);

    public Task<bool> HasAutomaticBreachEscalationAsync(long ticketId, CancellationToken cancellationToken = default) =>
        dbContext.TicketEscalations.AnyAsync(
            e => e.TicketId == ticketId && e.TriggerType == EscalationTriggerType.AutoBreach, cancellationToken);
}
