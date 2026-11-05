using System.Runtime.CompilerServices;

namespace TigerCS.Tests.Web;

/// <summary>
/// The Customer Profile page (`/Customers/{customerKey}`): a CRM-style
/// customer workspace keyed by the Customers directory identity — header
/// (name, phone, verification source, primary unit), five tabs (Overview /
/// Contact Info / Units / Tickets / Interactions) with Overview selected by
/// default, every ticket linking to Ticket Details, the CRM enrichment
/// reusing the existing ticket-anchored customer-profile endpoint unchanged,
/// and no CRM/history logic duplicated in the page itself. The old
/// ticket-anchored address (`/Tickets/{ticketId}/Customer`) resolves the
/// ticket's customer and redirects here.
/// </summary>
public sealed class CustomerProfileTests
{
    private static string SourceFile(string relativeToSrc, [CallerFilePath] string testFilePath = "")
    {
        var srcDir = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(testFilePath)!, "..", ".."));
        return Path.Combine(srcDir, relativeToSrc);
    }

    private static string ViewHtml() =>
        File.ReadAllText(SourceFile(Path.Combine("TigerCS.Web", "Pages", "CustomerProfile.cshtml")));

    private static string ModelSource() =>
        File.ReadAllText(SourceFile(Path.Combine("TigerCS.Web", "Pages", "CustomerProfile.cshtml.cs")));

    private static string RedirectModelSource() =>
        File.ReadAllText(SourceFile(Path.Combine("TigerCS.Web", "Pages", "TicketCustomer.cshtml.cs")));

    private static string TicketsApiClientSource() =>
        File.ReadAllText(SourceFile(Path.Combine("TigerCS.Web", "Services", "Api", "TicketsApiClient.cs")));

    private static string CustomersApiClientSource() =>
        File.ReadAllText(SourceFile(Path.Combine("TigerCS.Web", "Services", "Api", "CustomersApiClient.cs")));

    private static string Panel(string html, string panelId)
    {
        var start = html.IndexOf($"id=\"{panelId}\"", StringComparison.Ordinal);
        Assert.True(start >= 0, $"Expected a panel with id {panelId}.");
        var end = html.IndexOf("<div class=\"tab-panel\"", start + 1, StringComparison.Ordinal);
        return end < 0 ? html[start..] : html[start..end];
    }

    [Fact]
    public void View_IsKeyedByTheCustomersDirectoryIdentity_AndTheOldTicketAddressRedirectsToIt()
    {
        Assert.Contains("@page \"/Customers/{customerKey}\"", ViewHtml());

        var redirect = File.ReadAllText(SourceFile(Path.Combine("TigerCS.Web", "Pages", "TicketCustomer.cshtml")));
        Assert.Contains("@page \"/Tickets/{ticketId:long}/Customer\"", redirect);
        Assert.Contains("LocalRedirect($\"/Customers/{Uri.EscapeDataString(key)}?fromTicket={ticketId}\")", RedirectModelSource());
        Assert.Contains("GetCustomerHistoryAsync", RedirectModelSource());
    }

    [Fact]
    public void View_HasTheCustomerHeader_WithNamePhoneVerificationAndPrimaryUnit()
    {
        var html = ViewHtml();

        Assert.Contains("class=\"customer-header\"", html);
        Assert.Contains("<h1 class=\"customer-header__name\" data-contact-name>@Model.DisplayName</h1>", html);
        Assert.Contains("Model.PrimaryPhone is { } phone", html);
        Assert.Contains("badge-source-@CustomerProfileModel.SourceCssKey(profile.VerificationSource)", html);
        Assert.Contains("primaryUnit.UnitNumber", html);
        Assert.Contains("profile.Units.Count > 1", html);
    }

    [Fact]
    public void View_HasAllFiveTabs()
    {
        var html = ViewHtml();

        Assert.Contains("id=\"tab-overview\"", html);
        Assert.Contains(">Overview<", html);
        Assert.Contains("id=\"tab-contact\"", html);
        Assert.Contains(">Contact Info<", html);
        Assert.Contains("id=\"tab-units\"", html);
        Assert.Contains(">Units <span class=\"tab-count\">", html);
        Assert.Contains("id=\"tab-history\"", html);
        Assert.Contains(">Tickets <span class=\"tab-count\">@profile.TotalTickets</span>", html);
        Assert.Contains("id=\"tab-interactions\"", html);
        Assert.Contains(">Interactions <span class=\"tab-count\">", html);
        foreach (var panel in new[] { "panel-overview", "panel-contact", "panel-units", "panel-history", "panel-interactions" })
        {
            Assert.Contains($"id=\"{panel}\"", html);
        }
    }

    [Fact]
    public void View_OverviewTabIsSelectedByDefault()
    {
        var html = ViewHtml();

        var overviewStart = html.IndexOf("id=\"tab-overview\"", StringComparison.Ordinal);
        var overviewEnd = html.IndexOf("/>", overviewStart, StringComparison.Ordinal);
        Assert.Contains("checked", html[overviewStart..overviewEnd]);

        foreach (var otherTabId in new[] { "id=\"tab-contact\"", "id=\"tab-units\"", "id=\"tab-history\"", "id=\"tab-interactions\"" })
        {
            var start = html.IndexOf(otherTabId, StringComparison.Ordinal);
            var end = html.IndexOf("/>", start, StringComparison.Ordinal);
            Assert.DoesNotContain("checked", html[start..end]);
        }
    }

    [Fact]
    public void OverviewTab_ShowsTheCustomerFacts_AndRecentTickets()
    {
        var panel = Panel(ViewHtml(), "panel-overview");

        Assert.Contains("<dt>Customer Name</dt>", panel);
        Assert.Contains("<dt>Phone</dt>", panel);
        Assert.Contains("<dt>Verification</dt>", panel);
        Assert.Contains("<dt>CRM Customer ID</dt>", panel);
        Assert.Contains("<dt>Primary unit</dt>", panel);
        Assert.Contains("<dt>Customer since</dt>", panel);
        Assert.Contains("profile.Tickets.Take(5)", panel);
        Assert.Contains("href=\"/Tickets/@recent.TicketId\"", panel);
    }

    [Fact]
    public void ContactTab_ShowsIdentityAndReach_WithLiveCrmFirstAndPersistedFallbacks()
    {
        var panel = Panel(ViewHtml(), "panel-contact");

        Assert.Contains("<dt>Full Name English</dt>", panel);
        Assert.Contains("<dt>Full Name Arabic</dt>", panel);
        Assert.Contains("<dt>Mobile Number</dt>", panel);
        Assert.Contains("<dt>Email</dt>", panel);
        Assert.Contains("<dt>CRM Customer ID</dt>", panel);
        Assert.Contains("profile.DisplayName ?? crm?.FullNameEnglish", panel);
        // Reach comes from the page model, which picks the primary and the
        // genuinely-different aliases canonically — the view no longer
        // string-compares raw phone numbers.
        Assert.Contains("Model.PrimaryPhone", panel);
        Assert.Contains("Model.OtherPhones", panel);
        Assert.Contains("Model.PrimaryEmail", panel);
        Assert.Contains("Model.OtherEmails", panel);
        Assert.DoesNotContain("profile.PhoneNumbers.Where", panel);
        Assert.Contains("TicketDisplay.CustomerProfileStatusMessage(crm.Status)", panel);
    }

    [Fact]
    public void UnitsTab_ListsLiveCrmUnits_OrTheUnitsOnTheCustomersTickets()
    {
        var panel = Panel(ViewHtml(), "panel-units");

        Assert.Contains("<th>Project</th>", panel);
        Assert.Contains("<th>Unit Number</th>", panel);
        Assert.Contains("<th>Lead Status</th>", panel);
        Assert.Contains("<th>Unit Type</th>", panel);
        Assert.Contains("<th>Floor</th>", panel);
        Assert.Contains("@foreach (var unit in liveUnits)", panel);
        // Multiple units are shown as rows, each with how many tickets it carries.
        Assert.Contains("@foreach (var unit in profile.Units)", panel);
        Assert.Contains("unit.TicketCount", panel);
    }

    [Fact]
    public void TicketsTab_ListsEveryTicket_WithStatusPriorityDepartmentCreatedUnitAndLastActivity_EachOpeningTicketDetails()
    {
        var panel = Panel(ViewHtml(), "panel-history");

        foreach (var column in new[] { "<th>Ticket</th>", "<th>Status</th>", "<th>Priority</th>", "<th>Department</th>", "<th>Created</th>", "<th>Unit</th>", "<th>Last activity</th>" })
        {
            Assert.Contains(column, panel);
        }

        Assert.Contains("href=\"/Tickets/@row.TicketId\"", panel);
        Assert.Contains("data-href=\"/Tickets/@row.TicketId\"", panel);
        Assert.Contains("row.LastActivityAtUtc", panel);
        Assert.Contains("cell-truncate", panel);
    }

    [Fact]
    public void InteractionsTab_ListsCallsAndChatsAcrossTheCustomersTickets()
    {
        var panel = Panel(ViewHtml(), "panel-interactions");

        Assert.Contains("<th>Channel</th>", panel);
        Assert.Contains("<th>Ticket</th>", panel);
        Assert.Contains("<th>Agent</th>", panel);
        Assert.Contains("@foreach (var interaction in profile.Interactions)", panel);
        Assert.Contains("href=\"/Tickets/@interaction.TicketId\"", panel);
    }

    [Fact]
    public void Model_ReadsTheDirectoryProfile_AndEnrichesCrmCustomersThroughTheExistingTicketAnchoredEndpoint()
    {
        var source = ModelSource();

        Assert.Contains("CustomersApiClient", source);
        Assert.Contains("GetProfileAsync(customerKey", source);
        Assert.Contains("GetCustomerProfileAsync(Profile.LastTicketId", source);
        Assert.Contains("CustomersContext.HrefFromCookieValue", source);
        Assert.Contains("fromTicket", source);

        // No CRM or history logic duplicated in the Web layer.
        Assert.DoesNotContain("CrmBuyerLookupApiClient", source);
        Assert.DoesNotContain("CrmApiClient", source);
    }

    [Fact]
    public void ApiClients_CallTheDirectoryAndTheExistingProfileEndpoints_NeverCrmDirectly()
    {
        Assert.Contains("api/customers?", CustomersApiClientSource());
        Assert.Contains("api/customers/profile/", CustomersApiClientSource());
        Assert.Contains("api/tickets/{ticketId}/customer-profile", TicketsApiClientSource());
        Assert.DoesNotContain("api/crm", TicketsApiClientSource());
        Assert.DoesNotContain("api/crm", CustomersApiClientSource());
    }
}
