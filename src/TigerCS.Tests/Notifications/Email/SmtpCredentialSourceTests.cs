using System.Runtime.CompilerServices;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using TigerCS.Integrations.Modules.EmailIntegration;

namespace TigerCS.Tests.Notifications.Email;

/// <summary>
/// Where the SMTP mailbox password can come from. The committed settings carry it as an empty string, so the only
/// way a working password reaches <see cref="SmtpClientTransport"/> (which reads <see cref="EmailNotificationOptions.Password"/>
/// from <see cref="IOptions{TOptions}"/>) is a configuration source layered on top: user-secrets in Development, an
/// environment variable (<c>EmailNotifications__Password</c>) or any secret-store provider elsewhere. No real credential is
/// used here; the values are synthetic.
/// </summary>
public class SmtpCredentialSourceTests
{
    private const string Synthetic = "synthetic-smtp-secret-for-tests-only";

    private static string BaseSettingsFile([CallerFilePath] string testFilePath = "")
        => Path.GetFullPath(Path.Combine(
            Path.GetDirectoryName(testFilePath)!, "..", "..", "..", "TigerCS.Api", "appsettings.json"));

    private static EmailNotificationOptions Bind(IConfiguration configuration)
    {
        var services = new ServiceCollection();
        services.Configure<EmailNotificationOptions>(configuration.GetSection(EmailNotificationOptions.SectionName));
        using var provider = services.BuildServiceProvider();
        return provider.GetRequiredService<IOptions<EmailNotificationOptions>>().Value;
    }

    [Fact]
    public void TheCommittedBaseSettings_CarryNoPassword_SoEnablingSmtpWithoutASecretFailsClosed()
    {
        var configuration = new ConfigurationBuilder().AddJsonFile(BaseSettingsFile(), optional: false).Build();

        var options = Bind(configuration);
        options.Enabled = true;

        Assert.True(string.IsNullOrWhiteSpace(options.Password));
        var errors = EmailSenderSafety.Validate(options);
        Assert.Contains(errors, e => e.Contains("EmailNotifications:Password", StringComparison.Ordinal));
        Assert.DoesNotContain(errors, e => e.Contains(Synthetic, StringComparison.Ordinal));
    }

    [Fact]
    public void AnEnvironmentVariable_SuppliesThePassword_ToTheOptionsTheSmtpTransportReads()
    {
        // A unique prefix keeps this process-wide variable invisible to every other test host in the run.
        const string prefix = "TIGERCS_SMTP_CREDENTIAL_TEST_";
        const string variable = prefix + "EmailNotifications__Password";
        Environment.SetEnvironmentVariable(variable, Synthetic);
        try
        {
            var configuration = new ConfigurationBuilder()
                .AddJsonFile(BaseSettingsFile(), optional: false)
                .AddEnvironmentVariables(prefix)
                .Build();

            var options = Bind(configuration);
            options.Enabled = true;

            Assert.Equal(Synthetic, options.Password);
            Assert.DoesNotContain(
                EmailSenderSafety.Validate(options),
                e => e.Contains("EmailNotifications:Password", StringComparison.Ordinal));
        }
        finally
        {
            Environment.SetEnvironmentVariable(variable, null);
        }
    }

    [Fact]
    public void AnyLaterConfigurationSource_SuppliesThePassword_OverTheEmptyCommittedValue()
    {
        // Stands in for user-secrets and for any secret-store configuration provider: a later source wins.
        var configuration = new ConfigurationBuilder()
            .AddJsonFile(BaseSettingsFile(), optional: false)
            .AddInMemoryCollection(new Dictionary<string, string?> { ["EmailNotifications:Password"] = Synthetic })
            .Build();

        var options = Bind(configuration);
        options.Enabled = true;

        Assert.Equal(Synthetic, options.Password);
        Assert.DoesNotContain(EmailSenderSafety.Validate(options), e => e.Contains("Password", StringComparison.Ordinal));
    }
}
