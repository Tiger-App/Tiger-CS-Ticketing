using TigerCS.Application.Modules.Ticketing.Dto;
using TigerCS.Domain.Modules.IdentityAndAccess;
using TigerCS.Domain.Modules.Ticketing;
using TigerCS.Infrastructure.Persistence;

namespace TigerCS.Tests.Ticketing.Dashboard;

/// <summary>
/// The Dashboard's complete channel breakdown (every channel of the catalogue, zero rows included,
/// unmapped ids under "Other", no duplicate names, nothing lost) and the two columns on Tickets
/// Requiring Attention — Channel and Last Action By — proven against the real SQL read model.
/// </summary>
public sealed class DashboardChannelAndLastActionTests : IDisposable
{
    private static readonly string[] CrossDepartment = [Roles.CsSupervisor];
    private static readonly DateTime Now = DashboardSqliteFixture.Now;

    // Foreign keys off so a ticket can reference a channel id the catalogue does not have, exactly the "unmapped" case.
    private readonly DashboardSqliteFixture _db = new(enforceForeignKeys: false);

    public void Dispose() => _db.Dispose();

    private Task<DashboardOverviewDto> OverviewAsync(DashboardOverviewRequestDto? request = null, string[]? roles = null, Guid? caller = null)
    {
        using var context = _db.CreateContext();
        return _db.CreateService(context).GetOverviewAsync(caller ?? Guid.NewGuid(), roles ?? CrossDepartment, request ?? new DashboardOverviewRequestDto());
    }

    private static DashboardBreakdownItemDto Row(DashboardOverviewDto o, string label) => o.VolumeByChannel.Single(r => r.Label == label);

    // ---------------------------------------------------------------
    // Volume by Channel
    // ---------------------------------------------------------------

    [Fact]
    public async Task VolumeByChannel_ListsEveryActiveChannelOfTheCatalogue_WithZeroRowsAtZeroPercent()
    {
        using (var context = _db.CreateContext())
        {
            _db.AddTicket(context, _db.CustomerServiceId, channelId: WellKnownChannels.Phone);
            _db.AddTicket(context, _db.CustomerServiceId, channelId: WellKnownChannels.Phone);
            _db.AddTicket(context, _db.CustomerServiceId, channelId: WellKnownChannels.WhatsApp);
        }

        var overview = await OverviewAsync();

        string[] activeChannels =
        [
            "Phone", "WhatsApp", "Live Chat", "Social Media Direct Message", "Website", "Walk in / Kiosk",
            "Mobile App (Customer Portal)", "Instagram", "Facebook"
        ];
        Assert.Equal(activeChannels.Order(), overview.VolumeByChannel.Select(r => r.Label).Order());

        Assert.Equal(2, Row(overview, "Phone").Count);
        Assert.Equal(1, Row(overview, "WhatsApp").Count);
        var facebook = Row(overview, "Facebook");
        Assert.Equal(0, facebook.Count);
        Assert.Equal(0.0, facebook.Percentage);
        Assert.Equal(WellKnownChannels.Facebook.ToString(), facebook.Key);
        Assert.Equal("FACEBOOK", facebook.Code);

        // Retired legacy channels are not offered while they carry nothing.
        Assert.DoesNotContain(overview.VolumeByChannel, r => r.Label.Contains("Legacy", StringComparison.Ordinal));

        // Nothing lost, nothing double counted.
        Assert.Equal(3, overview.VolumeTotal);
        Assert.Equal(overview.VolumeTotal, overview.VolumeByChannel.Sum(r => r.Count));
        Assert.Equal(100.0, overview.VolumeByChannel.Sum(r => r.Percentage), precision: 0);
    }

    [Fact]
    public async Task VolumeByChannel_StillListsEveryChannel_WhenThereAreNoTickets()
    {
        var overview = await OverviewAsync();

        Assert.Equal(0, overview.VolumeTotal);
        Assert.Equal(9, overview.VolumeByChannel.Count);
        Assert.All(overview.VolumeByChannel, r =>
        {
            Assert.Equal(0, r.Count);
            Assert.Equal(0.0, r.Percentage);
        });
    }

    [Fact]
    public async Task VolumeByChannel_ShowsARetiredChannelWhileItCarriesTickets_SoHistoryIsNeverHidden()
    {
        using (var context = _db.CreateContext())
        {
            _db.AddTicket(context, _db.CustomerServiceId, channelId: WellKnownChannels.AppOrWebsite);
            _db.AddTicket(context, _db.CustomerServiceId, channelId: WellKnownChannels.Phone);
        }

        var overview = await OverviewAsync();

        Assert.Equal(1, Row(overview, "App / Website (Legacy)").Count);
        Assert.Equal(overview.VolumeTotal, overview.VolumeByChannel.Sum(r => r.Count));
    }

