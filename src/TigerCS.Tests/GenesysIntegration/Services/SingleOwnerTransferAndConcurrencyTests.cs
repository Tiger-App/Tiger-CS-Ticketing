using TigerCS.Application.Modules.GenesysIntegration.Dto;
using TigerCS.Application.Modules.Ticketing.Dto;
using TigerCS.Application.Modules.Ticketing.Services;
using TigerCS.Domain.Modules.IdentityAndAccess;
using TigerCS.Domain.Modules.Ticketing;
using TigerCS.Tests.GenesysIntegration.Fakes;
using TigerCS.Tests.Ticketing.Fakes;

namespace TigerCS.Tests.GenesysIntegration.Services;

/// <summary>Single current owner: acceptance, transfer (supersede, queue fallback, previous owner loses every right) and racing claims.</summary>
public class SingleOwnerTransferAndConcurrencyTests
{
    private static readonly Guid ServiceAccount = Guid.NewGuid();
    private static readonly IReadOnlyCollection<string> CsAgent = [Roles.CsAgent];

    private sealed record Setup(GenesysServiceFixture F, TicketAssignmentAppService Assign, long TicketId, int FromDepartmentId, int ToDepartmentId);

    private static async Task<Setup> NewAsync()
    {
        var f = new GenesysServiceFixture();
        var (from, _) = f.SeedGenesysDepartment("Call Center", "CC");
        var (to, _) = f.SeedGenesysDepartment("Collections", "COLL");
        var created = await f.Ingestion.IngestAsync(
            ServiceAccount,
            new GenesysInquiryDto("conv-own", GenesysChannel.WebsiteChat, CustomerPhone: "+971500000009", CustomerName: "Sara", DepartmentId: from.DepartmentId));
        var assign = new TicketAssignmentAppService(
            f.Tickets, f.TicketAssignments, f.DepartmentAssignments, f.Departments, f.UnitOfWork, f.Audit, TimeProvider.System,
            f.DepartmentSettings,
            new TicketAutoAssignmentService(f.AssignmentRules, f.DepartmentSettings, f.DepartmentAssignments, f.TicketAssignments, f.Audit),
            f.WorkflowEvents, handlerRepository: FakeTicketInteractionHandlerRepository.Permissive());
        return new Setup(f, assign, created.Ticket!.TicketId, from.DepartmentId, to.DepartmentId);
    }

    private static async Task<Guid> OwnAsync(Setup s, Guid? agent = null)
    {
        var owner = agent ?? Guid.NewGuid();
        s.F.DepartmentAssignments.Assignments.Add(new UserDepartmentAssignment(owner, s.FromDepartmentId, true, DateTime.UtcNow, null));
        var ticket = (await s.F.Tickets.GetByIdAsync(s.TicketId))!;
        ticket.AssignTo(owner);
        await s.F.TicketAssignments.AddAsync(new TicketAssignment(s.TicketId, owner, s.FromDepartmentId, DateTime.UtcNow, owner));
        return owner;
    }

    [Fact]
    public async Task TransferWithNoEligibleEmployee_QueuesTheTicketUnassigned_AndEndsThePreviousAssignmentKeepingHistory()
    {
        var s = await NewAsync();
        var previous = await OwnAsync(s);
        var manager = Guid.NewGuid();

        var result = await s.Assign.TransferAsync(manager, [Roles.CsManager], s.TicketId, new TransferTicketRequestDto(s.ToDepartmentId, "Collections issue", []));

        Assert.Equal(TicketMutationOutcome.Success, result.Outcome);
        var ticket = (await s.F.Tickets.GetByIdAsync(s.TicketId))!;
        Assert.Equal(s.ToDepartmentId, ticket.CurrentDepartmentId);
        Assert.Null(ticket.CurrentOwnerEmployeeId);                                           // department queue, unassigned
        Assert.DoesNotContain(s.F.TicketAssignments.Added, a => a.IsCurrent);                  // exactly zero current owners
        Assert.Contains(s.F.TicketAssignments.Added, a => a.AssignedEmployeeId == previous);   // history kept, only superseded
        Assert.Contains(s.F.Audit.Entries, e => e.Action == "Transfer");
    }

    [Fact]
    public async Task AfterATransfer_ThePreviousOwnerCanNoLongerResolveCloseOrTransfer()
    {
        var s = await NewAsync();
        var previous = await OwnAsync(s);
        await s.Assign.TransferAsync(Guid.NewGuid(), [Roles.CsManager], s.TicketId, new TransferTicketRequestDto(s.ToDepartmentId, "Collections issue", []));
        var ticket = (await s.F.Tickets.GetByIdAsync(s.TicketId))!;

        var resolve = await s.F.Lifecycle.ResolveAsync(
            previous, CsAgent, s.TicketId, new ResolveTicketRequestDto("Resolved", "x", null, null, ticket.RowVersion));
        var transfer = await s.Assign.TransferAsync(
            previous, CsAgent, s.TicketId, new TransferTicketRequestDto(s.FromDepartmentId, "take it back", []));

        // Resolve it legitimately (as a manager) so Close is the only thing left to refuse.
        ticket.Resolve(ResolutionOutcome.Resolved, duplicateOfTicketId: null);
        await s.F.Resolutions.AddAsync(new TicketResolution(s.TicketId, ResolutionOutcome.Resolved, "done", null, null, Guid.NewGuid(), DateTime.UtcNow));
        var close = await s.F.Lifecycle.CloseAsync(previous, CsAgent, s.TicketId, new CloseTicketRequestDto(ticket.RowVersion));

        Assert.Equal(TicketMutationOutcome.Forbidden, resolve.Outcome);
        Assert.Equal(TicketMutationOutcome.Forbidden, transfer.Outcome);
        Assert.Equal(TicketMutationOutcome.Forbidden, close.Outcome);
        Assert.Equal(s.ToDepartmentId, ticket.CurrentDepartmentId);
    }

