extern alias TigerCsWeb;

using System.Net;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc.Testing;
using Receivables = TigerCsWeb::TigerCS.Web.Pages.Collections.ReceivablesModel;
using Display = TigerCsWeb::TigerCS.Web.Pages.Collections.CollectionsDisplay;

namespace TigerCS.Tests.Web;

/// <summary>
/// Receivables page: one row per unit, View Details with the unit's unpaid instalments that are Due or Overdue (due before the end of the current month; Dubai: the fake API's
/// business date is 2026-10-09), search + Tower + Status filters, and one short line when a company could not be loaded.
/// </summary>
public sealed class PactReceivablesRenderTests
{
    private static readonly DateOnly Today = new(2026, 10, 9);

    private static string Between(string html, string start, string end)
    {
        var from = html.IndexOf(start, StringComparison.Ordinal);
        Assert.True(from >= 0, $"'{start}' not found");
        var to = html.IndexOf(end, from, StringComparison.Ordinal);
        return html[from..(to < 0 ? html.Length : to)];
    }

    private static List<string> Cells(string row) => Regex.Matches(row, "<td[^>]*>(.*?)</td>", RegexOptions.Singleline).Select(m => Regex.Replace(m.Groups[1].Value, "<[^>]+>", " ")).Select(t => Regex.Replace(System.Net.WebUtility.HtmlDecode(t), @"\s+", " ").Trim()).ToList();