    [Fact]
    public async Task VolumeByChannel_KeepsTicketsWithNoChannelHonest_AndFoldsUnmappedIdsIntoOther()
    {
        using (var context = _db.CreateContext())
        {
            _db.AddTicket(context, _db.CustomerServiceId, channelId: WellKnownChannels.Phone);
            _db.AddTicket(context, _db.CustomerServiceId); // no originating interaction at all
            _db.AddTicket(context, _db.CustomerServiceId, channelId: 200); // an id the catalogue does not know
            _db.AddTicket(context, _db.CustomerServiceId, channelId: 201);
        }

        var overview = await OverviewAsync();

        var other = Row(overview, "Other");
        Assert.Equal(2, other.Count);
        Assert.Null(other.Key); // not a drill-down: it is not one channel
        Assert.Equal("OTHER", other.Code);
        var unrecorded = Row(overview, "No channel recorded");
        Assert.Equal(1, unrecorded.Count);
        Assert.Null(unrecorded.Key);

        Assert.Equal(4, overview.VolumeTotal);
        Assert.Equal(4, overview.VolumeByChannel.Sum(r => r.Count));
        Assert.Equal(50.0, other.Percentage);
    }

    [Fact]
    public async Task VolumeByChannel_NeverListsTheSameChannelNameTwice_AndKeepsEveryTicket()
    {
        byte duplicateId;
        using (var context = _db.CreateContext())
        {
            // Two catalogue rows that differ only in case / padding: one channel to the reader.
            var duplicate = new Channel("  whatsapp ", "WA_DUPLICATE", true, true, displayOrder: 50);
            context.Channels.Add(duplicate);
            context.SaveChanges();
            duplicateId = duplicate.ChannelId;

            _db.AddTicket(context, _db.CustomerServiceId, channelId: WellKnownChannels.WhatsApp);
            _db.AddTicket(context, _db.CustomerServiceId, channelId: duplicateId);
        }

        var overview = await OverviewAsync();

        var whatsApp = overview.VolumeByChannel.Where(r => string.Equals(r.Label.Trim(), "whatsapp", StringComparison.OrdinalIgnoreCase)).ToList();
        var merged = Assert.Single(whatsApp);
        Assert.Equal(2, merged.Count);
        Assert.Equal(WellKnownChannels.WhatsApp.ToString(), merged.Key);
        Assert.Equal(overview.VolumeTotal, overview.VolumeByChannel.Sum(r => r.Count));
    }

    [Fact]
    public async Task VolumeByChannel_RespectsTheDateRangeAndTheCallersScope_WhileStillListingEveryChannel()
    {
        using (var context = _db.CreateContext())
        {
            _db.AddTicket(context, _db.CollectionsId, channelId: WellKnownChannels.Phone);
            _db.AddTicket(context, _db.CollectionsId, channelId: WellKnownChannels.WhatsApp, createdAtUtc: Now.AddDays(-90)); // outside the default 30 days
            _db.AddTicket(context, _db.RegistrationId, channelId: WellKnownChannels.Phone); // another department
        }

        var overview = await OverviewAsync(roles: [Roles.DepartmentHead], caller: _db.CollectionsHeadId);

        Assert.Equal(1, overview.VolumeTotal);
        Assert.Equal(1, Row(overview, "Phone").Count);
        Assert.Equal(0, Row(overview, "WhatsApp").Count);
        Assert.Equal(overview.VolumeTotal, overview.VolumeByChannel.Sum(r => r.Count));
    }

    // ---------------------------------------------------------------
    // Tickets Requiring Attention: Channel + Last Action By
    // ---------------------------------------------------------------

    [Fact]
    public async Task RecentTickets_CarryTheOriginatingChannel_OrNothingWhenNoneIsRecorded()
    {
        long withChannel, without;
        using (var context = _db.CreateContext())
        {
            withChannel = _db.AddTicket(context, _db.CustomerServiceId, channelId: WellKnownChannels.WhatsApp).TicketId;
            without = _db.AddTicket(context, _db.CustomerServiceId).TicketId;
        }

        var overview = await OverviewAsync();

        var a = overview.RecentTickets.Single(t => t.TicketId == withChannel);
        Assert.Equal("WhatsApp", a.ChannelName);
        Assert.Equal("WHATSAPP", a.ChannelCode);
        Assert.Equal(WellKnownChannels.WhatsApp, a.ChannelId);
        var b = overview.RecentTickets.Single(t => t.TicketId == without);
        Assert.Null(b.ChannelName);
        Assert.Null(b.ChannelId);
    }

