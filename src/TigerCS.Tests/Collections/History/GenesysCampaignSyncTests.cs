using Microsoft.Extensions.Logging.Abstractions;
using TigerCS.Application.Modules.Collections;
using TigerCS.Application.Modules.Collections.History;
using TigerCS.Application.Modules.Collections.Review;
using TigerCS.Application.Modules.Collections.Services;
using TigerCS.Domain.Modules.Collections.Review;
using TigerCS.Infrastructure.Modules.Collections;
using TigerCS.Tests.Collections.Review;
using TigerCS.Tests.IdentityAndAccess.Fakes;
using static TigerCS.Tests.Collections.History.HistoryKit;

namespace TigerCS.Tests.Collections.History;

/// <summary>The delivery rules and the manual Sync from Genesys: real counts from Genesys data, no double counting, no invented outcome, read only towards Genesys.</summary>
public sealed class GenesysCampaignSyncTests
{
    private const string Campaign = "5b0d3b2e-3f55-4d36-9d5c-0b7f3a6f0a11";

    // ------------------------------------------------------------ rules

    [Theory]
    [InlineData(GenesysContactOutcomes.Delivered, "Delivered")]
    [InlineData(GenesysContactOutcomes.Delivered, "Failed", "Delivered", "Sent")]
    [InlineData(GenesysContactOutcomes.Sent, "Failed", "Sent")]
    [InlineData(GenesysContactOutcomes.Failed, "Failed", "Failed")]
    [InlineData(GenesysContactOutcomes.Pending, "Queued")]
    [InlineData(GenesysContactOutcomes.Pending, "Unknown", "Queued")]
    [InlineData(GenesysContactOutcomes.Failed, "Failed", "Queued")]
    public void AContactIsOneOutcome_WhateverItsAttempts(string expected, params string[] statuses) =>
        Assert.Equal(expected, GenesysDeliveryRules.ContactOutcome(statuses));

    [Fact]
    public void NoMessages_IsPending_NotFailed_AndNeverReachedContactsAreNotSent()
    {
        Assert.Equal(GenesysContactOutcomes.Pending, GenesysDeliveryRules.Outcome(DispatchItemStatus.UploadedToGenesys, true, []));
        Assert.Equal(GenesysContactOutcomes.NotSent, GenesysDeliveryRules.Outcome(DispatchItemStatus.Excluded, false, []));
        Assert.Equal(GenesysContactOutcomes.NotSent, GenesysDeliveryRules.Outcome(DispatchItemStatus.Failed, true, ["Delivered"]));   // the upload was rejected: nothing could have been delivered
        Assert.Equal(GenesysContactOutcomes.NotSent, GenesysDeliveryRules.Outcome(DispatchItemStatus.Released, true, []));
    }

    [Theory]
    [InlineData("delivered", "Delivered")]
    [InlineData("read", "Delivered")]
    [InlineData("sent", "Sent")]
    [InlineData("published", "Sent")]
    [InlineData("failed", "Failed")]
    [InlineData("queued", "Queued")]
    [InlineData("something-new", "Unknown")]
    public void GenesysStatusesMapWithoutInventingAnything(string raw, string expected) => Assert.Equal(expected, GenesysMessageStates.FromGenesys(raw));

    [Fact]
    public void AnInboundReply_IsNotADeliveryState() => Assert.Null(GenesysMessageStates.FromGenesys("received"));

    // ------------------------------------------------------------ the fake Genesys

    private sealed class ReadClient : IGenesysOutboundClient
    {
        public List<GenesysCampaignConversation> Conversations { get; } = [];
        public Dictionary<string, List<GenesysMessageInfo>> Messages { get; } = new(StringComparer.OrdinalIgnoreCase);
        public GenesysUploadOutcome ConversationOutcome { get; set; } = GenesysUploadOutcome.Accepted;
        public int MessageReads, ConversationReads;

        public Task<GenesysCampaignConversationsResult> GetCampaignConversationsAsync(string campaignId, DateTime fromUtc, DateTime toUtc, CancellationToken ct)
        {
            ConversationReads++;
            return Task.FromResult(ConversationOutcome == GenesysUploadOutcome.Accepted
                ? new GenesysCampaignConversationsResult(GenesysUploadOutcome.Accepted, 200, Conversations, true, null)
                : new GenesysCampaignConversationsResult(ConversationOutcome, 403, [], false, "Genesys rejected the campaign interactions lookup: HTTP 403."));
        }

