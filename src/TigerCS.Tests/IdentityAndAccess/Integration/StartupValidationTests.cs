using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using TigerCS.Application.Modules.CustomerVerification.CrmIntegration;
using TigerCS.Application.Modules.CustomerVerification.CustomerLookup;
using TigerCS.Application.Modules.Notifications.Abstractions;
using TigerCS.Infrastructure.Persistence;
using TigerCS.Integrations.Modules.CrmIntegration;
using TigerCS.Application.Modules.Notifications;
using TigerCS.Application.Modules.Notifications.Services;
using TigerCS.Integrations.Modules.EmailIntegration;

namespace TigerCS.Tests.IdentityAndAccess.Integration;

/// <summary>
/// Review item 4/8: the app must fail fast at startup on insecure or missing
/// JWT/security configuration, rather than accepting a default or only
/// failing lazily on the first request.
/// </summary>
public class StartupValidationTests
{
    /// <summary>Every <see cref="IEmailSender"/> is wrapped in the customer-delivery guard; these tests are about which provider sits behind it.</summary>
    private static IEmailSender ProviderBehindTheGuard(Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program> factory) =>
        Assert.IsType<TigerCS.Application.Modules.Notifications.Services.CustomerDeliveryGuardEmailSender>(
            factory.Services.GetRequiredService<IEmailSender>()).Inner;

    private sealed class ConfiguredFactory(
        string environment, Dictionary<string, string?> overrides, bool useInMemoryDb = true)
        : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment(environment);

            // ADR-0015's Hangfire server would connect to SQL Server at
            // startup; these tests assert Program.cs's own startup guards, not
            // background-job execution. UseSetting rather than the overrides
            // below because the background-job registration reads this during
            // service registration — see TigerCsApiFactory for the full note.
            builder.UseSetting("BackgroundJobs:Enabled", "false");

            builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(overrides));

            if (useInMemoryDb)
            {
                builder.ConfigureServices(services =>
                {
                    var efCoreDescriptors = services
                        .Where(d => (d.ServiceType.FullName ?? string.Empty).Contains("EntityFrameworkCore", StringComparison.Ordinal)
                            || d.ServiceType == typeof(TigerCsDbContext)
                            || d.ServiceType == typeof(DbContextOptions<TigerCsDbContext>)
                            || d.ServiceType == typeof(DbContextOptions))
                        .ToList();
                    foreach (var descriptor in efCoreDescriptors)
                    {
                        services.Remove(descriptor);
                    }

                    services.AddDbContext<TigerCsDbContext>(o => o.UseInMemoryDatabase(Guid.NewGuid().ToString()));
                });
            }
        }
    }

    private static Dictionary<string, string?> ValidConfig() => new()
    {
        ["ConnectionStrings:TigerCsDatabase"] = "Server=(unused-for-tests);Database=(unused-for-tests);",
        ["Jwt:Issuer"] = "TigerCS.Tests",
        ["Jwt:Audience"] = "TigerCS.Tests.Client",
        ["Jwt:SigningKey"] = "test-only-signing-key-at-least-32-characters-long-1234567890",
        ["Jwt:ExpirationMinutes"] = "60"
    };

    [Fact]
    public void MissingSigningKey_FailsAtStartup()
    {
        var config = ValidConfig();
        config.Remove("Jwt:SigningKey");
        using var factory = new ConfiguredFactory("Testing", config);

        var ex = Assert.ThrowsAny<Exception>(() => factory.Server);
        Assert.Contains("Jwt:SigningKey", ex.ToString());
    }

    [Fact]
    public void SigningKeyShorterThan32Bytes_FailsAtStartup()
    {
        var config = ValidConfig();
        config["Jwt:SigningKey"] = "too-short-key"; // 13 bytes, well under 32
        using var factory = new ConfiguredFactory("Testing", config);

        var ex = Assert.ThrowsAny<Exception>(() => factory.Server);
        Assert.Contains("32 bytes", ex.ToString());
    }

    [Fact]
    public void MissingIssuerOrAudience_FailsAtStartup()
    {
        var config = ValidConfig();
        config.Remove("Jwt:Issuer");
        using var factory = new ConfiguredFactory("Testing", config);

        var ex = Assert.ThrowsAny<Exception>(() => factory.Server);
        Assert.Contains("Jwt:Issuer", ex.ToString());
    }

    /// <summary>
    /// Superseded by the CRM Verification increment. PR #9's original
    /// version of this test proved Production wasn't unconditionally
    /// refused at the JWT/security-config level, by ASPNETCORE_ENVIRONMENT
    /// name alone — that decision (no blanket "if IsProduction() throw")
    /// still stands and is unchanged. What's new: MockCrmGateway must never
    /// run in Production (explicit review requirement) — Program.cs fails
    /// fast if Crm:Provider resolves to "Mock" outside Development/Testing.
    /// "Http" is now the standard provider for every real environment
    /// (<c>CrmGatewayOptions</c>' default and every committed appsettings)
    /// and starts cleanly everywhere, see
    /// <see cref="AnyRealEnvironment_WithHttpCrmProvider_StartsAndNeverResolvesTheMock"/>;
    /// this test configures "Mock" explicitly to pin the guard — a real,
    /// narrow, risk-specific code gate rather than documentation alone
    /// (ADR-0022, docs/DEV-SETUP.md). This guard is conditional
    /// on the selected gateway type, not the environment name itself — a
    /// non-Mock provider starts cleanly in Production, see
    /// <see cref="ProductionEnvironment_WithNonMockCrmProvider_StartsSuccessfully"/>.
    /// </summary>
    [Fact]
    public void ProductionEnvironment_WithMockCrmProvider_FailsAtStartup()
    {
        // Named explicitly: the committed appsettings.json no longer defaults
        // Crm:Provider to "Mock", and this test is about the Mock guard.
        var config = ValidConfig();
        config["Crm:Provider"] = "Mock";

        using var factory = new ConfiguredFactory("Production", config);

        var ex = Assert.ThrowsAny<Exception>(() => factory.Server);
        Assert.Contains("Crm:Provider", ex.ToString());
        Assert.Contains("Mock", ex.ToString());
    }

    /// <summary>
    /// The Notifications increment's equivalent guard.
    /// <c>RecordingEmailSender</c> reports every send as successful without
    /// contacting a provider, so running it outside Development/Testing would
    /// mark tickets acknowledged, write <c>Sent</c> notification rows and
    /// satisfy every dashboard while no customer received anything — a silent
    /// total failure that looks exactly like success. Program.cs refuses to
    /// start instead.
    ///
    /// <para>
    /// The CRM provider is set to a non-Mock value here so the failure this
    /// asserts can only be the email guard: with both at their defaults the
    /// CRM guard would fire first and the test would pass for the wrong
    /// reason. Email delivery is switched on explicitly because the
    /// committed <c>appsettings.json</c> sets
    /// <c>EmailNotifications:Enabled</c> to <c>false</c> for the current
    /// no-delivery phase (see
    /// <see cref="UatEnvironment_WithRecordingEmailProviderAndEmailDisabled_StartsSuccessfully"/>);
    /// the guard only fires when delivery is expected.
    /// </para>
    /// </summary>
    [Theory]
    [InlineData("Production")]
    [InlineData("UAT")]
    public void NonDevelopmentEnvironment_WithRecordingEmailProviderAndEmailEnabled_FailsAtStartup(string environment)
    {
        var config = ValidConfig();
        config["Crm:Provider"] = "InternalCrmGateway";
        config["EmailNotifications:Enabled"] = "true";
        config["EmailNotifications:Provider"] = "Recording";

        using var factory = new ConfiguredFactory(environment, config);

        var ex = Assert.ThrowsAny<Exception>(() => factory.Server);
        Assert.Contains("EmailNotifications:Provider", ex.ToString());
        Assert.Contains("EmailNotifications:Enabled", ex.ToString());
        Assert.Contains("Recording", ex.ToString());
    }

    /// <summary>
    /// Email delivery is not part of the UAT / management-demo phase and only
    /// the recording adapter exists, so a UAT host must be able to start with
    /// <c>EmailNotifications:Provider</c> = <c>Recording</c> once
    /// <c>EmailNotifications:Enabled</c> is <c>false</c>. The flag relaxes
    /// the email startup guard only: the CRM guard is still exercised (a
    /// non-Mock provider is required for the host to start at all), and the
    /// adapter that ends up wired is still <c>RecordingEmailSender</c>, which
    /// contacts no provider — nothing is delivered.
    /// </summary>
    /// <summary>
    /// VerificationTesting redirects OTP/document messages for REAL CRM customers to test inboxes, so it must never be
    /// switchable on in Production (or any non-Local/UAT environment) — nor half-configured anywhere.
    /// </summary>
    [Theory]
    [InlineData("Production", "971564114806", "nidaa.basim@tigergroup.ae")]
    [InlineData("UAT", "", "nidaa.basim@tigergroup.ae")]
    [InlineData("UAT", "971564114806", "")]
    public void VerificationTesting_EnabledInProductionOrIncomplete_FailsAtStartup(string environment, string sms, string email)
    {
        var config = ValidConfig();
        config["Crm:Provider"] = "InternalCrmGateway";
        config["VerificationTesting:Enabled"] = "true";
        config["VerificationTesting:BlockRealCustomerDelivery"] = "true";
        config["VerificationTesting:SmsOverride"] = sms;
        config["VerificationTesting:EmailOverride"] = email;

        using var factory = new ConfiguredFactory(environment, config);

        var ex = Assert.ThrowsAny<Exception>(() => factory.Server);
        Assert.Contains("VerificationTesting", ex.ToString());
    }

    [Fact]
    public void VerificationTesting_DisabledInProduction_StartsNormally()
    {
        var config = ValidConfig();
        config["Crm:Provider"] = "InternalCrmGateway";
        config["VerificationTesting:Enabled"] = "false";

        using var factory = new ConfiguredFactory("Production", config);

        Assert.NotNull(factory.Server);
        Assert.False(factory.Services.GetRequiredService<TigerCS.Application.Modules.CrmDocuments.VerificationTestingPolicy>().Enabled);
    }

    [Fact]
    public void UatEnvironment_WithRecordingEmailProviderAndEmailDisabled_StartsSuccessfully()
    {
        var config = ValidConfig();
        config["Crm:Provider"] = "InternalCrmGateway";
        config["EmailNotifications:Enabled"] = "false";
        config["EmailNotifications:Provider"] = "Recording";

        using var factory = new ConfiguredFactory("UAT", config);

        var server = factory.Server;

        Assert.NotNull(server);
        Assert.IsType<RecordingEmailSender>(ProviderBehindTheGuard(factory));
    }

    /// <summary>Same as the UAT case for a Production host: disabled delivery plus the recording adapter is allowed, and still delivers nothing.</summary>
    [Fact]
    public void ProductionEnvironment_WithRecordingEmailProviderAndEmailDisabled_StartsSuccessfully()
    {
        var config = ValidConfig();
        config["Crm:Provider"] = "InternalCrmGateway";
        config["EmailNotifications:Enabled"] = "false";
        config["EmailNotifications:Provider"] = "Recording";

        using var factory = new ConfiguredFactory("Production", config);

        var server = factory.Server;

        Assert.NotNull(server);
        Assert.IsType<RecordingEmailSender>(ProviderBehindTheGuard(factory));
    }

    /// <summary>
    /// Disabling delivery must not weaken any other startup validation: a UAT
    /// host with <c>EmailNotifications:Enabled</c> = <c>false</c> is still
    /// refused for the Mock CRM adapter, exactly as
    /// <see cref="ProductionEnvironment_WithMockCrmProvider_FailsAtStartup"/>
    /// proves for Production.
    /// </summary>
    [Fact]
    public void UatEnvironment_WithEmailDisabled_StillRefusesMockCrmProvider()
    {
        var config = ValidConfig();
        config["Crm:Provider"] = "Mock";
        config["EmailNotifications:Enabled"] = "false";
        config["EmailNotifications:Provider"] = "Recording";

        using var factory = new ConfiguredFactory("UAT", config);

        var ex = Assert.ThrowsAny<Exception>(() => factory.Server);
        Assert.Contains("Crm:Provider", ex.ToString());
        Assert.Contains("Mock", ex.ToString());
    }

    /// <summary>
    /// Development/Testing keep working with the recording adapter whether or
    /// not delivery is flagged as enabled — the allow-list has always
    /// permitted it there, and the new flag must not change that.
    /// </summary>
    [Theory]
    [InlineData("Development", "true")]
    [InlineData("Development", "false")]
    [InlineData("Testing", "true")]
    [InlineData("Testing", "false")]
    public void DevelopmentAndTesting_WithRecordingEmailProvider_StartSuccessfully(string environment, string emailEnabled)
    {
        var config = ValidConfig();
        config["EmailNotifications:Enabled"] = emailEnabled;
        config["EmailNotifications:Provider"] = "Recording";

        using var factory = new ConfiguredFactory(environment, config);

        var server = factory.Server;

        Assert.NotNull(server);
        Assert.IsType<RecordingEmailSender>(ProviderBehindTheGuard(factory));
    }

    /// <summary>
    /// Final correction: proves the guard is conditional on the selected
    /// gateway type (Crm:Provider), not simply the environment name — a
    /// non-Mock provider must be able to start in Production, since a
    /// future real InternalCrmGateway configuration needs to run there.
    /// <see cref="TigerCS.Integrations.Modules.CrmIntegration.CrmGatewaySafety.IsUnsafe"/>
    /// only ever flags "Mock" outside Development/Testing (see its own unit
    /// tests, CrmGatewaySafetyTests) — a literal, not-yet-implemented
    /// provider value like this test's is registered lazily
    /// (IntegrationsServiceCollectionExtensions) and so does not prevent the
    /// host itself from starting; it would only fail a request that
    /// actually resolves <c>ICrmGateway</c>, which this test never makes.
    /// </summary>
    /// <summary>
    /// Enabling SMTP delivery without the settings it needs would pass
    /// startup and then fail on the first customer email, in a background
    /// job. The guard names the missing keys — and never a value.
    /// </summary>
    [Fact]
    public void ProductionEnvironment_WithSmtpEnabledButNoPassword_FailsAtStartupNamingTheKey()
    {
        var config = ValidConfig();
        config["Crm:Provider"] = "InternalCrmGateway";
        config["EmailNotifications:Enabled"] = "true";
        config["EmailNotifications:Provider"] = "Smtp";
        config["EmailNotifications:Username"] = "no_reply@example.com";
        config["EmailNotifications:FromEmail"] = "no_reply@example.com";
        config["EmailNotifications:Password"] = "";

        using var factory = new ConfiguredFactory("Production", config);

        var ex = Assert.ThrowsAny<Exception>(() => factory.Server);

        Assert.Contains("EmailNotifications:Password", ex.ToString());
        Assert.Contains("docs/Customer-Email-Notifications.md", ex.ToString());
    }

    [Fact]
    public void ProductionEnvironment_WithSmtpFullyConfigured_StartsAndResolvesTheSmtpAdapter()
    {
        var config = ValidConfig();
        config["Crm:Provider"] = "InternalCrmGateway";
        config["EmailNotifications:Enabled"] = "true";
        config["EmailNotifications:Provider"] = "Smtp";
        config["EmailNotifications:SmtpHost"] = "smtp.office365.com";
        config["EmailNotifications:SmtpPort"] = "587";
        config["EmailNotifications:Username"] = "no_reply@example.com";
        config["EmailNotifications:FromEmail"] = "no_reply@example.com";
        config["EmailNotifications:Password"] = "test-only-not-a-real-secret";

        using var factory = new ConfiguredFactory("Production", config);

        Assert.NotNull(factory.Server);
        Assert.IsType<SmtpEmailSender>(ProviderBehindTheGuard(factory));
        Assert.IsType<CustomerEmailSender>(factory.Services.GetRequiredService<ICustomerEmailSender>());
        Assert.True(factory.Services.GetRequiredService<CustomerNotificationPolicy>().Enabled);
    }

    private static Dictionary<string, string?> ProductionConfig()
    {
        var config = ValidConfig();
        config["Crm:Provider"] = "InternalCrmGateway";
        config["EmailNotifications:Provider"] = "Smtp";
        return config;
    }

    /// <summary>
    /// Customer document delivery (email OTP + sending a customer's documents)
    /// stays disabled in Production until real UAT verification passes:
    /// enabling it there without the separate, deliberate AllowInProduction
    /// switch keeps the feature OFF (fail closed) but the host still starts.
    /// </summary>
    [Fact]
    public void ProductionEnvironment_WithCustomerDocumentsEnabledButNotApproved_StartsWithFeatureDisabled()
    {
        var config = ProductionConfig();
        config["CrmDocuments:Enabled"] = "true";
        config["CrmDocuments:OtpCodePepper"] = "test-pepper";
        config["CrmDocuments:OtpSmsEnabled"] = "false";

        using var factory = new ConfiguredFactory("Production", config);

        Assert.NotNull(factory.Server);
        Assert.False(factory.Services.GetRequiredService<TigerCS.Application.Modules.CrmDocuments.CrmDocumentOptions>().Enabled);
    }

    [Fact]
    public void ProductionEnvironment_WithCustomerDocumentsDisabled_OrExplicitlyApproved_Starts()
    {
        using (var disabled = new ConfiguredFactory("Production", ProductionConfig()))
        {
            Assert.NotNull(disabled.Server);
        }

        var approved = ProductionConfig();
        approved["CrmDocuments:Enabled"] = "true";
        approved["CrmDocuments:AllowInProduction"] = "true";
        approved["CrmDocuments:OtpCodePepper"] = "test-pepper";
        approved["CrmDocuments:OtpSmsEnabled"] = "false";
        using var factory = new ConfiguredFactory("Production", approved);
        Assert.NotNull(factory.Server);
        Assert.True(factory.Services.GetRequiredService<TigerCS.Application.Modules.CrmDocuments.CrmDocumentOptions>().Enabled);
    }

    [Fact]
    public void UatEnvironment_WithCustomerDocumentsEnabledButNoOtpPepper_StartsWithFeatureDisabled()
    {
        var config = ProductionConfig();
        config["CrmDocuments:Enabled"] = "true";

        using var factory = new ConfiguredFactory("Staging", config);

        Assert.NotNull(factory.Server);
        Assert.False(factory.Services.GetRequiredService<TigerCS.Application.Modules.CrmDocuments.CrmDocumentOptions>().Enabled);
    }

    [Fact]
    public void UatEnvironment_WithCustomerDocumentsEnabled_Starts()
    {
        var config = ProductionConfig();
        config["CrmDocuments:Enabled"] = "true";
        config["CrmDocuments:OtpCodePepper"] = "test-pepper";

        using var factory = new ConfiguredFactory("Staging", config);

        Assert.NotNull(factory.Server);
    }

    [Fact]
    public void ProductionEnvironment_WithNonMockCrmProvider_StartsSuccessfully()
    {
        var config = ValidConfig();
        config["Crm:Provider"] = "InternalCrmGateway";

        // The Notifications increment added a second, identically-shaped
        // startup guard (EmailSenderSafety): RecordingEmailSender may not run
        // outside Development/Testing either. Both must therefore name a
        // non-double provider for this test to still be about what it says it
        // is about — that Production is not refused by environment name
        // alone.
        config["EmailNotifications:Provider"] = "Smtp";

        using var factory = new ConfiguredFactory("Production", config);

        var server = factory.Server;

        Assert.NotNull(server);
    }

    /// <summary>
    /// "Http" is the standard Crm:Provider for every real environment
    /// (Development, UAT, Production — appsettings.json). It must start
    /// everywhere: it never resolves MockCrmGateway, so CrmGatewaySafety has
    /// nothing to refuse, and the two ports Tiger CRM publishes no endpoint
    /// for resolve UnimplementedCrmHttpGateway — failing closed on use, never
    /// at startup, and never with fixture data.
    /// </summary>
    [Theory]
    [InlineData("Development")]
    [InlineData("UAT")]
    [InlineData("Production")]
    public void AnyRealEnvironment_WithHttpCrmProvider_StartsAndNeverResolvesTheMock(string environment)
    {
        var config = ValidConfig();
        config["Crm:Provider"] = "Http";
        config["EmailNotifications:Enabled"] = "false";
        config["EmailNotifications:Provider"] = "Recording";

        using var factory = new ConfiguredFactory(environment, config);

        Assert.NotNull(factory.Server);
        using var scope = factory.Services.CreateScope();
        Assert.IsType<UnimplementedCrmHttpGateway>(scope.ServiceProvider.GetRequiredService<ICrmGateway>());
        Assert.IsType<UnimplementedCrmHttpGateway>(scope.ServiceProvider.GetRequiredService<ICrmCustomerLookupGateway>());
    }

    [Fact]
    public void PasswordPolicyBelowFloor_FailsAtStartup()
    {
        var config = ValidConfig();
        config["Identity:Password:RequiredLength"] = "4"; // below the 8-char floor
        using var factory = new ConfiguredFactory("Testing", config);

        var ex = Assert.ThrowsAny<Exception>(() => factory.Server);
        Assert.Contains("RequiredLength", ex.ToString());
    }

    [Fact]
    public void ValidConfiguration_StartsSuccessfully()
    {
        using var factory = new ConfiguredFactory("Testing", ValidConfig());

        var server = factory.Server;

        Assert.NotNull(server);
    }
}
