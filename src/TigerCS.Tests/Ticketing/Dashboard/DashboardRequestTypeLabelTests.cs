using TigerCS.Application.Modules.Ticketing.Dto;
using TigerCS.Domain.Modules.IdentityAndAccess;
using TigerCS.Domain.Modules.SlaAndEscalation;
using TigerCS.Domain.Modules.Ticketing;
using TigerCS.Domain.Modules.WorkflowConfiguration;
using TigerCS.Infrastructure.Persistence;

namespace TigerCS.Tests.Ticketing.Dashboard;

/// <summary>
/// Volume by Request Type when several departments each configure a request type with the same name
/// (every department has a "General"): such rows read "Name — Department", stay separate by RequestTypeId,
/// and keep their counts, percentages and drill-down keys.
/// </summary>
public sealed class DashboardRequestTypeLabelTests : IDisposable
{
    private static readonly string[] CrossDepartment = [Roles.CsSupervisor];

    // Foreign keys off so a request type can name a department that does not exist (the "no department" case).
    private readonly DashboardSqliteFixture _db = new(enforceForeignKeys: false);

    public void Dispose() => _db.Dispose();

    private Task<DashboardOverviewDto> OverviewAsync(DashboardOverviewRequestDto? request = null)
    {
        using var context = _db.CreateContext();
        return _db.CreateService(context).GetOverviewAsync(Guid.NewGuid(), CrossDepartment, request ?? new DashboardOverviewRequestDto());
    }

    private static RequestType AddRequestType(TigerCsDbContext context, int departmentId, string name)
    {
        var workflowId = context.Workflows.First().WorkflowId;
        var requestType = new RequestType(departmentId, name, workflowId, (byte)PriorityLevel.Medium, true, true, true, true);
        context.RequestTypes.Add(requestType);
        context.SaveChanges();
        return requestType;
    }

    [Fact]
    public async Task SameNamedRequestTypes_AreShownWithTheirDepartment_AndStaySeparateByTheirOwnId()
    {
        int csGeneral, collectionsGeneral, registrationGeneral;
        using (var context = _db.CreateContext())
        {
            csGeneral = AddRequestType(context, _db.CustomerServiceId, "General").RequestTypeId;
            collectionsGeneral = AddRequestType(context, _db.CollectionsId, "General").RequestTypeId;
            registrationGeneral = AddRequestType(context, _db.RegistrationId, "General").RequestTypeId;

            for (var i = 0; i < 3; i++) { _db.AddTicket(context, _db.CustomerServiceId, requestTypeId: csGeneral); }
            for (var i = 0; i < 2; i++) { _db.AddTicket(context, _db.CollectionsId, requestTypeId: collectionsGeneral); }
            _db.AddTicket(context, _db.RegistrationId, requestTypeId: registrationGeneral);
            _db.AddTicket(context, _db.CollectionsId, requestTypeId: _db.CollectionsRequestTypeId); // a uniquely named type
        }

        var overview = await OverviewAsync();
        var rows = overview.VolumeByRequestType;

        // Three separate rows, one per RequestTypeId, each named after its own department, with its own count.
        DashboardBreakdownItemDto Row(string label) => rows.Single(r => r.Label == label);
        Assert.Equal(csGeneral.ToString(), Row("General — Customer Service").Key);
        Assert.Equal(3, Row("General — Customer Service").Count);
        Assert.Equal(collectionsGeneral.ToString(), Row("General — Collections").Key);
        Assert.Equal(2, Row("General — Collections").Count);
        Assert.Equal(registrationGeneral.ToString(), Row("General — Registration").Key);
        Assert.Equal(1, Row("General — Registration").Count);
        Assert.DoesNotContain(rows, r => r.Label == "General");

        // A name that is not duplicated is left exactly as configured.
        Assert.Equal(1, Row("Payment Reminder").Count);

        // Counts and percentages are untouched: nothing merged, nothing lost.
        Assert.Equal(7, overview.VolumeTotal);
        Assert.Equal(overview.VolumeTotal, rows.Sum(r => r.Count));
        Assert.Equal(300.0 / 7, Row("General — Customer Service").Percentage, precision: 0);
    }

    [Fact]
    public async Task EachRowStillFiltersByItsOwnRequestTypeId()
    {
        int csGeneral, collectionsGeneral;
        using (var context = _db.CreateContext())
        {
            csGeneral = AddRequestType(context, _db.CustomerServiceId, "General").RequestTypeId;
            collectionsGeneral = AddRequestType(context, _db.CollectionsId, "General").RequestTypeId;
            _db.AddTicket(context, _db.CustomerServiceId, requestTypeId: csGeneral);
            _db.AddTicket(context, _db.CollectionsId, requestTypeId: collectionsGeneral);
            _db.AddTicket(context, _db.CollectionsId, requestTypeId: collectionsGeneral);
        }

        var filtered = await OverviewAsync(new DashboardOverviewRequestDto(RequestTypeId: collectionsGeneral));

        Assert.Equal(2, filtered.VolumeTotal);
        var row = Assert.Single(filtered.VolumeByRequestType);
        Assert.Equal("General — Collections", row.Label);
        Assert.Equal(collectionsGeneral.ToString(), row.Key);
        Assert.Equal(2, row.Count);
    }

    [Fact]
    public async Task ARequestTypeWithNoResolvableDepartment_KeepsItsPlainName_NothingIsInvented()
    {
        int orphan, normal;
        using (var context = _db.CreateContext())
        {
            normal = AddRequestType(context, _db.CustomerServiceId, "General").RequestTypeId;
            orphan = AddRequestType(context, departmentId: 9999, "General").RequestTypeId; // no such department
            _db.AddTicket(context, _db.CustomerServiceId, requestTypeId: normal);
            _db.AddTicket(context, _db.CustomerServiceId, requestTypeId: orphan);
        }

        var overview = await OverviewAsync();

        Assert.Equal(normal.ToString(), overview.VolumeByRequestType.Single(r => r.Label == "General — Customer Service").Key);
        Assert.Equal(orphan.ToString(), overview.VolumeByRequestType.Single(r => r.Label == "General").Key);
        Assert.Equal(2, overview.VolumeByRequestType.Sum(r => r.Count));
    }

    [Fact]
    public async Task ALabelDoesNotDependOnTheDateRange_ADuplicatedNameStaysQualifiedEvenWhenOnlyOneOfThemHasTickets()
    {
        using (var context = _db.CreateContext())
        {
            var cs = AddRequestType(context, _db.CustomerServiceId, "General").RequestTypeId;
            AddRequestType(context, _db.CollectionsId, "General"); // no tickets at all
            _db.AddTicket(context, _db.CustomerServiceId, requestTypeId: cs);
        }

        var overview = await OverviewAsync();

        Assert.Equal("General — Customer Service", Assert.Single(overview.VolumeByRequestType).Label);
    }
}
