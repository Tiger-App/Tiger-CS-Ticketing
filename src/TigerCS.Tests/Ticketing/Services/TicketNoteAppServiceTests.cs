using TigerCS.Application.Modules.Ticketing.Dto;
using TigerCS.Application.Modules.Ticketing.Services;
using TigerCS.Domain.Modules.IdentityAndAccess;
using TigerCS.Domain.Modules.SlaAndEscalation;
using TigerCS.Domain.Modules.Ticketing;
using TigerCS.Tests.CustomerVerification.Fakes;
using TigerCS.Tests.IdentityAndAccess.Fakes;
using TigerCS.Tests.Ticketing.Fakes;

namespace TigerCS.Tests.Ticketing.Services;

public class TicketNoteAppServiceTests
{
    private sealed record Fixture(
        TicketNoteAppService Service, FakeTicketRepository Tickets, FakeTicketNoteRepository Notes,
        FakeUserDepartmentAssignmentRepository DepartmentAssignments, FakeAuditEntryWriter Audit, FakeTicketingUnitOfWork UnitOfWork);

    private static Fixture CreateService()
    {
        var tickets = new FakeTicketRepository();
        var notes = new FakeTicketNoteRepository();
        var departmentAssignments = new FakeUserDepartmentAssignmentRepository();
        var audit = new FakeAuditEntryWriter();
        var unitOfWork = new FakeTicketingUnitOfWork();
        var query = new TicketQueryAppService(
            tickets, departmentAssignments, new FakeTicketResolutionRepository(), new FakeTicketStatusHistoryRepository(), ReopenPolicy.Default, TimeProvider.System);

        return new Fixture(
            new TicketNoteAppService(tickets, notes, query, unitOfWork, audit, TimeProvider.System),
            tickets, notes, departmentAssignments, audit, unitOfWork);
    }

    private static async Task<Ticket> SeedTicketAsync(FakeTicketRepository repo, int departmentId = 2)
    {
        var ticket = Ticket.CreateVerified(
            "TG-CS-20260821-0030", departmentId, 10, 20, 5, (byte)PriorityLevel.High, "AC not cooling", DateTime.UtcNow);
        await repo.AddAsync(ticket);
        return ticket;
    }

    [Fact]
    public async Task AddNoteAsync_EmployeeAssignedTheTicket_Succeeds()
    {
        var f = CreateService();
        var ticket = await SeedTicketAsync(f.Tickets);
        var employeeId = Guid.NewGuid();
        f.DepartmentAssignments.Assignments.Add(new UserDepartmentAssignment(employeeId, ticket.CurrentDepartmentId, true, DateTime.UtcNow, null));
        ticket.AssignTo(employeeId);

        var result = await f.Service.AddNoteAsync(
            employeeId, [Roles.DepartmentEmployee], ticket.TicketId, new CreateNoteRequestDto("Contacted the tenant."));

        Assert.Equal(NoteOutcome.Success, result.Outcome);
        Assert.Single(f.Notes.Added);
        Assert.Contains(f.Audit.Written, w => w.Action == "AddNote");
        Assert.Equal(1, f.UnitOfWork.TransactionsCommitted);
    }

    [Fact]
    public async Task AddNoteAsync_AnInternalNote_NeverStopsTheFirstResponseSla()
    {
        // A note is internal: the customer cannot see it, so it is not a response to them.
        var f = CreateService();
        var ticket = await SeedTicketAsync(f.Tickets);
        var employeeId = Guid.NewGuid();
        f.DepartmentAssignments.Assignments.Add(new UserDepartmentAssignment(employeeId, ticket.CurrentDepartmentId, true, DateTime.UtcNow, null));
        ticket.AssignTo(employeeId);

        var result = await f.Service.AddNoteAsync(
            employeeId, [Roles.DepartmentEmployee], ticket.TicketId, new CreateNoteRequestDto("Called the tenant, no answer."));

        Assert.Equal(NoteOutcome.Success, result.Outcome);
        Assert.Null(ticket.FirstHumanResponseAtUtc);
    }

    [Fact]
    public async Task AddNoteAsync_DepartmentColleagueNotAssignedTheTicket_ReturnsForbidden_ForUnassignedAndAColleaguesTicket()
    {
        var f = CreateService();
        var unassigned = await SeedTicketAsync(f.Tickets);
        var colleagues = await SeedTicketAsync(f.Tickets);
        colleagues.AssignTo(Guid.NewGuid());
        var employeeId = Guid.NewGuid();
        f.DepartmentAssignments.Assignments.Add(new UserDepartmentAssignment(employeeId, unassigned.CurrentDepartmentId, true, DateTime.UtcNow, null));

        foreach (var ticket in new[] { unassigned, colleagues })
        {
            var result = await f.Service.AddNoteAsync(
                employeeId, [Roles.DepartmentEmployee], ticket.TicketId, new CreateNoteRequestDto("Should not be allowed."));
            Assert.Equal(NoteOutcome.Forbidden, result.Outcome);
            Assert.Equal(TicketQueryOutcome.Forbidden, (await f.Service.ListNotesAsync(employeeId, [Roles.DepartmentEmployee], ticket.TicketId, 1, 50)).Outcome);
        }

        Assert.Empty(f.Notes.Added);
    }

    [Fact]
    public async Task AddNoteAsync_CallerOutsideDepartmentScope_ReturnsForbidden()
    {
        var f = CreateService();
        var ticket = await SeedTicketAsync(f.Tickets);
        var employeeId = Guid.NewGuid();
        f.DepartmentAssignments.Assignments.Add(new UserDepartmentAssignment(employeeId, 999, true, DateTime.UtcNow, null));

        var result = await f.Service.AddNoteAsync(
            employeeId, [Roles.DepartmentEmployee], ticket.TicketId, new CreateNoteRequestDto("Should not be allowed."));

        Assert.Equal(NoteOutcome.Forbidden, result.Outcome);
        Assert.Empty(f.Notes.Added);
    }

    [Fact]
    public async Task ListNotesAsync_CsLayerRole_SeesNotesAcrossDepartments()
    {
        var f = CreateService();
        var ticket = await SeedTicketAsync(f.Tickets, departmentId: 7);
        await f.Service.AddNoteAsync(Guid.NewGuid(), [Roles.CsManager], ticket.TicketId, new CreateNoteRequestDto("First note."));

        var result = await f.Service.ListNotesAsync(Guid.NewGuid(), [Roles.CsSupervisor], ticket.TicketId, 1, 50);

        Assert.Equal(TicketQueryOutcome.Success, result.Outcome);
        Assert.Single(result.Response!.Items);
    }
}
