using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Configuration;
using TigerCS.Api.OpenApi;

namespace TigerCS.Tests.Deployment;

/// <summary>
/// The committed Production configuration (appsettings.json layered with appsettings.Production.json, exactly as the host does
/// outside Development) is safe to ship: no secrets, no test-mode redirection, nothing that sends or runs until deliberately enabled.
/// </summary>
public sealed class ProductionConfigurationTests
{
    private static string ProjectDir(string project, [CallerFilePath] string testFilePath = "") =>
        Path.Combine(Path.GetFullPath(Path.Combine(Path.GetDirectoryName(testFilePath)!, "..", "..")), project);

    private static IConfigurationRoot Layered(string project) =>
        new ConfigurationBuilder()
            .AddJsonFile(Path.Combine(ProjectDir(project), "appsettings.json"), optional: false)
            .AddJsonFile(Path.Combine(ProjectDir(project), "appsettings.Production.json"), optional: false)
            .Build();

    [Fact]
    public void Api_HasNoTestModeRedirection_AndNoOutboundDeliveryEnabled()
    {
        var c = Layered("TigerCS.Api");

        Assert.False(c.GetValue<bool>("VerificationTesting:Enabled"));
        Assert.True(string.IsNullOrEmpty(c["VerificationTesting:SmsOverride"]));
        Assert.True(string.IsNullOrEmpty(c["VerificationTesting:EmailOverride"]));
        Assert.Equal("false", c["CustomerNotifications:IsTestMode"]);
        Assert.True(string.IsNullOrEmpty(c["CustomerNotifications:TestEmail"]));
        Assert.True(string.IsNullOrEmpty(c["CustomerNotifications:TestPhone"]));

        Assert.Equal("Disabled", c["Sms:Provider"]);
        Assert.False(c.GetValue<bool>("EmailNotifications:Enabled"));
        Assert.False(c.GetValue<bool>("CrmDocuments:Enabled"));
        Assert.False(c.GetValue<bool>("CrmDocuments:AllowInProduction"));
        Assert.False(c.GetValue<bool>("Collections:GenesysOutbound:Enabled"));
        Assert.False(c.GetValue<bool>("Collections:GenesysOutbound:LiveCustomerDispatchEnabled"));
        Assert.False(c.GetValue<bool>("Collections:Channels:SmsEnabled"));
        Assert.False(c.GetValue<bool>("Collections:Channels:EmailEnabled"));
        Assert.False(c.GetValue<bool>("Collections:Campaigns:AdminNotification:Enabled"));
    }

    [Fact]
    public void Api_KeepsBackgroundJobsAndStartupRefreshesOff_UntilDeliberatelyEnabled()
    {
        var c = Layered("TigerCS.Api");

        Assert.False(c.GetValue<bool>("BackgroundJobs:Enabled"));
        Assert.False(c.GetValue<bool>("Collections:Enabled"));
        Assert.False(c.GetValue<bool>("Collections:ReceivablesSnapshot:RefreshOnStartup"));
        Assert.False(c.GetValue<bool>("CollectionsSource:CrmOwners:RefreshOnStartup"));
        Assert.False(c.GetValue<bool>("Genesys:Enabled"));
    }

    [Fact]
    public void Api_ShipsTheCallCenterReconcilerDisabled_AndInDryRun()
    {
        var c = Layered("TigerCS.Api");

        // The reconciler writes ticket ownership when enabled with DryRun=false. Production ships it OFF and, even if switched on, writing
        // nothing: enabling it for real is a deliberate, separate decision.
        Assert.False(c.GetValue<bool>("CallCenterReconciliation:Enabled"));
        Assert.True(c.GetValue<bool>("CallCenterReconciliation:DryRun"));
        Assert.True(c.GetValue<bool>("CallCenterReconciliation:CsAgentRoleOnly"));
    }