        public Task<GenesysConversationMessagesResult> GetConversationMessagesAsync(string conversationId, CancellationToken ct)
        {
            MessageReads++;
            return Task.FromResult(new GenesysConversationMessagesResult(GenesysUploadOutcome.Accepted, 200, Messages.GetValueOrDefault(conversationId) ?? [], null));
        }

        // Everything that could change Genesys is forbidden here: a sync must never reach it.
        public Task<GenesysUploadResult> UploadContactsAsync(string id, IReadOnlyList<GenesysContactPayload> contacts, CancellationToken ct) => throw new InvalidOperationException("upload");
        public Task<GenesysSuppressResult> SetNotCallableAsync(string id, string contactId, GenesysContactPayload contact, CancellationToken ct) => throw new InvalidOperationException("suppress");
        public Task<GenesysCampaignCreateResult> CreateCampaignAsync(string name, string id, GenesysCampaignOptions template, CancellationToken ct) => throw new InvalidOperationException("create");
        public Task<GenesysCampaignLookupResult> FindCampaignsByNameAsync(string name, CancellationToken ct) => throw new InvalidOperationException("lookup");
    }

    private sealed class FixedTime(DateTime utc) : TimeProvider { public override DateTimeOffset GetUtcNow() => new(utc, TimeSpan.Zero); }

    private static GenesysCampaignSyncService NewSync(ReviewHarness h, TigerCS.Infrastructure.Persistence.TigerCsDbContext context, ReadClient client) =>
        new(new CollectionsAuthorizationService(h.Options, new FakeDepartmentRepository()), new CollectionsClock(h.Options, new FixedTime(new DateTime(2026, 10, 20, 12, 0, 0, DateTimeKind.Utc))),
            new GenesysDeliveryStore(context), client, NullLogger<GenesysCampaignSyncService>.Instance);

    private static GenesysHistoryService NewHistory(ReviewHarness h, TigerCS.Infrastructure.Persistence.TigerCsDbContext context) =>
        new(h.Options, h.Genesys, new CollectionsAuthorizationService(h.Options, new FakeDepartmentRepository()), new GenesysHistoryStore(context),
            NullLogger<GenesysHistoryService>.Instance, null, null, new GenesysDeliveryStore(context));

    /// <summary>Six contacts: four uploaded, one whose upload Genesys rejected, one excluded before upload.</summary>
    private static async Task<(Guid Id, long DispatchId, List<string> ContactIds)> SeedCampaignAsync(ReviewHarness h)
    {
        using var context = h.CreateContext();
        var d = await SeedAsync(context, DispatchStatus.CompletedWithErrors,
        [
            new(DispatchItemStatus.UploadedToGenesys), new(DispatchItemStatus.UploadedToGenesys), new(DispatchItemStatus.UploadedToGenesys), new(DispatchItemStatus.UploadedToGenesys),
            new(DispatchItemStatus.Failed, Reason: "rejected"), new(DispatchItemStatus.Excluded, InBatch: false)
        ], campaignId: Campaign);
        return (d.PublicId, d.CollectionsDispatchId, d.Items.Select(i => i.GenesysContactId).Where(x => x != null).Select(x => x!).ToList());
    }

    private static GenesysMessageInfo Msg(string id, string status, int minute) => new(id, status, new DateTime(2026, 10, 14, 9, minute, 0, DateTimeKind.Utc), status == "failed" ? "Carrier rejected the number" : null);

    private static ReadClient ScriptedClient(IReadOnlyList<string> contacts)
    {
        var client = new ReadClient();
        var c = new[] { "c1", "c2", "c3" }.Select(n => Guid.NewGuid().ToString("D")).ToArray();
        for (var i = 0; i < 3; i++) client.Conversations.Add(new(c[i], contacts[i], "list", null));
        client.Conversations.Add(new(Guid.NewGuid().ToString("D"), "contact-from-another-dispatch", "list", null));       // a reused Genesys campaign: not this dispatch's contact
        client.Messages[c[0]] = [Msg("m1", "delivered", 2)];
        client.Messages[c[1]] = [Msg("m2", "failed", 3), Msg("m3", "sent", 8)];                                          // a retry: one contact, two attempts
        client.Messages[c[2]] = [Msg("m4", "failed", 4)];
        return client;                                                                                                   // the 4th uploaded contact has no interaction at all
    }

