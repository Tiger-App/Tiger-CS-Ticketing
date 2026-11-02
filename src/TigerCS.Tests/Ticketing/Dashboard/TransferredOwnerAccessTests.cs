using TigerCS.Application.Modules.Ticketing.Services;
using TigerCS.Domain.Modules.IdentityAndAccess;
using TigerCS.Domain.Modules.Ticketing;
using TigerCS.Infrastructure.Modules.Ticketing.Repositories;
using TigerCS.Tests.IdentityAndAccess.Fakes;

namespace TigerCS.Tests.Ticketing.Dashboard;

/// <summary>
/// After a Transfer the previous owner and the previous Genesys handler lose ordinary modify access (Resolve/Close/Transfer rest on it), while
/// Supervisor/Manager administrative access is unchanged. Runs the real handler repository over SQLite, so the query behind the rule is
/// exercised, not a double.
/// </summary>
public sealed class TransferredOwnerAccessTests : IDisposable
{
    private readonly DashboardSqliteFixture _db = new();
    private static readonly string[] Agent = [Roles.CsAgent];

    public void Dispose() => _db.Dispose();

    private static void AddTransfer(TigerCS.Infrastructure.Persistence.TigerCsDbContext context, long ticketId, DateTime at, TicketTransferReason reason)
    {
        context.TicketWorkflowEvents.Add(TicketWorkflowEvent.ForTransfer(
            ticketId, at, actorEmployeeId: null, reason, fromDepartmentId: 1, toDepartmentId: 2, toEmployeeId: null, "t", Guid.NewGuid()));
        context.SaveChanges();
    }

    private static Task<TicketAccessLevel> AccessAsync(
        TigerCS.Infrastructure.Persistence.TigerCsDbContext context, Guid caller, string[] roles, Ticket ticket) =>
        TicketVisibilityRule.GetTicketAccessAsync(
            new FakeUserDepartmentAssignmentRepository(), new TicketInteractionHandlerRepository(context), caller, roles, ticket);

    [Fact]
    public async Task PreviousGenesysHandler_BeforeAManualTransfer_KeepsReadOnly_ButCannotModify()
    {
        using var context = _db.CreateContext();
        var ticket = _db.AddTicket(context, _db.CustomerServiceId);
        var handler = _db.CsAgentId;
        _db.AddHandler(context, ticket, handler);                                   // handled 30 minutes before "now"

        Assert.Equal(TicketAccessLevel.Full, await AccessAsync(context, handler, Agent, ticket));

        AddTransfer(context, ticket.TicketId, DashboardSqliteFixture.Now.AddMinutes(-10), TicketTransferReason.WrongDepartment);

        Assert.Equal(TicketAccessLevel.ReadOnly, await AccessAsync(context, handler, Agent, ticket));
        Assert.False(await TicketVisibilityRule.CanModifyTicketAsync(
            new FakeUserDepartmentAssignmentRepository(), new TicketInteractionHandlerRepository(context), handler, Agent, ticket));
    }

    [Fact]
    public async Task SystemRoutingToCallCenter_DoesNotRevokeTheHandler()
    {
        using var context = _db.CreateContext();
        var ticket = _db.AddTicket(context, _db.CustomerServiceId);
        var handler = _db.CsAgentId;
        _db.AddHandler(context, ticket, handler);
        AddTransfer(context, ticket.TicketId, DashboardSqliteFixture.Now.AddMinutes(-10), TicketTransferReason.SystemRouting);

        Assert.Equal(TicketAccessLevel.Full, await AccessAsync(context, handler, Agent, ticket));
    }

    [Fact]
    public async Task HandlingRecordedAfterTheTransfer_GrantsAccessAgain()
    {
        using var context = _db.CreateContext();
        var ticket = _db.AddTicket(context, _db.CustomerServiceId);
        var first = _db.CsAgentId;
        var second = _db.RegistrationEmployeeId;
        var firstHandling = _db.AddHandler(context, ticket, first);                  // recorded 30 minutes before now
        AddTransfer(context, ticket.TicketId, DashboardSqliteFixture.Now.AddMinutes(-20), TicketTransferReason.WrongDepartment);
        context.TicketInteractionHandlers.Add(TicketInteractionHandler.Create(      // a later interaction on the same ticket, after the transfer
            firstHandling.TicketInteractionId, ticket.TicketId, second, "genesys-second",
            InteractionHandlerSource.Routing, connectedAtUtc: null, DashboardSqliteFixture.Now.AddMinutes(-5)));
        context.SaveChanges();

        Assert.Equal(TicketAccessLevel.ReadOnly, await AccessAsync(context, first, Agent, ticket));
        Assert.Equal(TicketAccessLevel.Full, await AccessAsync(context, second, Agent, ticket));
    }

    [Fact]
    public async Task PreviousOwner_AfterTransfer_HasNoAccess_WhileCurrentOwnerSupervisorAndManagerKeepTheirs()
    {
        using var context = _db.CreateContext();
        var previousOwner = _db.CsAgentId;
        var newOwner = _db.RegistrationEmployeeId;
        var ticket = _db.AddTicket(context, _db.CustomerServiceId, owner: previousOwner);
        AddTransfer(context, ticket.TicketId, DashboardSqliteFixture.Now, TicketTransferReason.WrongDepartment);
        ticket.TransferToDepartment(_db.CollectionsId);
        ticket.AssignTo(newOwner);

        Assert.Equal(TicketAccessLevel.None, await AccessAsync(context, previousOwner, Agent, ticket));
        Assert.Equal(TicketAccessLevel.Full, await AccessAsync(context, newOwner, Agent, ticket));
        Assert.Equal(TicketAccessLevel.Full, await AccessAsync(context, Guid.NewGuid(), [Roles.CsSupervisor], ticket));
        Assert.Equal(TicketAccessLevel.Full, await AccessAsync(context, Guid.NewGuid(), [Roles.CsManager], ticket));
    }
}