    [Fact]
    public void Api_ShipsTheStalePhoneAssignmentDisabled_InDryRun_WithTheApprovedSixHourThreshold()
    {
        var c = Layered("TigerCS.Api");

        // Approved rule: a live Phone conversation silent for more than 6 hours stops blocking assignment. The recurring process writes ownership when
        // enabled with DryRun=false, so Production ships it OFF and, even if switched on, writing nothing until live use is approved.
        Assert.False(c.GetValue<bool>("StalePhoneAssignment:Enabled"));
        Assert.True(c.GetValue<bool>("StalePhoneAssignment:DryRun"));
        Assert.Equal(6, c.GetValue<int>("StalePhoneAssignment:StaleAfterHours"));
        Assert.Equal(6, c.GetValue<int>("CsAgentBackfill:StalePhoneAiAfterHours"));
        Assert.False(c.GetValue<bool>("CsAgentBackfill:Enabled"));                                   // a live historical backfill stays gated
        Assert.Null(c["CsAgentBackfill:StaleAiAfterHours"]);                                         // the generic all-channel stale rule stays off
    }

    [Fact]
    public void Api_UsesTheRealProviders_NotMockOrFixtureOnes()
    {
        var c = Layered("TigerCS.Api");

        Assert.Equal("Http", c["Crm:Provider"]);
        Assert.Equal("Unavailable", c["CollectionsSource:Provider"]);
        Assert.False(string.Equals("Recording", c["EmailNotifications:Provider"], StringComparison.OrdinalIgnoreCase));
        Assert.False(string.Equals("Fixture", c["CollectionsSource:EdsmProvider"], StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Api_CarriesNoSecretsAndNoDevelopmentOrUatHosts()
    {
        var c = Layered("TigerCS.Api");

        // Every credential-bearing key must be empty in the committed files; real values arrive via environment variables.
        string[] secretKeys =
        [
            "Jwt:SigningKey", "Crm:SecretKey", "PactApi:ApiKey", "EmailNotifications:Password", "Sms:Broadnet:Password",
            "CrmDocuments:OtpCodePepper", "Collections:GenesysOutbound:ClientSecret"
        ];
        foreach (var key in secretKeys)
        {
            Assert.True(string.IsNullOrEmpty(c[key]), $"{key} must not be committed");
        }

        foreach (var name in new[] { "TigerCsDatabase", "PACTRPT", "CrmDatabase" })
        {
            var connectionString = c.GetConnectionString(name) ?? string.Empty;
            Assert.DoesNotMatch(new Regex(@"Password\s*=\s*[^;\s]", RegexOptions.IgnoreCase), connectionString);
            // TrustServerCertificate=True is a temporary connectivity-test setting, never the committed Production value.
            Assert.DoesNotMatch(new Regex(@"TrustServerCertificate\s*=\s*(True|Yes)", RegexOptions.IgnoreCase), connectionString);
        }

        var production = c.GetConnectionString("TigerCsDatabase")!;
        Assert.Contains("Database=TigerCS_Ticketing_Prod", production);
        Assert.Contains("Encrypt=True", production);
        // The shared base file's non-Production database hosts must not leak through the Production layer.
        Assert.True(string.IsNullOrEmpty(c.GetConnectionString("PACTRPT")));
        Assert.True(string.IsNullOrEmpty(c.GetConnectionString("CrmDatabase")));
        Assert.True(string.IsNullOrEmpty(c["PactApi:BaseUrl"]));
    }

    [Fact]
    public void Api_JwtIdentifiersAreCommitted_ButTheSigningKeyIsNot()
    {
        var c = Layered("TigerCS.Api");

        Assert.False(string.IsNullOrWhiteSpace(c["Jwt:Issuer"]));
        Assert.False(string.IsNullOrWhiteSpace(c["Jwt:Audience"]));
        Assert.True(string.IsNullOrEmpty(c["Jwt:SigningKey"]));
    }

    [Fact]
    public void Swagger_IsNeverExposedInProduction() =>
        Assert.DoesNotContain("Production", OpenApiDocumentation.EnabledEnvironments);

    [Fact]
    public void Web_CallsTheApiOnItsInternalLoopbackAddress_WithATrailingSlash()
    {
        var baseUrl = Layered("TigerCS.Web")["TigerCsApi:BaseUrl"];

        Assert.Equal("http://localhost:8091/", baseUrl);
        // HttpClient.BaseAddress only keeps a path prefix when it ends in '/'.
        Assert.True(Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri) && uri.AbsolutePath == "/");
    }
}
