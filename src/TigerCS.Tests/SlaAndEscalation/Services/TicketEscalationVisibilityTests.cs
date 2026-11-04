using TigerCS.Application.Modules.SlaAndEscalation.Dto;
using TigerCS.Domain.Modules.IdentityAndAccess;
using TigerCS.Domain.Modules.SlaAndEscalation;
using TigerCS.Domain.Modules.Ticketing;
using TigerCS.Tests.IdentityAndAccess.Fakes;
using TigerCS.Tests.SlaAndEscalation.Fakes;

namespace TigerCS.Tests.SlaAndEscalation.Services;

/// <summary>
/// Escalation is a resource-level action: besides the role gate, the caller must be able to open and modify THIS ticket. These tests use
/// a STRICT department-assignment fake (nobody is "responsible" by default), unlike the shared fixture's permissive one that other tests
/// rely on, so a restricted CS Agent's per-ticket scope is actually exercised.
/// </summary>
public class TicketEscalationVisibilityTests
{
    private const int TicketDepartmentId = 2;
    private static readonly DateTime CreatedAt = new(2026, 8, 23, 5, 0, 0, DateTimeKind.Utc);

    private sealed record Harness(SlaServiceFixture Sla, Ticket Ticket, Guid OwnerId)
    {
        public Task<SlaOperationResult<TicketEscalationResponseDto>> Escalate(Guid caller, string role, byte level = (byte)EscalationLevel.Level2) =>
            Sla.CreateEscalationService().EscalateAsync(
                caller, [role], Ticket.TicketId,
                new ManualEscalationRequestDto(level, level == 4 ? nameof(EscalationTriggerType.ManualLevel4) : nameof(EscalationTriggerType.ManualFlag), "note", []));
    }

    private static async Task<Harness> CreateAsync(bool owned = true, string? ownerRole = Roles.CsAgent)
    {
        var sla = new SlaServiceFixture(departmentAssignments: new FakeUserDepartmentAssignmentRepository());   // strict
        var ticket = Ticket.CreateVerified(
            "TG-CS-20260823-0001", TicketDepartmentId, unitReferenceId: 10, contactReferenceId: 20,
            categoryId: 5, (byte)PriorityLevel.High, "AC not cooling", CreatedAt);
        await sla.Tickets.AddAsync(ticket);
        var ownerId = Guid.NewGuid();
        if (owned)
        {
            ticket.AssignTo(ownerId);
            ticket.ChangeStatus(TicketStatus.InProgress);
            if (ownerRole is not null)
            {
                sla.DepartmentAssignments.RolesByEmployee[ownerId] = [ownerRole];
            }
        }

        return new Harness(sla, ticket, ownerId);
    }

    private static void Join(SlaServiceFixture sla, Guid employee, int department) =>
        sla.DepartmentAssignments.Assignments.Add(new UserDepartmentAssignment(employee, department, isPrimary: true, CreatedAt, assignedByEmployeeId: null));

    private static void AssertUntouched(Harness h)
    {
        Assert.Equal(EscalationLevel.None, h.Ticket.EscalationLevel);
        Assert.Empty(h.Sla.Escalations.All);
    }

    [Fact]
    public async Task CsAgent_OwnerOfTheTicket_MayEscalate()
    {
        var h = await CreateAsync();

        var result = await h.Escalate(h.OwnerId, Roles.CsAgent);

        Assert.Equal(SlaOperationOutcome.Success, result.Outcome);
    }

    [Fact]
    public async Task CsAgent_AssignedToAnotherAgent_IsForbidden_AndNothingChanges()
    {
        var h = await CreateAsync();

        var result = await h.Escalate(Guid.NewGuid(), Roles.CsAgent);

        Assert.Equal(SlaOperationOutcome.Forbidden, result.Outcome);
        AssertUntouched(h);
    }

    [Fact]
    public async Task CsAgent_OnAnUnassignedTicket_IsForbidden()
    {
        var h = await CreateAsync(owned: false);

        Assert.Equal(SlaOperationOutcome.Forbidden, (await h.Escalate(Guid.NewGuid(), Roles.CsAgent)).Outcome);
        AssertUntouched(h);
    }