    [Fact]
    public async Task OnlyTheCurrentOwnerCanResolve_NotAColleagueInTheSameDepartment()
    {
        var s = await NewAsync();
        var owner = await OwnAsync(s);
        var colleague = Guid.NewGuid();
        s.F.DepartmentAssignments.Assignments.Add(new UserDepartmentAssignment(colleague, s.FromDepartmentId, true, DateTime.UtcNow, null));
        var ticket = (await s.F.Tickets.GetByIdAsync(s.TicketId))!;

        var byColleague = await s.F.Lifecycle.ResolveAsync(
            colleague, [Roles.DepartmentEmployee], s.TicketId, new ResolveTicketRequestDto("Resolved", "x", null, null, ticket.RowVersion));
        var byOwner = await s.F.Lifecycle.ResolveAsync(
            owner, [Roles.DepartmentEmployee], s.TicketId, new ResolveTicketRequestDto("Resolved", "done", null, null, ticket.RowVersion));

        Assert.Equal(TicketMutationOutcome.Forbidden, byColleague.Outcome);
        Assert.Equal(TicketMutationOutcome.Success, byOwner.Outcome);
    }

    // ----------------------------------------------------------------------------------------------

    private static async Task<(Setup S, long HandoffId, Guid A, Guid B)> PendingForTwoAgentsAsync()
    {
        var s = await NewAsync();
        var handoff = new TigerCS.Application.Modules.GenesysIntegration.Services.GenesysAgentHandoffAppService(
            s.F.Options, s.F.Conversations, s.F.Handoffs, s.F.WorkflowEvents, s.F.Tickets, s.F.UnitOfWork, s.F.Audit, TimeProvider.System,
            s.F.Departments, s.F.TicketAssignments);
        await handoff.RequestAsync(ServiceAccount, new GenesysHandoffRequestDto("conv-own", Reason: "wants a person", Trigger: "CustomerRequestedHuman", TicketId: s.TicketId));
        var (a, b) = (Guid.NewGuid(), Guid.NewGuid());
        var ccId = (await s.F.Tickets.GetByIdAsync(s.TicketId))!.CurrentDepartmentId;
        foreach (var id in new[] { a, b })
        {
            s.F.DepartmentAssignments.Assignments.Add(new UserDepartmentAssignment(id, ccId, true, DateTime.UtcNow, null));
        }

        return (s, Assert.Single(s.F.Handoffs.All).TicketAgentHandoffId, a, b);
    }

    [Fact]
    public async Task TwoAgentsAcceptingTheSameInteractionTogether_OnlyOneBecomesTheOwner()
    {
        var (s, handoffId, a, b) = await PendingForTwoAgentsAsync();

        var results = await Task.WhenAll(
            s.F.PendingWork.AcceptAsync(a, CsAgent, handoffId),
            s.F.PendingWork.AcceptAsync(b, CsAgent, handoffId));

        Assert.Single(results, r => r.Outcome == AgentHandoffOutcome.Success);
        Assert.Single(results, r => r.Outcome == AgentHandoffOutcome.AlreadyClaimed);
        var winner = results[0].Outcome == AgentHandoffOutcome.Success ? a : b;
        var ticket = (await s.F.Tickets.GetByIdAsync(s.TicketId))!;
        Assert.Equal(winner, ticket.CurrentOwnerEmployeeId);
        Assert.Single(s.F.TicketAssignments.Added, x => x.IsCurrent);                           // one current owner, always
    }

    [Fact]
    public async Task AcceptingNeverTakesATicketAnotherPersonOwns()
    {
        var (s, handoffId, a, _) = await PendingForTwoAgentsAsync();
        var ticket = (await s.F.Tickets.GetByIdAsync(s.TicketId))!;
        var humanOwner = Guid.NewGuid();
        ticket.AssignTo(humanOwner);
        await s.F.TicketAssignments.AddAsync(new TicketAssignment(s.TicketId, humanOwner, ticket.CurrentDepartmentId, DateTime.UtcNow, Guid.NewGuid()));

        var refused = await s.F.PendingWork.AcceptAsync(a, CsAgent, handoffId);

        Assert.Equal(AgentHandoffOutcome.TicketOwnedByAnotherEmployee, refused.Outcome);
        Assert.Equal(humanOwner, ticket.CurrentOwnerEmployeeId);
        Assert.Equal(AgentHandoffStatus.WaitingForAgent, Assert.Single(s.F.Handoffs.All).Status);   // nothing claimed
    }
}
