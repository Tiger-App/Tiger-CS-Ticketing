using TigerCS.Application.Modules.GenesysIntegration.Dto;
using TigerCS.Application.Modules.Ticketing.Dto;
using TigerCS.Domain.Modules.IdentityAndAccess;
using TigerCS.Tests.GenesysIntegration.Fakes;

namespace TigerCS.Tests.GenesysIntegration.Services;

/// <summary>
/// Who sees and who may act on the Pending Interactions work list, per the
/// agreed role model: CS Manager / CS Supervisor see work in every department
/// (Call Center included); acting (accept = own the ticket) is the
/// <c>AgentHandoffAppService</c> rule - the caller must be a member of the
/// ticket's department, and the cross-department route is Department Transfer.
/// A Call Center agent is a CS Agent who is a member of the CC department, so
/// they accept CC work exactly as any CS Agent accepts their own department's.
/// Chairman/CEO is read-only: the list, never the actions.
/// </summary>
public class PendingInteractionRoleScopeTests
{
    private static readonly Guid ServiceAccount = Guid.NewGuid();

    private static async Task<(GenesysServiceFixture F, long HandoffId, int DepartmentId)> WaitingInAsync(
        string departmentName, string departmentCode)
    {
        var f = new GenesysServiceFixture();
        var (department, _) = f.SeedGenesysDepartment(departmentName, departmentCode);
        var created = await f.Ingestion.IngestAsync(
            ServiceAccount,
            new GenesysInquiryDto(
                "conv-scope", GenesysChannel.WebsiteChat,
                CustomerPhone: "+971500000001", CustomerName: "Ahmed Ali", DepartmentId: department.DepartmentId));
        await f.TicketUpdate.UpdateAsync(
            ServiceAccount, created.Ticket!.TicketId,
            new GenesysTicketUpdateDto(
                "conv-scope",
                Handoff: new GenesysHandoffUpdateDto(Required: true, Trigger: "AiEscalated", Reason: "The bot could not finish."),
                Ended: new GenesysConversationEndUpdateDto(
                    DateTime.UtcNow.AddMinutes(-5), "CustomerDisconnect",
                    [new GenesysTranscriptMessageDto("VirtualAgent", DateTime.UtcNow.AddMinutes(-6), "Hello?", SenderName: "Tiger Bot", SenderId: "bot-1")])));
        return (f, Assert.Single(f.Handoffs.All).TicketAgentHandoffId, department.DepartmentId);
    }

    private static void Member(GenesysServiceFixture f, Guid employee, int departmentId) =>
        f.DepartmentAssignments.Assignments.Add(new UserDepartmentAssignment(employee, departmentId, true, DateTime.UtcNow, null));

    [Theory]
    [InlineData(Roles.CsManager)]
    [InlineData(Roles.CsSupervisor)]
    [InlineData(Roles.GeneralManager)]
    [InlineData(Roles.ChairmanCeo)]
    public async Task CrossDepartmentRoles_SeeCallCenterWork_WithoutBelongingToTheCallCenter(string role)
    {
        var (f, _, _) = await WaitingInAsync("Call Center", "CC");

        var list = await f.PendingWork.ListAsync(Guid.NewGuid(), [role], new AgentHandoffListRequestDto());

        Assert.Single(list.Items);
    }

    [Fact]
    public async Task CallCenterAgent_AcceptsCallCenterWork_ACsAgentOutsideTheCallCenterDoesNot()
    {
        var (f, handoffId, departmentId) = await WaitingInAsync("Call Center", "CC");
        var callCenterAgent = Guid.NewGuid();
        Member(f, callCenterAgent, departmentId);
        var customerServiceAgent = Guid.NewGuid();
        Member(f, customerServiceAgent, departmentId + 100);

        // The outsider does not own the ticket and is not a member of its department: CS Agent alone gives no cross-department right to accept.
        var ticket = Assert.Single(f.Tickets.All);
        Assert.Equal(
            AgentHandoffOutcome.Forbidden,
            (await f.PendingWork.AcceptAsync(customerServiceAgent, [Roles.CsAgent], handoffId)).Outcome);

        Assert.Equal(
            AgentHandoffOutcome.Success,
            (await f.PendingWork.AcceptAsync(callCenterAgent, [Roles.CsAgent], handoffId)).Outcome);
    }

    [Fact]
    public async Task TheCsAgentWhoAlreadyOwnsTheTicket_AcceptsItsWork_EvenAfterTheTicketMovedToADepartmentTheyAreNotIn()
    {
        var (f, handoffId, departmentId) = await WaitingInAsync("Customer Service", "CS");
        var responsibleAgent = Guid.NewGuid();
        Member(f, responsibleAgent, departmentId + 100);   // not a member of the ticket's (receiving) department
        var ticket = Assert.Single(f.Tickets.All);
        ticket.AssignTo(responsibleAgent);                 // a department change keeps the responsible CS Agent on the ticket

        var accepted = await f.PendingWork.AcceptAsync(responsibleAgent, [Roles.CsAgent], handoffId);

        Assert.Equal(AgentHandoffOutcome.Success, accepted.Outcome);   // no Take Ownership step, no membership requirement
        Assert.Equal(responsibleAgent, ticket.CurrentOwnerEmployeeId);
    }

    [Fact]
    public async Task CsSupervisor_WhoIsInTheDepartment_Accepts_ACsManagerOutsideItMustTransferFirst()
    {
        var (f, handoffId, departmentId) = await WaitingInAsync("Customer Service", "CS");
        var supervisor = Guid.NewGuid();
        Member(f, supervisor, departmentId);
        var manager = Guid.NewGuid();

        Assert.Equal(
            AgentHandoffOutcome.AgentNotInTicketDepartment,
            (await f.PendingWork.AcceptAsync(manager, [Roles.CsManager], handoffId)).Outcome);
        Assert.Equal(
            AgentHandoffOutcome.Success,
            (await f.PendingWork.AcceptAsync(supervisor, [Roles.CsSupervisor], handoffId)).Outcome);
    }

    [Theory]
    [InlineData(Roles.ChairmanCeo)]
    [InlineData(Roles.ReportingUser)]
    public async Task ReadOnlyRoles_CannotAcceptCompleteOrCancel_EvenAsMembersOfTheDepartment(string role)
    {
        var (f, handoffId, departmentId) = await WaitingInAsync("Customer Service", "CS");
        var caller = Guid.NewGuid();
        Member(f, caller, departmentId);

        Assert.Equal(AgentHandoffOutcome.Forbidden, (await f.PendingWork.AcceptAsync(caller, [role], handoffId)).Outcome);
        Assert.Equal(
            AgentHandoffOutcome.Forbidden,
            (await f.PendingWork.CompleteAsync(caller, [role], handoffId, new CompleteAgentHandoffRequestDto("done"))).Outcome);
        Assert.Equal(
            AgentHandoffOutcome.Forbidden,
            (await f.PendingWork.CancelAsync(caller, [role], handoffId, new CancelAgentHandoffRequestDto("no"))).Outcome);

        var handoff = Assert.Single(f.Handoffs.All);
        Assert.Null(handoff.AssignedEmployeeId);
        Assert.True(handoff.IsOpen);
    }
}
