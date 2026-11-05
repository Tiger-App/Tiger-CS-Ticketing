// TigerCS.Web is referenced under an alias — see TigerCS.Tests.csproj.
extern alias TigerCsWeb;

using TigerCS.Application.Modules.Ticketing.Dto;
using TigerCsWeb::TigerCS.Web.Models;

namespace TigerCS.Tests.Web;

/// <summary>Ticket Details "Routed to": the latest transfer destination comes only from the recorded transfer events.</summary>
public sealed class TicketRoutingSummaryTests
{
    private static readonly Guid Sara = Guid.NewGuid();
    private static readonly DateTime T0 = new(2026, 11, 1, 8, 0, 0, DateTimeKind.Utc);

    private static string? Destination(params TicketWorkflowEventDto[] events) =>
        TicketDisplay.LatestTransferDestination(
            events, id => id == Sara ? "Sara A." : null, id => id switch { 2 => "Billing", 3 => "Collections", _ => null });

    private static TicketWorkflowEventDto Transfer(DateTime at, int? toDepartment, Guid? toEmployee = null, string type = "TicketTransferred") =>
        new(type, at, Guid.NewGuid(), null, "WrongDepartment", 1, toDepartment, toEmployee);

    [Fact]
    public void NoTransferEvents_MeansNoDestination() =>
        Assert.Null(Destination(new TicketWorkflowEventDto("ApprovalRequested", T0, null, null)));

    [Fact]
    public void TheNewestTransferWins_AndNamesTheQueueWhenNoPersonWasChosen()
    {
        Assert.Equal("Collections (queue)", Destination(Transfer(T0, 2), Transfer(T0.AddHours(1), 3)));
        Assert.Equal("Collections (queue)", Destination(Transfer(T0.AddHours(1), 3), Transfer(T0, 2)));
    }

    [Fact]
    public void ATransferToAPerson_NamesThePersonAndTheirDepartment()
    {
        Assert.Equal("Sara A. in Billing", Destination(Transfer(T0, 2, Sara)));
        Assert.Equal("Sara A.", Destination(Transfer(T0, null, Sara)));
    }

    [Fact]
    public void ATransferWithNoRecordedDestination_InventsNothing() =>
        Assert.Null(Destination(Transfer(T0, 99)));
}
