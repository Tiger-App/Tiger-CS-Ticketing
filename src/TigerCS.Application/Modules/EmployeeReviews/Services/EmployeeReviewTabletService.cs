using TigerCS.Application.Modules.EmployeeReviews.Abstractions;
using TigerCS.Application.Modules.EmployeeReviews.Dto;
using TigerCS.Domain.Modules.EmployeeReviews;

namespace TigerCS.Application.Modules.EmployeeReviews.Services;

/// <summary>
/// The server side of the customer tablet. Reached without a login: the device token is the only credential, and every decision is
/// made here. The employee a review is for is <b>always</b> read from the tablet's current assignment — the request has no field for it.
/// </summary>
public sealed class EmployeeReviewTabletService(IEmployeeReviewRepository repository, TimeProvider timeProvider)
{
    /// <summary>A tablet that was seen less than this long ago is not written again — the page only needs "recently".</summary>
    private static readonly TimeSpan LastSeenResolution = TimeSpan.FromMinutes(1);

    public async Task<TabletSessionDto> GetSessionAsync(string? token, CancellationToken cancellationToken = default)
    {
        var (device, employee) = await ResolveAsync(token, cancellationToken);
        if (device is null)
        {
            return new TabletSessionDto(TabletSessionStatus.Invalid);
        }

        if (!device.CanCollectReviews || employee is not { IsActive: true })
        {
            return new TabletSessionDto(TabletSessionStatus.Inactive);
        }

        await TouchAsync(device, cancellationToken);
        return new TabletSessionDto(TabletSessionStatus.Ready, new TabletInfoDto(device.DeviceName, employee.DisplayName));
    }

    public async Task<SubmitEmployeeReviewResultDto> SubmitAsync(
        string? token, SubmitEmployeeReviewRequestDto request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var (device, employee) = await ResolveAsync(token, cancellationToken);
        if (device is null)
        {
            return new SubmitEmployeeReviewResultDto(SubmitEmployeeReviewOutcome.InvalidDevice);
        }

        if (!device.CanCollectReviews || employee is not { IsActive: true })
        {
            return new SubmitEmployeeReviewResultDto(SubmitEmployeeReviewOutcome.InactiveDevice);
        }

        if (request.SubmissionId == Guid.Empty)
        {
            return new SubmitEmployeeReviewResultDto(SubmitEmployeeReviewOutcome.InvalidSubmission);
        }

        if (request.Rating is < EmployeeReview.MinRating or > EmployeeReview.MaxRating)
        {
            return new SubmitEmployeeReviewResultDto(SubmitEmployeeReviewOutcome.InvalidRating);
        }

        var comment = string.IsNullOrWhiteSpace(request.Comment) ? null : request.Comment.Trim();
        if (comment is { Length: > EmployeeReview.CommentMaxLength })
        {
            return new SubmitEmployeeReviewResultDto(SubmitEmployeeReviewOutcome.InvalidComment);
        }

        // A retry of a review that already arrived: report success without touching anything.
        if (await repository.SubmissionExistsAsync(request.SubmissionId, cancellationToken))
        {
            return new SubmitEmployeeReviewResultDto(SubmitEmployeeReviewOutcome.AlreadyRecorded);
        }

        var now = timeProvider.GetUtcNow().UtcDateTime;
        var review = new EmployeeReview(device, request.SubmissionId, request.Rating, comment, now);
        if (ShouldTouch(device, now))
        {
            device.MarkSeen(now);
        }

        if (!await repository.TryAddReviewAsync(review, cancellationToken))
        {
            return new SubmitEmployeeReviewResultDto(SubmitEmployeeReviewOutcome.AlreadyRecorded);
        }

        return new SubmitEmployeeReviewResultDto(SubmitEmployeeReviewOutcome.Submitted);
    }

    private async Task<(EmployeeReviewDevice? Device, ReviewEmployeeInfo? Employee)> ResolveAsync(string? token, CancellationToken cancellationToken)
    {
        var hash = DeviceToken.Hash(token);
        if (hash is null)
        {
            return (null, null);
        }

        var device = await repository.GetDeviceByTokenHashAsync(hash, cancellationToken);
        if (device is null)
        {
            return (null, null);
        }

        return (device, await repository.GetEmployeeInfoAsync(device.EmployeeId, cancellationToken));
    }

    private async Task TouchAsync(EmployeeReviewDevice device, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow().UtcDateTime;
        if (ShouldTouch(device, now))
        {
            device.MarkSeen(now);
            await repository.SaveChangesAsync(cancellationToken);
        }
    }

    private static bool ShouldTouch(EmployeeReviewDevice device, DateTime now) =>
        device.LastSeenAtUtc is null || now - device.LastSeenAtUtc >= LastSeenResolution;
}
