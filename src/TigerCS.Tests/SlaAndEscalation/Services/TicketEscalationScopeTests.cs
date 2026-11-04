using TigerCS.Application.Modules.SlaAndEscalation.Dto;
using TigerCS.Application.Modules.SlaAndEscalation.Services;
using TigerCS.Domain.Modules.IdentityAndAccess;
using TigerCS.Domain.Modules.SlaAndEscalation;
using TigerCS.Domain.Modules.Ticketing;
using TigerCS.Tests.IdentityAndAccess.Fakes;
using TigerCS.Tests.SlaAndEscalation.Fakes;

namespace TigerCS.Tests.SlaAndEscalation.Services;

/// <summary>
/// Manual escalation is refused on the SERVER for a ticket the caller may not act on - not only hidden in the UI and not only filtered at the
/// API edge. A CS Agent holds the cross-department "escalate" right, so the role check alone would let them escalate any ticket in the system;
/// the service must also require the ticket to be one they can work (assigned to them, or department work they are the responsible CS Agent
/// for). Every role that could escalate before still can, on the tickets it could escalate.
///
/// These tests deliberately use a fixture in which NOT every user counts as the responsible CS Agent (the default SLA fixture does, for
/// tests whose subject is something else), so the visibility rule is what is under test.
/// </summary>
public class TicketEscalationScopeTests
{
    private const int TicketDepartmentId = 2;
    private static readonly DateTime CreatedAt = new(2026, 8, 23, 5, 0, 0, DateTimeKind.Utc);

    private sealed record Harness(SlaServiceFixture Sla, TicketEscalationAppService Service, Ticket Ticket, Guid OwnerId);

    private static async Task<Harness> CreateAsync(bool ownerIsCsAgent = true, bool assignOwner = true)
    {
        var sla = new SlaServiceFixture(departmentAssignments: new FakeUserDepartmentAssignmentRepository());

        var ticket = Ticket.CreateVerified(
            "TG-CS-20260823-0001", TicketDepartmentId, unitReferenceId: 10, contactReferenceId: 20,
            categoryId: 5, (byte)PriorityLevel.High, "AC not cooling", CreatedAt);
        await sla.Tickets.AddAsync(ticket);

        var ownerId = Guid.NewGuid();
        if (assignOwner)
        {
            ticket.AssignTo(ownerId);
            ticket.ChangeStatus(TicketStatus.InProgress);
            if (ownerIsCsAgent)
            {
                sla.DepartmentAssignments.RolesByEmployee[ownerId] = [Roles.CsAgent];
            }
        }

        return new Harness(sla, sla.CreateEscalationService(), ticket, ownerId);
    }

    private static ManualEscalationRequestDto Request(
        byte level = (byte)EscalationLevel.Level2, string triggerType = nameof(EscalationTriggerType.ManualFlag)) =>
        new(level, triggerType, "Cannot resolve at first contact.", RowVersion: []);

    private static void AssertNothingEscalated(Harness h)
    {
        Assert.Equal(EscalationLevel.None, h.Ticket.EscalationLevel);
        Assert.Empty(h.Sla.Escalations.All);
        Assert.DoesNotContain(h.Sla.Audit.Entries, e => e.Action == "EscalateTicket");
        Assert.DoesNotContain(h.Sla.StatusHistory.Added, e => e.Dimension == TicketStatusDimension.EscalationLevel);
    }

    // -----------------------------------------------------------------
    // Unauthorized: a CS Agent outside the ticket's scope
    // -----------------------------------------------------------------

    [Fact]
    public async Task CsAgent_CannotEscalateATicketAssignedToAnotherCsAgent()
    {
        var h = await CreateAsync();

        var result = await h.Service.EscalateAsync(Guid.NewGuid(), [Roles.CsAgent], h.Ticket.TicketId, Request());

        Assert.Equal(SlaOperationOutcome.Forbidden, result.Outcome);
        AssertNothingEscalated(h);
    }

    [Fact]
    public async Task CsAgent_CannotEscalateAnUnassignedTicket()
    {
        var h = await CreateAsync(assignOwner: false);

        var result = await h.Service.EscalateAsync(Guid.NewGuid(), [Roles.CsAgent], h.Ticket.TicketId, Request());

        Assert.Equal(SlaOperationOutcome.Forbidden, result.Outcome);
        AssertNothingEscalated(h);
    }

    /// <summary>Having once been the responsible agent is not enough while another CS Agent holds the ticket.</summary>
    [Fact]
    public async Task CsAgent_WhoIsOnlyTheFormerResponsibleAgent_CannotEscalateATicketAnotherCsAgentNowHolds()
    {
        var h = await CreateAsync();
        var former = Guid.NewGuid();
        h.Sla.DepartmentAssignments.ResponsibleCsAgentByTicket[h.Ticket.TicketId] = former;

        var result = await h.Service.EscalateAsync(former, [Roles.CsAgent], h.Ticket.TicketId, Request());

        Assert.Equal(SlaOperationOutcome.Forbidden, result.Outcome);
        AssertNothingEscalated(h);
    }

    [Fact]
    public async Task CsAgent_CannotEscalateAClosedTicketThatIsNotTheirs_AndLearnsNothingAboutItsStatus()
    {
        var h = await CreateAsync();
        h.Ticket.Resolve(TigerCS.Domain.Modules.Ticketing.ResolutionOutcome.Resolved, duplicateOfTicketId: null);
        h.Ticket.Close();

        var result = await h.Service.EscalateAsync(Guid.NewGuid(), [Roles.CsAgent], h.Ticket.TicketId, Request());

        // Forbidden, not TicketClosed: authorization is decided before the ticket's state is revealed.
        Assert.Equal(SlaOperationOutcome.Forbidden, result.Outcome);
    }

