extern alias TigerCsWeb;

using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TigerCS.Application.Modules.Collections.Dto;
using TigerCS.Domain.Modules.IdentityAndAccess;

namespace TigerCS.Tests.Web;

/// <summary>Shared fake of TigerCS.Api for the Collections page tests (no PACT, no SQL: only the API contract the pages consume).</summary>
internal sealed class FakeCollectionsApi : HttpMessageHandler
{
    /// <summary>The campaign preview also lists a unit the stage rule does not make eligible (status NotEligible) next to a ready one.</summary>
    public bool IncludeNotEligible { get; set; }
    public HttpStatusCode Status { get; set; } = HttpStatusCode.OK;
    public bool Covered { get; set; } = true;
    public bool Loading { get; set; }
    /// <summary>Only Tiger Group Dubai (company 4) is being loaded right now; Sharjah is not.</summary>
    public bool DubaiLoadingOnly { get; set; }
    public bool NothingLoaded { get; set; }
    /// <summary>The campaign preview reports that the financial source is not reconciled against PACT.</summary>
    public bool SourceUnreconciled { get; set; }
    /// <summary>Settings the readiness endpoint reports as blocking (empty = Genesys sending is configured).</summary>
    public List<string> DispatchBlockers { get; } = [];
    /// <summary>The caller lacks the export / send permission (the preview's CanExportReview is false).</summary>
    public bool ExportForbidden { get; set; }
    public bool Stale { get; set; }
    /// <summary>The preview also reports the outstanding balance of the eligible customers (null = not reported, so no such card).</summary>
    public decimal? OutstandingAmount { get; set; }
    /// <summary>The API reports that no scheduled refresh runs (BackgroundJobs:Enabled is false).</summary>
    public bool ScheduleOff { get; set; }
    public bool PaidRetained { get; set; } = true;
    public bool Breakdown { get; set; }
    /// <summary>Tiger Group Sharjah (company 32) never loaded: its latest refresh failed, so the Dubai rows are served without it.</summary>
    public bool SharjahFailed { get; set; }
    /// <summary>What GET api/collections/reconciliation answers (null = nothing has been run).</summary>
    public TigerCS.Application.Modules.Collections.Reconciliation.ReconciliationOverviewDto? Reconciliation { get; set; }
    public List<string> Requests { get; } = [];
    public List<PactInstalmentRowDto> Rows { get; } = [];
    /// <summary>The tower catalogue the fake API serves (null = the two default towers 7 and 8).</summary>
    public List<CollectionsTowerDto>? TowerCatalogue { get; set; }
    /// <summary>What GET api/collections/tower-assignments answers (null = an empty month the viewer may manage).</summary>
    public TigerCS.Application.Modules.Collections.TowerAssignments.TowerAssignmentMonthDto? TowerMonth { get; set; }

    public IEnumerable<string> Calls(string path) => Requests.Where(r => r.Contains(path, StringComparison.Ordinal));

