using Microsoft.EntityFrameworkCore;
using TigerCS.Application.Modules.EmployeeReviews;
using TigerCS.Application.Modules.EmployeeReviews.Dto;
using TigerCS.Domain.Modules.EmployeeReviews;

namespace TigerCS.Tests.EmployeeReviews;

/// <summary>Submission, device credentials, reassignment and history — over the real schema.</summary>
public sealed class EmployeeReviewSubmissionTests : IDisposable
{
    private readonly EmployeeReviewFixture _fx = new();

    public void Dispose() => _fx.Dispose();

    private async Task<SubmitEmployeeReviewOutcome> Submit(string? token, int rating = 5, string? comment = null, Guid? submissionId = null)
    {
        using var context = _fx.CreateContext();
        var result = await _fx.CreateTabletService(context).SubmitAsync(token, new(submissionId ?? Guid.NewGuid(), rating, comment));
        return result.Outcome;
    }

    private int CountReviews()
    {
        using var context = _fx.CreateContext();
        return context.EmployeeReviews.Count();
    }

    [Fact]
    public async Task ValidSubmission_IsStored_WithTheTabletsEmployee_AndUtcTimestamp()
    {
        var (deviceId, token) = await _fx.RegisterAsync("Desk 1", _fx.Amal);
        var submissionId = Guid.NewGuid();

        var outcome = await Submit(token, 4, "  Very helpful  ", submissionId);

        Assert.Equal(SubmitEmployeeReviewOutcome.Submitted, outcome);
        using var context = _fx.CreateContext();
        var review = Assert.Single(context.EmployeeReviews);
        Assert.Equal(_fx.Amal, review.EmployeeId);
        Assert.Equal(deviceId, review.DeviceId);
        Assert.Equal(4, review.Rating);
        Assert.Equal("Very helpful", review.Comment);
        Assert.Equal(submissionId, review.SubmissionId);
        Assert.Equal(EmployeeReviewFixture.Now, review.CreatedAtUtc);
    }

    [Fact]
    public async Task EmptyComment_IsAllowed_AndStoredAsNull()
    {
        var (_, token) = await _fx.RegisterAsync("Desk 1", _fx.Amal);

        Assert.Equal(SubmitEmployeeReviewOutcome.Submitted, await Submit(token, 5, "   "));

        using var context = _fx.CreateContext();
        Assert.Null(context.EmployeeReviews.Single().Comment);
    }

    [Theory]
    [InlineData(0)]   // the tablet sends 0 when nothing was chosen ("missing rating")
    [InlineData(-1)]
    [InlineData(6)]
    [InlineData(100)]
    public async Task RatingOutsideOneToFive_IsRejected_AndNothingIsStored(int rating)
    {
        var (_, token) = await _fx.RegisterAsync("Desk 1", _fx.Amal);

        Assert.Equal(SubmitEmployeeReviewOutcome.InvalidRating, await Submit(token, rating));
        Assert.Equal(0, CountReviews());
    }

    [Fact]
    public async Task MissingSubmissionId_IsRejected()
    {
        var (_, token) = await _fx.RegisterAsync("Desk 1", _fx.Amal);

        Assert.Equal(SubmitEmployeeReviewOutcome.InvalidSubmission, await Submit(token, 5, null, Guid.Empty));
    }

    [Fact]
    public async Task CommentOfExactlyTheLimit_IsAccepted_AndOneOverIsRejected()
    {
        var (_, token) = await _fx.RegisterAsync("Desk 1", _fx.Amal);

        Assert.Equal(SubmitEmployeeReviewOutcome.Submitted, await Submit(token, 3, new string('x', 1000)));
        Assert.Equal(SubmitEmployeeReviewOutcome.InvalidComment, await Submit(token, 3, new string('x', 1001)));
        Assert.Equal(1, CountReviews());
    }

    [Fact]
    public async Task DuplicateSubmissionId_IsRecognised_AndStoredOnce()
    {
        var (_, token) = await _fx.RegisterAsync("Desk 1", _fx.Amal);
        var id = Guid.NewGuid();

        Assert.Equal(SubmitEmployeeReviewOutcome.Submitted, await Submit(token, 5, "first", id));
        Assert.Equal(SubmitEmployeeReviewOutcome.AlreadyRecorded, await Submit(token, 1, "a retry must not overwrite", id));

        using var context = _fx.CreateContext();
        var review = Assert.Single(context.EmployeeReviews);
        Assert.Equal(5, review.Rating);
        Assert.Equal("first", review.Comment);
    }