    [Fact]
    public async Task CsAgent_CannotReachALevelFourEscalation_OnTheirOwnTicketEither()
    {
        var h = await CreateAsync();

        var result = await h.Service.EscalateAsync(
            h.OwnerId, [Roles.CsAgent], h.Ticket.TicketId,
            Request((byte)EscalationLevel.Level4, nameof(EscalationTriggerType.ManualLevel4)));

        Assert.Equal(SlaOperationOutcome.Forbidden, result.Outcome);
        AssertNothingEscalated(h);
    }

    // -----------------------------------------------------------------
    // Authorized: existing permissions are preserved
    // -----------------------------------------------------------------

    [Fact]
    public async Task CsAgent_CanEscalateATicketAssignedToThem()
    {
        var h = await CreateAsync();

        var result = await h.Service.EscalateAsync(h.OwnerId, [Roles.CsAgent], h.Ticket.TicketId, Request());

        Assert.Equal(SlaOperationOutcome.Success, result.Outcome);
        Assert.Equal(EscalationLevel.Level2, h.Ticket.EscalationLevel);
        Assert.Single(h.Sla.Escalations.All);
        Assert.Contains(h.Sla.Audit.Entries, e => e.Action == "EscalateTicket" && e.ActorEmployeeId == h.OwnerId);
    }

    /// <summary>A department employee holds the ticket; the CS Agent who is responsible for that work may follow it up - including escalating it.</summary>
    [Fact]
    public async Task CsAgent_CanEscalateDepartmentWorkTheyAreTheResponsibleAgentFor()
    {
        var h = await CreateAsync(ownerIsCsAgent: false);
        var responsible = Guid.NewGuid();
        h.Sla.DepartmentAssignments.ResponsibleCsAgentByTicket[h.Ticket.TicketId] = responsible;

        var result = await h.Service.EscalateAsync(responsible, [Roles.CsAgent], h.Ticket.TicketId, Request());

        Assert.Equal(SlaOperationOutcome.Success, result.Outcome);
    }

    [Theory]
    [InlineData(Roles.CsSupervisor)]
    [InlineData(Roles.CsManager)]
    [InlineData(Roles.GeneralManager)]
    [InlineData(Roles.SystemAdministrator)]
    public async Task SupervisoryRoles_CanEscalateAnyTicket_WithoutOwningIt(string role)
    {
        var h = await CreateAsync();

        var result = await h.Service.EscalateAsync(Guid.NewGuid(), [role], h.Ticket.TicketId, Request());

        Assert.Equal(SlaOperationOutcome.Success, result.Outcome);
    }

    [Theory]
    [InlineData(Roles.CsManager)]
    [InlineData(Roles.GeneralManager)]
    public async Task LevelFour_RemainsAvailableToCsManagerAndGeneralManager_OnAnyTicket(string role)
    {
        var h = await CreateAsync();

        var result = await h.Service.EscalateAsync(
            Guid.NewGuid(), [role], h.Ticket.TicketId,
            Request((byte)EscalationLevel.Level4, nameof(EscalationTriggerType.ManualLevel4)));

        Assert.Equal(SlaOperationOutcome.Success, result.Outcome);
    }

    [Theory]
    [InlineData(Roles.DepartmentEmployee)]
    [InlineData(Roles.DepartmentHead)]
    public async Task DepartmentRoles_CanEscalateInTheirOwnDepartment_AndNotInAnother(string role)
    {
        var member = Guid.NewGuid();

        var inside = await CreateAsync();
        inside.Sla.DepartmentAssignments.Assignments.Add(
            new UserDepartmentAssignment(member, TicketDepartmentId, isPrimary: true, CreatedAt, assignedByEmployeeId: null));
        var permitted = await inside.Service.EscalateAsync(member, [role], inside.Ticket.TicketId, Request());

        var outside = await CreateAsync();
        outside.Sla.DepartmentAssignments.Assignments.Add(
            new UserDepartmentAssignment(member, TicketDepartmentId + 1, isPrimary: true, CreatedAt, assignedByEmployeeId: null));
        var refused = await outside.Service.EscalateAsync(member, [role], outside.Ticket.TicketId, Request());

        Assert.Equal(SlaOperationOutcome.Success, permitted.Outcome);
        Assert.Equal(SlaOperationOutcome.Forbidden, refused.Outcome);
        AssertNothingEscalated(outside);
    }

    /// <summary>A person who is a CS Agent AND a Department Employee keeps the department scope that second role grants.</summary>
    [Fact]
    public async Task CsAgentWhoIsAlsoADepartmentEmployee_CanEscalateInThatDepartment()
    {
        var h = await CreateAsync();
        var member = Guid.NewGuid();
        h.Sla.DepartmentAssignments.Assignments.Add(
            new UserDepartmentAssignment(member, TicketDepartmentId, isPrimary: true, CreatedAt, assignedByEmployeeId: null));

        var result = await h.Service.EscalateAsync(member, [Roles.CsAgent, Roles.DepartmentEmployee], h.Ticket.TicketId, Request());

        Assert.Equal(SlaOperationOutcome.Success, result.Outcome);
    }

    [Theory]
    [InlineData(Roles.ChairmanCeo)]
    [InlineData(Roles.ReportingUser)]
    public async Task ReadOnlyRoles_StillCannotEscalate(string role)
    {
        var h = await CreateAsync();

        var result = await h.Service.EscalateAsync(Guid.NewGuid(), [role], h.Ticket.TicketId, Request());

        Assert.Equal(SlaOperationOutcome.Forbidden, result.Outcome);
        AssertNothingEscalated(h);
    }
}
