using TigerCS.Application.Modules.GenesysIntegration.Dto;
using TigerCS.Application.Modules.Ticketing.Dto;
using TigerCS.Application.Modules.Ticketing.Services;
using TigerCS.Domain.Modules.IdentityAndAccess;
using TigerCS.Domain.Modules.SlaAndEscalation;
using TigerCS.Domain.Modules.Ticketing;
using TigerCS.Tests.GenesysIntegration.Fakes;

namespace TigerCS.Tests.GenesysIntegration.Services;

/// <summary>
/// The Awaiting Human Agent exception to CS Agent ticket visibility: a CS Agent still sees — and can claim — <b>waiting, unclaimed</b>
/// work in the departments they belong to, and nothing else. It exposes the work item only: the ticket behind it stays closed to them
/// until the claim assigns it to them. Authorization and exclusivity are enforced by the same server-side claim as before.
/// </summary>
public class AwaitingHumanAgentClaimTests
{
    private static readonly Guid ServiceAccount = Guid.NewGuid();
    private static readonly string[] Agent = [Roles.CsAgent];

    private static void Member(GenesysServiceFixture f, Guid employee, int departmentId) =>
        f.DepartmentAssignments.Assignments.Add(new UserDepartmentAssignment(employee, departmentId, true, DateTime.UtcNow, null));

    /// <summary>A website chat in <paramref name="departmentId"/> that ended with nobody having taken it.</summary>
    private static async Task<(long TicketId, long HandoffId)> WaitingAsync(GenesysServiceFixture f, int departmentId, string conversation)
    {
        var created = await f.Ingestion.IngestAsync(
            ServiceAccount,
            new GenesysInquiryDto(conversation, GenesysChannel.WebsiteChat, CustomerPhone: "+971500000001", CustomerName: "Ahmed Ali", DepartmentId: departmentId));
        await f.TicketUpdate.UpdateAsync(
            ServiceAccount, created.Ticket!.TicketId,
            new GenesysTicketUpdateDto(conversation, Handoff: new GenesysHandoffUpdateDto(Required: true, Trigger: "AiEscalated", Reason: "Needs a person")));
        var handoff = f.Handoffs.All.Single(h => h.TicketId == created.Ticket.TicketId);
        return (created.Ticket.TicketId, handoff.TicketAgentHandoffId);
    }

    private static async Task<long[]> ListedAsync(GenesysServiceFixture f, Guid caller, params string[] roles) =>
        [.. (await f.StrictPendingWork.ListAsync(caller, roles, new AgentHandoffListRequestDto())).Items.Select(i => i.TicketAgentHandoffId).Order()];

    [Fact]
    public async Task AnAgent_SeesWaitingWorkOfTheirOwnDepartment_AndOnlyThat()
    {
        var f = new GenesysServiceFixture();
        var (cs, _) = f.SeedGenesysDepartment("Customer Service", "CS");
        var (fm, _) = f.SeedGenesysDepartment("Facilities", "FM");
        var agent = Guid.NewGuid();
        Member(f, agent, cs.DepartmentId);
        var (_, inMine) = await WaitingAsync(f, cs.DepartmentId, "conv-cs");
        var (_, elsewhere) = await WaitingAsync(f, fm.DepartmentId, "conv-fm");

        Assert.Equal([inMine], await ListedAsync(f, agent, Roles.CsAgent));
        Assert.Contains(elsewhere, await ListedAsync(f, Guid.NewGuid(), Roles.CsSupervisor)); // a supervisor still sees all
    }

    [Fact]
    public async Task AnAgentWithNoDepartment_SeesNoWaitingWork()
    {
        var f = new GenesysServiceFixture();
        var (cs, _) = f.SeedGenesysDepartment("Customer Service", "CS");
        await WaitingAsync(f, cs.DepartmentId, "conv-cs");

        Assert.Empty(await ListedAsync(f, Guid.NewGuid(), Roles.CsAgent));
    }

    [Fact]
    public async Task TheExceptionExposesTheWorkItemOnly_NotTheTicketBehindIt()
    {
        var f = new GenesysServiceFixture();
        var (cs, _) = f.SeedGenesysDepartment("Customer Service", "CS");
        var agent = Guid.NewGuid();
        Member(f, agent, cs.DepartmentId);
        var (ticketId, handoffId) = await WaitingAsync(f, cs.DepartmentId, "conv-cs");

        Assert.Equal([handoffId], await ListedAsync(f, agent, Roles.CsAgent));
        Assert.Equal(TicketQueryOutcome.Forbidden, (await f.StrictTicketQuery.GetDetailAsync(agent, Agent, ticketId)).Outcome);
        Assert.Equal(TicketQueryOutcome.Forbidden, (await f.StrictTicketQuery.GetLifecycleHistoryAsync(agent, Agent, ticketId)).Outcome);
        Assert.Empty((await f.StrictTicketQuery.GetQueueAsync(
            agent, Agent, new TicketListRequestDto(null, null, null, null, null, null, null, null, null, 1, 50))).Items);
    }

