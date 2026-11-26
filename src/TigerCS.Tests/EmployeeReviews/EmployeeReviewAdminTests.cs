using Microsoft.EntityFrameworkCore;
using TigerCS.Application.Modules.Administration.Dto;

namespace TigerCS.Tests.EmployeeReviews;

/// <summary>Tablet administration over the real schema: only existing, active Customer Service employees; no second employee store.</summary>
public sealed class EmployeeReviewAdminTests : IDisposable
{
    private readonly EmployeeReviewFixture _fx = new();

    public void Dispose() => _fx.Dispose();

    [Fact]
    public async Task EligibleEmployees_AreTheActiveCustomerServiceStaff_ReadFromTheExistingEmployeeTables()
    {
        using var context = _fx.CreateContext();

        var eligible = await _fx.CreateAdminService(context).ListEligibleEmployeesAsync();

        // Agents and the supervisor/manager of Customer Service; not Collections' head, not the administrator, not the deactivated agent.
        Assert.Equal(
            new[] { _fx.Amal, _fx.Bilal, _fx.Manager, _fx.Supervisor }.Order(),
            eligible.Select(e => e.EmployeeId).Order());
        Assert.All(eligible, e => Assert.Equal("Customer Service", e.Department));
        Assert.Contains(eligible, e => e.DisplayName == "Amal Agent");
    }