    public SnapshotStatusDto Snapshot(DateOnly from, DateOnly through)
    {
        var now = DateTime.UtcNow;
        var company = NothingLoaded
            ? new SnapshotCompanyStatusDto(4, "Tiger Group Dubai", false, null, null, "Never", null, 0, 0, null, null, 0, 0m, 0, 0m, 0, 0, "Missing", null, Loading || DubaiLoadingOnly)
            : new SnapshotCompanyStatusDto(4, "Tiger Group Dubai", true, now.AddMinutes(Stale ? -300 : -5), now.AddMinutes(Stale ? -300 : -5), "Succeeded", null, 0, 50,
                new DateOnly(2026, 1, 1), new DateOnly(2099, 12, 31), 0, 0m, 0, 0m, 0, 0, Stale ? "Stale" : "Fresh", Stale ? 300 : 5, Loading || DubaiLoadingOnly, null, null, PaidRetained, Breakdown, Breakdown ? 0 : 3);
        var gaps = NothingLoaded ? [new CoverageGapDto(4, "Tiger Group Dubai", from, through)]
            : Covered ? [] : (IReadOnlyList<CoverageGapDto>)[new CoverageGapDto(4, "Tiger Group Dubai", from, new DateOnly(2025, 12, 31))];
        var schedule = ScheduleOff ? (false, "Scheduled refresh is OFF on this server (BackgroundJobs:Enabled is false): the data only changes when someone presses Retry.") : ((bool?)null, (string?)null);
        if (!SharjahFailed) return new SnapshotStatusDto([company], [], 90, from, through, gaps, schedule.Item1, schedule.Item2);
        var sharjah = new SnapshotCompanyStatusDto(32, "Tiger Group Sharjah", false, null, now.AddMinutes(-2), "Failed", 7399, 3, 0, null, null, 0, 0m, 0, 0m, 0, 0, "Missing", null, Loading);
        return new SnapshotStatusDto([company, sharjah], [], 90, from, through, [.. gaps, new CoverageGapDto(32, "Tiger Group Sharjah", from, through)], schedule.Item1, schedule.Item2);
    }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Requests.Add(request.Method + " " + request.RequestUri!.PathAndQuery);
        var q = System.Web.HttpUtility.ParseQueryString(request.RequestUri.Query);
        switch (request.RequestUri.AbsolutePath)
        {
            case "/api/collections/receivables/towers":
                return Json(TowerCatalogue ?? [new(7, "124", "Tower 124", 4, true), new(8, "127", "Faradis", 32, true)]);
            case "/api/collections/tower-assignments":
                return Json(TowerMonth ?? new TigerCS.Application.Modules.Collections.TowerAssignments.TowerAssignmentMonthDto(2026, 11, true, [], [], [], 0));
            case "/api/collections/tower-assignments/history":
                return Json(new List<TigerCS.Application.Modules.Collections.TowerAssignments.TowerAssignmentHistoryDto>());
            case "/api/collections/reconciliation":
                return Json(Reconciliation ?? new TigerCS.Application.Modules.Collections.Reconciliation.ReconciliationOverviewDto(
                    new(false, "EvidenceNotRun", "No automated reconciliation has been run for Tiger Group Dubai and Tiger Group Sharjah.", null, 0, true, null), null, []));
            case "/api/collections/reconciliation/run":
                return Json(new TigerCS.Application.Modules.Collections.Reconciliation.ReconciliationRunDto(Guid.NewGuid(), "Running", DateTime.UtcNow, null, new(2026, 1, 1), new(2026, 10, 31), null, 0.01m, null, []), HttpStatusCode.Accepted);
            case "/api/collections/receivables/snapshot/status":
            {
                var today = new DateOnly(2026, 10, 9);
                return Json(Snapshot(today, today));
            }
            case "/api/collections/receivables/coverage/load":
                return Json(new ReceivablesRangeLoadDto(true, false, false, "started"), HttpStatusCode.Accepted);
            case "/api/collections/receivables/instalments":
            {
                if (Status != HttpStatusCode.OK) return Task.FromResult(new HttpResponseMessage(Status));
                var from = DateOnly.Parse(q["dateFrom"] ?? "2026-01-01"); var to = DateOnly.Parse(q["dateTo"] ?? "2026-10-31");
                var filter = q["paymentStatus"] ?? "outstanding";
                var min = decimal.Parse(q["minAmount"] ?? "100", System.Globalization.CultureInfo.InvariantCulture);
                var all = Rows.ToList();
                var months = all.GroupBy(r => (r.DueDate.Year, r.DueDate.Month)).OrderBy(g => g.Key).Select(g => new PactInstalmentMonthDto(g.Key.Year, g.Key.Month, g.Count(),
                    g.Sum(r => r.RemainingAmount), g.Count(r => r.Classification == "Overdue"), g.Where(r => r.Classification == "Overdue").Sum(r => r.RemainingAmount))).ToList();
                var dueMonth = q["dueMonth"];
                var rows = dueMonth is null ? all : all.Where(r => r.DueDate.ToString("yyyy-MM") == dueMonth).ToList();
                // Unit view only: the units having an instalment of the chosen status (today = 2026-10-09, Dubai; Overdue: before 1 Oct, Due: inside October), with ALL of their instalments.
                var monthStart = new DateOnly(2026, 10, 1);
                if (q["view"] == "units" && q["status"] is { Length: > 0 } wanted)
                    rows = rows.GroupBy(r => (r.CompanyId, r.TenantId, r.UnitId, r.UnitCode))
                        .Where(g => g.Any(r => wanted switch { "overdue" => r.DueDate < monthStart, "due" => r.DueDate >= monthStart && r.DueDate < monthStart.AddMonths(1), _ => true }))
                        .SelectMany(g => g).ToList();
                if (!string.IsNullOrWhiteSpace(q["search"]))
                    rows = rows.Where(r => new[] { r.CustomerName, r.Mobile, r.Email, r.UnitCode, r.TowerNumber ?? "", r.TowerName ?? "" }.Any(v => v.Contains(q["search"]!, StringComparison.OrdinalIgnoreCase))).ToList();
                var totals = new PactInstalmentTotalsDto(rows.Count, rows.Sum(r => r.RemainingAmount), rows.Count(r => r.Classification == "Overdue"), rows.Where(r => r.Classification == "Overdue").Sum(r => r.RemainingAmount),
                    rows.Count(r => r.Classification == "Due"), rows.Where(r => r.Classification == "Due").Sum(r => r.RemainingAmount), 0, 0m, rows.Count(r => r.PaymentStatus == "FullyPaid"));
                var snapshot = Snapshot(from, to);
                var byUnit = q["view"] == "units";
                var units = byUnit ? rows.GroupBy(r => (r.CompanyId, r.TenantId, r.UnitId, r.UnitCode)).Select(g => new PactInstalmentUnitDto(g.Key.CompanyId, g.First().CompanyName, g.First().TowerNumber,
                    g.First().TowerName, g.Key.UnitId ?? 0, g.Key.UnitCode, g.Key.TenantId, g.First().CustomerName, g.Count(), g.Sum(r => r.RemainingAmount), g.Min(r => r.DueDate), g.ToList())).ToList() : null;
                if (byUnit) totals = totals with { UnitCount = units!.Count };
                return Json(new PactInstalmentsPageDto(new DateOnly(2026, 10, 9), new DateOnly(2026, 10, 1), new DateOnly(2026, 10, 31), from, to, int.TryParse(q["towerId"], out var t) ? t : null,
                    filter, min, filter is not ("paid" or "all"), "AED", totals, int.Parse(q["page"] ?? "1"), 25, rows, snapshot,
                    new PaymentViewAvailabilityDto(true, snapshot.BreakdownAvailable, snapshot.BreakdownAvailable, snapshot.PaidRetained, snapshot.PaidRetained, snapshot.UnclassifiedRows),
                    ["Payment status and Due/Overdue are independent: an instalment can be partially paid and overdue at the same time."], DateTime.UtcNow,
                    new ServerTimingsDto(12, 3, 20), byUnit ? "units" : "instalments", units, months, dueMonth));
            }
            case "/api/collections/review/runs/current":
                return Json<TigerCS.Application.Modules.Collections.Review.ReviewRunDto?>(null);
            case "/api/collections/review/dispatches/readiness":
                return Json(new TigerCS.Application.Modules.Collections.Review.DispatchReadinessDto(DispatchBlockers.Count == 0, DispatchBlockers));
            case "/api/collections/review/dispatches":
                return Json(new List<TigerCS.Application.Modules.Collections.Review.DispatchDto>());
            case "/api/collections/campaigns/export":
                if (Status != HttpStatusCode.OK) return Task.FromResult(new HttpResponseMessage(Status));
                return Json(new CollectionsCampaignExportDto("campaign-review.csv", "CustomerName\r\nExample\r\n", 1));
            default:   // campaign preview
            {
                if (Status != HttpStatusCode.OK) return Task.FromResult(new HttpResponseMessage(Status));
                var from = DateOnly.Parse(q["dateFrom"] ?? "2026-01-01"); var to = DateOnly.Parse(q["dateTo"] ?? "2026-10-31");
                var date = new DateOnly(2026, 10, 14);
                if (IncludeNotEligible && !NothingLoaded)
                    return Json(new CollectionsCampaignPreviewDto(date, date, DateTime.UtcNow, "PACT", "CurrentMonthReminder", "2026-10:CurrentMonthReminder", [date], true, !SourceUnreconciled, false, !ExportForbidden,
                        2, 1, 0, 1, 25,
                        [new("ID", "ext:Pact:3001", 4, "3001", "Campaign Customer", "+971500003001", "c@example.test", 101, "TP140-101", "TP140", 500m, "AED", date, "CurrentMonthReminder", "2026-10:CurrentMonthReminder", "Ready", "Qualifies", "140", "Al Ghaf", 0m, 650m),
                         new("ID2", "ext:Pact:3002", 4, "3002", "Not Eligible Customer", "+971500003002", "n@example.test", 102, "TP140-102", "TP140", 0m, "AED", null, "CurrentMonthReminder", "2026-10:CurrentMonthReminder", "NotEligible", "NoQualifyingBalance", "140", "Al Ghaf", 300m, 0m)],
                        from, to, [], null, Snapshot(from, to), 100m, new ServerTimingsDto(9, 2, 15), null, 1, q["paymentStatus"] ?? "Both", 650m, OutstandingAmount));
                return Json(new CollectionsCampaignPreviewDto(date, date, DateTime.UtcNow, "PACT", "CurrentMonthReminder", "2026-10:CurrentMonthReminder", [date], true, !SourceUnreconciled, false, !ExportForbidden,
                    NothingLoaded ? 0 : 1, 0, NothingLoaded ? 0 : 1, 1, 25,
                    NothingLoaded ? [] : [new("ID", "ext:Pact:3001", 4, "3001", "Campaign Customer", "+971500003001", "", 101, "TP140-101", "TP140", 500m, "AED", date, "CurrentMonthReminder", "2026-10:CurrentMonthReminder", "NeedsReview", "SourceReconciliationRequired", "140", "Al Ghaf", 0m, 650m)],
                    from, to, [], int.TryParse(q["towerId"], out var tower) ? tower : null, Snapshot(from, to),
                    decimal.Parse(q["minAmount"] ?? "100", System.Globalization.CultureInfo.InvariantCulture), new ServerTimingsDto(9, 2, 15), null, 0, q["paymentStatus"] ?? "Both", NothingLoaded ? 0m : 650m, OutstandingAmount));
            }
        }
    }

    private static Task<HttpResponseMessage> Json<T>(T value, HttpStatusCode status = HttpStatusCode.OK) =>
        Task.FromResult(new HttpResponseMessage(status) { Content = JsonContent.Create(value) });

    public static PactInstalmentRowDto Row(string status, string classification, decimal remaining, decimal? original = null, decimal? paid = null, string voucher = "INV-1", string unit = "TP124-1001") =>
        new(4, "Tiger Group Dubai", "124", "Tower 124", 101, unit, "3001", "Example Customer", "+971500003001", "x@example.test", voucher, "", new DateOnly(2026, 9, 15), original, paid, remaining, status, classification, "Installment");

    /// <summary>An unpaid instalment of the unit <paramref name="unit"/> (its own UnitId) due on <paramref name="due"/>.</summary>
    public static PactInstalmentRowDto Unpaid(DateOnly due, decimal remaining, string voucher, string unit = "TP124-1001", int unitId = 101, string customer = "Example Customer", string tenant = "3001") =>
        new(4, "Tiger Group Dubai", "124", "Tower 124", unitId, unit, tenant, customer, "+971500003001", "x@example.test", voucher, "", due, null, null, remaining, "Unknown", "Overdue", "Installment");
}

internal static class CollectionsWebHost
{
    public static WebApplicationFactory<TigerCsWeb::Program> Factory(HttpMessageHandler api) =>
        new WebApplicationFactory<TigerCsWeb::Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Development");
            builder.ConfigureTestServices(services =>
            {
                services.ConfigureAll<HttpClientFactoryOptions>(o => o.HttpMessageHandlerBuilderActions.Add(b => b.PrimaryHandler = api));
                services.AddAuthentication().AddScheme<AuthenticationSchemeOptions, TestAuth>("Test", _ => { });
                services.PostConfigure<AuthenticationOptions>(o => { o.DefaultAuthenticateScheme = "Test"; o.DefaultChallengeScheme = "Test"; });
            });
        });

    private sealed class TestAuth(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync() => Task.FromResult(
            AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(new ClaimsIdentity([
                new Claim(ClaimTypes.NameIdentifier, "6f1d2a40-8f0e-4c7b-9b57-0a1f3c2d4e5f"), new Claim(ClaimTypes.Name, "Manager"), new Claim(ClaimTypes.Role, Roles.CsManager)
            ], "Test")), "Test")));
    }
}
