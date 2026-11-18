using TigerCS.Application.Modules.GenesysIntegration.Dto;
using TigerCS.Application.Modules.GenesysIntegration.Services;
using TigerCS.Application.Modules.IdentityAndAccess.Abstractions;
using TigerCS.Domain.Modules.GenesysIntegration;
using TigerCS.Domain.Modules.IdentityAndAccess;
using TigerCS.Tests.GenesysIntegration.Fakes;
using TigerCS.Tests.Notifications.Fakes;

namespace TigerCS.Tests.GenesysIntegration.Services;

/// <summary>
/// Secure Screen Pop at the service level, with a controllable clock: the
/// one-hour window to the second, single use, hash-only storage, the landing
/// priority, and the rule that a launch is refused once the agent's mapping
/// or account no longer allows sign-in.
/// </summary>
public class GenesysScreenPopAppServiceTests
{
    private static readonly Guid ServiceAccount = Guid.NewGuid();
    private static readonly DateTime IssuedAt = new(2026, 9, 28, 9, 0, 0, DateTimeKind.Utc);
    private const string WebBase = "https://tigercs-uat.example";

    private sealed class RecordingTokenService : ITokenService
    {
        public List<(Guid EmployeeId, string DisplayName, IReadOnlyCollection<string> Roles)> Issued { get; } = [];

        public List<string> Stamps { get; } = [];

        public IssuedToken CreateAccessToken(Guid employeeId, string displayName, IReadOnlyCollection<string> roles, string securityStamp)
        {
            Issued.Add((employeeId, displayName, roles));
            Stamps.Add(securityStamp);
            return new IssuedToken($"jwt-for-{employeeId}", IssuedAt.AddHours(2));
        }
    }

    /// <summary>Only the lockout read and the security-stamp read are part of Screen Pop; nothing else on the port is used.</summary>
    private sealed class LockoutAccounts : IUserAccountManager
    {
        public const string Stamp = "stamp-at-redeem";

        public HashSet<Guid> Locked { get; } = [];

        public Task<UserAccountInfo?> GetAccountAsync(Guid employeeId, CancellationToken cancellationToken = default) =>
            Task.FromResult<UserAccountInfo?>(new UserAccountInfo(employeeId, $"user-{employeeId:N}", null, Locked.Contains(employeeId)));

        public Task<string?> GetSecurityStampAsync(Guid employeeId, CancellationToken cancellationToken = default) =>
            Task.FromResult<string?>(Stamp);

