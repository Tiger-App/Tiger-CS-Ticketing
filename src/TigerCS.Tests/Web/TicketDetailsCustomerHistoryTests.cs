using System.Runtime.CompilerServices;

namespace TigerCS.Tests.Web;

/// <summary>
/// Ticket Details' Customer History presentation (Customer -> Previous
/// Ticket History, this increment; later revised to a dedicated "Previous
/// Tickets" tab so it never visually competes with the primary ticket
/// information): renders the customer's other tickets in their own tab,
/// defaults to the Details tab, labels unverified (phone-snapshot) history
/// clearly, links each row to its own Ticket Details page, and never calls
/// CRM live. This is a presentation-only concern — the underlying
/// CustomerHistory API/business logic is unchanged and covered separately
/// (CustomerHistoryAppServiceTests).
/// </summary>
public sealed class TicketDetailsCustomerHistoryTests
{
    private static string SourceFile(string relativeToSrc, [CallerFilePath] string testFilePath = "")
    {
        var srcDir = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(testFilePath)!, "..", ".."));
        return Path.Combine(srcDir, relativeToSrc);
    }

    private static string TicketDetailsViewHtml() =>
        File.ReadAllText(SourceFile(Path.Combine("TigerCS.Web", "Pages", "TicketDetails.cshtml")));

    private static string TicketDetailsModelSource() =>
        File.ReadAllText(SourceFile(Path.Combine("TigerCS.Web", "Pages", "TicketDetails.cshtml.cs")));

    private static string TicketsApiClientSource() =>
        File.ReadAllText(SourceFile(Path.Combine("TigerCS.Web", "Services", "Api", "TicketsApiClient.cs")));

    [Fact]
    public void View_RendersAPreviousTicketsSection()
    {
        var html = TicketDetailsViewHtml();

        Assert.Contains("Previous Tickets", html);
        Assert.Contains("history.TotalTickets", html);
        Assert.Contains("history.OpenTickets", html);
        Assert.Contains("history.ClosedTickets", html);
    }

    [Fact]
    public void View_HasSeparateOverviewAndCustomerTabs()
    {
        // The redesigned Ticket Details: Overview | Activity | Customer |
        // Approvals | Attachments — Previous Tickets live on the Customer tab,
        // never competing with the primary ticket facts. The conversation is part of the Overview.
        var html = TicketDetailsViewHtml();

        Assert.Contains("id=\"tab-details\"", html);
        Assert.Contains("for=\"tab-details\"", html);
        Assert.Contains("id=\"tab-customer\"", html);
        Assert.Contains("for=\"tab-customer\"", html);
        Assert.Contains("id=\"panel-details\"", html);
        Assert.Contains("id=\"panel-customer\"", html);
        foreach (var tab in new[] { "tab-activity", "tab-approvals", "tab-attachments" })
        {
            Assert.Contains($"id=\"{tab}\"", html);
        }
    }

    [Fact]
    public void View_DetailsTabIsSelectedByDefault_NotPreviousTickets()
    {
        var html = TicketDetailsViewHtml();

        var detailsInputStart = html.IndexOf("id=\"tab-details\"", StringComparison.Ordinal);
        var detailsInputEnd = html.IndexOf("/>", detailsInputStart, StringComparison.Ordinal);
        var detailsInputTag = html[detailsInputStart..detailsInputEnd];

        var historyInputStart = html.IndexOf("id=\"tab-customer\"", StringComparison.Ordinal);
        var historyInputEnd = html.IndexOf("/>", historyInputStart, StringComparison.Ordinal);
        var historyInputTag = html[historyInputStart..historyInputEnd];

        Assert.Contains("checked", detailsInputTag);
        Assert.DoesNotContain("checked", historyInputTag);
    }

    [Fact]
    public void View_ShowsThePreviousTicketsCountInTheTabLabel()
    {
        var html = TicketDetailsViewHtml();

        Assert.Contains("Previous Tickets@(Model.CustomerHistory is not null ? $\" ({Model.CustomerHistory.TotalTickets})\" : \"\")", html);
    }

    [Fact]
    public void View_PreviousTicketsTabPanelLivesOutsideTheFactsPanel_DoesNotCompeteWithPrimaryInformation()
    {
        // The whole reason for the tab: Customer History is useful context
        // but not always relevant to the current ticket, so it must not sit
        // inline inside the primary facts-panel any more.
        var html = TicketDetailsViewHtml();

        var factsPanelEnd = html.IndexOf("</aside>", StringComparison.Ordinal);
        var panelHistoryStart = html.IndexOf("id=\"panel-customer\"", StringComparison.Ordinal);

        Assert.True(factsPanelEnd > 0, "Expected a facts-panel <aside> in the view.");
        Assert.True(panelHistoryStart > factsPanelEnd, "The Previous Tickets tab panel must be a sibling of, not nested inside, the facts-panel.");
    }

    [Fact]
    public void View_LabelsUnverifiedHistoryClearly()
    {
        var html = TicketDetailsViewHtml();

        Assert.Contains("Unverified Customer History", html);
        Assert.Contains("history.VerificationType == \"Unverified\"", html);
    }

    [Fact]
    public void View_HistoryRowsLinkToTheirOwnTicketDetailsPage()
    {
        var html = TicketDetailsViewHtml();

        Assert.Contains("/Tickets/@row.TicketId", html);
    }

    [Fact]
    public void Model_LoadsCustomerHistory_ViaTicketsApiClient()
    {
        var source = TicketDetailsModelSource();

        Assert.Contains("GetCustomerHistoryAsync", source);
        Assert.Contains("CustomerHistory", source);
    }

    [Fact]
    public void Model_DoesNotDependOnAnyCrmApiClient_CustomerHistoryNeverCallsCrmLive()
    {
        var source = TicketDetailsModelSource();

        Assert.DoesNotContain("CrmBuyerLookupApiClient", source);
        Assert.DoesNotContain("CrmApiClient", source);
        Assert.DoesNotContain("GetBuyerByPhone", source);
    }

    [Fact]
    public void ApiClient_CustomerHistoryCall_NeverTouchesCrmEndpoints()
    {
        var source = TicketsApiClientSource();

        Assert.Contains("api/tickets/{ticketId}/customer-history", source);
        Assert.DoesNotContain("api/crm", source);
    }
}