    [Fact]
    public async Task InitialPage_HasSearchTowerStatusMonthYearAndMinimumTotal_AndDoesNotWaitForTheData()
    {
        var api = new FakeCollectionsApi();
        using var factory = CollectionsWebHost.Factory(api); using var client = factory.CreateClient();
        var response = await client.GetAsync("/Collections/Receivables?towerId=7&status=overdue");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var html = await response.Content.ReadAsStringAsync();
        Assert.Empty(api.Calls("/receivables/instalments"));                         // the shell made no data call
        Assert.Contains("data-results-loading", html);
        Assert.Contains("name=\"search\"", html); Assert.Contains("Customer name, mobile, tower or apartment", html);
        Assert.Matches("<input type=\"checkbox\" name=\"towerId\" value=\"7\" checked=\"checked\" />", html);   // the old single-tower URL ticks that tower in the shared multi-select
        Assert.Contains("data-tower-multi", html);
        Assert.Contains("name=\"status\"", html); Assert.Contains("<option value=\"overdue\" selected=\"selected\">Overdue</option>", html);
        Assert.DoesNotContain("upcoming", html, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("name=\"month\"", html); Assert.Contains("<option value=\"\">All months</option>", html); Assert.Contains("name=\"year\"", html);
        Assert.Contains("Minimum Total (AED)", html); Assert.Matches("<input[^>]*name=\"minTotal\"[^>]*value=\"100\"", html);          // default 100
        foreach (var gone in new[] { "name=\"dateFrom\"", "name=\"dateTo\"", "name=\"minAmount\"", "name=\"paymentStatus\"", "name=\"view\"", "data-last-six-months", "How Due / Overdue" })
            Assert.DoesNotContain(gone, html);
    }

    [Fact]
    public async Task ACustomerWithTwoApartments_GetsTwoRows_WithSeparateTotals_AndAnApartmentWithManyInstalmentsOneRow()
    {
        var api = new FakeCollectionsApi();
        // Apartment 1001: overdue 5 Aug, due TODAY and a later 5 Nov instalment (next month: in neither Due nor Overdue, never listed or counted); apartment 2002 of the SAME customer: one overdue instalment.
        api.Rows.Add(FakeCollectionsApi.Unpaid(new DateOnly(2026, 11, 5), 150m, "INV-5"));
        api.Rows.Add(FakeCollectionsApi.Unpaid(new DateOnly(2026, 8, 5), 300m, "INV-1"));
        api.Rows.Add(FakeCollectionsApi.Unpaid(Today, 200m, "INV-3"));
        api.Rows.Add(FakeCollectionsApi.Unpaid(new DateOnly(2026, 9, 20), 80m, "INV-2", "TP124-2002", 102));
        using var factory = CollectionsWebHost.Factory(api); using var client = factory.CreateClient();
        var html = await (await client.GetAsync("/Collections/Receivables?handler=Results")).Content.ReadAsStringAsync();

        Assert.Contains("view=units", Assert.Single(api.Calls("/receivables/instalments")));
        Assert.DoesNotContain("minAmount", Assert.Single(api.Calls("/receivables/instalments")));    // no per-instalment minimum: only the unit total is filtered
        var rows = Regex.Matches(html, "<tr data-unit-row>.*?</tr>", RegexOptions.Singleline).Select(m => m.Value).ToList();
        Assert.Equal(2, rows.Count);                                                                   // one row per unit, not per instalment and not per customer
        var headers = Cells(Regex.Match(html, "<thead><tr>.*?</tr></thead>", RegexOptions.Singleline).Value.Replace("th", "td"));
        Assert.Equal(["Customer", "Contact", "Tower", "Apartment", "Due", "Overdue", "Total", "View Details"], headers);

        // Apartment 1001: total 500 = due 200 + overdue 300; the 150 due in November is not part of anything on this page. Tower = the tower NAME.
        var first = Cells(rows.Single(r => r.Contains("TP124-1001")));
        Assert.Equal(["Example Customer", "+971500003001 x@example.test", "Tower 124", "TP124-1001", "200.00", "300.00", "500.00", "View Details"], first);
        // Apartment 2002 is NOT added to 1001: its own totals.
        var second = Cells(rows.Single(r => r.Contains("TP124-2002")));
        Assert.Equal(["Example Customer", "+971500003001 x@example.test", "Tower 124", "TP124-2002", "—", "80.00", "80.00", "View Details"], second);

        // The details of 1001: newest due date first, statuses by date, days late only for overdue, and the sums equal the main row.
        var details = Regex.Matches(html, "<tr class=\"unit-detail\".*?</tbody>\\s*</table>", RegexOptions.Singleline).Select(m => m.Value).ToList();
        Assert.Equal(2, details.Count);
        var d1 = details.Single(d => d.Contains("INV-1"));
        Assert.DoesNotContain("INV-2", d1);                                                           // no instalment of another apartment
        var lines = Regex.Matches(d1, "<tr class=\"[^\"]*\" data-instalment-status=\"(\\w+)\">(.*?)</tr>", RegexOptions.Singleline).Select(m => (Status: m.Groups[1].Value, Cells: Cells(m.Value))).ToList();
        Assert.Equal(["Due", "Overdue"], lines.Select(l => l.Status));
        Assert.DoesNotContain("INV-5", html); Assert.DoesNotContain("Upcoming", html);
        Assert.Equal(["Voucher INV-3", "09 Oct 2026", "—", "—", "200.00", "Due", "—"], lines[0].Cells);                // due today is not late
        Assert.Equal(["Voucher INV-1", "05 Aug 2026", "—", "—", "300.00", "Overdue", "65"], lines[1].Cells);          // 5 Aug -> 9 Oct = 65 days
        Assert.Equal(500m, lines.Sum(l => decimal.Parse(l.Cells[4])));                                  // details add up to the row's total remaining
        Assert.Matches("instalment-row--overdue[^>]*data-instalment-status=\"Overdue\"", d1);          // overdue rows are the red ones
        Assert.DoesNotContain("instalment-row--overdue\" data-instalment-status=\"Due\"", d1);
        foreach (var column in new[] { ">Instalment<", ">Due date<", ">Original amount<", ">Paid<", ">Remaining<", ">Status<", ">Days late<" }) Assert.Contains(column, d1);
    }

    [Fact]
    public async Task OriginalAndPaid_AreShownWhenTheSourceProvidesThem_AndNeverInvented()
    {
        var api = new FakeCollectionsApi();
        api.Rows.Add(FakeCollectionsApi.Unpaid(new DateOnly(2026, 9, 1), 600m, "INV-7") with { OriginalAmount = 1000m, PaidAmount = 400m });
        api.Rows.Add(FakeCollectionsApi.Unpaid(new DateOnly(2026, 10, 1), 90m, "INV-8"));
        using var factory = CollectionsWebHost.Factory(api); using var client = factory.CreateClient();
        var html = await (await client.GetAsync("/Collections/Receivables?handler=Results")).Content.ReadAsStringAsync();
        var lines = Regex.Matches(html, "data-instalment-status=\"\\w+\">(.*?)</tr>", RegexOptions.Singleline).Select(m => Cells("<tr>" + m.Value)).ToList();
        Assert.Equal(["Voucher INV-8", "01 Oct 2026", "—", "—", "90.00", "Due", "8"], lines[0]);                   // newest due date first
        Assert.Equal(["Voucher INV-7", "01 Sep 2026", "1,000.00", "400.00", "600.00", "Overdue", "38"], lines[1]);
    }

    [Theory]
    [InlineData("overdue")]
    [InlineData("due")]
    public async Task TheStatusFilter_IsSentToTheApi_AndKeptInThePagingLinks(string status)
    {
        var api = new FakeCollectionsApi();
        api.Rows.Add(FakeCollectionsApi.Unpaid(new DateOnly(2026, 8, 5), 300m, "INV-1"));
        api.Rows.Add(FakeCollectionsApi.Unpaid(Today, 200m, "INV-2", "TP124-2002", 102));
        using var factory = CollectionsWebHost.Factory(api); using var client = factory.CreateClient();
        var html = await (await client.GetAsync($"/Collections/Receivables?handler=Results&status={status}&towerId=7&search=Example")).Content.ReadAsStringAsync();
        var call = Assert.Single(api.Calls("/receivables/instalments"));
        foreach (var expected in new[] { $"status={status}", "towerId=7", "search=Example", "view=units" }) Assert.Contains(expected, call);
        var units = Regex.Matches(html, "<tr data-unit-row>.*?</tr>", RegexOptions.Singleline).Select(m => Cells(m.Value)[3]).ToList();
        Assert.Equal(status == "overdue" ? "TP124-1001" : "TP124-2002", Assert.Single(units));
    }

    [Fact]
    public async Task MinimumTotal_DefaultsTo100_CanBeChangedOrCleared_AndSurvivesPaging()
    {
        var api = new FakeCollectionsApi();
        api.Rows.Add(FakeCollectionsApi.Unpaid(new DateOnly(2026, 8, 5), 300m, "INV-1"));
        using var factory = CollectionsWebHost.Factory(api); using var client = factory.CreateClient();
        await client.GetAsync("/Collections/Receivables?handler=Results");                                  // nothing typed: the default
        await client.GetAsync("/Collections/Receivables?handler=Results&minTotal=250.5");                   // changed
        await client.GetAsync("/Collections/Receivables?handler=Results&minTotal=");                        // cleared: every unit with a Due or Overdue amount
        var calls = api.Calls("/receivables/instalments").ToList();
        Assert.Contains("minTotal=100", calls[0]);
        Assert.Contains("minTotal=250.5", calls[1]);
        Assert.DoesNotContain("minTotal", calls[2]);
        // The value is part of the form, of every paging link and of the no-JavaScript page.
        var shell = await (await client.GetAsync("/Collections/Receivables?minTotal=250.5&month=9&year=2026&status=overdue&search=Example")).Content.ReadAsStringAsync();
        Assert.Matches("<input[^>]*name=\"minTotal\"[^>]*value=\"250.5\"", shell);
        var cleared = await (await client.GetAsync("/Collections/Receivables?minTotal=")).Content.ReadAsStringAsync();
        Assert.Matches("<input[^>]*name=\"minTotal\"[^>]*value=\"\"", cleared);
    }

    [Fact]
    public async Task PagingLinks_KeepTheTowerStatusSearchMonthYearAndMinimumTotal()
    {
        var api = new FakeCollectionsApi();
        for (var n = 0; n < 30; n++) api.Rows.Add(FakeCollectionsApi.Unpaid(new DateOnly(2026, 9, 1 + n % 20), 300m, "INV-" + n, "TP124-" + (1000 + n), 200 + n, "Customer " + n, "T" + n));
        using var factory = CollectionsWebHost.Factory(api); using var client = factory.CreateClient();
        var html = await (await client.GetAsync("/Collections/Receivables?handler=Results&towerId=7&status=overdue&month=9&year=2026&minTotal=&search=Customer")).Content.ReadAsStringAsync();
        var next = Regex.Match(html, "data-results-link href=\"([^\"]+)\"").Groups[1].Value;
        if (next.Length > 0)
            foreach (var kept in new[] { "towerId=7", "status=overdue", "month=9", "year=2026", "minTotal=&", "search=Customer" }) Assert.Contains(kept, System.Net.WebUtility.HtmlDecode(next) + "&");
    }

    [Fact]
    public async Task MonthAndYear_FilterByDueDate_NeverPastTheEndOfTheCurrentMonth()
    {
        var today = Display.DubaiToday();
        var thisMonthStart = new DateOnly(today.Year, today.Month, 1);
        var previous = thisMonthStart.AddMonths(-1);
        var future = thisMonthStart.AddMonths(3);
        var api = new FakeCollectionsApi();
        using var factory = CollectionsWebHost.Factory(api); using var client = factory.CreateClient();
        async Task<string> Call(string query) { api.Requests.Clear(); return await (await client.GetAsync("/Collections/Receivables?handler=Results&" + query)).Content.ReadAsStringAsync(); }
        string Dates() { var call = Assert.Single(api.Calls("/receivables/instalments")); return Regex.Match(call, "dateFrom=([\\d-]+)").Groups[1].Value + ".." + Regex.Match(call, "dateTo=([\\d-]+)").Groups[1].Value; }

        await Call($"month={previous.Month}&year={previous.Year}");                                              // the previous month: all of it
        Assert.Equal($"{previous:yyyy-MM-dd}..{thisMonthStart.AddDays(-1):yyyy-MM-dd}", Dates());
        var thisMonthEnd = thisMonthStart.AddMonths(1).AddDays(-1);
        await Call($"month={today.Month}&year={today.Year}");                                                    // the current month: ALL of it, the days still to come included
        Assert.Equal($"{thisMonthStart:yyyy-MM-dd}..{thisMonthEnd:yyyy-MM-dd}", Dates());
        await Call($"month={today.Month}");                                                                      // a month without a year = the current year
        Assert.Equal($"{thisMonthStart:yyyy-MM-dd}..{thisMonthEnd:yyyy-MM-dd}", Dates());
        await Call("");                                                                                          // All months: the history plus the whole current month
        Assert.Equal($"2000-01-01..{thisMonthEnd:yyyy-MM-dd}", Dates());
        var next = thisMonthStart.AddMonths(1);
        var html = await Call($"month={next.Month}&year={next.Year}");                                           // the next month: neither Due nor Overdue, no data call
        Assert.Empty(api.Calls("/receivables/instalments")); Assert.Contains("Nothing is due in this month yet.", html);
        html = await Call($"month={future.Month}&year={future.Year}");                                           // a later month: the same
        Assert.Empty(api.Calls("/receivables/instalments")); Assert.Contains("Nothing is due in this month yet.", html);
    }

    [Fact]
    public async Task AUnitWithNothingDueThisMonthOrEarlier_IsNotListed_AndTheApiIsAskedOnlyUpToTheEndOfTheMonth()
    {
        var api = new FakeCollectionsApi();
        api.Rows.Add(FakeCollectionsApi.Unpaid(new DateOnly(2026, 11, 1), 500m, "INV-N", "TP124-3003", 103));   // the first day of next month: neither Due nor Overdue
        api.Rows.Add(FakeCollectionsApi.Unpaid(new DateOnly(2027, 1, 1), 900m, "INV-L", "TP124-3003", 103));
        using var factory = CollectionsWebHost.Factory(api); using var client = factory.CreateClient();
        var html = await (await client.GetAsync("/Collections/Receivables?handler=Results")).Content.ReadAsStringAsync();
        Assert.DoesNotContain("data-unit-row", html); Assert.DoesNotContain("TP124-3003", html);
        var today = Display.DubaiToday();
        Assert.Contains($"dateTo={new DateOnly(today.Year, today.Month, 1).AddMonths(1).AddDays(-1):yyyy-MM-dd}", Assert.Single(api.Calls("/receivables/instalments")));
        Assert.DoesNotContain("upcoming", Assert.Single(api.Calls("/receivables/instalments")));
    }

    [Fact]
    public async Task AComingDayOfTheCurrentMonth_IsDue_AndCountsInTheRowAndItsDetails()
    {
        var api = new FakeCollectionsApi();
        api.Rows.Add(FakeCollectionsApi.Unpaid(new DateOnly(2026, 10, 31), 250m, "INV-E", "TP124-3004", 104));   // the last day of the month, still to come (today is 9 Oct)
        api.Rows.Add(FakeCollectionsApi.Unpaid(new DateOnly(2026, 9, 30), 40m, "INV-S", "TP124-3004", 104));    // the last day of the previous month: Overdue
        using var factory = CollectionsWebHost.Factory(api); using var client = factory.CreateClient();
        var html = await (await client.GetAsync("/Collections/Receivables?handler=Results&minTotal=")).Content.ReadAsStringAsync();
        var row = Cells(Regex.Match(html, "<tr data-unit-row>.*?</tr>", RegexOptions.Singleline).Value);
        Assert.Equal(["250.00", "40.00", "290.00"], row.Skip(4).Take(3));                                         // Due, Overdue, Total
        var lines = Regex.Matches(html, "<tr class=\"[^\"]*\" data-instalment-status=\"(\\w+)\">(.*?)</tr>", RegexOptions.Singleline).Select(m => (Status: m.Groups[1].Value, Cells: Cells(m.Value))).ToList();
        Assert.Equal(["Due", "Overdue"], lines.Select(l => l.Status));
        Assert.Equal(["Voucher INV-E", "31 Oct 2026", "—", "—", "250.00", "Due", "—"], lines[0].Cells);          // a coming day: due, not late
    }

    [Fact]
    public async Task Search_FindsByCustomerMobileTowerAndApartment_AndAllIsSentWithoutAStatus()
    {
        var api = new FakeCollectionsApi();
        api.Rows.Add(FakeCollectionsApi.Unpaid(new DateOnly(2026, 8, 5), 300m, "INV-1"));
        using var factory = CollectionsWebHost.Factory(api); using var client = factory.CreateClient();
        foreach (var term in new[] { "Example", "500003001", "Tower 124", "TP124-1001" })
        {
            var html = await (await client.GetAsync($"/Collections/Receivables?handler=Results&status=all&search={Uri.EscapeDataString(term)}")).Content.ReadAsStringAsync();
            Assert.Contains("data-unit-row", html);
        }
        Assert.DoesNotContain("status=", string.Concat(api.Calls("/receivables/instalments")));
        var none = await (await client.GetAsync("/Collections/Receivables?handler=Results&search=NoSuchCustomer")).Content.ReadAsStringAsync();
        Assert.DoesNotContain("data-unit-row", none); Assert.Contains("No units match these filters.", none);
    }

    [Fact]
    public async Task NoExplanations_NoMonthCards_NoDiagnostics()
    {
        var api = new FakeCollectionsApi();
        api.Rows.Add(FakeCollectionsApi.Unpaid(new DateOnly(2026, 8, 5), 300m, "INV-1"));
        using var factory = CollectionsWebHost.Factory(api); using var client = factory.CreateClient();
        var html = await (await client.GetAsync("/Collections/Receivables?handler=Results")).Content.ReadAsStringAsync();
        foreach (var gone in new[] { "receivables-metrics", "month-card", "unit-months", "Data details", "snapshot-status", "receivables-help", "How Due", "Payment status", "Not yet due", "Fully paid", "Upcoming" })
            Assert.DoesNotContain(gone, html);
        Assert.DoesNotContain("<html", html);                                                         // a fragment, not a page
    }

    [Fact]
    public async Task WhenSharjahCouldNotBeLoaded_OneShortLineNamesIt_WithRetryForThatCompany_AndDubaiRowsStillShow()
    {
        var api = new FakeCollectionsApi { SharjahFailed = true };
        api.Rows.Add(FakeCollectionsApi.Unpaid(new DateOnly(2026, 8, 5), 300m, "INV-1"));
        using var factory = CollectionsWebHost.Factory(api); using var client = factory.CreateClient();
        var html = await (await client.GetAsync("/Collections/Receivables?handler=Results")).Content.ReadAsStringAsync();
        var problem = Between(html, "<div class=\"receivables-notice receivables-notice--warning load-problem\"", "</form>");
        Assert.Contains("Could not load data for <strong>Tiger Group Sharjah</strong>.", problem);
        Assert.DoesNotContain("Dubai", problem);                                                      // only the failing company is named
        Assert.Contains("name=\"companyId\" value=\"32\"", problem);                                  // Retry reloads Sharjah only
        Assert.Contains(">Retry<", problem); Assert.Contains("data-load-button", problem);
        Assert.Single(Regex.Matches(html, "data-load-problem"));                               // one message, not one per gap
        Assert.Contains("data-unit-row", html);
        foreach (var gone in new[] { "Data details", "error 7399", "consecutive", "Last refresh attempt", "Export is disabled", "snapshot-status__table" }) Assert.DoesNotContain(gone, html);
    }

    [Fact]
    public async Task WhileTheRetryRuns_TheLineSaysSoAndThePageKeepsPolling()
    {
        var api = new FakeCollectionsApi { SharjahFailed = true, Loading = true };
        using var factory = CollectionsWebHost.Factory(api); using var client = factory.CreateClient();
        var html = await (await client.GetAsync("/Collections/Receivables?handler=Results")).Content.ReadAsStringAsync();
        Assert.Contains("data-load-running", html); Assert.DoesNotContain("data-load-button", html);
    }

    [Fact]
    public async Task WhileOneCompanyLoads_TheOtherFailedCompanyStillGetsItsRetry()
    {
        // Dubai is really being loaded; Sharjah's last refresh failed and nothing is running for it: it must not be hidden behind "Loading...".
        var api = new FakeCollectionsApi { SharjahFailed = true, Stale = true, DubaiLoadingOnly = true };
        using var factory = CollectionsWebHost.Factory(api); using var client = factory.CreateClient();
        var html = await (await client.GetAsync("/Collections/Receivables?handler=Results")).Content.ReadAsStringAsync();
        Assert.Contains("data-load-running", html);
        Assert.Contains("Loading data for Tiger Group Dubai…", html);
        Assert.Contains("Could not load data for <strong>Tiger Group Sharjah</strong>.", html);
        Assert.Contains("name=\"companyId\" value=\"32\"", html);
        Assert.Contains("data-load-button", html);
    }

    [Fact]
    public async Task Retry_PostsTheCompanyToTheApi_AndComesBackToTheSameFilters()
    {
        var api = new FakeCollectionsApi { SharjahFailed = true };
        using var factory = CollectionsWebHost.Factory(api); using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var page = await (await client.GetAsync("/Collections/Receivables?render=full&status=overdue")).Content.ReadAsStringAsync();
        var token = Regex.Match(page, "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"").Groups[1].Value;
        Assert.True(token.Length > 20);
        var post = await client.PostAsync("/Collections/Receivables?handler=LoadCoverage", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["dateFrom"] = "2000-01-01", ["dateTo"] = "2099-12-31", ["companyId"] = "32", ["returnUrl"] = "/Collections/Receivables?status=overdue", ["__RequestVerificationToken"] = token
        }));
        Assert.Equal(HttpStatusCode.Redirect, post.StatusCode);
        Assert.Equal("/Collections/Receivables?status=overdue&load=started", post.Headers.Location!.OriginalString);
        Assert.Contains("POST /api/collections/receivables/coverage/load?dateFrom=2000-01-01&dateTo=2099-12-31&companyId=32", api.Requests);
    }

