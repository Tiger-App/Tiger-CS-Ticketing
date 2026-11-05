using TigerCS.Application.Modules.Collections.Review;
using TigerCS.Application.Modules.Collections.Services;

namespace TigerCS.Tests.Collections.Review;

/// <summary>The configuration gates in front of "Approve &amp; Send to Genesys" are named (settings only, never secrets) and enforced again at confirmation.</summary>
public sealed class DispatchReadinessTests
{
    [Fact]
    public async Task Readiness_NamesEverySettingThatBlocksSending()
    {
        var h = new ReviewHarness();
        h.Genesys.Enabled = false; h.Genesys.LiveCustomerDispatchEnabled = false; h.Genesys.SuppressionEnabled = false;
        h.Scheduler.Available = false;
        using var scope = h.NewScope();
        var result = await scope.Dispatch.ReadinessAsync(h.Manager, CancellationToken.None);
        var readiness = Assert.IsType<DispatchReadinessDto>(result.Value);
        Assert.False(readiness.Ready);
        foreach (var setting in new[] { "GenesysOutbound:Enabled", "LiveCustomerDispatchEnabled", "SuppressionEnabled", "ClientId / ClientSecret", "BackgroundJobs:Enabled" })
            Assert.Contains(readiness.Blockers, b => b.Contains(setting, StringComparison.Ordinal));
        Assert.DoesNotContain(readiness.Blockers, b => b.Contains("Secret=", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Readiness_IsReady_OnlyWhenEverySettingIsPresent()
    {
        var h = new ReviewHarness();
        h.Genesys.ClientId = "client"; h.Genesys.ClientSecret = "secret";
        using var scope = h.NewScope();
        var readiness = (await scope.Dispatch.ReadinessAsync(h.Manager, CancellationToken.None)).Value!;
        Assert.True(readiness.Ready); Assert.Empty(readiness.Blockers);
        h.Genesys.ContactListIds["Overdue"] = "not-a-guid";
        readiness = (await scope.Dispatch.ReadinessAsync(h.Manager, CancellationToken.None)).Value!;
        Assert.False(readiness.Ready); Assert.Contains(readiness.Blockers, b => b.Contains("ContactListIds:Overdue", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Confirm_IsRefusedWithoutBackgroundJobs_AndNothingIsApprovedOrQueued()
    {
        var h = new ReviewHarness();
        h.Source.Items.Add(ReviewHarness.Row(1, 150m));
        await h.RefreshAsync();
        h.Scheduler.Available = false;
        using var scope = h.NewScope();
        var selection = new SelectionRequest(new ReviewFilter(ReminderType: "CurrentMonth"), ReviewQueryService.ModeSelectedUnits, UnitKeys: [ReviewQueryService.UnitKey(4, "T0001", "TP140-1")]);
        var summary = (await scope.Query.SummarizeAsync(h.Manager, selection, CancellationToken.None)).Value!;
        var result = await scope.Dispatch.ConfirmAsync(h.Manager, new ConfirmDispatchRequest(selection, summary.Count, summary.Fingerprint, "k1", true), CancellationToken.None);
        Assert.Equal(CollectionsOutcome.Disabled, result.Outcome);
        Assert.Contains("BackgroundJobs:Enabled", result.Detail);
        Assert.Empty(h.Scheduler.Dispatches);
        Assert.Empty((await scope.Dispatch.ListAsync(h.Manager, CancellationToken.None)).Value!);
    }
}