    [Fact]
    public async Task CsAgent_ResponsibleForADepartmentEmployeesTicket_MayEscalate()
    {
        var h = await CreateAsync(ownerRole: Roles.DepartmentEmployee);
        var agent = Guid.NewGuid();
        h.Sla.DepartmentAssignments.ResponsibleCsAgentByTicket[h.Ticket.TicketId] = agent;

        Assert.Equal(SlaOperationOutcome.Success, (await h.Escalate(agent, Roles.CsAgent)).Outcome);
    }

    [Fact]
    public async Task CsAgent_ResponsibleForADifferentTicket_IsForbidden()
    {
        var h = await CreateAsync(ownerRole: Roles.DepartmentEmployee);
        var agent = Guid.NewGuid();
        h.Sla.DepartmentAssignments.ResponsibleCsAgentByTicket[h.Ticket.TicketId + 1] = agent;

        Assert.Equal(SlaOperationOutcome.Forbidden, (await h.Escalate(agent, Roles.CsAgent)).Outcome);
        AssertUntouched(h);
    }

    [Theory]
    [InlineData(Roles.CsSupervisor)]
    [InlineData(Roles.CsManager)]
    [InlineData(Roles.GeneralManager)]
    public async Task CsLayerAndManagement_StillEscalateAnyTicket_WithoutDepartmentMembership(string role)
    {
        var h = await CreateAsync();

        Assert.Equal(SlaOperationOutcome.Success, (await h.Escalate(Guid.NewGuid(), role)).Outcome);
    }

    [Theory]
    [InlineData(Roles.CsManager)]
    [InlineData(Roles.GeneralManager)]
    public async Task Level4_StillPermittedForManagers(string role)
    {
        var h = await CreateAsync();

        Assert.Equal(SlaOperationOutcome.Success, (await h.Escalate(Guid.NewGuid(), role, level: 4)).Outcome);
    }

    [Fact]
    public async Task DepartmentHead_MayEscalateInTheirDepartment_ButNotInAnother()
    {
        var h = await CreateAsync();
        var inside = Guid.NewGuid();
        var outside = Guid.NewGuid();
        Join(h.Sla, inside, TicketDepartmentId);
        Join(h.Sla, outside, 99);

        Assert.Equal(SlaOperationOutcome.Forbidden, (await h.Escalate(outside, Roles.DepartmentHead)).Outcome);
        AssertUntouched(h);
        Assert.Equal(SlaOperationOutcome.Success, (await h.Escalate(inside, Roles.DepartmentHead)).Outcome);
    }

    [Fact]
    public async Task SystemAdministratorOverride_StillApplies()
    {
        var h = await CreateAsync();

        Assert.Equal(SlaOperationOutcome.Success, (await h.Escalate(Guid.NewGuid(), Roles.SystemAdministrator)).Outcome);
    }

    [Fact]
    public async Task RestrictedAgentWhoAlsoHoldsSupervisor_KeepsTheSupervisorsCrossDepartmentReach()
    {
        var h = await CreateAsync();

        var result = await h.Sla.CreateEscalationService().EscalateAsync(
            Guid.NewGuid(), [Roles.CsAgent, Roles.CsSupervisor], h.Ticket.TicketId,
            new ManualEscalationRequestDto((byte)EscalationLevel.Level2, nameof(EscalationTriggerType.ManualFlag), null, []));

        Assert.Equal(SlaOperationOutcome.Success, result.Outcome);
    }

    [Fact]
    public async Task UnknownTicket_StillNotFound_BeforeAnyAccessDecision()
    {
        var h = await CreateAsync();

        var result = await h.Sla.CreateEscalationService().EscalateAsync(
            Guid.NewGuid(), [Roles.CsAgent], 9999,
            new ManualEscalationRequestDto((byte)EscalationLevel.Level2, nameof(EscalationTriggerType.ManualFlag), null, []));

        Assert.Equal(SlaOperationOutcome.NotFound, result.Outcome);
    }
}