    [Fact]
    public async Task RenderFull_IsTheNoJavaScriptFallback_AndIncludesTheResults()
    {
        var api = new FakeCollectionsApi();
        api.Rows.Add(FakeCollectionsApi.Unpaid(new DateOnly(2026, 8, 5), 300m, "INV-1"));
        using var factory = CollectionsWebHost.Factory(api); using var client = factory.CreateClient();
        var html = await (await client.GetAsync("/Collections/Receivables?render=full")).Content.ReadAsStringAsync();
        Assert.Contains("Example Customer", html);
        Assert.DoesNotContain("data-results-loading", html);
        Assert.Contains("data-unit-toggle", html);                                                    // View Details is the button of every row
        Assert.Contains("View Details", html);
    }

    [Theory]
    [InlineData(HttpStatusCode.Forbidden, "does not have permission")]
    [InlineData(HttpStatusCode.ServiceUnavailable, "could not be loaded")]
    public async Task Errors_AreExplicitInTheResultsArea_NeverAnEmptyList(HttpStatusCode status, string text)
    {
        var api = new FakeCollectionsApi { Status = status };
        using var factory = CollectionsWebHost.Factory(api); using var client = factory.CreateClient();
        var html = await (await client.GetAsync("/Collections/Receivables?handler=Results")).Content.ReadAsStringAsync();
        Assert.Contains(text, html);
        Assert.DoesNotContain("receivables-table", html);
        Assert.DoesNotContain("No units match", html);
    }

