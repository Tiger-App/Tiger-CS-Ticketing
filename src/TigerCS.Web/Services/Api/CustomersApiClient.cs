using System.Globalization;
using System.Web;
using Microsoft.Extensions.Logging;
using TigerCS.Application.Modules.Ticketing.Dto;

namespace TigerCS.Web.Services.Api;

/// <summary>
/// Calls TigerCS.Api's Customers directory — <c>GET api/customers</c> (the
/// paginated list of customers TigerCS already knows, from its own persisted
/// tickets) and <c>GET api/customers/profile/{customerKey}</c> (one customer's
/// tickets, units, phones and interactions). Never a CRM call.
/// </summary>
public sealed class CustomersApiClient(HttpClient httpClient, ILogger<CustomersApiClient> logger) : ApiClientBase(httpClient, logger)
{
    public Task<ApiResult<CustomerDirectoryListResultDto>> ListAsync(CustomerDirectoryListRequestDto request, CancellationToken cancellationToken) =>
        GetAsync<CustomerDirectoryListResultDto>($"api/customers?{BuildQuery(request)}", cancellationToken);

    /// <summary>The Unknown customers of the same query, counted by why they are Unknown.</summary>
    public Task<ApiResult<CustomerLocationBreakdownDto>> GetUnknownBreakdownAsync(CustomerDirectoryListRequestDto request, CancellationToken cancellationToken) =>
        GetAsync<CustomerLocationBreakdownDto>($"api/customers/unknown-breakdown?{BuildQuery(request)}", cancellationToken);

    private static System.Collections.Specialized.NameValueCollection BuildQuery(CustomerDirectoryListRequestDto request)
    {
        var query = HttpUtility.ParseQueryString(string.Empty);
        if (!string.IsNullOrWhiteSpace(request.Search)) query["search"] = request.Search;
        if (!string.IsNullOrWhiteSpace(request.VerificationSource)) query["verificationSource"] = request.VerificationSource;
        if (request.DepartmentId is int departmentId) query["departmentId"] = departmentId.ToString(CultureInfo.InvariantCulture);
        if (request.OpenOnly) query["openOnly"] = "true";
        if (!string.IsNullOrWhiteSpace(request.Location)) query["location"] = request.Location;
        // The Dashboard's own filters, when the list was opened from one of its cards.
        if (request.DateFrom is { } dateFrom) query["dateFrom"] = dateFrom.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        if (request.DateTo is { } dateTo) query["dateTo"] = dateTo.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        if (request.OwnerEmployeeId is { } ownerEmployeeId) query["ownerEmployeeId"] = ownerEmployeeId.ToString();
        if (request.ChannelId is { } channelId) query["channelId"] = channelId.ToString(CultureInfo.InvariantCulture);
        if (request.RequestTypeId is { } requestTypeId) query["requestTypeId"] = requestTypeId.ToString(CultureInfo.InvariantCulture);
        if (!string.IsNullOrWhiteSpace(request.TicketStatus)) query["ticketStatus"] = request.TicketStatus;
        if (request.PriorityId is { } priorityId) query["priorityId"] = priorityId.ToString(CultureInfo.InvariantCulture);
        query["page"] = request.Page.ToString(CultureInfo.InvariantCulture);
        query["pageSize"] = request.PageSize.ToString(CultureInfo.InvariantCulture);

        return query;
    }

    /// <summary>Corrects the phone, project and unit a customer is shown with (<c>PUT api/customers/profile/{customerKey}</c>). Local to Tiger Ticketing; the Api decides who may and validates every value.</summary>
    public Task<ApiResult<CustomerProfileEditedDto>> UpdateProfileAsync(string customerKey, UpdateCustomerProfileRequestDto request, CancellationToken cancellationToken) =>
        PutAsync<UpdateCustomerProfileRequestDto, CustomerProfileEditedDto>($"api/customers/profile/{Uri.EscapeDataString(customerKey)}", request, cancellationToken);

    /// <summary>Adds a manually associated property / unit (<c>POST api/customers/profile/{customerKey}/properties</c>). Never verified ownership; local to Tiger Ticketing.</summary>
    public Task<ApiResult<CustomerPropertyResult>> AddPropertyAsync(string customerKey, CustomerPropertyRequestDto request, CancellationToken cancellationToken) =>
        PostAsync<CustomerPropertyRequestDto, CustomerPropertyResult>($"api/customers/profile/{Uri.EscapeDataString(customerKey)}/properties", request, cancellationToken);

    public Task<ApiResult<CustomerPropertyResult>> UpdatePropertyAsync(string customerKey, long associationId, CustomerPropertyRequestDto request, CancellationToken cancellationToken) =>
        PutAsync<CustomerPropertyRequestDto, CustomerPropertyResult>($"api/customers/profile/{Uri.EscapeDataString(customerKey)}/properties/{associationId}", request, cancellationToken);

    public Task<ApiResult<CustomerPropertyResult>> RemovePropertyAsync(string customerKey, long associationId, Guid correlationId, CancellationToken cancellationToken) =>
        DeleteAsync<CustomerPropertyResult>($"api/customers/profile/{Uri.EscapeDataString(customerKey)}/properties/{associationId}?correlationId={correlationId}", cancellationToken);

    /// <summary>One page of the customer's change history (<c>GET api/customers/profile/{customerKey}/history</c>). Needs <c>Customers.ViewHistory</c>.</summary>
    public Task<ApiResult<CustomerChangeHistoryDto>> GetHistoryAsync(string customerKey, int page, int pageSize, CancellationToken cancellationToken) =>
        GetAsync<CustomerChangeHistoryDto>($"api/customers/profile/{Uri.EscapeDataString(customerKey)}/history?page={page}&pageSize={pageSize}", cancellationToken);

    public Task<ApiResult<CustomerDirectoryProfileDto>> GetProfileAsync(string customerKey, CancellationToken cancellationToken) =>
        GetAsync<CustomerDirectoryProfileDto>($"api/customers/profile/{Uri.EscapeDataString(customerKey)}", cancellationToken);
}