    [Fact]
    public async Task LastActionBy_IsTheActorOfTheNewestMeaningfulAction_NeverTheOwnerOrACreator()
    {
        long none, newestStatusChange, systemIgnored, aiReply, humanReply;
        using (var context = _db.CreateContext())
        {
            // No recorded action at all - and an owner, who must NOT be assumed to be the last actor.
            none = _db.AddTicket(context, _db.CustomerServiceId, owner: _db.CsAgentId, channelId: WellKnownChannels.Phone).TicketId;

            // A note, then a newer status change by someone else.
            newestStatusChange = _db.AddTicket(context, _db.CustomerServiceId, channelId: WellKnownChannels.Phone).TicketId;
            context.TicketNotes.Add(new TicketNote(newestStatusChange, "called the customer", _db.CsAgentId, Now.AddMinutes(-50)));
            context.TicketStatusHistoryEntries.Add(new TicketStatusHistory(
                newestStatusChange, TicketStatusDimension.TicketStatus, (byte)TicketStatus.Open, (byte)TicketStatus.InProgress,
                _db.RegistrationEmployeeId, false, null, Guid.NewGuid(), Now.AddMinutes(-20)));

            // A person's assignment, then a NEWER system row: the system row is background activity and never counts.
            systemIgnored = _db.AddTicket(context, _db.CustomerServiceId, channelId: WellKnownChannels.Phone).TicketId;
            context.TicketAssignments.Add(new TicketAssignment(systemIgnored, _db.CsAgentId, _db.CustomerServiceId, Now.AddMinutes(-40), _db.CollectionsHeadId));
            context.TicketStatusHistoryEntries.Add(new TicketStatusHistory(
                systemIgnored, TicketStatusDimension.TicketStatus, (byte)TicketStatus.Open, (byte)TicketStatus.InProgress,
                null, true, null, Guid.NewGuid(), Now.AddMinutes(-5)));
            context.TicketStatusHistoryEntries.Add(new TicketStatusHistory(
                systemIgnored, TicketStatusDimension.SlaState, 1, 2, _db.RegistrationEmployeeId, false, null, Guid.NewGuid(), Now.AddMinutes(-4)));

            // A human note, then a newer virtual-agent reply: the AI is named as such.
            aiReply = _db.AddTicket(context, _db.CustomerServiceId, channelId: WellKnownChannels.WhatsApp).TicketId;
            context.TicketNotes.Add(new TicketNote(aiReply, "customer asked about the deed", _db.CsAgentId, Now.AddMinutes(-30)));
            var aiInteraction = context.TicketInteractions.First(i => i.TicketId == aiReply);
            context.TicketInteractionMessages.Add(new TicketInteractionMessage(aiInteraction.TicketInteractionId, 1, InteractionMessageSender.VirtualAgent, "Tiger Bot", null, Now.AddMinutes(-10), "How can I help?"));

            // A human reply (named) then a newer customer line and a platform line: only the human counts.
            humanReply = _db.AddTicket(context, _db.CustomerServiceId, channelId: WellKnownChannels.LiveChat).TicketId;
            context.TicketNotes.Add(new TicketNote(humanReply, "older note", _db.CsAgentId, Now.AddMinutes(-45)));
            var chat = context.TicketInteractions.First(i => i.TicketId == humanReply);
            context.TicketInteractionMessages.AddRange(
                new TicketInteractionMessage(chat.TicketInteractionId, 1, InteractionMessageSender.HumanAgent, "Sara Agent", "agent-1", Now.AddMinutes(-15), "On it."),
                new TicketInteractionMessage(chat.TicketInteractionId, 2, InteractionMessageSender.Customer, "Customer", null, Now.AddMinutes(-8), "Thanks"),
                new TicketInteractionMessage(chat.TicketInteractionId, 3, InteractionMessageSender.System, null, null, Now.AddMinutes(-2), "Conversation ended"));
            context.SaveChanges();
        }

        var overview = await OverviewAsync();

        string? LastBy(long id) => overview.RecentTickets.Single(t => t.TicketId == id).LastActionByName;
        Assert.Null(LastBy(none));
        Assert.Equal("Rana Registrar", LastBy(newestStatusChange));
        Assert.Equal("Hadi Head", LastBy(systemIgnored));
        Assert.Equal("AI Assistant", LastBy(aiReply));
        Assert.Equal("Sara Agent", LastBy(humanReply));
    }

    [Fact]
    public async Task LastActionBy_ReadsTransfersAndNotes_AndIsPerTicket()
    {
        long transferred, noted;
        using (var context = _db.CreateContext())
        {
            transferred = _db.AddTicket(context, _db.CustomerServiceId, channelId: WellKnownChannels.Phone).TicketId;
            context.TicketWorkflowEvents.Add(new TicketWorkflowEvent(
                transferred, WorkflowEventType.RequestTypeReclassified, Now.AddMinutes(-12), _db.CollectionsHeadId, null, null, Guid.NewGuid()));
            context.TicketWorkflowEvents.Add(new TicketWorkflowEvent(
                transferred, WorkflowEventType.ManualAssignmentRequired, Now.AddMinutes(-3), null, null, null, Guid.NewGuid())); // system, ignored

            noted = _db.AddTicket(context, _db.CustomerServiceId, channelId: WellKnownChannels.Phone).TicketId;
            context.TicketNotes.Add(new TicketNote(noted, "left a voicemail", _db.CsAgentId, Now.AddMinutes(-9)));
            context.SaveChanges();
        }

        var overview = await OverviewAsync();

        Assert.Equal("Hadi Head", overview.RecentTickets.Single(t => t.TicketId == transferred).LastActionByName);
        Assert.Equal("Amal Agent", overview.RecentTickets.Single(t => t.TicketId == noted).LastActionByName);
    }
}
