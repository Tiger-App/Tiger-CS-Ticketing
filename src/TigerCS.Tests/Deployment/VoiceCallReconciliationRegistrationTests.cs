using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using TigerCS.Application.Modules.GenesysIntegration.Services;
using TigerCS.Infrastructure.BackgroundJobs;
using TigerCS.Tests.OpenApi;

namespace TigerCS.Tests.Deployment;

/// <summary>
/// The voice reconciler is wired into the real host: its services resolve (a missing registration would only surface in Production, as a silent job failure), its settings bind
/// from the shipped appsettings with the documented values, and it runs as a hosted service of its own - not behind Hangfire.
/// </summary>
public class VoiceCallReconciliationRegistrationTests
{
    [Theory]
    [InlineData("Development")]
    [InlineData("Production")]
    public void TheServicesResolve_AndTheShippedSettingsAreTheDocumentedOnes(string environment)
    {
        using var factory = new SwaggerApiFactory(environment);
        using var scope = factory.Services.CreateScope();

        Assert.NotNull(scope.ServiceProvider.GetRequiredService<VoiceCallReconciler>());
        Assert.NotNull(scope.ServiceProvider.GetRequiredService<VoiceCallReconciliationJob>());

        var options = scope.ServiceProvider.GetRequiredService<IOptionsMonitor<VoiceCallReconciliationOptions>>().CurrentValue;
        Assert.True(options.Enabled);
        // Every environment ships LIVE (DryRun = false). It used to ship observe-only (true) and was never switched: tickets then stayed assigned to the AI Agent in the
        // Call Center queue for ever (P0). A dry-run default must never return - it silently disables the only job that gives a failed-AI voice call to a person.
        Assert.False(options.DryRun);
        Assert.Equal(300, options.NoAnswerWindowSeconds);                                      // the documented, proposed window
        Assert.Equal(24, options.LookbackHours);
    }

    [Fact]
    public void ItRunsAsItsOwnHostedService_IndependentOfHangfire()
    {
        using var factory = new SwaggerApiFactory("Development");

        var hosted = factory.Services.GetServices<IHostedService>().Select(s => s.GetType()).ToList();

        Assert.Contains(typeof(VoiceCallReconciliationHostedService), hosted);
    }
}