    [Fact]
    public async Task ClaimingAssignsTheTicketToTheAgent_StartsTheWork_AndThenTheTicketIsTheirs()
    {
        var f = new GenesysServiceFixture();
        var (cs, _) = f.SeedGenesysDepartment("Customer Service", "CS");
        var agent = Guid.NewGuid();
        Member(f, agent, cs.DepartmentId);
        var (ticketId, handoffId) = await WaitingAsync(f, cs.DepartmentId, "conv-cs");

        var claimed = await f.StrictPendingWork.AcceptAsync(agent, Agent, handoffId);

        Assert.Equal(AgentHandoffOutcome.Success, claimed.Outcome);
        Assert.Equal("InProgress", claimed.Handoff!.Status);
        Assert.Equal(agent, claimed.Handoff.AssignedEmployeeId);
        var ticket = f.Tickets.All.Single(t => t.TicketId == ticketId);
        Assert.Equal(agent, ticket.CurrentOwnerEmployeeId);
        Assert.Equal(TicketStatus.InProgress, ticket.TicketStatus);
        Assert.Equal(TicketQueryOutcome.Success, (await f.StrictTicketQuery.GetDetailAsync(agent, Agent, ticketId)).Outcome);
        // The claimer keeps the (now their own) item in the list; it is no longer offered to anyone else in the department.
        Assert.Equal([handoffId], await ListedAsync(f, agent, Roles.CsAgent));
        var colleague = Guid.NewGuid();
        Member(f, colleague, cs.DepartmentId);
        Assert.Empty(await ListedAsync(f, colleague, Roles.CsAgent));
    }

    [Fact]
    public async Task AnAgentCannotClaimWork_InADepartmentTheyDoNotBelongTo()
    {
        var f = new GenesysServiceFixture();
        var (cs, _) = f.SeedGenesysDepartment("Customer Service", "CS");
        var (fm, _) = f.SeedGenesysDepartment("Facilities", "FM");
        var outsider = Guid.NewGuid();
        Member(f, outsider, fm.DepartmentId);
        var (ticketId, handoffId) = await WaitingAsync(f, cs.DepartmentId, "conv-cs");

        var result = await f.StrictPendingWork.AcceptAsync(outsider, Agent, handoffId);

        Assert.Equal(AgentHandoffOutcome.Forbidden, result.Outcome);
        var handoff = Assert.Single(f.Handoffs.All);
        Assert.Equal(AgentHandoffStatus.WaitingForAgent, handoff.Status);
        Assert.Null(handoff.AssignedEmployeeId);
        Assert.Null(f.Tickets.All.Single(t => t.TicketId == ticketId).CurrentOwnerEmployeeId);
    }

    [Fact]
    public async Task ASecondAgent_IsToldWhoHoldsTheWork_AndTheFirstClaimIsUnchanged()
    {
        var f = new GenesysServiceFixture();
        var (cs, _) = f.SeedGenesysDepartment("Customer Service", "CS");
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        Member(f, first, cs.DepartmentId);
        Member(f, second, cs.DepartmentId);
        var (ticketId, handoffId) = await WaitingAsync(f, cs.DepartmentId, "conv-cs");
        Assert.Equal(AgentHandoffOutcome.Success, (await f.StrictPendingWork.AcceptAsync(first, Agent, handoffId)).Outcome);

        var loser = await f.StrictPendingWork.AcceptAsync(second, Agent, handoffId);

        Assert.Equal(AgentHandoffOutcome.AlreadyClaimed, loser.Outcome);
        Assert.Equal(first, loser.HolderEmployeeId);
        Assert.Equal(first, f.Tickets.All.Single(t => t.TicketId == ticketId).CurrentOwnerEmployeeId);
        Assert.Equal(first, Assert.Single(f.Handoffs.All).AssignedEmployeeId);
    }

    [Fact]
    public async Task TheExceptionIsOnlyForClaiming_AnAgentCannotCompleteOrCancelWorkTheyHaveNotClaimed()
    {
        var f = new GenesysServiceFixture();
        var (cs, _) = f.SeedGenesysDepartment("Customer Service", "CS");
        var agent = Guid.NewGuid();
        Member(f, agent, cs.DepartmentId);
        var (_, handoffId) = await WaitingAsync(f, cs.DepartmentId, "conv-cs");

        var cancel = await f.StrictPendingWork.CancelAsync(agent, Agent, handoffId, new CancelAgentHandoffRequestDto("not needed"));
        var complete = await f.StrictPendingWork.CompleteAsync(agent, Agent, handoffId, new CompleteAgentHandoffRequestDto("done"));

        Assert.Equal(AgentHandoffOutcome.Forbidden, cancel.Outcome);
        Assert.Equal(AgentHandoffOutcome.Forbidden, complete.Outcome);
        Assert.Equal(AgentHandoffStatus.WaitingForAgent, Assert.Single(f.Handoffs.All).Status);
    }

