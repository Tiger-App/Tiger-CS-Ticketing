using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using TigerCS.Application.Modules.EmployeeReviews.Dto;

namespace TigerCS.Web.Services.Api;

/// <summary>How the Api answered one tablet submission, reduced to what the tablet page must do next.</summary>
public enum TabletSubmitStatus
{
    /// <summary>Stored — or already stored from an earlier attempt of the same submission. Either way the review is safe.</summary>
    Saved,

    /// <summary>The Api refused the review itself (rating/comment/id). Retrying the same review cannot succeed.</summary>
    Rejected,

    /// <summary>The tablet's credential is unknown, revoked or replaced.</summary>
    InvalidDevice,

    /// <summary>The tablet or its employee is switched off.</summary>
    InactiveDevice,

    /// <summary>The Api could not be reached, was rate limiting this tablet, or failed. The review must be kept and sent again.</summary>
    Unavailable
}

/// <summary>
/// Calls TigerCS.Api's anonymous <c>api/public/employee-reviews</c> endpoints on behalf of a review tablet. No bearer token exists — the device
/// token (from the tablet's own HttpOnly cookie) is sent in the <c>X-Device-Token</c> header. It is never put in a URL or log line.
/// </summary>
public sealed class EmployeeReviewTabletApiClient(HttpClient httpClient, ILogger<EmployeeReviewTabletApiClient> logger)
{
    public const string TokenHeader = "X-Device-Token";

    /// <summary>The tablet's session, or <c>null</c> when the Api could not be reached.</summary>
    public async Task<TabletSessionDto?> GetSessionAsync(string token, CancellationToken cancellationToken)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "api/public/employee-reviews/session");
            request.Headers.TryAddWithoutValidation(TokenHeader, token);
            using var response = await httpClient.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning("Employee review tablet session check failed with status {Status}.", (int)response.StatusCode);
                return null;
            }

            return await response.Content.ReadFromJsonAsync<TabletSessionDto>(ApiJson.Options, cancellationToken);
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException || ex is TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            // An unreachable Api and an answer we cannot read look the same to a kiosk: show "unavailable" and look again shortly.
            logger.LogWarning(ex, "Employee review tablet session check could not reach or read the Api.");
            return null;
        }
    }

    public async Task<TabletSubmitStatus> SubmitAsync(string token, SubmitEmployeeReviewRequestDto review, CancellationToken cancellationToken)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "api/public/employee-reviews/submit")
            {
                Content = JsonContent.Create(review, options: ApiJson.Options)
            };
            request.Headers.TryAddWithoutValidation(TokenHeader, token);
            using var response = await httpClient.SendAsync(request, cancellationToken);
            return response.StatusCode switch
            {
                HttpStatusCode.OK => TabletSubmitStatus.Saved,
                HttpStatusCode.BadRequest => TabletSubmitStatus.Rejected,
                HttpStatusCode.Unauthorized => TabletSubmitStatus.InvalidDevice,
                HttpStatusCode.Forbidden => TabletSubmitStatus.InactiveDevice,
                _ => TabletSubmitStatus.Unavailable
            };
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning(ex, "Employee review submission could not reach the Api.");
            return TabletSubmitStatus.Unavailable;
        }
    }
}