    [Theory]
    [InlineData("2026-09-30", "Overdue", 9)]    // the day before the month starts
    [InlineData("2026-10-01", "Due", 8)]        // the first day of the month
    [InlineData("2026-10-08", "Due", 1)]        // earlier this month: Due, and already late by a day
    [InlineData("2026-10-09", "Due", null)]
    [InlineData("2026-10-31", "Due", null)]     // the last day of the month, still to come
    public void TheStatusDependsOnlyOnTheDueDateAgainstTheCurrentMonth(string due, string expected, int? daysLate)
    {
        var date = DateOnly.Parse(due);
        Assert.Equal(expected, Receivables.Classify(date, Today));
        Assert.Equal(daysLate, Receivables.DaysLate(date, Today));
    }

    [Fact]
    public async Task TheStatusFilter_DefaultsToDuePlusOverdue_AndOffersEachStateAlone()
    {
        var api = new FakeCollectionsApi();
        using var factory = CollectionsWebHost.Factory(api); using var client = factory.CreateClient();
        var html = await (await client.GetAsync("/Collections/Receivables")).Content.ReadAsStringAsync();
        var select = Between(html, "id=\"receivableStatus\"", "</select>");
        Assert.Equal(["Due + Overdue", "Due", "Overdue"], Regex.Matches(select, "<option[^>]*>(.*?)</option>").Select(m => WebUtility.HtmlDecode(m.Groups[1].Value)));
        Assert.Matches("<option value=\"all\" selected=\"selected\">Due (\\+|&#x2B;) Overdue</option>", select);          // the default shows everything due before the end of the current month
    }