    // ------------------------------------------------------------ the sync

    [Fact]
    public async Task ASync_CountsContactsOnce_KeepsAttemptsApart_AndKeepsNotSentOutOfFailures()
    {
        using var h = new ReviewHarness();
        var (id, dispatchId, contacts) = await SeedCampaignAsync(h);
        var client = ScriptedClient(contacts);

        using (var context = h.CreateContext())
        {
            var result = await NewSync(h, context, client).SyncAsync(h.Manager, [id], false, CancellationToken.None);
            Assert.True(result.IsSuccess, result.Detail);
            Assert.Equal((1, 0, 0), (result.Value!.Synced, result.Value.Failed, result.Value.Skipped));
        }

        using var read = h.CreateContext();
        var row = (await NewHistory(h, read).ListAsync(h.Manager, new GenesysHistoryFilter(), 1, 25, CancellationToken.None)).Value!.Items.Single(i => i.SubmissionId == id).Delivery!;
        Assert.True(row.Synced);
        Assert.Equal((2, 1, 1, 1, 2, 4), (row.Sent!.Value, row.Delivered!.Value, row.Failed!.Value, row.Pending!.Value, row.NotSent, row.MessageAttempts));
        // 6 contacts = Sent 2 (including the 1 delivered) + Failed 1 + Pending 1 + NotSent 2: the rejected upload and the exclusion are not delivery failures, and the
        // retried contact is one contact with two attempts.
        Assert.Equal(6, row.Sent + row.Failed + row.Pending + row.NotSent);
        Assert.Equal(3, client.MessageReads);                        // one read per matched interaction, none for the foreign one
    }

    [Fact]
    public async Task SyncingAgain_NeverDuplicatesMessagesOrChangesTheFigures_AndFinalInteractionsAreNotReadAgain()
    {
        using var h = new ReviewHarness();
        var (id, dispatchId, contacts) = await SeedCampaignAsync(h);
        var client = ScriptedClient(contacts);
        for (var i = 0; i < 2; i++)
        {
            using var context = h.CreateContext();
            Assert.True((await NewSync(h, context, client).SyncAsync(h.Manager, [id], true, CancellationToken.None)).IsSuccess);
        }
        using var check = h.CreateContext();
        Assert.Equal(4, check.CollectionsGenesysMessageResults.Count());                    // m1..m4 once each
        var state = Assert.Single(check.CollectionsGenesysSyncStates);
        Assert.Equal((2, 1, 1, 1, 2, 4), (state.Sent, state.Delivered, state.Failed, state.Pending, state.NotSent, state.MessageAttempts));
        Assert.Equal(3 + 1, client.MessageReads);   // second pass re-read only the interaction that is not final (its second message is only Sent)
    }

    [Fact]
    public async Task AFailedSync_KeepsTheLastConfirmedFigures_AndSaysItFailed()
    {
        using var h = new ReviewHarness();
        var (id, _, contacts) = await SeedCampaignAsync(h);
        var client = ScriptedClient(contacts);
        using (var context = h.CreateContext()) Assert.True((await NewSync(h, context, client).SyncAsync(h.Manager, [id], true, CancellationToken.None)).IsSuccess);

        client.ConversationOutcome = GenesysUploadOutcome.Rejected;
        using (var context = h.CreateContext())
        {
            var result = await NewSync(h, context, client).SyncAsync(h.Manager, [id], true, CancellationToken.None);
            Assert.Equal((0, 1), (result.Value!.Synced, result.Value.Failed));
            Assert.Contains("403", Assert.Single(result.Value.Problems));
        }
        using var read = h.CreateContext();
        var delivery = (await NewHistory(h, read).ListAsync(h.Manager, new GenesysHistoryFilter(), 1, 25, CancellationToken.None)).Value!.Items.Single(i => i.SubmissionId == id).Delivery!;
        Assert.Equal("Failed", delivery.SyncOutcome);
        Assert.Equal((2, 1, 1, 1), (delivery.Sent, delivery.Delivered, delivery.Failed, delivery.Pending));      // not zeroed, not guessed
    }