    [Fact]
    public async Task NoSecondEmployeeTableExists()
    {
        using var context = _fx.CreateContext();

        var tables = context.Model.GetEntityTypes().Select(t => t.GetTableName()).Where(n => n is not null).ToList();

        Assert.Contains("EmployeeReviewDevices", tables);
        Assert.Contains("EmployeeReviews", tables);
        Assert.Single(tables, n => n == "Employees");
        Assert.DoesNotContain(tables, n => n!.Contains("ReviewEmployee", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Register_RejectsAnEmployeeWhoIsNotActiveCustomerService_AndAnUnknownOne()
    {
        using var context = _fx.CreateContext();
        var admin = _fx.CreateAdminService(context);

        foreach (var employee in new[] { _fx.Hadi, _fx.Departed, Guid.NewGuid(), Guid.Empty })
        {
            var result = await admin.RegisterAsync(_fx.Admin, new("Desk", employee));
            Assert.Equal(AdminOutcome.ValidationFailed, result.Outcome);
        }

        Assert.Empty(await admin.ListAsync());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("   ")]
    public async Task Register_RequiresAName(string? name)
    {
        using var context = _fx.CreateContext();

        var result = await _fx.CreateAdminService(context).RegisterAsync(_fx.Admin, new(name, _fx.Amal));

        Assert.Equal(AdminOutcome.ValidationFailed, result.Outcome);
    }

    [Fact]
    public async Task Register_RefusesADuplicateName_CaseInsensitively()
    {
        await _fx.RegisterAsync("Reception Desk", _fx.Amal);
        using var context = _fx.CreateContext();

        var result = await _fx.CreateAdminService(context).RegisterAsync(_fx.Admin, new("reception desk", _fx.Bilal));

        Assert.Equal(AdminOutcome.Conflict, result.Outcome);
    }

    [Fact]
    public async Task Reassign_ToAnIneligibleEmployee_IsRefused_ButRenamingWithoutChangingTheEmployeeStillWorks()
    {
        var (deviceId, _) = await _fx.RegisterAsync("Desk 1", _fx.Amal);
        using var context = _fx.CreateContext();
        var admin = _fx.CreateAdminService(context);

        Assert.Equal(AdminOutcome.ValidationFailed, (await admin.UpdateAsync(_fx.Admin, deviceId, new("Desk 1", _fx.Hadi))).Outcome);

        // Amal later leaves: the tablet can still be renamed; it just cannot be pointed at her again once changed.
        context.Employees.Single(e => e.EmployeeId == _fx.Amal).Deactivate(EmployeeReviewFixture.Now);
        context.SaveChanges();
        var renamed = await admin.UpdateAsync(_fx.Admin, deviceId, new("Desk 1 (renamed)", _fx.Amal));
        Assert.True(renamed.IsSuccess);
        Assert.False(renamed.Value!.EmployeeIsActive);
    }

    [Fact]
    public async Task UnknownDevice_IsNotFound_ForEveryChange()
    {
        using var context = _fx.CreateContext();
        var admin = _fx.CreateAdminService(context);
        var id = Guid.NewGuid();

        Assert.Equal(AdminOutcome.NotFound, (await admin.UpdateAsync(_fx.Admin, id, new("x", _fx.Amal))).Outcome);
        Assert.Equal(AdminOutcome.NotFound, (await admin.SetActiveAsync(_fx.Admin, id, true)).Outcome);
        Assert.Equal(AdminOutcome.NotFound, (await admin.IssueActivationCodeAsync(_fx.Admin, id)).Outcome);
        Assert.Equal(AdminOutcome.NotFound, (await admin.RevokeAsync(_fx.Admin, id)).Outcome);
    }

    [Fact]
    public async Task List_ShowsNameEmployeeStatusAndLastActivity()
    {
        await _fx.RegisterAsync("Desk 1", _fx.Amal);
        using var context = _fx.CreateContext();

        var device = Assert.Single(await _fx.CreateAdminService(context).ListAsync());

        Assert.Equal("Desk 1", device.DeviceName);
        Assert.Equal("Amal Agent", device.EmployeeName);
        Assert.Equal("Customer Service", device.Department);
        Assert.True(device.IsActive && device.HasCredential && device.EmployeeIsActive);
        Assert.Null(device.LastSeenAtUtc);
    }

    [Fact]
    public async Task IssuingACodeForAnActivatedTablet_WithoutConfirmation_IsRefused_AndSilentlyChangesNothing()
    {
        var (deviceId, token) = await _fx.RegisterAsync("Desk 1", _fx.Amal);
        using var context = _fx.CreateContext();
        var admin = _fx.CreateAdminService(context);
        var auditBefore = context.AuditEntries.Count();

        var refused = await admin.IssueActivationCodeAsync(_fx.Admin, deviceId);

        Assert.Equal(TigerCS.Application.Modules.Administration.Dto.AdminOutcome.Conflict, refused.Outcome);
        Assert.Contains("Confirm", string.Join(' ', refused.Errors ?? []));
        using var check = _fx.CreateContext();
        var device = await check.EmployeeReviewDevices.AsNoTracking().SingleAsync(d => d.Id == deviceId);
        Assert.Equal(TigerCS.Application.Modules.EmployeeReviews.DeviceToken.Hash(token), device.TokenHash);   // still activated
        Assert.Null(device.ActivationCodeHash);                                                                // no code was created
        Assert.True(device.IsActive);
        Assert.Equal(auditBefore, check.AuditEntries.Count());                                                 // and nothing was audited
        Assert.Equal(TigerCS.Application.Modules.EmployeeReviews.Dto.TabletSessionStatus.Ready, (await _fx.CreateTabletService(check).GetSessionAsync(token)).Status);
    }

    [Fact]
    public async Task IssuingACodeForAnActivatedTablet_WithConfirmation_SwitchesItsCredentialOff_AndIsAuditedAsAReactivation()
    {
        var (deviceId, token) = await _fx.RegisterAsync("Desk 1", _fx.Amal);
        using var context = _fx.CreateContext();

        var result = await _fx.CreateAdminService(context).IssueActivationCodeAsync(_fx.Admin, deviceId, confirmReplaceActiveCredential: true);

        Assert.True(result.IsSuccess);
        Assert.False(result.Value!.Device.HasCredential);
        Assert.True(result.Value.Device.ActivationPending);
        using var check = _fx.CreateContext();
        Assert.Equal(TigerCS.Application.Modules.EmployeeReviews.Dto.TabletSessionStatus.Invalid, (await _fx.CreateTabletService(check).GetSessionAsync(token)).Status);
        var entry = Assert.Single(check.AuditEntries.Where(a => a.Action == "AdminReactivateReviewDevice").ToList());
        Assert.Equal("credential active", entry.BeforeValue);
        Assert.DoesNotContain(token, entry.AfterValue ?? "");
        Assert.DoesNotContain(result.Value.ActivationCode, entry.AfterValue ?? "");
    }

    [Fact]
    public async Task IssuingACode_ForATabletWithNoCredential_NeedsNoConfirmation_BecauseNothingIsSwitchedOff()
    {
        var (pendingId, _) = await _fx.RegisterPendingAsync("Pending desk", _fx.Amal);
        var (revokedId, _) = await _fx.RegisterAsync("Revoked desk", _fx.Bilal);
        using var context = _fx.CreateContext();
        var admin = _fx.CreateAdminService(context);
        await admin.RevokeAsync(_fx.Admin, revokedId);

        Assert.True((await admin.IssueActivationCodeAsync(_fx.Admin, pendingId)).IsSuccess);     // replaces a code nobody has used yet
        Assert.True((await admin.IssueActivationCodeAsync(_fx.Admin, revokedId)).IsSuccess);     // brings a revoked tablet back
    }

    [Fact]
    public void TheDomain_RefusesToDropAnActiveCredential_WithoutBeingTold()
    {
        var device = new TigerCS.Domain.Modules.EmployeeReviews.EmployeeReviewDevice(
            "Desk", _fx.Amal, new byte[32], EmployeeReviewFixture.Now);

        Assert.Throws<InvalidOperationException>(() =>
            device.IssueActivationCode(new byte[32], new byte[16], EmployeeReviewFixture.Now.AddHours(24), replaceActiveCredential: false, EmployeeReviewFixture.Now));
        Assert.NotNull(device.TokenHash);

        device.IssueActivationCode(new byte[32], new byte[16], EmployeeReviewFixture.Now.AddHours(24), replaceActiveCredential: true, EmployeeReviewFixture.Now);
        Assert.Null(device.TokenHash);
    }
}
