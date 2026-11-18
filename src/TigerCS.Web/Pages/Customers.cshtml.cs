using System.Text.Json;
using TigerCS.Application.Modules.Ticketing.CustomerNames;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using TigerCS.Domain.Modules.IdentityAndAccess.Permissions;
using TigerCS.Application.Modules.Ticketing.Dto;
using TigerCS.Domain.Modules.Ticketing;
using TigerCS.Web.Models;
using TigerCS.Web.Services;
using TigerCS.Web.Services.Api;
using TigerCS.Web.Services.Auth;

namespace TigerCS.Web.Pages;

/// <summary>
/// The Customers directory (`/Customers`): a paginated list of every
/// customer TigerCS already knows — derived by the Api from its own
/// persisted tickets, deduplicated by CRM Buyer id, then external-verification
/// identity, then intake phone — shown immediately, no search required.
/// Search and the filters narrow that list; the cross-source phone lookup
/// (Tiger CRM / PACT / Tasleeh) for customers with no ticket yet lives at
/// `/Customers/Lookup`. Every row opens the Customer Profile.
/// </summary>
public sealed class CustomersModel(
    CustomersApiClient customersApiClient, TicketNameResolver nameResolver, ViewerAccessService viewerAccess, ProjectCatalogApiClient projectCatalogApiClient) : PageModel
{
    public const int DefaultPageSize = 25;

    /// <summary>The verification-source filter options: the directory's identity kinds/sources, by the label the rest of the app uses.</summary>
    public static readonly IReadOnlyList<(string Value, string Label)> SourceOptions =
    [
        ("Crm", "Tiger CRM"),
        ("Pact", "PACT"),
        ("Tasleeh", "Tasleeh"),
        ("Unverified", "Unverified (phone only)"),
    ];

    public string? Search { get; private set; }
    public string? VerificationSource { get; private set; }
    public int? DepartmentId { get; private set; }
    public bool OpenOnly { get; private set; }
    public int PageNumber { get; private set; } = 1;
    public int PageSize { get; private set; } = DefaultPageSize;

    /// <summary>The Dashboard's Customers by Location filter ("within-uae", "outside-uae", "unknown"), when the list was opened from that card.</summary>
    public CustomerLocation? Location { get; private set; }

    /// <summary>Why the Unknown customers are Unknown (only for the Unknown list, and only when that read succeeded).</summary>
    public CustomerLocationBreakdownDto? UnknownBreakdown { get; private set; }

    /// <summary>The Dashboard's own filters, carried from the card so the list shows exactly the customers the card counted. Null when the list was not opened from the Dashboard.</summary>
    public CustomerDirectoryListRequestDto? DashboardScope { get; private set; }

    public string? LocationLabel => Location is { } location ? CustomerLocationKeys.Label(location) : null;

    /// <summary>The dashboard period the list is limited to, as the card showed it ("Aug 12 – Sep 10, 2026"), or null.</summary>
    public string? DashboardPeriodLabel => DashboardScope is { DateFrom: { } from, DateTo: { } to } ? $"{from:MMM d} – {to:MMM d, yyyy}" : null;

    public bool HasFilters => !string.IsNullOrWhiteSpace(Search) || VerificationSource is not null || DepartmentId is not null || OpenOnly || Location is not null || DashboardScope is not null;

    public ApiOutcome Outcome { get; private set; } = ApiOutcome.Success;
    public IReadOnlyList<CustomerDirectoryRowDto> Rows { get; private set; } = [];
    public int TotalCount { get; private set; }
    public int TotalPages => TotalCount == 0 ? 1 : (int)Math.Ceiling(TotalCount / (double)PageSize);

    public TicketNameResolver NameResolver => nameResolver;
    public CurrentUser? Viewer { get; private set; }
    public bool CanCreateTicket { get; private set; }

    /// <summary>May the viewer edit a customer's phone, project and unit (<c>Customers.Edit</c>: System Administrator and CS Manager, or an explicit grant)? Hides the Edit action otherwise; the Api enforces it regardless.</summary>
    public bool CanEdit { get; private set; }

    /// <summary>The known project names, offered as suggestions in the Edit form (the Api still validates what is entered). Empty unless the viewer can edit.</summary>
    public IReadOnlyList<string> ProjectOptions { get; private set; } = [];

    /// <summary>Outcome of the last Edit, carried across the redirect.</summary>
    public string? EditStatus { get; private set; }
    public string? EditError { get; private set; }

    /// <summary>The rejected Edit's customer and typed values (JSON), so the form reopens with them and the error beside it.</summary>
    public string? EditReopenJson { get; private set; }

    /// <summary>The phone-lookup page, carrying the current search text forward when it looks like a phone number.</summary>
    public string LookupHref =>
        !string.IsNullOrWhiteSpace(Search) && Search.Any(char.IsDigit)
            ? $"/Customers/Lookup?phoneNumber={Uri.EscapeDataString(Search.Trim())}"
            : "/Customers/Lookup";

    public async Task OnGetAsync(
        string? search, string? phoneNumber, string? verificationSource, int? departmentId, bool openOnly, int page, int pageSize,
        CancellationToken cancellationToken, string? location = null, DateOnly? dateFrom = null, DateOnly? dateTo = null,
        Guid? ownerEmployeeId = null, byte? channelId = null, int? requestTypeId = null, string? ticketStatus = null, byte? priorityId = null)
    {
        Viewer = CurrentUser.FromPrincipal(User);
        CanCreateTicket = TicketCreationPolicy.AppliesTo(Viewer);
        CanEdit = (await viewerAccess.GetAsync(cancellationToken)).HoldsEffective(Permissions.CustomersEdit, Viewer?.Roles ?? []);
        EditStatus = TempData["CustomerEditStatus"] as string;
        EditError = TempData["CustomerEditError"] as string;
        EditReopenJson = TempData["CustomerEditReopen"] as string;
        if (CanEdit)
        {
            var projects = await projectCatalogApiClient.ListProjectsAsync(cancellationToken);
            ProjectOptions = projects.IsSuccess && projects.Value is not null ? projects.Value.Select(p => p.Name).ToList() : [];
        }

        // The Dashboard's customer search still posts `phoneNumber`; here it
        // is simply a search term over the directory.
        Search = string.IsNullOrWhiteSpace(search) ? (string.IsNullOrWhiteSpace(phoneNumber) ? null : phoneNumber.Trim()) : search.Trim();
        VerificationSource = SourceOptions.Any(o => string.Equals(o.Value, verificationSource, StringComparison.OrdinalIgnoreCase))
            ? SourceOptions.First(o => string.Equals(o.Value, verificationSource, StringComparison.OrdinalIgnoreCase)).Value
            : null;
        DepartmentId = departmentId;
        OpenOnly = openOnly;
        Location = CustomerLocationKeys.Parse(location);
        var openedFromDashboard = dateFrom is not null || dateTo is not null || ownerEmployeeId is not null || channelId is not null
            || requestTypeId is not null || !string.IsNullOrWhiteSpace(ticketStatus) || priorityId is not null;
        DashboardScope = openedFromDashboard
            ? new CustomerDirectoryListRequestDto(
                DepartmentId: departmentId, DateFrom: dateFrom, DateTo: dateTo, OwnerEmployeeId: ownerEmployeeId, ChannelId: channelId,
                RequestTypeId: requestTypeId, TicketStatus: string.IsNullOrWhiteSpace(ticketStatus) ? null : ticketStatus, PriorityId: priorityId)
            : null;
        PageNumber = page < 1 ? 1 : page;
        PageSize = pageSize is < 1 or > 100 ? DefaultPageSize : pageSize;

        RememberContext();

        await nameResolver.PrimeDepartmentsAsync(cancellationToken);

        var result = await customersApiClient.ListAsync(
            new CustomerDirectoryListRequestDto(
                Search, VerificationSource, DepartmentId, OpenOnly, PageNumber, PageSize, CustomerLocationKeys.ToKey(Location),
                DashboardScope?.DateFrom, DashboardScope?.DateTo, DashboardScope?.OwnerEmployeeId, DashboardScope?.ChannelId,
                DashboardScope?.RequestTypeId, DashboardScope?.TicketStatus, DashboardScope?.PriorityId),
            cancellationToken);
        Outcome = result.Outcome;
        if (result.IsSuccess && result.Value is not null)
        {
            Rows = result.Value.Items;
            TotalCount = result.Value.TotalCount;
            if (Location == CustomerLocation.Unknown)
            {
                // Why these customers are Unknown, counted over the whole list (not just this page). A failed read just omits the panel.
                var breakdown = await customersApiClient.GetUnknownBreakdownAsync(
                    new CustomerDirectoryListRequestDto(
                        Search, VerificationSource, DepartmentId, OpenOnly, 1, DefaultPageSize, CustomerLocationKeys.ToKey(Location),
                        DashboardScope?.DateFrom, DashboardScope?.DateTo, DashboardScope?.OwnerEmployeeId, DashboardScope?.ChannelId,
                        DashboardScope?.RequestTypeId, DashboardScope?.TicketStatus, DashboardScope?.PriorityId),
                    cancellationToken);
                UnknownBreakdown = breakdown.IsSuccess ? breakdown.Value : null;
            }
        }
    }

    /// <summary>
    /// Saves the Edit form. The Api does everything that matters - who may edit, the phone's normalization, the project / unit check, the
    /// audit - so this only forwards the values and carries the outcome back to the list the user was on (the same search, filters and page).
    /// A rejected edit reopens the form with what was typed and the reasons.
    /// </summary>
    public async Task<IActionResult> OnPostEditAsync(
        string customerKey, string? phoneNumber, string? projectName, string? unitNumber, string? returnUrl, CancellationToken cancellationToken)
    {
        var result = await customersApiClient.UpdateProfileAsync(
            customerKey, new UpdateCustomerProfileRequestDto(phoneNumber, projectName, unitNumber), cancellationToken);

        if (result.IsSuccess)
        {
            TempData["CustomerEditStatus"] = result.Value is { Changed: false } ? "Nothing to change." : "Customer updated.";
        }
        else
        {
            TempData["CustomerEditError"] = result.Outcome switch
            {
                ApiOutcome.ValidationError => result.Detail ?? "The values are not valid.",
                ApiOutcome.Forbidden or ApiOutcome.Unauthorized => "You are not allowed to edit customers.",
                ApiOutcome.NotFound => "That customer is no longer in the directory.",
                ApiOutcome.Unreachable or ApiOutcome.BadGateway => "Tiger Ticketing System could not reach the ticketing service.",
                _ => result.Detail ?? "The customer could not be saved.",
            };
            TempData["CustomerEditReopen"] = JsonSerializer.Serialize(new { key = customerKey, phone = phoneNumber, project = projectName, unit = unitNumber });
        }

        // Back to the list as it was - local URLs under /Customers only.
        return LocalRedirect(!string.IsNullOrEmpty(returnUrl) && Url.IsLocalUrl(returnUrl) && returnUrl.StartsWith("/Customers", StringComparison.Ordinal)
            ? returnUrl
            : "/Customers");
    }

    /// <summary>The list as the viewer has it now (path and query), posted back with the Edit form so they return to the same search, filters and page.</summary>
    public string CurrentUrl => $"{Request.Path}{Request.QueryString}";

    /// <summary>Remembers this list (search, filters, page) so a Customer Profile can lead back to it.</summary>
    private void RememberContext()
    {
        var query = Request.Query.ToList();
        if (Search is not null && !Request.Query.ContainsKey("search"))
        {
            query.Add(new KeyValuePair<string, Microsoft.Extensions.Primitives.StringValues>("search", Search));
        }

        Response.Cookies.Append(
            CustomersContext.CookieName,
            CustomersContext.ToCookieValue(query),
            new CookieOptions { HttpOnly = true, SameSite = SameSiteMode.Strict, Secure = Request.IsHttps, IsEssential = true, Path = "/" });
    }

    public string PageUrl(int page)
    {
        var query = System.Web.HttpUtility.ParseQueryString(string.Empty);
        if (Search is not null) query["search"] = Search;
        if (VerificationSource is not null) query["verificationSource"] = VerificationSource;
        if (DepartmentId is { } departmentId) query["departmentId"] = departmentId.ToString();
        if (OpenOnly) query["openOnly"] = "true";
        foreach (var (name, value) in DashboardQuery()) query[name] = value;
        query["page"] = page.ToString();
        if (PageSize != DefaultPageSize) query["pageSize"] = PageSize.ToString();
        return $"/Customers?{query}";
    }

    /// <summary>The location and Dashboard-filter query values this list was opened with — re-emitted by the paging links and the filter form so neither silently drops them.</summary>
    public IEnumerable<(string Name, string Value)> DashboardQuery()
    {
        if (CustomerLocationKeys.ToKey(Location) is { } locationKey) yield return ("location", locationKey);
        if (DashboardScope is not { } scope) yield break;
        if (scope.DateFrom is { } from) yield return ("dateFrom", from.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture));
        if (scope.DateTo is { } to) yield return ("dateTo", to.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture));
        if (scope.OwnerEmployeeId is { } owner) yield return ("ownerEmployeeId", owner.ToString());
        if (scope.ChannelId is { } channel) yield return ("channelId", channel.ToString(System.Globalization.CultureInfo.InvariantCulture));
        if (scope.RequestTypeId is { } requestType) yield return ("requestTypeId", requestType.ToString(System.Globalization.CultureInfo.InvariantCulture));
        if (!string.IsNullOrWhiteSpace(scope.TicketStatus)) yield return ("ticketStatus", scope.TicketStatus!);
        if (scope.PriorityId is { } priority) yield return ("priorityId", priority.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    public static string ProfileHref(string customerKey) => $"/Customers/{Uri.EscapeDataString(customerKey)}";

    public static string SourceLabel(string verificationSource) => verificationSource switch
    {
        "Unverified" => "Unverified",
        _ => TicketDisplay.LookupSourceLabel(verificationSource),
    };

    public static string SourceCssKey(string verificationSource) => verificationSource switch
    {
        "Crm" => "verified",
        "Unverified" => "unverified",
        _ => "external",
    };

    /// <summary>The customer's display name, or an honest placeholder naming the identity we do have — never a raw id dressed as a name.</summary>
    public static string NameOrPlaceholder(CustomerDirectoryRowDto row) =>
        CustomerNameResolver.FirstValid(row.DisplayName) is { } name ? name
        : row.IdentityKind == "Phone" ? "Unnamed caller"
        : $"{SourceLabel(row.VerificationSource)} customer";
}
