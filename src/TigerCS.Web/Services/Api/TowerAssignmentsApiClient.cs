using Microsoft.Extensions.Logging;
using TigerCS.Application.Modules.Collections.TowerAssignments;

namespace TigerCS.Web.Services.Api;

/// <summary>The Collections Tower Assignments page's only route to the data: TigerCS.Api's <c>/api/collections/tower-assignments</c>, called server-side as the signed-in user.</summary>
public sealed class TowerAssignmentsApiClient(HttpClient httpClient, ILogger<TowerAssignmentsApiClient> logger) : ApiClientBase(httpClient, logger)
{
    private const string Base = "api/collections/tower-assignments";

    public Task<ApiResult<TowerAssignmentMonthDto>> GetMonthAsync(int year, int month, CancellationToken cancellationToken) =>
        GetAsync<TowerAssignmentMonthDto>($"{Base}?year={year}&month={month}", cancellationToken);

    public Task<ApiResult<TowerAssignmentMonthDto>> SaveEmployeeAsync(Guid employeeId, SaveEmployeeTowersRequest request, CancellationToken cancellationToken) =>
        PutAsync<SaveEmployeeTowersRequest, TowerAssignmentMonthDto>($"{Base}/employees/{employeeId}", request, cancellationToken);

    public Task<ApiResult<TowerAssignmentMonthDto>> ActivateAsync(ActivateTowerAssignmentsRequest request, CancellationToken cancellationToken) =>
        PostAsync<ActivateTowerAssignmentsRequest, TowerAssignmentMonthDto>($"{Base}/activate", request, cancellationToken);

    public Task<ApiResult<CopyPreviousMonthResultDto>> CopyPreviousAsync(CopyPreviousMonthRequest request, CancellationToken cancellationToken) =>
        PostAsync<CopyPreviousMonthRequest, CopyPreviousMonthResultDto>($"{Base}/copy-previous", request, cancellationToken);

    public Task<ApiResult<IReadOnlyList<TowerAssignmentHistoryDto>>> GetHistoryAsync(int? year, int? month, CancellationToken cancellationToken) =>
        GetAsync<IReadOnlyList<TowerAssignmentHistoryDto>>(
            $"{Base}/history?{(year is { } y ? $"year={y}&" : "")}{(month is { } m ? $"month={m}" : "")}", cancellationToken);
}
