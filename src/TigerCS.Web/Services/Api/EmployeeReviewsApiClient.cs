using System.Globalization;
using System.Web;
using Microsoft.Extensions.Logging;
using TigerCS.Application.Modules.EmployeeReviews.Dto;

namespace TigerCS.Web.Services.Api;

/// <summary>
/// Calls TigerCS.Api's signed-in Employee Reviews endpoints: the dashboard (<c>api/employee-reviews</c>) and review tablet
/// administration (<c>api/admin/employee-review-devices</c>). The Api decides who may read or change what; this client relays the outcome.
/// </summary>
public sealed class EmployeeReviewsApiClient(HttpClient httpClient, ILogger<EmployeeReviewsApiClient> logger) : ApiClientBase(httpClient, logger)
{
    private const string Admin = "api/admin/employee-review-devices";

    public Task<ApiResult<EmployeeReviewsOverviewDto>> GetOverviewAsync(EmployeeReviewFilterRequestDto filter, CancellationToken cancellationToken) =>
        GetAsync<EmployeeReviewsOverviewDto>("api/employee-reviews" + Query(filter), cancellationToken);

    public Task<ApiResult<EmployeeReviewDetailDto>> GetEmployeeAsync(Guid employeeId, EmployeeReviewFilterRequestDto filter, CancellationToken cancellationToken) =>
        GetAsync<EmployeeReviewDetailDto>($"api/employee-reviews/employees/{employeeId}" + Query(filter with { EmployeeId = null }), cancellationToken);

    public Task<ApiResult<IReadOnlyList<EmployeeReviewDeviceDto>>> GetDevicesAsync(CancellationToken cancellationToken) =>
        GetAsync<IReadOnlyList<EmployeeReviewDeviceDto>>(Admin, cancellationToken);

    public Task<ApiResult<IReadOnlyList<EligibleReviewEmployeeDto>>> GetEligibleEmployeesAsync(CancellationToken cancellationToken) =>
        GetAsync<IReadOnlyList<EligibleReviewEmployeeDto>>($"{Admin}/eligible-employees", cancellationToken);

    public Task<ApiResult<EmployeeReviewActivationCodeDto>> RegisterAsync(RegisterEmployeeReviewDeviceRequestDto request, CancellationToken cancellationToken) =>
        PostAsync<RegisterEmployeeReviewDeviceRequestDto, EmployeeReviewActivationCodeDto>(Admin, request, cancellationToken);

    public Task<ApiResult<EmployeeReviewDeviceDto>> UpdateAsync(Guid deviceId, UpdateEmployeeReviewDeviceRequestDto request, CancellationToken cancellationToken) =>
        PutAsync<UpdateEmployeeReviewDeviceRequestDto, EmployeeReviewDeviceDto>($"{Admin}/{deviceId}", request, cancellationToken);

    public Task<ApiResult<EmployeeReviewDeviceDto>> SetActiveAsync(Guid deviceId, bool isActive, CancellationToken cancellationToken) =>
        PostAsync<SetEmployeeReviewDeviceActiveRequestDto, EmployeeReviewDeviceDto>(
            $"{Admin}/{deviceId}/activation", new SetEmployeeReviewDeviceActiveRequestDto(isActive), cancellationToken);

    public Task<ApiResult<EmployeeReviewActivationCodeDto>> RegenerateAsync(Guid deviceId, bool confirmReplaceActiveCredential, CancellationToken cancellationToken) =>
        PostAsync<IssueEmployeeReviewActivationCodeRequestDto, EmployeeReviewActivationCodeDto>(
            $"{Admin}/{deviceId}/credential", new IssueEmployeeReviewActivationCodeRequestDto(confirmReplaceActiveCredential), cancellationToken);

    public Task<ApiResult<EmployeeReviewDeviceDto>> RevokeAsync(Guid deviceId, CancellationToken cancellationToken) =>
        DeleteAsync<EmployeeReviewDeviceDto>($"{Admin}/{deviceId}/credential", cancellationToken);

    private static string Query(EmployeeReviewFilterRequestDto filter)
    {
        var query = HttpUtility.ParseQueryString(string.Empty);
        if (filter.DateFrom is { } from) query["dateFrom"] = from.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        if (filter.DateTo is { } to) query["dateTo"] = to.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        if (filter.EmployeeId is { } employeeId) query["employeeId"] = employeeId.ToString();
        if (filter.DepartmentId is { } departmentId) query["departmentId"] = departmentId.ToString(CultureInfo.InvariantCulture);
        if (filter.Rating is { } rating) query["rating"] = rating.ToString(CultureInfo.InvariantCulture);
        return query.Count == 0 ? string.Empty : "?" + query;
    }
}
