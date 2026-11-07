using TigerCS.Application.Abstractions;
using TigerCS.Application.Modules.Notifications.Services;
using TigerCS.Application.Modules.Ticketing.Abstractions;
using TigerCS.Domain.Modules.Ticketing;

namespace TigerCS.Application.Modules.Collections.TowerAssignments;

public enum CollectionsRoutingOutcome
{
    /// <summary>The ticket is not in Collections: nothing to do.</summary>
    NotApplicable,

    /// <summary>Assigned to the collector responsible for the ticket's tower this month.</summary>
    Assigned,

    /// <summary>The ticket already belongs to that collector.</summary>
    AlreadyAssigned,

    /// <summary>The unit or tower could not be determined. The ticket is left for manual handling.</summary>
    TowerUnresolved,

    /// <summary>The tower is known but nobody holds it (Active) this month. Left for manual handling.</summary>
    NoActiveAssignment,

    /// <summary>The responsible collector is no longer an active Collections employee. Left for manual handling.</summary>
    CollectorUnavailable,

    /// <summary>The ticket entered Collections from a department other than the Call Center with an owner already: that owner is kept (the existing transfer rule) and tower routing does not override it.</summary>
    OwnerKept,

    /// <summary>Someone assigned the ticket by hand while it was in Collections: never overwritten.</summary>
    ManualAssignmentKept
}

public sealed record CollectionsRoutingResult(CollectionsRoutingOutcome Outcome, Guid? AssignedEmployeeId = null, string? Detail = null)
{
    /// <summary>True when the automation made a decision about the ticket (so no other automatic assignment, like the CS Agent rotation, should pick someone for it).</summary>
    public bool Handled => Outcome != CollectionsRoutingOutcome.NotApplicable;
}