    [Fact]
    public async Task TheResults_SayWhenTheDataWasLastUpdated_InDubaiTime()
    {
        var api = new FakeCollectionsApi();
        api.Rows.Add(FakeCollectionsApi.Unpaid(new DateOnly(2026, 8, 5), 300m, "INV-1"));
        using var factory = CollectionsWebHost.Factory(api); using var client = factory.CreateClient();
        var html = await (await client.GetAsync("/Collections/Receivables?handler=Results")).Content.ReadAsStringAsync();
        Assert.Matches(@"data-updated>Updated \d{2} \w{3} \d{4} \d{2}:\d{2} \(Dubai\)<", html);
    }

    [Fact]
    public async Task WhenNothingHasBeenLoaded_ThePageSaysLoadingOrFailed_AndShowsNoTableAndNoZero()
    {
        foreach (var loading in new[] { true, false })
        {
            var api = new FakeCollectionsApi { NothingLoaded = true, Loading = loading };
            using var factory = CollectionsWebHost.Factory(api); using var client = factory.CreateClient();
            var html = await (await client.GetAsync("/Collections/Receivables?handler=Results")).Content.ReadAsStringAsync();
            Assert.Contains(loading ? "data-load-running" : "data-load-problem", html);          // a clear Loading or could-not-load state, with Retry in the second case
            Assert.DoesNotContain("receivables-table", html);                                    // never an empty table that reads as "no receivables"
            Assert.DoesNotContain("No units", html); Assert.DoesNotContain("0.00", html);
            if (!loading) Assert.Contains("Retry", html);
        }
    }
}
