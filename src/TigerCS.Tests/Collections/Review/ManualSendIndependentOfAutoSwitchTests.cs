using TigerCS.Application.Modules.Collections;
using TigerCS.Application.Modules.Collections.Review;
using TigerCS.Application.Modules.Collections.Services;
using TigerCS.Domain.Modules.Collections.Review;

namespace TigerCS.Tests.Collections.Review;

/// <summary>
/// <c>Collections:Campaigns:AutoSendingEnabled</c> switches the SCHEDULED automatic dispatcher only. An authorized person's Manual "Approve &amp; Send to Genesys" never consults it: the
/// controller admits Manual lists only, the approval and the upload are gated by the financial, authorization, contact and Genesys-configuration checks alone. What stops a Manual send in
/// a shipped environment is the Genesys outbound configuration (Enabled, LiveCustomerDispatchEnabled, SuppressionEnabled, the OAuth client, background jobs), named by the readiness
/// endpoint, never the Auto switch. The harness clock is 14 Oct 2026; the fake Genesys client records uploads and makes no call.
/// </summary>
public sealed class ManualSendIndependentOfAutoSwitchTests
{
    private static ReviewFilter Manual() => new(ReminderType: "CurrentMonth", SendingMode: "Manual", MinRemaining: 0m);

    [Fact]
    public async Task WithAutoSendingOff_AManualApproveAndSend_IsAcceptedExecutedAndUploaded_WhileAutoDoesNothing()
    {
        using var h = new ReviewHarness();
        Assert.False(h.Campaign.AutoSendingEnabled);                                                    // the default, and it stays off throughout
        h.Source.Items.AddRange([ReviewHarness.Row(1, 500m), ReviewHarness.Row(2, 700m)]);
        await h.RefreshAsync();
        var (summary, confirm) = await h.PrepareAsync(h.All(Manual()), "manual-only");
        Assert.Equal(2, summary.Count); Assert.True(summary.SourceReconciled);
        using var scope = h.NewScope();
        // The automatic dispatcher is inert ...
        var auto = await scope.Auto.RunAsync(CancellationToken.None);
        Assert.False(auto.Ran); Assert.Contains("AutoSendingEnabled is false", auto.Detail); Assert.Empty(h.Scheduler.Dispatches);
        // ... and a person's Manual send goes through every gate and is uploaded.
        var accepted = await scope.Dispatch.ConfirmAsync(h.Manager, confirm, CancellationToken.None);
        Assert.Equal(CollectionsOutcome.Accepted, accepted.Outcome);
        var dispatchId = Assert.Single(h.Scheduler.Dispatches);
        await scope.Dispatch.ExecuteAsync(dispatchId, CancellationToken.None);
        var dispatch = (await scope.Dispatch.GetAsync(h.Manager, accepted.Value!.DispatchId, CancellationToken.None)).Value!;
        Assert.Equal((DispatchStatus.Completed.ToString(), 2, 2), (dispatch.Status, dispatch.ApprovedCount, dispatch.UploadedCount));
        Assert.Equal(2, h.Client.Calls.Single().Contacts.Count);
        Assert.False(h.Campaign.AutoSendingEnabled);                                                    // nothing on the way switched it on
    }

    [Fact]
    public async Task TheReadinessGates_NeverNameAutoSendingEnabled_TheyAreTheGenesysAndJobSettings()
    {
        using var h = new ReviewHarness();
        h.Genesys.ClientId = "client"; h.Genesys.ClientSecret = "secret";
        using var scope = h.NewScope();
        var ready = (await scope.Dispatch.ReadinessAsync(h.Manager, CancellationToken.None)).Value!;
        Assert.True(ready.Ready);                                                                       // Auto off, and a Manual send is still fully possible
        // The shipped configuration shape: Genesys outbound off, live dispatch off, suppression off, no client. Those, and only those, block a Manual send.
        h.Genesys.Enabled = false; h.Genesys.LiveCustomerDispatchEnabled = false; h.Genesys.SuppressionEnabled = false; h.Genesys.ClientId = ""; h.Genesys.ClientSecret = "";
        var blocked = (await scope.Dispatch.ReadinessAsync(h.Manager, CancellationToken.None)).Value!;
        Assert.False(blocked.Ready);
        Assert.DoesNotContain(blocked.Blockers, b => b.Contains("AutoSendingEnabled", StringComparison.Ordinal));
        Assert.Equal(4, blocked.Blockers.Count);
        foreach (var setting in new[] { "GenesysOutbound:Enabled", "LiveCustomerDispatchEnabled", "SuppressionEnabled", "ClientId / ClientSecret" })
            Assert.Contains(blocked.Blockers, b => b.Contains(setting, StringComparison.Ordinal));
        // Confirmation refuses on the same grounds, without mentioning the Auto switch.
        var refused = await scope.Dispatch.ConfirmAsync(h.Manager, new ConfirmDispatchRequest(h.All(Manual()), 0, "x", "blocked", true), CancellationToken.None);
        Assert.Equal(CollectionsOutcome.Disabled, refused.Outcome); Assert.DoesNotContain("AutoSendingEnabled", refused.Detail ?? "");
    }

    [Fact]
    public void TheManualSendPath_DoesNotReadTheAutoSwitch_AndTheShippedFilesKeepItOff()
    {
        var root = AppContext.BaseDirectory; while (root is not null && !Directory.Exists(Path.Combine(root, "src", "TigerCS.Api"))) root = Path.GetDirectoryName(root);
        Assert.NotNull(root);
        // The Manual chain: controller -> DispatchService (approval, revalidation, upload). None of them consults AutoSendingEnabled; only the Auto dispatcher and its job registration do.
        foreach (var file in new[] { "src/TigerCS.Api/Controllers/CollectionsReviewController.cs", "src/TigerCS.Application/Modules/Collections/Review/DispatchService.cs" })
        {
            var code = File.ReadAllText(Path.Combine(root!, file));
            Assert.DoesNotContain("campaignOptions.AutoSendingEnabled", code);
            Assert.DoesNotContain(".AutoSendingEnabled)", code);
        }
        Assert.Contains("campaignOptions.AutoSendingEnabled", File.ReadAllText(Path.Combine(root!, "src/TigerCS.Application/Modules/Collections/Review/AutoDispatchService.cs")));
        foreach (var file in Directory.GetFiles(Path.Combine(root!, "src", "TigerCS.Api"), "appsettings*.json"))
        {
            var json = File.ReadAllText(file);
            Assert.DoesNotMatch("\"AutoSendingEnabled\"\\s*:\\s*true", json);                           // automatic sending stays off everywhere
            Assert.DoesNotMatch("\"LiveCustomerDispatchEnabled\"\\s*:\\s*true", json);                  // live dispatch is enabled per environment through secrets / variables, never committed
        }
        Assert.False(new CollectionsCampaignOptions().AutoSendingEnabled);
    }
}
