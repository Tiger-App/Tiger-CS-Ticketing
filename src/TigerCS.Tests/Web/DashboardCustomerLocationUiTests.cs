// TigerCS.Web is referenced under an alias — see TigerCS.Tests.csproj.
extern alias TigerCsWeb;

using System.Net;
using System.Web;
using Microsoft.Extensions.Logging.Abstractions;
using TigerCS.Application.Modules.Ticketing.Abstractions;
using TigerCS.Application.Modules.Ticketing.Dto;
using TigerCS.Domain.Modules.Ticketing;
using TigerCS.Tests.Web.Fakes;
using TigerCsWeb::TigerCS.Web.Models;
using TigerCsWeb::TigerCS.Web.Pages;
using TigerCsWeb::TigerCS.Web.Services.Api;

namespace TigerCS.Tests.Web;

/// <summary>The Customers by Location card as the page presents it: figures straight from the Api, Unknown only when positive, links that carry every dashboard filter into the Customers list.</summary>
public sealed class DashboardCustomerLocationUiTests
{
    private static readonly DashboardAppliedFiltersDto Filters =
        new(new DateOnly(2026, 8, 12), new DateOnly(2026, 9, 10), 4, Guid.Parse("11111111-1111-1111-1111-111111111111"), 3, 17, "Open", 2);

    private static DashboardOverviewDto OverviewWith(DashboardCustomerLocationDto? location) =>
        new(Filters, new DashboardKpisDto(0, 0, 0, 0, 0, 0), 0, [], [], [], [], [], [],
            [], new DashboardFilterOptionsDto([], [], [], [], [], []), location);

    private static readonly DashboardCustomerLocationDto Counts = new(30, 60, 15, 30, 5, 10, 50);

    [Fact]
    public void TheCard_ShowsTheApisCountsAndShares_AndNothingIsRecomputed()
    {
        var card = DashboardModel.BuildCustomerLocationCard(OverviewWith(Counts), canOpenCustomers: true)!;

        Assert.Equal((30, 60.0), (card.Within.Count, card.Within.Percentage));
        Assert.Equal((15, 30.0), (card.Outside.Count, card.Outside.Percentage));
        Assert.Equal(50, card.Total);
        Assert.Equal("Customers by Location", CustomerLocationCard.Title);
    }

    [Fact]
    public void Unknown_IsShownOnlyWhenGreaterThanZero()
    {
        Assert.NotNull(DashboardModel.BuildCustomerLocationCard(OverviewWith(Counts), true)!.Unknown);
        Assert.Null(DashboardModel.BuildCustomerLocationCard(OverviewWith(new DashboardCustomerLocationDto(30, 66.7, 15, 33.3, 0, 0, 45)), true)!.Unknown);
    }

    [Fact]
    public void NoCardIsBuilt_WhenTheApiSentNone()
    {
        Assert.Null(DashboardModel.BuildCustomerLocationCard(OverviewWith(null), true));
    }

    [Fact]
    public void BarSegments_AreStylesheetClasses_NeverInlineStyles()
    {
        var card = DashboardModel.BuildCustomerLocationCard(OverviewWith(Counts), true)!;

        Assert.Equal("bar-fill--w60", card.Within.WidthClass);
        Assert.Equal("bar-fill--w30", card.Outside.WidthClass);
        Assert.Equal("bar-fill--w10", card.Unknown!.WidthClass);
        Assert.Equal("bar-fill--w100", new CustomerLocationSegment("x", "x", 1, 100, null).WidthClass);
        Assert.Equal("bar-fill--w0", new CustomerLocationSegment("x", "x", 0, 0, null).WidthClass);
    }

    [Fact]
    public void BothCounts_LinkToTheCustomersList_WithEveryDashboardFilter()
    {
        var card = DashboardModel.BuildCustomerLocationCard(OverviewWith(Counts), true)!;

        foreach (var (segment, location) in new[] { (card.Within, "within-uae"), (card.Outside, "outside-uae") })
        {
            var uri = new Uri("http://x" + segment.Href);
            var q = HttpUtility.ParseQueryString(uri.Query);
            Assert.Equal("/Customers", uri.AbsolutePath);
            Assert.Equal(location, q["location"]);
            Assert.Equal("2026-08-12", q["dateFrom"]);
            Assert.Equal("2026-09-10", q["dateTo"]);
            Assert.Equal("4", q["departmentId"]);
            Assert.Equal("11111111-1111-1111-1111-111111111111", q["ownerEmployeeId"]);
            Assert.Equal("3", q["channelId"]);
            Assert.Equal("17", q["requestTypeId"]);
            Assert.Equal("Open", q["ticketStatus"]);
            Assert.Equal("2", q["priorityId"]);
        }
    }

    [Fact]
    public void WithoutAccessToTheCustomersDirectory_TheCountsAreFiguresNotLinks()
    {
        var card = DashboardModel.BuildCustomerLocationCard(OverviewWith(Counts), canOpenCustomers: false)!;

        Assert.All(new[] { card.Within, card.Outside, card.Unknown! }, s => Assert.Null(s.Href));
        Assert.Equal(30, card.Within.Count);
    }

    /// <summary>The link the card emits, handed to the Customers page's own API call, asks the Api for exactly the same customers.</summary>
    [Fact]
    public async Task TheLinkRoundTrips_IntoTheApiRequestTheCustomersPageMakes()
    {
        var card = DashboardModel.BuildCustomerLocationCard(OverviewWith(Counts), true)!;
        var q = HttpUtility.ParseQueryString(new Uri("http://x" + card.Within.Href).Query);

        var handler = new FakeApiHandler((_, _) => FakeApiHandler.JsonResponse(HttpStatusCode.OK, new CustomerDirectoryListResultDto([], 0, 1, 25)));
        var client = new CustomersApiClient(new HttpClient(handler) { BaseAddress = new Uri("http://localhost/") }, NullLogger<CustomersApiClient>.Instance);

        await client.ListAsync(new CustomerDirectoryListRequestDto(
            DepartmentId: int.Parse(q["departmentId"]!), Location: q["location"],
            DateFrom: DateOnly.Parse(q["dateFrom"]!), DateTo: DateOnly.Parse(q["dateTo"]!), OwnerEmployeeId: Guid.Parse(q["ownerEmployeeId"]!),
            ChannelId: byte.Parse(q["channelId"]!), RequestTypeId: int.Parse(q["requestTypeId"]!), TicketStatus: q["ticketStatus"], PriorityId: byte.Parse(q["priorityId"]!)),
            CancellationToken.None);

        var sent = HttpUtility.ParseQueryString(new Uri(handler.Requests.Single().RequestUri).Query);
        foreach (var key in new[] { "location", "dateFrom", "dateTo", "departmentId", "ownerEmployeeId", "channelId", "requestTypeId", "ticketStatus", "priorityId" })
        {
            Assert.Equal(q[key], sent[key]);
        }
    }

    [Fact]
    public void LocationKeys_RoundTrip_AndRejectNonsense()
    {
        foreach (var location in Enum.GetValues<CustomerLocation>())
        {
            Assert.Equal(location, CustomerLocationKeys.Parse(CustomerLocationKeys.ToKey(location)));
        }

        Assert.Null(CustomerLocationKeys.Parse("nope"));
        Assert.Null(CustomerLocationKeys.Parse(null));
        Assert.Equal(CustomerLocation.WithinUae, CustomerLocationKeys.Parse(" Within-UAE "));
    }
}