/// <summary>
/// Automatic assignment of a ticket that has entered Collections: tower -> this month's ACTIVE collector -> assigned. It reuses the ordinary ownership records (<c>TicketAssignments</c>,
/// the audit trail) and adds the Collections assignment history. It never guesses (unknown unit/tower = left in the queue), never uses a Draft, never reassigns on its own when the
/// monthly mapping changes (it only runs when a ticket ENTERS Collections), and never overwrites a manual assignment made while the ticket is in Collections. Status, priority and SLA
/// are not touched. Runs inside the caller's transaction and does not save.
/// </summary>
public sealed class CollectionsTicketRoutingService(
    CollectionsTeam team,
    CollectionsTicketTowerResolver resolver,
    ICollectionsTowerAssignmentRepository towerAssignments,
    ITicketAssignmentRepository ticketAssignments,
    ITicketAssignmentHistoryRepository history,
    IAuditEntryWriter auditWriter,
    CollectionsOptions options,
    IOutboxWriter? outboxWriter = null)
{
    private readonly TicketNotificationEvents? _notificationEvents = TicketNotificationEvents.CreateOrNull(outboxWriter, auditWriter);

    /// <param name="ticket">The ticket, already moved into its new department.</param>
    /// <param name="departmentEntry">True when the ticket has just entered Collections (a new stint): earlier manual assignments belong to a previous stint and do not block it.</param>
    /// <param name="fromCallCenter">True when the ticket came from the Call Center: its Call Center agent never stays the owner in Collections - a collector replaces them, or the ticket is released to the Collections queue.</param>
    /// <param name="nowUtc">The current UTC time (also picks the month whose assignments apply).</param>
    /// <param name="correlationId">Ties the audit entries to the surrounding operation.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    public async Task<CollectionsRoutingResult> RouteAsync(
        Ticket ticket, bool departmentEntry, bool fromCallCenter, DateTime nowUtc, Guid correlationId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(ticket);
        if (!await team.IsCollectionsDepartmentAsync(ticket.CurrentDepartmentId, cancellationToken))
            return new(CollectionsRoutingOutcome.NotApplicable);

        if (departmentEntry && !fromCallCenter && ticket.CurrentOwnerEmployeeId is not null)
            return new(CollectionsRoutingOutcome.OwnerKept, ticket.CurrentOwnerEmployeeId);

        if (!departmentEntry
            && await history.GetLatestAsync(ticket.TicketId, cancellationToken) is { Kind: TicketAssignmentKind.Manual } manual
            && manual.DepartmentId == ticket.CurrentDepartmentId)
            return new(CollectionsRoutingOutcome.ManualAssignmentKept);

        var resolution = await resolver.ResolveAsync(ticket, cancellationToken);
        if (resolution.Tower is not { } tower)
            return await LeftInQueueAsync(ticket, CollectionsRoutingOutcome.TowerUnresolved, resolution.Reason, fromCallCenter, nowUtc, correlationId, cancellationToken);

        var (year, month) = BusinessMonth(nowUtc);
        var row = await towerAssignments.GetActivePrimaryAsync(tower.TowerId, year, month, cancellationToken);
        if (row is null)
            return await LeftInQueueAsync(ticket, CollectionsRoutingOutcome.NoActiveAssignment,
                $"Tower {tower.TowerCode} has no active collector for {year}-{month:00}.", fromCallCenter, nowUtc, correlationId, cancellationToken);

        if (!await team.IsActiveMemberAsync(row.EmployeeId, ticket.CurrentDepartmentId, cancellationToken))
            return await LeftInQueueAsync(ticket, CollectionsRoutingOutcome.CollectorUnavailable,
                $"The collector of tower {tower.TowerCode} for {year}-{month:00} is not an active Collections employee.", fromCallCenter, nowUtc, correlationId, cancellationToken);

        var collector = row.EmployeeId;
        if (ticket.CurrentOwnerEmployeeId == collector)
            return new(CollectionsRoutingOutcome.AlreadyAssigned, collector);

        var previousOwner = ticket.CurrentOwnerEmployeeId;
        (await ticketAssignments.GetCurrentAsync(ticket.TicketId, cancellationToken))?.MarkSuperseded();
        ticket.AssignTo(collector);
        await ticketAssignments.AddAsync(
            new TicketAssignment(ticket.TicketId, collector, ticket.CurrentDepartmentId, nowUtc, assigningActorEmployeeId: null), cancellationToken);
        await history.AddAsync(
            new TicketAssignmentHistoryEntry(
                ticket.TicketId, ticket.CurrentDepartmentId, previousOwner, collector, TicketAssignmentKind.Automatic, changedByEmployeeId: null, nowUtc, reason: null,
                tower.TowerId, tower.TowerCode, tower.TowerName, resolution.UnitNumber),
            cancellationToken);
        await auditWriter.WriteAsync(
            actorEmployeeId: null, "AutoAssign", "Ticket", ticket.TicketId.ToString(),
            beforeValue: previousOwner?.ToString(),
            afterValue: $"AssignedEmployeeId={collector};DepartmentId={ticket.CurrentDepartmentId};Mode=CollectionsTower;TowerId={tower.TowerId};Tower={tower.TowerCode};Unit={resolution.UnitNumber ?? "(none)"};Month={year}-{month:00};Trigger=CollectionsEntry",
            correlationId, cancellationToken);
        if (_notificationEvents is not null)
            await _notificationEvents.QueueAssignedAsync(ticket, previousOwner, collector, actorEmployeeId: null, correlationId, nowUtc, cancellationToken);
        return new(CollectionsRoutingOutcome.Assigned, collector);
    }

    /// <summary>The business month (Collections time zone) a UTC instant falls in - the month whose tower assignments apply.</summary>
    public (int Year, int Month) BusinessMonth(DateTime utc) => CollectionsMonths.Of(utc, options.TimeZoneId);

    private async Task<CollectionsRoutingResult> LeftInQueueAsync(
        Ticket ticket, CollectionsRoutingOutcome outcome, string reason, bool releaseCallCenterOwner, DateTime nowUtc, Guid correlationId,
        CancellationToken cancellationToken)
    {
        var previousOwner = ticket.CurrentOwnerEmployeeId;
        // No collector can be named: the ticket must wait in the Collections queue, not stay with the Call Center agent who transferred it. Only the owner
        // pointer and the current assignment row change (the row is superseded, never deleted); status, priority and SLA are untouched.
        var released = releaseCallCenterOwner && previousOwner is not null;
        if (released)
        {
            (await ticketAssignments.GetCurrentAsync(ticket.TicketId, cancellationToken))?.MarkSuperseded();
            ticket.ReleaseOwner();
        }

        await auditWriter.WriteAsync(
            actorEmployeeId: null, "CollectionsAutoAssignSkipped", "Ticket", ticket.TicketId.ToString(),
            beforeValue: previousOwner?.ToString() ?? "DepartmentQueue",
            afterValue: $"Outcome={outcome};Reason={reason};Result={(released ? "ReleasedToCollectionsQueue" : "CollectionsQueue")}", correlationId, cancellationToken);
        return new(outcome, null, reason);
    }
}

public static class CollectionsMonths
{
    public static (int Year, int Month) Of(DateTime utc, string timeZoneId)
    {
        TimeZoneInfo zone;
        try { zone = TimeZoneInfo.FindSystemTimeZoneById(timeZoneId); } catch (TimeZoneNotFoundException) { zone = TimeZoneInfo.Utc; }
        var local = TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), zone);
        return (local.Year, local.Month);
    }
}