    [Fact]
    public async Task ReadOnlyRoles_StillCannotClaim_EvenInTheirDepartment()
    {
        var f = new GenesysServiceFixture();
        var (cs, _) = f.SeedGenesysDepartment("Customer Service", "CS");
        var viewer = Guid.NewGuid();
        Member(f, viewer, cs.DepartmentId);
        var (_, handoffId) = await WaitingAsync(f, cs.DepartmentId, "conv-cs");

        Assert.Equal(AgentHandoffOutcome.Forbidden, (await f.StrictPendingWork.AcceptAsync(viewer, [Roles.ReportingUser], handoffId)).Outcome);
    }

    [Fact]
    public async Task AnAgentWithWaitingWorkInTwoDepartments_SeesBoth_AndAsAnotherDepartmentsMemberStillCannotOpenEitherTicket()
    {
        var f = new GenesysServiceFixture();
        var (cs, _) = f.SeedGenesysDepartment("Customer Service", "CS");
        var (fm, _) = f.SeedGenesysDepartment("Facilities", "FM");
        var agent = Guid.NewGuid();
        Member(f, agent, cs.DepartmentId);
        Member(f, agent, fm.DepartmentId);
        var (t1, h1) = await WaitingAsync(f, cs.DepartmentId, "conv-1");
        var (t2, h2) = await WaitingAsync(f, fm.DepartmentId, "conv-2");

        Assert.Equal([h1, h2], await ListedAsync(f, agent, Roles.CsAgent));
        Assert.Equal(TicketQueryOutcome.Forbidden, (await f.StrictTicketQuery.GetDetailAsync(agent, Agent, t1)).Outcome);
        Assert.Equal(TicketQueryOutcome.Forbidden, (await f.StrictTicketQuery.GetDetailAsync(agent, Agent, t2)).Outcome);
    }

    [Fact]
    public async Task ACreatorOfATicketInAnotherDepartment_CannotClaimCompleteOrCancelItsWork_AndDoesNotSeeItListed()
    {
        var f = new GenesysServiceFixture();
        var (cs, _) = f.SeedGenesysDepartment("Customer Service", "CS");
        var (fm, category) = f.SeedGenesysDepartment("Facilities", "FM");
        var creator = Guid.NewGuid();
        Member(f, creator, cs.DepartmentId);   // belongs to Customer Service; the ticket lives in Facilities
        var intake = new IntakeRecord(WellKnownChannels.Phone, "+971500000077", fm.DepartmentId, false, null, null, creator, DateTime.UtcNow);
        await f.IntakeRecords.AddAsync(intake);
        var created = await f.ManualTicketCreation.CreateAsync(
            creator, new CreateTicketRequestDto(intake.IntakeRecordId, null, null, category.CategoryId, (byte)PriorityLevel.High, "Lift out of service"),
            callerRoles: Agent);
        var ticketId = created.Response!.TicketId;
        var interaction = Assert.Single(f.Interactions.All, i => i.TicketId == ticketId);
        var handoff = new TicketAgentHandoff(
            ticketId, interaction.TicketInteractionId, fm.DepartmentId, WellKnownChannels.Phone, DateTime.UtcNow, DateTime.UtcNow);
        await f.Handoffs.AddAsync(handoff);

        Assert.Equal(creator, f.Tickets.All.Single(t => t.TicketId == ticketId).CreatedByEmployeeId);
        // Creating a ticket grants a CS Agent nothing (it used to leave them read-only access): not the ticket, and not its work.
        Assert.Equal(TicketQueryOutcome.Forbidden, (await f.StrictTicketQuery.GetDetailAsync(creator, Agent, ticketId)).Outcome);
        Assert.Empty(await ListedAsync(f, creator, Roles.CsAgent));
        Assert.Equal(AgentHandoffOutcome.Forbidden, (await f.StrictPendingWork.AcceptAsync(creator, Agent, handoff.TicketAgentHandoffId)).Outcome);
        Assert.Equal(AgentHandoffOutcome.Forbidden, (await f.StrictPendingWork.CompleteAsync(
            creator, Agent, handoff.TicketAgentHandoffId, new CompleteAgentHandoffRequestDto("done"))).Outcome);
        Assert.Equal(AgentHandoffOutcome.Forbidden, (await f.StrictPendingWork.CancelAsync(
            creator, Agent, handoff.TicketAgentHandoffId, new CancelAgentHandoffRequestDto("not needed"))).Outcome);
        Assert.Equal(AgentHandoffStatus.WaitingForAgent, handoff.Status);
        Assert.Null(handoff.AssignedEmployeeId);
    }
}
