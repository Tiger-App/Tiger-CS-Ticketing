using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using TigerCS.Application.Modules.Collections;
using TigerCS.Application.Modules.Collections.Abstractions;
using TigerCS.Application.Modules.Collections.Review;
using TigerCS.Application.Modules.Collections.Services;
using TigerCS.Domain.Modules.Collections.Review;
using TigerCS.Tests.IdentityAndAccess.Fakes;

namespace TigerCS.Tests.Collections.Review;

/// <summary>
/// "The Follow Up contact list could not be checked in Genesys (... HTTP 404 ...)": the failing request is GET /api/v2/outbound/contactlists/{id} at the configured API host, made before anything is
/// uploaded. These tests cover what a person needs to correct it (the exact setting key and id, the region and organization, the permission), the READ-ONLY connection check that verifies all of it, and
/// that a send never falls back to another list. Everything runs against the fake client: nothing here reaches Genesys, uploads, or switches a send on.
/// </summary>
public sealed class GenesysConnectionCheckTests
{
    private static readonly GenesysContactListInfo NotFound = new(GenesysUploadOutcome.Rejected, 404, [], "Genesys rejected the contact list lookup (GET /api/v2/outbound/contactlists/{contactListId} at api.mypurecloud.de): HTTP 404 Not Found. Nothing was changed.");
    private static readonly GenesysContactListInfo Forbidden = new(GenesysUploadOutcome.Rejected, 403, [], "Genesys rejected the contact list lookup (...): HTTP 403 Forbidden. Nothing was changed.");

    private static ReviewHarness Configured(Action<ReviewHarness>? configure = null)
    {
        var h = new ReviewHarness();
        h.Genesys.ClientId = "client-id-VALUE"; h.Genesys.ClientSecret = "client-secret-VALUE";
        configure?.Invoke(h);
        return h;
    }

    private static GenesysConnectionCheckService Service(ReviewHarness h)
    {
        var auth = new CollectionsAuthorizationService(h.Options, new FakeDepartmentRepository());
        return new GenesysConnectionCheckService(h.Options, h.Genesys, auth, new CollectionsClock(h.Options, h.Time), h.Client, NullLogger<GenesysConnectionCheckService>.Instance);
    }

    private static string ListId(ReviewHarness h, CampaignReminderType type) => h.Genesys.ContactListIdFor(type);

    private static GenesysCheckLineDto Line(GenesysConnectionCheckDto check, string name) => Assert.Single(check.Lines, l => l.Name == name);

    private static void AssertNothingWasWritten(ReviewHarness h)
    {
        Assert.Empty(h.Client.Calls); Assert.Empty(h.Client.Suppressions); Assert.Empty(h.Client.CampaignCreates); Assert.Empty(h.Client.CampaignLookups);   // read only: no upload, no suppression, no campaign touched
        Assert.Empty(h.Scheduler.Dispatches); Assert.False(h.Campaign.AutoSendingEnabled);
    }

    // ------------------------------------------------------------ the connection check

    [Fact]
    public async Task AHealthyConfiguration_ChecksTheRegion_TheOrganization_AndEveryListOnce_AndWritesNothing()
    {
        using var h = Configured();
        var check = (await Service(h).CheckAsync(h.Manager, CancellationToken.None)).Value!;
        Assert.True(check.Ok);
        Assert.Equal(("login.mypurecloud.de", "api.mypurecloud.de"), (check.LoginHost, check.ApiHost));
        Assert.Equal("Ok", Line(check, "Region").Status); Assert.Contains("mypurecloud.de", Line(check, "Region").Detail);
        var org = Line(check, "OAuth client and organization");
        Assert.Equal("Ok", org.Status); Assert.Contains("Tiger Test Org", org.Detail); Assert.Contains("org-1", org.Detail);
        foreach (var type in Enum.GetValues<CampaignReminderType>())
        {
            var line = Line(check, $"ContactListIds:{type}");
            Assert.Equal("Ok", line.Status); Assert.Contains(ListId(h, type), line.Detail); Assert.Contains("six template columns", line.Detail);
        }
        var manual = Line(check, "ContactListIds:Manual");                                           // the dedicated Manual list is read too
        Assert.Equal("Ok", manual.Status); Assert.Contains(ReviewHarness.ManualListId, manual.Detail);
        Assert.Equal(Enum.GetValues<CampaignReminderType>().Length + 1, h.Client.ListChecks.Distinct().Count());
        Assert.Equal(1, h.Client.OrganizationChecks);
        AssertNothingWasWritten(h);
    }

