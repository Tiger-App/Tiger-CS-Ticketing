using System.Reflection;
using TigerCS.Domain.Modules.Ticketing;

namespace TigerCS.Tests.Ticketing;

/// <summary>
/// <b>Test-data setup only.</b> Closed is terminal, so <see cref="Ticket.Reopen"/> always throws. Production history, however, still contains tickets that were reopened
/// BEFORE that rule existed (status active again, <c>ReopenCount</c> &gt; 0, owner cleared, department set by the reopener), and dashboards, reports, notifications and endpoint
/// tests need such rows as input. This helper produces exactly that persisted shape without going through a (now impossible) domain transition. It must never be used in
/// code that asserts the Closed rule - those tests assert the rejection instead.
/// </summary>
public static class LegacyReopenedTicket
{
    public static void Apply(Ticket ticket, int departmentId)
    {
        ArgumentNullException.ThrowIfNull(ticket);
        Set(ticket, nameof(Ticket.TicketStatus), TicketStatus.InProgress);
        Set(ticket, nameof(Ticket.ResolutionOutcome), (byte?)null);
        Set(ticket, nameof(Ticket.CurrentDepartmentId), departmentId);
        Set(ticket, nameof(Ticket.CurrentOwnerEmployeeId), (Guid?)null);
        Set(ticket, nameof(Ticket.ReopenCount), ticket.ReopenCount + 1);
    }

    private static void Set(Ticket ticket, string property, object? value)
    {
        var setter = typeof(Ticket).GetProperty(property, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)?.GetSetMethod(nonPublic: true)
            ?? throw new InvalidOperationException($"Ticket.{property} has no setter to model legacy data with.");
        setter.Invoke(ticket, [value]);
    }
}
