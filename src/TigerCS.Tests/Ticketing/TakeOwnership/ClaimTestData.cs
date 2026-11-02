using System.Reflection;
using TigerCS.Domain.Modules.Ticketing;
using TigerCS.Domain.Modules.WorkflowConfiguration;

namespace TigerCS.Tests.Ticketing.TakeOwnership;

/// <summary>Builds the ticket shapes the Take Ownership rules distinguish. Test setup only - it writes the domain objects directly.</summary>
internal static class ClaimTestData
{
    public static readonly DateTime Now = new(2026, 11, 2, 9, 0, 0, DateTimeKind.Utc);

    /// <summary>An old Open ticket sitting in a department with nobody assigned.</summary>
    public static Ticket Open(string number, int departmentId, int ageHours = 24 * 30) =>
        Ticket.CreateUnclassified(number, departmentId, "AC not cooling", Now.AddHours(-ageHours));

    /// <summary>InProgress and unowned: it was worked, then released (a shape the legacy backlog really has).</summary>
    public static void MakeInProgressUnowned(Ticket ticket, Guid formerWorker)
    {
        ticket.AssignTo(formerWorker);
        ticket.ChangeStatus(TicketStatus.InProgress);
        ticket.ReleaseOwner();
    }

    public static void MakeResolvedUnowned(Ticket ticket, Guid formerWorker)
    {
        MakeInProgressUnowned(ticket, formerWorker);
        ticket.AssignTo(formerWorker);
        ticket.Resolve(ResolutionOutcome.Resolved, null);
        ticket.ReleaseOwner();
    }

    public static void MakeClosedUnowned(Ticket ticket, Guid formerWorker)
    {
        MakeResolvedUnowned(ticket, formerWorker);
        ticket.Close();
    }

    /// <summary>A Genesys interaction; the default is an ACTIVE AI conversation (live, no human requested, nobody took it).</summary>
    public static TicketInteraction Genesys(
        long ticketId, bool requiresHuman = false, bool ended = false, Guid? handledBy = null, bool inactivityClosed = false)
    {
        var interaction = TicketInteraction.CreateFromGenesys(
            ticketId, WellKnownChannels.Phone, "+971500000000", "conv-" + Guid.NewGuid().ToString("N")[..16],
            calledNumber: null, genesysQueueId: null, genesysQueueName: null, genesysAgentId: null, genesysAgentName: null,
            interactionStartedAtUtc: Now.AddDays(-30), direction: "Inbound", createdAtUtc: Now.AddDays(-30),
            isOriginatingInteraction: true, genesysServiceRequestId: Guid.NewGuid().ToString("N")[..8], requiresHumanFollowUp: requiresHuman);
        if (ended)
        {
            interaction.End(Now.AddDays(-29), "customer-disconnect");
        }

        if (handledBy is { } agent)
        {
            interaction.RecordHandlingAgentIfAbsent("genesys-" + agent.ToString("N")[..8], agent);
        }

        if (inactivityClosed)
        {
            interaction.RecordInactivityClosure(Now.AddDays(-28));
        }

        return interaction;
    }

    /// <summary>An approval waiting for a decision (the private constructor is the only way to build one without a configured requirement).</summary>
    public static TicketApproval PendingApproval(long ticketId, Guid requestedBy)
    {
        var constructor = typeof(TicketApproval).GetConstructors(BindingFlags.NonPublic | BindingFlags.Instance)
            .Single(c => c.GetParameters().Length == 10);
        return (TicketApproval)constructor.Invoke(
            [ticketId, ApprovalType.AccountingApproval, ApprovalTargetKind.Department, null, null, null, requestedBy, Now.AddDays(-1), null, Guid.NewGuid()]);
    }
}