        public Task<IReadOnlyDictionary<Guid, UserAccountInfo>> GetAccountsAsync(IReadOnlyCollection<Guid> employeeIds, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<UserAccountResult> CreateAccountAsync(string userName, string? email, string initialPassword, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<UserAccountResult> UpdateEmailAsync(Guid employeeId, string? email, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<UserAccountResult> SetUserNameAsync(Guid employeeId, string userName, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<UserAccountResult> SetRolesAsync(Guid employeeId, IReadOnlyCollection<string> roleNames, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyCollection<Guid>> SearchAccountIdsAsync(string search, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<PasswordChangeResult> ResetPasswordAsync(Guid employeeId, string newPassword, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<PasswordChangeResult> ChangePasswordAsync(Guid employeeId, string currentPassword, string newPassword, CancellationToken cancellationToken = default) => throw new NotSupportedException();        public PasswordPolicyInfo GetPasswordPolicy() => throw new NotSupportedException();
        public Task<bool> UnlockAccountAsync(Guid employeeId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class Harness
    {
        public GenesysServiceFixture Genesys { get; } = new();
        public FakeGenesysScreenPopLaunchStore Launches { get; } = new();
        public RecordingTokenService Tokens { get; } = new();
        public LockoutAccounts Accounts { get; } = new();
        public FakeTimeProvider Clock { get; } = new(IssuedAt);
        public GenesysScreenPopAppService Service { get; }

        public Harness(string? webBaseUrl = WebBase)
        {
            Genesys.Options.ScreenPopWebBaseUrl = webBaseUrl;
            Service = new GenesysScreenPopAppService(
                Genesys.Options, Genesys.AgentContext, Genesys.AgentResolution, Genesys.Tickets, Launches,
                Tokens, Genesys.DepartmentAssignments, Accounts, Genesys.Audit, Clock);
        }

        public async Task<string> IssueTokenAsync(string genesysUserId, GenesysScreenPopRequestDto? request = null)
        {
            var issued = await Service.IssueAsync(ServiceAccount, request ?? new GenesysScreenPopRequestDto(genesysUserId));
            Assert.Equal(GenesysScreenPopIssueOutcome.Issued, issued.Outcome);
            return TokenOf(issued.LaunchUrl!);
        }
    }

    private static string TokenOf(string launchUrl)
    {
        var query = new Uri(launchUrl).Query;
        Assert.StartsWith("?token=", query);
        return query["?token=".Length..];
    }

    // ---- Issue ----

    [Fact]
    public async Task Issue_ReturnsAOneHourLaunchUrl_OnTheWebBase_AndStoresOnlyTheTokenHash()
    {
        var h = new Harness();
        var userId = h.Genesys.MapAgent("ga-7", roles: Roles.CsAgent);

        var issued = await h.Service.IssueAsync(ServiceAccount, new GenesysScreenPopRequestDto("ga-7"));

        Assert.Equal(GenesysScreenPopIssueOutcome.Issued, issued.Outcome);
        Assert.StartsWith($"{WebBase}/ScreenPop?token=", issued.LaunchUrl);
        Assert.Equal(IssuedAt.AddHours(1), issued.ExpiresAtUtc);

        var token = TokenOf(issued.LaunchUrl!);
        Assert.Equal(43, token.Length); // 32 random bytes, base64url

        var launch = Assert.Single(h.Launches.All);
        Assert.Equal(GenesysScreenPopAppService.HashToken(token), launch.TokenHash);
        Assert.NotEqual(token, launch.TokenHash);
        Assert.DoesNotContain(token, launch.TokenHash);
        Assert.Equal(userId, launch.UserId);
        Assert.Equal("ga-7", launch.GenesysUserId);

        // The token itself is never audited either.
        Assert.DoesNotContain(h.Genesys.Audit.Entries, e => (e.AfterValue ?? string.Empty).Contains(token));
    }

    [Fact]
    public async Task Issue_TokensAreRandom_AndCarryNoUserData()
    {
        var h = new Harness();
        var userId = h.Genesys.MapAgent("ga-7", displayName: "Layla");

        var first = await h.IssueTokenAsync("ga-7");
        var second = await h.IssueTokenAsync("ga-7");

        Assert.NotEqual(first, second);
        foreach (var token in new[] { first, second })
        {
            Assert.DoesNotContain("ga-7", token);
            Assert.DoesNotContain("Layla", token);
            Assert.DoesNotContain(userId.ToString("N"), token, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain('.', token); // not a JWT
        }
    }

    [Fact]
    public async Task Issue_UnmappedAgent_IsRefused_AndNoLaunchIsStored()
    {
        var h = new Harness();

        var issued = await h.Service.IssueAsync(ServiceAccount, new GenesysScreenPopRequestDto("ga-unknown"));

        Assert.Equal(GenesysScreenPopIssueOutcome.AgentNotMapped, issued.Outcome);
        Assert.Null(issued.LaunchUrl);
        Assert.Empty(h.Launches.All);
    }

    [Fact]
    public async Task Issue_InactiveAgent_IsRefused()
    {
        var h = new Harness();
        h.Genesys.MapAgent("ga-7", isActive: false);

        var issued = await h.Service.IssueAsync(ServiceAccount, new GenesysScreenPopRequestDto("ga-7"));

        Assert.Equal(GenesysScreenPopIssueOutcome.AgentInactive, issued.Outcome);
        Assert.Empty(h.Launches.All);
    }

    [Fact]
    public async Task Issue_WithoutAConfiguredWebBaseUrl_IsRefused()
    {
        var h = new Harness(webBaseUrl: null);
        h.Genesys.MapAgent("ga-7");

        var issued = await h.Service.IssueAsync(ServiceAccount, new GenesysScreenPopRequestDto("ga-7"));

        Assert.Equal(GenesysScreenPopIssueOutcome.NotConfigured, issued.Outcome);
        Assert.Empty(h.Launches.All);
    }

    [Fact]
    public async Task Issue_WhenTheIntegrationIsDisabled_IsRefused()
    {
        var h = new Harness();
        h.Genesys.MapAgent("ga-7");
        h.Genesys.Options.Enabled = false;

        var issued = await h.Service.IssueAsync(ServiceAccount, new GenesysScreenPopRequestDto("ga-7"));

        Assert.Equal(GenesysScreenPopIssueOutcome.IntegrationDisabled, issued.Outcome);
    }

    // ---- Landing priority ----

    private static async Task<(long TicketId, string ConversationId)> IngestAsync(GenesysServiceFixture f, int departmentId)
    {
        var conversationId = "conv-" + Guid.NewGuid().ToString("N");
        var result = await f.Ingestion.IngestAsync(
            ServiceAccount, new GenesysInquiryDto(conversationId, GenesysChannel.Phone, DepartmentId: departmentId));
        return (result.Ticket!.TicketId, conversationId);
    }

    [Fact]
    public async Task Landing_ConversationTicket_WinsOverEverythingElse_AndRecordsTheHandler()
    {
        var h = new Harness();
        var userId = h.Genesys.MapAgent("ga-7");
        var (department, _) = h.Genesys.SeedGenesysDepartment("Customer Service", "CS");
        var (conversationTicket, conversationId) = await IngestAsync(h.Genesys, department.DepartmentId);
        var (otherTicket, _) = await IngestAsync(h.Genesys, department.DepartmentId);

        var issued = await h.Service.IssueAsync(ServiceAccount,
            new GenesysScreenPopRequestDto("ga-7", conversationId, otherTicket, "971501234567"));

        Assert.Equal($"/Tickets/{conversationTicket}", issued.TargetPath);
        Assert.Equal(conversationTicket, issued.TicketId);
        Assert.Equal(userId, h.Genesys.InteractionFor(conversationId)!.HandledByUserId);
    }

    [Fact]
    public async Task Landing_TicketId_WhenTheConversationHasNoTicketYet()
    {
        var h = new Harness();
        h.Genesys.MapAgent("ga-7");
        var (department, _) = h.Genesys.SeedGenesysDepartment("Customer Service", "CS");
        var (ticketId, _) = await IngestAsync(h.Genesys, department.DepartmentId);

        var issued = await h.Service.IssueAsync(ServiceAccount,
            new GenesysScreenPopRequestDto("ga-7", "conv-not-ingested-yet", ticketId, "971501234567"));

        Assert.Equal(GenesysScreenPopIssueOutcome.Issued, issued.Outcome);
        Assert.Equal($"/Tickets/{ticketId}", issued.TargetPath);
    }

    [Fact]
    public async Task Landing_CustomerLookup_WhenNoTicketApplies()
    {
        var h = new Harness();
        h.Genesys.MapAgent("ga-7");

        var issued = await h.Service.IssueAsync(ServiceAccount,
            new GenesysScreenPopRequestDto("ga-7", TicketId: 999999, CustomerPhone: "+971 50 123 4567"));

        Assert.Equal("/Customers/Lookup?phoneNumber=%2B971501234567", issued.TargetPath);
        Assert.Null(issued.TicketId);
    }

    [Fact]
    public async Task Landing_TicketsList_WhenNothingElseIsSupplied()
    {
        var h = new Harness();
        h.Genesys.MapAgent("ga-7");

        var issued = await h.Service.IssueAsync(ServiceAccount, new GenesysScreenPopRequestDto("ga-7"));

        Assert.Equal("/Tickets", issued.TargetPath);
    }

    // ---- Redeem: the one-hour window ----

    [Theory]
    [InlineData(0)]
    [InlineData(59 * 60)]
    [InlineData(60 * 60)] // exactly one hour: still valid
    public async Task Redeem_WithinOneHour_SignsTheMappedUserIn(int secondsAfterIssue)
    {
        var h = new Harness();
        var userId = h.Genesys.MapAgent("ga-7", displayName: "Layla", roles: Roles.CsAgent);
        var token = await h.IssueTokenAsync("ga-7");

        h.Clock.Advance(TimeSpan.FromSeconds(secondsAfterIssue));
        var redeemed = await h.Service.RedeemAsync(token);

        Assert.Equal(GenesysScreenPopRedeemOutcome.SignedIn, redeemed.Outcome);
        Assert.Equal(userId, redeemed.Session!.EmployeeId);
        Assert.Equal("Layla", redeemed.Session.DisplayName);
        Assert.Equal($"jwt-for-{userId}", redeemed.Session.AccessToken);
        // Bound to the account's current security stamp, like a password login's token.
        Assert.Equal(LockoutAccounts.Stamp, Assert.Single(h.Tokens.Stamps));
        Assert.Equal("/Tickets", redeemed.TargetPath);

        // The ordinary session: the user's own roles, nothing added.
        var (employeeId, _, roles) = Assert.Single(h.Tokens.Issued);
        Assert.Equal(userId, employeeId);
        Assert.Equal([Roles.CsAgent], roles);
    }

    [Theory]
    [InlineData(60 * 60 + 1)]
    [InlineData(2 * 60 * 60)]
    public async Task Redeem_AfterOneHour_IsRejectedAsExpired(int secondsAfterIssue)
    {
        var h = new Harness();
        h.Genesys.MapAgent("ga-7");
        var token = await h.IssueTokenAsync("ga-7");

        h.Clock.Advance(TimeSpan.FromSeconds(secondsAfterIssue));
        var redeemed = await h.Service.RedeemAsync(token);

        Assert.Equal(GenesysScreenPopRedeemOutcome.Expired, redeemed.Outcome);
        Assert.Null(redeemed.Session);
        Assert.Empty(h.Tokens.Issued);
    }

    // ---- Redeem: one time only ----

    [Fact]
    public async Task Redeem_Twice_TheSecondIsRejected()
    {
        var h = new Harness();
        h.Genesys.MapAgent("ga-7");
        var token = await h.IssueTokenAsync("ga-7");

        var first = await h.Service.RedeemAsync(token);
        var second = await h.Service.RedeemAsync(token);

        Assert.Equal(GenesysScreenPopRedeemOutcome.SignedIn, first.Outcome);
        Assert.Equal(GenesysScreenPopRedeemOutcome.AlreadyUsed, second.Outcome);
        Assert.Single(h.Tokens.Issued);
    }

    [Fact]
    public async Task Redeem_LosingAConcurrentRedemptionRace_IsRejected_AndIssuesNoSession()
    {
        var h = new Harness();
        h.Genesys.MapAgent("ga-7");
        var token = await h.IssueTokenAsync("ga-7");
        h.Launches.LoseNextSaveRace = true;

        var redeemed = await h.Service.RedeemAsync(token);

        Assert.Equal(GenesysScreenPopRedeemOutcome.AlreadyUsed, redeemed.Outcome);
        Assert.Empty(h.Tokens.Issued);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not-a-token-anyone-issued")]
    public async Task Redeem_UnknownOrBlankToken_IsRejected(string? token)
    {
        var h = new Harness();
        h.Genesys.MapAgent("ga-7");
        await h.IssueTokenAsync("ga-7");

        var redeemed = await h.Service.RedeemAsync(token);

        Assert.Equal(GenesysScreenPopRedeemOutcome.InvalidToken, redeemed.Outcome);
        Assert.Empty(h.Tokens.Issued);
    }

    // ---- Redeem: the mapping must still hold ----

    [Fact]
    public async Task Redeem_AfterTheAgentWasUnmapped_IsRejected_AndTheTokenIsConsumed()
    {
        var h = new Harness();
        h.Genesys.MapAgent("ga-7");
        var token = await h.IssueTokenAsync("ga-7");
        h.Genesys.AgentMappings.Unmap("ga-7");

        var redeemed = await h.Service.RedeemAsync(token);

        Assert.Equal(GenesysScreenPopRedeemOutcome.AgentNotMapped, redeemed.Outcome);
        Assert.Empty(h.Tokens.Issued);
        Assert.NotNull(Assert.Single(h.Launches.All).RedeemedAtUtc);
    }

    [Fact]
    public async Task Redeem_AfterTheAgentWasRemappedToAnotherUser_IsRejected()
    {
        var h = new Harness();
        h.Genesys.MapAgent("ga-7");
        var token = await h.IssueTokenAsync("ga-7");
        h.Genesys.AgentMappings.Unmap("ga-7");
        h.Genesys.MapAgent("ga-7");

        var redeemed = await h.Service.RedeemAsync(token);

        Assert.Equal(GenesysScreenPopRedeemOutcome.AgentNotMapped, redeemed.Outcome);
        Assert.Empty(h.Tokens.Issued);
    }

    [Fact]
    public async Task Redeem_AfterTheMappedUserWasDeactivated_IsRejected()
    {
        var h = new Harness();
        h.Genesys.MapAgent("ga-7");
        var token = await h.IssueTokenAsync("ga-7");
        h.Genesys.AgentMappings.Deactivate("ga-7");

        var redeemed = await h.Service.RedeemAsync(token);

        Assert.Equal(GenesysScreenPopRedeemOutcome.AgentInactive, redeemed.Outcome);
        Assert.Empty(h.Tokens.Issued);
    }

    [Fact]
    public async Task Redeem_WhenTheMappedAccountIsLockedOut_IsRejected()
    {
        var h = new Harness();
        var userId = h.Genesys.MapAgent("ga-7");
        var token = await h.IssueTokenAsync("ga-7");
        h.Accounts.Locked.Add(userId);

        var redeemed = await h.Service.RedeemAsync(token);

        Assert.Equal(GenesysScreenPopRedeemOutcome.AgentInactive, redeemed.Outcome);
        Assert.Empty(h.Tokens.Issued);
    }

    // ---- Domain guard ----

    [Theory]
    [InlineData("https://evil.example/x")]
    [InlineData("//evil.example/x")]
    [InlineData("/\\evil.example")]
    [InlineData("Tickets")]
    public void Launch_RefusesAnyTargetThatIsNotAppRelative(string targetPath) =>
        Assert.Throws<ArgumentException>(() => new GenesysScreenPopLaunch(
            new string('a', 64), "ga-7", Guid.NewGuid(), targetPath, null, null, ServiceAccount, IssuedAt));
}