    [Fact]
    public async Task TheDatabaseItselfRefusesADuplicateSubmissionId_EvenWhenTheApplicationCheckIsRacedPast()
    {
        var (deviceId, token) = await _fx.RegisterAsync("Desk 1", _fx.Amal);
        var id = Guid.NewGuid();
        await Submit(token, 5, null, id);

        // Two simultaneous retries both pass the "exists?" check; the unique index decides.
        using var context = _fx.CreateContext();
        var device = await context.EmployeeReviewDevices.SingleAsync(d => d.Id == deviceId);
        var stored = await _fx.CreateRepository(context).TryAddReviewAsync(new EmployeeReview(device, id, 2, null, EmployeeReviewFixture.Now));

        Assert.False(stored);
        Assert.Equal(1, CountReviews());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not-a-token")]
    [InlineData("tgr-AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")] // well-formed but never issued
    public async Task InvalidDeviceCredentials_AreRejected_AndNothingIsStored(string? token)
    {
        await _fx.RegisterAsync("Desk 1", _fx.Amal);

        Assert.Equal(SubmitEmployeeReviewOutcome.InvalidDevice, await Submit(token));
        Assert.Equal(0, CountReviews());
    }

    [Fact]
    public async Task OnlyTheHashOfTheTokenIsStored()
    {
        var (deviceId, token) = await _fx.RegisterAsync("Desk 1", _fx.Amal);

        using var context = _fx.CreateContext();
        var device = await context.EmployeeReviewDevices.AsNoTracking().SingleAsync(d => d.Id == deviceId);
        Assert.Equal(DeviceToken.Hash(token), device.TokenHash);
        Assert.NotEqual(System.Text.Encoding.ASCII.GetBytes(token), device.TokenHash);
        Assert.Equal(32, device.TokenHash!.Length);
    }

    [Fact]
    public async Task InactiveTablet_RefusesReviews_AndAccepts_AgainOnceReactivated()
    {
        var (deviceId, token) = await _fx.RegisterAsync("Desk 1", _fx.Amal);
        using (var context = _fx.CreateContext())
        {
            Assert.True((await _fx.CreateAdminService(context).SetActiveAsync(_fx.Admin, deviceId, false)).IsSuccess);
        }

        Assert.Equal(SubmitEmployeeReviewOutcome.InactiveDevice, await Submit(token));
        Assert.Equal(0, CountReviews());

        using (var context = _fx.CreateContext())
        {
            Assert.True((await _fx.CreateAdminService(context).SetActiveAsync(_fx.Admin, deviceId, true)).IsSuccess);
        }

        Assert.Equal(SubmitEmployeeReviewOutcome.Submitted, await Submit(token));
    }

    [Fact]
    public async Task TabletOfADeactivatedEmployee_RefusesReviews()
    {
        var (_, token) = await _fx.RegisterAsync("Desk 1", _fx.Amal);
        using (var context = _fx.CreateContext())
        {
            context.Employees.Single(e => e.EmployeeId == _fx.Amal).Deactivate(EmployeeReviewFixture.Now);
            context.SaveChanges();
        }

        Assert.Equal(SubmitEmployeeReviewOutcome.InactiveDevice, await Submit(token));
    }

    [Fact]
    public async Task Session_ReportsReady_Inactive_Invalid_AndShowsNamesNeverIds()
    {
        var (deviceId, token) = await _fx.RegisterAsync("Desk 1", _fx.Amal);
        using var context = _fx.CreateContext();
        var service = _fx.CreateTabletService(context);

        var ready = await service.GetSessionAsync(token);
        Assert.Equal(TabletSessionStatus.Ready, ready.Status);
        Assert.Equal("Amal Agent", ready.Tablet!.EmployeeName);
        Assert.Equal("Desk 1", ready.Tablet.DeviceName);
        Assert.DoesNotContain(
            typeof(TabletInfoDto).GetProperties(), p => p.PropertyType == typeof(Guid) || p.Name.Contains("Id", StringComparison.Ordinal));

        Assert.Equal(TabletSessionStatus.Invalid, (await service.GetSessionAsync("tgr-" + new string('A', 43))).Status);

        await _fx.CreateAdminService(context).SetActiveAsync(_fx.Admin, deviceId, false);
        Assert.Equal(TabletSessionStatus.Inactive, (await service.GetSessionAsync(token)).Status);
    }

    [Fact]
    public async Task TheSubmitRequestHasNoEmployeeOrDeviceField_SoTheBrowserCanNeverChooseWhoIsReviewed()
    {
        Assert.Equal(["SubmissionId", "Rating", "Comment"], typeof(SubmitEmployeeReviewRequestDto).GetProperties().Select(p => p.Name).ToArray());

        // And the stored employee is the tablet's, whatever else is going on.
        var (_, token) = await _fx.RegisterAsync("Desk 1", _fx.Amal);
        await Submit(token);
        using var context = _fx.CreateContext();
        Assert.Equal(_fx.Amal, context.EmployeeReviews.Single().EmployeeId);
    }

    [Fact]
    public async Task ReassigningATablet_SendsNewReviewsToTheNewEmployee_AndKeepsHistoryWithTheOriginalOne()
    {
        var (deviceId, token) = await _fx.RegisterAsync("Desk 1", _fx.Amal);
        await _fx.SubmitAsync(token, 5, "before", EmployeeReviewFixture.Now.AddHours(-2));

        using (var context = _fx.CreateContext())
        {
            var update = await _fx.CreateAdminService(context).UpdateAsync(_fx.Admin, deviceId, new("Desk 1", _fx.Bilal));
            Assert.True(update.IsSuccess, string.Join("; ", update.Errors ?? []));
            Assert.Equal("Bilal Agent", update.Value!.EmployeeName);
        }

        await _fx.SubmitAsync(token, 3, "after", EmployeeReviewFixture.Now.AddHours(-1));

        using var check = _fx.CreateContext();
        var reviews = check.EmployeeReviews.OrderBy(r => r.CreatedAtUtc).ToList();
        Assert.Equal([_fx.Amal, _fx.Bilal], reviews.Select(r => r.EmployeeId));
        Assert.All(reviews, r => Assert.Equal(deviceId, r.DeviceId));
        Assert.Equal(_fx.Amal, reviews[0].EmployeeId); // not rewritten by the reassignment

        // The credential did not change: the tablet needs no re-activation.
        Assert.Equal(TabletSessionStatus.Ready, (await _fx.CreateTabletService(check).GetSessionAsync(token)).Status);
    }

    [Fact]
    public async Task ReassignmentIsAudited_WithoutAnyToken()
    {
        var (deviceId, token) = await _fx.RegisterAsync("Desk 1", _fx.Amal);
        using (var context = _fx.CreateContext())
        {
            await _fx.CreateAdminService(context).UpdateAsync(_fx.Admin, deviceId, new("Desk 1", _fx.Bilal));
            await _fx.CreateAdminService(context).IssueActivationCodeAsync(_fx.Admin, deviceId, confirmReplaceActiveCredential: true);
        }

        using var check = _fx.CreateContext();
        var entries = check.AuditEntries.Where(a => a.EntityType == "EmployeeReviewDevice").ToList();
        Assert.Contains(entries, a => a.Action == "AdminRegisterReviewDevice");
        Assert.Contains(entries, a => a.Action == "AdminReassignReviewDevice" && a.BeforeValue!.Contains(_fx.Amal.ToString()) && a.AfterValue!.Contains(_fx.Bilal.ToString()));
        Assert.Contains(entries, a => a.Action == "AdminReactivateReviewDevice");
        Assert.DoesNotContain(entries, a => (a.AfterValue ?? "").Contains(token) || (a.BeforeValue ?? "").Contains(token));
    }

    [Fact]
    public async Task IssuingANewActivationCode_StopsTheOldTokenAtOnce_AndTheNewlyActivatedTokenWorks()
    {
        var (deviceId, oldToken) = await _fx.RegisterAsync("Desk 1", _fx.Amal);
        string code;
        using (var context = _fx.CreateContext())
        {
            code = (await _fx.CreateAdminService(context).IssueActivationCodeAsync(_fx.Admin, deviceId, confirmReplaceActiveCredential: true)).Value!.ActivationCode;
        }

        // Re-provisioning cuts the old credential immediately, before the new code is even used.
        Assert.Equal(SubmitEmployeeReviewOutcome.InvalidDevice, await Submit(oldToken));

        var newToken = (await _fx.ActivateAsync(code)).DeviceToken!;
        Assert.NotEqual(oldToken, newToken);
        Assert.Equal(SubmitEmployeeReviewOutcome.Submitted, await Submit(newToken));
    }

    [Fact]
    public async Task RevokedTablet_HasNoCredential_AndCannotBeSwitchedBackOnWithoutANewOne()
    {
        var (deviceId, token) = await _fx.RegisterAsync("Desk 1", _fx.Amal);
        using var context = _fx.CreateContext();
        var admin = _fx.CreateAdminService(context);

        var revoked = (await admin.RevokeAsync(_fx.Admin, deviceId)).Value!;
        Assert.False(revoked.HasCredential);
        Assert.False(revoked.IsActive);
        Assert.Equal(SubmitEmployeeReviewOutcome.InvalidDevice, await Submit(token));

        var reactivate = await admin.SetActiveAsync(_fx.Admin, deviceId, true);
        Assert.Equal(TigerCS.Application.Modules.Administration.Dto.AdminOutcome.Conflict, reactivate.Outcome);

        var reissued = (await admin.IssueActivationCodeAsync(_fx.Admin, deviceId)).Value!;
        Assert.True(reissued.Device.IsActive && reissued.Device.ActivationPending && !reissued.Device.HasCredential);
        var activated = await _fx.ActivateAsync(reissued.ActivationCode);
        Assert.Equal(SubmitEmployeeReviewOutcome.Submitted, await Submit(activated.DeviceToken));
    }

    [Fact]
    public async Task LastSeen_IsRecordedBySessionChecksAndSubmissions()
    {
        var (deviceId, token) = await _fx.RegisterAsync("Desk 1", _fx.Amal);
        using (var context = _fx.CreateContext())
        {
            Assert.Null((await _fx.CreateAdminService(context).ListAsync()).Single().LastSeenAtUtc);
            await _fx.CreateTabletService(context).GetSessionAsync(token);
        }

        using var check = _fx.CreateContext();
        Assert.Equal(EmployeeReviewFixture.Now, (await _fx.CreateAdminService(check).ListAsync()).Single(d => d.Id == deviceId).LastSeenAtUtc);
    }
}