    [Fact]
    public async Task A404OnOneList_PointsAtThatSettingAndId_InTheOrganizationTheClientActsIn_AndTheOthersStayOk()
    {
        using var h = Configured();
        var followUp = ListId(h, CampaignReminderType.FollowUp);
        h.Client.ListAnswers[followUp] = NotFound;
        var check = (await Service(h).CheckAsync(h.Manager, CancellationToken.None)).Value!;
        Assert.False(check.Ok);
        var bad = Line(check, "ContactListIds:FollowUp");
        Assert.Equal("Failed", bad.Status);
        Assert.Contains("HTTP 404", bad.Detail); Assert.Contains(followUp, bad.Detail);
        Assert.Contains("organization \"Tiger Test Org\" at api.mypurecloud.de", bad.Detail);
        Assert.Contains("correct Collections:GenesysOutbound:ContactListIds:FollowUp", bad.Detail);
        Assert.Contains("another organization or region", bad.Detail);
        Assert.Equal(5, check.Lines.Count(l => l.Name.StartsWith("ContactListIds:", StringComparison.Ordinal) && l.Status == "Ok"));
        AssertNothingWasWritten(h);
    }

    [Fact]
    public async Task AllListsMissing_IsTheSignatureOfAWrongOrganizationOrRegion_EveryKeyIsNamed()
    {
        using var h = Configured();
        foreach (var type in Enum.GetValues<CampaignReminderType>()) h.Client.ListAnswers[ListId(h, type)] = NotFound;
        var check = (await Service(h).CheckAsync(h.Manager, CancellationToken.None)).Value!;
        Assert.False(check.Ok);
        Assert.Equal("Ok", Line(check, "OAuth client and organization").Status);                     // authenticated fine: the lists are simply not in this organization
        Assert.All(Enum.GetValues<CampaignReminderType>(), type => Assert.Equal("Failed", Line(check, $"ContactListIds:{type}").Status));
        h.Client.ListAnswers[ReviewHarness.ManualListId] = NotFound;
        Assert.Equal("Failed", Line((await Service(h).CheckAsync(h.Manager, CancellationToken.None)).Value!, "ContactListIds:Manual").Status);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-a-guid")]
    public async Task AMissingOrInvalidManualListId_IsNamed_AndNothingIsReadForIt(string id)
    {
        using var h = Configured(x => x.Genesys.ContactListIds["Manual"] = id);
        var check = (await Service(h).CheckAsync(h.Manager, CancellationToken.None)).Value!;
        var line = Line(check, "ContactListIds:Manual");
        Assert.Equal("Failed", line.Status); Assert.Contains("ContactListIds:Manual", line.Detail);
        Assert.False(check.Ok); Assert.DoesNotContain(ReviewHarness.ManualListId, h.Client.ListChecks);
    }

    [Fact]
    public async Task A403OnAList_NamesTheMissingPermission()
    {
        using var h = Configured();
        h.Client.ListAnswers[ListId(h, CampaignReminderType.Overdue)] = Forbidden;
        var line = Line((await Service(h).CheckAsync(h.Manager, CancellationToken.None)).Value!, "ContactListIds:Overdue");
        Assert.Equal("Failed", line.Status); Assert.Contains("HTTP 403", line.Detail); Assert.Contains("outbound:contactList:view", line.Detail);
    }

    [Fact]
    public async Task ADifferentRegionForLoginAndApi_IsReportedWithoutAnyRequest_Needed()
    {
        using var h = Configured(x => { x.Genesys.LoginBaseUrl = "https://login.mypurecloud.de"; x.Genesys.ApiBaseUrl = "https://api.mec1.pure.cloud"; });
        var check = (await Service(h).CheckAsync(h.Manager, CancellationToken.None)).Value!;
        var region = Line(check, "Region");
        Assert.Equal("Failed", region.Status); Assert.Contains("mypurecloud.de", region.Detail); Assert.Contains("mec1.pure.cloud", region.Detail); Assert.Contains("not valid in the other", region.Detail);
        Assert.False(check.Ok);
        Assert.Equal(("login.mypurecloud.de", "api.mec1.pure.cloud"), (check.LoginHost, check.ApiHost));
    }

    [Fact]
    public async Task UnusualHostNames_AreNotComparedButSaySo()
    {
        using var h = Configured(x => { x.Genesys.LoginBaseUrl = "https://sso.example.test"; x.Genesys.ApiBaseUrl = "https://gateway.example.test"; });
        var region = Line((await Service(h).CheckAsync(h.Manager, CancellationToken.None)).Value!, "Region");
        Assert.Equal("Info", region.Status); Assert.Contains("were not compared", region.Detail);
    }

    [Fact]
    public async Task AnOAuthFailure_IsReported_AndTheListsAreSkippedBecauseTheyCouldOnlyRepeatIt()
    {
        using var h = Configured(x => x.Client.Organization = new(GenesysUploadOutcome.Rejected, null, null, null, "The Genesys login service refused the credentials (HTTP 400, invalid_client)."));
        var check = (await Service(h).CheckAsync(h.Manager, CancellationToken.None)).Value!;
        Assert.False(check.Ok);
        var org = Line(check, "OAuth client and organization");
        Assert.Equal("Failed", org.Status); Assert.Contains("invalid_client", org.Detail);
        Assert.Equal("Skipped", Line(check, "Contact lists").Status); Assert.Empty(h.Client.ListChecks);
    }

    [Fact]
    public async Task AnOrganizationAnswerFromTheApi_EvenAnError_DoesNotStopTheListChecks()
    {
        using var h = Configured(x => x.Client.Organization = new(GenesysUploadOutcome.Rejected, 403, null, null, "Genesys rejected the organization lookup (...): HTTP 403 Forbidden."));
        var check = (await Service(h).CheckAsync(h.Manager, CancellationToken.None)).Value!;
        Assert.Equal("Failed", Line(check, "OAuth client and organization").Status);
        Assert.Equal(Enum.GetValues<CampaignReminderType>().Length + 1, h.Client.ListChecks.Count);   // the token worked: the lists are still checked
    }

    [Fact]
    public async Task WhenTheIntegrationIsOffOrIncomplete_NothingIsSentToGenesys()
    {
        using var off = Configured(x => x.Genesys.Enabled = false);
        var offCheck = (await Service(off).CheckAsync(off.Manager, CancellationToken.None)).Value!;
        Assert.False(offCheck.Ok); Assert.Equal("Failed", Line(offCheck, "Collections:GenesysOutbound:Enabled").Status);
        Assert.Empty(off.Client.ListChecks); Assert.Equal(0, off.Client.OrganizationChecks);

        using var noSecret = Configured(x => x.Genesys.ClientSecret = "");
        var noSecretCheck = (await Service(noSecret).CheckAsync(noSecret.Manager, CancellationToken.None)).Value!;
        Assert.Equal("Failed", Line(noSecretCheck, "ClientId / ClientSecret").Status); Assert.Equal("Skipped", Line(noSecretCheck, "Genesys requests").Status);
        Assert.Empty(noSecret.Client.ListChecks); Assert.Equal(0, noSecret.Client.OrganizationChecks);

        using var badUrl = Configured(x => x.Genesys.ApiBaseUrl = "api.mypurecloud.de");
        Assert.Equal("Failed", Line((await Service(badUrl).CheckAsync(badUrl.Manager, CancellationToken.None)).Value!, "Collections:GenesysOutbound:ApiBaseUrl").Status);
        Assert.Equal(0, badUrl.Client.OrganizationChecks);
    }

    [Fact]
    public async Task ListIdsThatAreMissingOrShared_AreNamed()
    {
        using var h = Configured(x => { x.Genesys.ContactListIds["LegalCase"] = "not-a-guid"; x.Genesys.ContactListIds["FollowUp"] = x.Genesys.ContactListIds["CurrentMonth"]; });
        var check = (await Service(h).CheckAsync(h.Manager, CancellationToken.None)).Value!;
        Assert.Equal("Failed", Line(check, "ContactListIds:LegalCase").Status); Assert.Contains("GUID", Line(check, "ContactListIds:LegalCase").Detail);
        var shared = check.Lines.Where(l => l.Name == "ContactListIds:CurrentMonth / ContactListIds:FollowUp").ToList();
        Assert.Equal(["Info", "Ok"], shared.Select(l => l.Status));                                    // said once as information, and the one shared list is read once
        Assert.Contains("share one list id", shared[0].Detail);
        Assert.Equal(4, h.Client.ListChecks.Count);                                                  // the shared id is read once
    }

    [Fact]
    public async Task TheCheckNeedsThePermissionToSend_AndShowsNoSecret()
    {
        using var h = Configured();
        Assert.Equal(CollectionsOutcome.Forbidden, (await Service(h).CheckAsync(h.Agent, CancellationToken.None)).Outcome);
        Assert.Equal(CollectionsOutcome.Forbidden, (await Service(h).CheckAsync(h.Reporting, CancellationToken.None)).Outcome);
        Assert.Equal(0, h.Client.OrganizationChecks);
        h.Options.Enabled = false;
        Assert.Equal(CollectionsOutcome.Disabled, (await Service(h).CheckAsync(h.Manager, CancellationToken.None)).Outcome);
        h.Options.Enabled = true;
        var json = JsonSerializer.Serialize((await Service(h).CheckAsync(h.Manager, CancellationToken.None)).Value);
        Assert.DoesNotContain("client-id-VALUE", json); Assert.DoesNotContain("client-secret-VALUE", json); Assert.DoesNotContain("Bearer", json); Assert.DoesNotContain("https://", json);   // hosts only, never a URL
    }

    [Fact]
    public async Task TheCheckIsIndependentOfTheAutoSwitchAndOfLiveDispatch()
    {
        using var h = Configured(x => { x.Campaign.AutoSendingEnabled = false; x.Genesys.LiveCustomerDispatchEnabled = false; x.Genesys.SuppressionEnabled = false; });
        var check = (await Service(h).CheckAsync(h.Manager, CancellationToken.None)).Value!;
        Assert.True(check.Ok);
        Assert.False(h.Campaign.AutoSendingEnabled); Assert.False(h.Genesys.LiveCustomerDispatchEnabled);      // nothing was switched on by checking
    }

    // ------------------------------------------------------------ the real send stops with the exact setting to correct, and never falls back

    private static async Task<(ReviewHarness H, ConfirmDispatchRequest Confirm)> ManualSendAsync(Action<ReviewHarness>? configure = null)
    {
        var h = Configured(configure);
        h.Time.Advance(TimeSpan.FromDays(1)); h.Source.ReadAt = ReviewHarness.Now.AddDays(1);
        h.Source.Items.AddRange([ReviewHarness.Row(1, 500m), ReviewHarness.Row(2, 700m)]);
        await h.RefreshAsync();
        var (_, confirm) = await h.PrepareAsync(h.All(new ReviewFilter(ReminderType: "CurrentMonth", SendingMode: "Manual", MinRemaining: 0m)), "manual");
        return (h, confirm);
    }

    [Fact]
    public async Task AManualSend_Stopped_By404_NamesTheExactSettingAndId_UploadsNothing_AndNeverUsesAnotherList()
    {
        var (h, confirm) = await ManualSendAsync();
        using (h)
        {
            var followUp = ReviewHarness.ManualListId;
            h.Client.ListAnswers[followUp] = NotFound;
            using var scope = h.NewScope();
            var refused = await scope.Dispatch.ConfirmAsync(h.Manager, confirm, CancellationToken.None);
            Assert.Equal(CollectionsOutcome.InvalidRequest, refused.Outcome);
            var detail = refused.Detail!;
            Assert.StartsWith("The TigerCS Manual Collections contact list could not be checked in Genesys (Genesys rejected the contact list lookup (GET /api/v2/outbound/contactlists/{contactListId} at api.mypurecloud.de)", detail);
            Assert.Contains("HTTP 404 Not Found", detail);
            Assert.Contains($"Check Collections:GenesysOutbound:ContactListIds:Manual (list id {followUp})", detail);
            Assert.Contains("organization of the OAuth client", detail); Assert.Contains("ApiBaseUrl / LoginBaseUrl", detail);
            Assert.EndsWith("Nothing was uploaded.", detail);
            Assert.DoesNotContain("AutoSendingEnabled", detail);                                    // the Auto switch is not part of this
            Assert.Equal([followUp], h.Client.ListChecks);                                          // only the chosen list was looked at: no fallback to a stage list
            AssertNothingWasWritten(h);
            Assert.Empty(h.Client.CampaignChecks);
        }
    }

    [Fact]
    public async Task A403OnTheList_NamesThePermission_AndTheSendStillStops()
    {
        var (h, confirm) = await ManualSendAsync();
        using (h)
        {
            h.Client.ListAnswers[ReviewHarness.ManualListId] = Forbidden;
            using var scope = h.NewScope();
            var refused = await scope.Dispatch.ConfirmAsync(h.Manager, confirm, CancellationToken.None);
            Assert.Equal(CollectionsOutcome.InvalidRequest, refused.Outcome); Assert.Contains("outbound:contactList:view", refused.Detail);
            AssertNothingWasWritten(h);
        }
    }

    [Fact]
    public async Task AWorkingList_StillSendsOnManual_WithAutoOff_AndTheFinancialAndContactChecksIntact()
    {
        var (h, confirm) = await ManualSendAsync();
        using (h)
        {
            using var scope = h.NewScope();
            Assert.False(h.Campaign.AutoSendingEnabled);
            Assert.Equal(CollectionsOutcome.Accepted, (await scope.Dispatch.ConfirmAsync(h.Manager, confirm, CancellationToken.None)).Outcome);
            await scope.Dispatch.ExecuteAsync(h.Scheduler.Dispatches.Single(), CancellationToken.None);
            var upload = h.Client.Calls.Single();
            Assert.Equal(ReviewHarness.ManualListId, upload.ContactListId); Assert.Equal(2, upload.Contacts.Count);
        }
        // The financial gate is untouched by any of this.
        var (unreconciled, blocked) = await ManualSendAsync(x => x.Campaign.FinancialSourceValidated = false);
        using (unreconciled)
        {
            using var scope = unreconciled.NewScope();
            var refused = await scope.Dispatch.ConfirmAsync(unreconciled.Manager, blocked, CancellationToken.None);
            Assert.Equal(CollectionsOutcome.InvalidRequest, refused.Outcome); Assert.Contains("not reconciled", refused.Detail);
            Assert.Empty(unreconciled.Client.ListChecks);                                           // refused before Genesys was asked anything
        }
    }
}
