using TigerCS.Application.Modules.Ticketing.CustomerNames;
using Microsoft.AspNetCore.Mvc.RazorPages;
using TigerCS.Application.Modules.Ticketing.Dto;
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
public sealed class CustomersModel(CustomersApiClient customersApiClient, TicketNameResolver nameResolver) : PageModel
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

    public bool HasFilters => !string.IsNullOrWhiteSpace(Search) || VerificationSource is not null || DepartmentId is not null || OpenOnly;

    public ApiOutcome Outcome { get; private set; } = ApiOutcome.Success;
    public IReadOnlyList<CustomerDirectoryRowDto> Rows { get; private set; } = [];
    public int TotalCount { get; private set; }
    public int TotalPages => TotalCount == 0 ? 1 : (int)Math.Ceiling(TotalCount / (double)PageSize);

    public TicketNameResolver NameResolver => nameResolver;
    public CurrentUser? Viewer { get; private set; }
    public bool CanCreateTicket { get; private set; }

    /// <summary>The phone-lookup page, carrying the current search text forward when it looks like a phone number.</summary>
    public string LookupHref =>
        !string.IsNullOrWhiteSpace(Search) && Search.Any(char.IsDigit)
            ? $"/Customers/Lookup?phoneNumber={Uri.EscapeDataString(Search.Trim())}"
            : "/Customers/Lookup";

    public async Task OnGetAsync(
        string? search, string? phoneNumber, string? verificationSource, int? departmentId, bool openOnly, int page, int pageSize,
        CancellationToken cancellationToken)
    {
        Viewer = CurrentUser.FromPrincipal(User);
        CanCreateTicket = TicketCreationPolicy.AppliesTo(Viewer);

        // The Dashboard's customer search still posts `phoneNumber`; here it
        // is simply a search term over the directory.
        Search = string.IsNullOrWhiteSpace(search) ? (string.IsNullOrWhiteSpace(phoneNumber) ? null : phoneNumber.Trim()) : search.Trim();
        VerificationSource = SourceOptions.Any(o => string.Equals(o.Value, verificationSource, StringComparison.OrdinalIgnoreCase))
            ? SourceOptions.First(o => string.Equals(o.Value, verificationSource, StringComparison.OrdinalIgnoreCase)).Value
            : null;
        DepartmentId = departmentId;
        OpenOnly = openOnly;
        PageNumber = page < 1 ? 1 : page;
        PageSize = pageSize is < 1 or > 100 ? DefaultPageSize : pageSize;

        RememberContext();

        await nameResolver.PrimeDepartmentsAsync(cancellationToken);

        var result = await customersApiClient.ListAsync(
            new CustomerDirectoryListRequestDto(Search, VerificationSource, DepartmentId, OpenOnly, PageNumber, PageSize),
            cancellationToken);
        Outcome = result.Outcome;
        if (result.IsSuccess && result.Value is not null)
        {
            Rows = result.Value.Items;
            TotalCount = result.Value.TotalCount;
        }
    }

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
        query["page"] = page.ToString();
        if (PageSize != DefaultPageSize) query["pageSize"] = PageSize.ToString();
        return $"/Customers?{query}";
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