    [Fact]
    public async Task ACampaignThatWasNeverSynced_ShowsNoDeliveryFigures_NotZeros()
    {
        using var h = new ReviewHarness();
        var (id, _, _) = await SeedCampaignAsync(h);
        using var read = h.CreateContext();
        var delivery = (await NewHistory(h, read).ListAsync(h.Manager, new GenesysHistoryFilter(), 1, 25, CancellationToken.None)).Value!.Items.Single(i => i.SubmissionId == id).Delivery!;
        Assert.False(delivery.Synced);
        Assert.Null(delivery.Sent); Assert.Null(delivery.Delivered); Assert.Null(delivery.Failed); Assert.Null(delivery.Pending);
        Assert.Equal(2, delivery.NotSent);
    }

    [Fact]
    public async Task ACampaignWithoutAGenesysCampaignId_CannotBeMatched_AndSaysSo()
    {
        using var h = new ReviewHarness();
        Guid id;
        using (var context = h.CreateContext())
            id = (await SeedAsync(context, DispatchStatus.Completed, [new(DispatchItemStatus.UploadedToGenesys)], campaignId: null)).PublicId;
        using var sync = h.CreateContext();
        var result = await NewSync(h, sync, new ReadClient()).SyncAsync(h.Manager, [id], true, CancellationToken.None);
        Assert.Contains("no Genesys campaign id", Assert.Single(result.Value!.Problems));
    }

    [Fact]
    public async Task OnlyASenderCanSync_AndAnUnknownCampaignIsReported()
    {
        using var h = new ReviewHarness();
        var (id, _, _) = await SeedCampaignAsync(h);
        using var context = h.CreateContext();
        var sync = NewSync(h, context, new ReadClient());
        Assert.Equal(CollectionsOutcome.Forbidden, (await sync.SyncAsync(h.Agent, [id], true, CancellationToken.None)).Outcome);
        Assert.Equal(CollectionsOutcome.InvalidRequest, (await sync.SyncAsync(h.Manager, [], true, CancellationToken.None)).Outcome);
        Assert.Equal(1, (await sync.SyncAsync(h.Manager, [Guid.NewGuid()], true, CancellationToken.None)).Value!.Failed);
    }

    [Fact]
    public async Task TheRecordsShowStatusTimesReasonAndInteractionId()
    {
        using var h = new ReviewHarness();
        var (id, _, contacts) = await SeedCampaignAsync(h);
        using (var context = h.CreateContext()) await NewSync(h, context, ScriptedClient(contacts)).SyncAsync(h.Manager, [id], true, CancellationToken.None);
        using var read = h.CreateContext();
        var records = (await NewHistory(h, read).RecordsAsync(h.Manager, id, null, null, 1, 50, CancellationToken.None)).Value!.Items;
        var byStatus = records.GroupBy(r => r.MessageStatus).ToDictionary(g => g.Key!, g => g.ToList());
        var delivered = Assert.Single(byStatus["Delivered"]);
        Assert.NotNull(delivered.DeliveredAtUtc);
        Assert.NotNull(delivered.InteractionId);
        var failed = Assert.Single(byStatus["Failed"]);
        Assert.Equal("Carrier rejected the number", failed.FailureReason);
        var retried = Assert.Single(byStatus["Sent"]);
        Assert.Equal((2, "Sent"), (retried.MessageAttempts, retried.MessageStatus));
        Assert.NotNull(retried.SentAtUtc);
        Assert.Equal(2, byStatus["Not sent"].Count);
        Assert.Single(byStatus["Pending / Unknown"]);
    }

    [Fact]
    public async Task TheCompanyFilter_AndColumn_SeparateDubaiFromSharjah()
    {
        using var h = new ReviewHarness();
        await SeedCampaignAsync(h);
        var all = await ListAsync(h);
        Assert.Equal(["Dubai"], Assert.Single(all.Items).Companies);
        Assert.Single((await ListAsync(h, new GenesysHistoryFilter(Company: 4))).Items);
        Assert.Empty((await ListAsync(h, new GenesysHistoryFilter(Company: 32))).Items);
        using var context = h.CreateContext();
        var bad = await NewHistory(h, context).ListAsync(h.Manager, new GenesysHistoryFilter(Company: 7), 1, 25, CancellationToken.None);
        Assert.Equal(CollectionsOutcome.InvalidRequest, bad.Outcome);
    }
}
