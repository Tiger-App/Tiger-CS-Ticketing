using TigerCS.Application.Modules.Ticketing.Dto;
using TigerCS.Domain.Modules.IdentityAndAccess;
using TigerCS.Domain.Modules.Ticketing;
using TigerCS.Tests.Ticketing.Dashboard;

namespace TigerCS.Tests.Ticketing.Integration;

/// <summary>
/// Real SQL over the real schema: a ticket that entered on any Genesys channel (Phone, WhatsApp, Live Chat, Instagram, Facebook, generic
/// social) is found under THAT channel in the Tickets list's Channel filter, whether Open or Closed, and is visible only to the people
/// entitled to it — its owner (My Tickets / Closed) and nobody else. The channel filter keys on the ORIGINATING interaction's channel id,
/// so this also proves the ids the Genesys resolver writes (<c>WHATSAPP</c>, <c>INSTAGRAM</c>, <c>FACEBOOK</c>, <c>LIVE_CHAT</c>, ...) are the
/// ids the filter and the channel catalogue use.
/// </summary>
public sealed class DigitalChannelTicketVisibilitySqliteTests : IDisposable
{
    private static readonly string[] Agent = [Roles.CsAgent];

    private readonly DashboardSqliteFixture _db = new(enforceForeignKeys: false);
    private readonly Guid _owner = Guid.NewGuid();
    private readonly Guid _otherAgent = Guid.NewGuid();

    public void Dispose() => _db.Dispose();

    private static readonly byte[] AllChannelIds =
    [
        WellKnownChannels.Phone, WellKnownChannels.WhatsApp, WellKnownChannels.LiveChat,
        WellKnownChannels.Instagram, WellKnownChannels.Facebook, WellKnownChannels.SocialMediaDirectMessage
    ];

    public static TheoryData<byte> GenesysChannelIds => new(AllChannelIds);

    private async Task<long[]> ListAsync(Guid caller, TicketListRequestDto request)
    {
        using var context = _db.CreateContext();
        var result = await _db.CreateQueryService(context).GetQueueAsync(caller, Agent, request);
        Assert.Equal(result.Items.Count, result.TotalCount);
        return [.. result.Items.Select(i => i.TicketId).Order()];
    }

    private static TicketListRequestDto Request(byte? channelId = null, string? status = null, Guid? owner = null) =>
        new(null, null, null, status, null, owner, null, null, null, 1, 100, ChannelId: channelId);

    [Theory]
    [MemberData(nameof(GenesysChannelIds))]
    public async Task OpenAndClosedTicketsOfAChannel_AreFoundUnderThatChannelOnly_InTheOwnersViews(byte channelId)
    {
        long open, closed;
        long[] otherChannels;
        using (var context = _db.CreateContext())
        {
            open = _db.AddTicket(context, _db.CustomerServiceId, owner: _owner, channelId: channelId).TicketId;
            closed = _db.AddTicket(context, _db.CustomerServiceId, owner: _owner, channelId: channelId, status: TicketStatus.Closed).TicketId;
            otherChannels = AllChannelIds.Where(c => c != channelId)
                .Select(c => _db.AddTicket(context, _db.CustomerServiceId, owner: _owner, channelId: c).TicketId).ToArray();
        }

        // Tickets list filtered by channel: both the Open and the Closed ticket of that channel, none of any other channel.
        Assert.Equal([open, closed], await ListAsync(_owner, Request(channelId)));
        // My Tickets (owner = viewer) + channel.
        Assert.Equal([open, closed], await ListAsync(_owner, Request(channelId, owner: _owner)));
        // Closed view + channel: only the closed one. Open ticket never appears in it.
        Assert.Equal([closed], await ListAsync(_owner, Request(channelId, status: "Closed")));
        Assert.Equal([open], await ListAsync(_owner, Request(channelId, status: "Open")));
        // No channel filter: everything the owner owns.
        Assert.Equal(new[] { open, closed }.Concat(otherChannels).Order().ToArray(), await ListAsync(_owner, Request()));
    }

    [Theory]
    [MemberData(nameof(GenesysChannelIds))]
    public async Task ATicketOwnedBySomeoneElse_IsNeverVisibleToAnotherAgent_OnAnyChannel(byte channelId)
    {
        using (var context = _db.CreateContext())
        {
            _db.AddTicket(context, _db.CustomerServiceId, owner: _owner, channelId: channelId);
            _db.AddTicket(context, _db.CustomerServiceId, owner: _owner, channelId: channelId, status: TicketStatus.Closed);
        }

        Assert.Empty(await ListAsync(_otherAgent, Request(channelId)));
        Assert.Empty(await ListAsync(_otherAgent, Request(channelId, status: "Closed")));
    }
}
