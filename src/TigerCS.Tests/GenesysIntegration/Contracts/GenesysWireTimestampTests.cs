// TigerCS.Web is referenced under an alias — see TigerCS.Tests.csproj.
extern alias TigerCsWeb;

using System.Text.Json;
using TigerCS.Api.Controllers;
using TigerCS.Application.Modules.GenesysIntegration.Services;
using TigerCsWeb::TigerCS.Web.Models;

namespace TigerCS.Tests.GenesysIntegration.Contracts;

/// <summary>
/// The timestamp as it actually travels: the JSON text a sender writes, through the real contract types and mapper (the same System.Text.Json defaults MVC uses),
/// the receipt-time guard, and the single UAE conversion in the UI. Every wire form (<c>…Z</c>, <c>…+04:00</c>, <c>…+00:00</c>, no zone, fractional seconds) must
/// store the same instant whatever zone the server process runs in (run this class under <c>TZ=UTC</c> and <c>TZ=Asia/Dubai</c> - the result does not change), and an
/// impossible one - ticket TG-REG-20261119-0027, whose "AI conversation started" lay 4 hours AFTER the ticket was created - is never stored.
/// </summary>
public sealed class GenesysWireTimestampTests
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    /// <summary>The instant of the real ticket's server receipt: 13:51:04 UAE = 09:51:04 UTC, 19 Nov 2026.</summary>
    private static readonly DateTime Received = new(2026, 11, 19, 9, 51, 4, DateTimeKind.Utc);
    private static readonly DateTime TrueStart = new(2026, 11, 19, 9, 50, 26, DateTimeKind.Utc);   // 13:50:26 UAE

    private static GenesysTimestampContext Context() => new(Received, TimeSpan.FromMinutes(5));

    private static string Inquiry(string startedAt) =>
        $$"""{"conversationId":"c-1","channel":"WhatsApp","customerPhone":"+971500000001","startedAtUtc":"{{startedAt}}"}""";

    private static string Update(string startedAt, string endedAt) =>
        $$$"""{"conversationId":"c-1","startedAtUtc":"{{{startedAt}}}","ended":{"endedAtUtc":"{{{endedAt}}}","endReason":"CustomerDisconnect"}}""";

    private static (DateTime? Start, GenesysTimestampContext Ctx) CreateAsReceived(string startedAt)
    {
        var request = JsonSerializer.Deserialize<GenesysInquiryRequest>(Inquiry(startedAt), Web)!;
        Assert.True(GenesysContractMapper.TryMap(request, out var dto, out _));
        var ctx = Context();
        return (GenesysTimestamps.Normalize(dto, ctx).StartedAtUtc, ctx);
    }

    [Theory]
    [InlineData("2026-11-19T09:50:26Z")]
    [InlineData("2026-11-19T09:50:26.000Z")]
    [InlineData("2026-11-19T13:50:26+04:00")]
    [InlineData("2026-11-19T09:50:26+00:00")]
    [InlineData("2026-11-19T05:50:26-04:00")]
    [InlineData("2026-11-19T09:50:26")]            // zoneless: UTC by the contract
    [InlineData("2026-11-19T09:50:26.0000000")]
    public void EveryWireForm_OfTheSameInstant_IsStoredAsTheSameUtcInstant_AndNothingIsFlagged(string wire)
    {
        var (start, ctx) = CreateAsReceived(wire);

        Assert.Equal(TrueStart, start);
        Assert.Equal(DateTimeKind.Utc, start!.Value.Kind);
        Assert.False(ctx.HasFindings);
        // The UI converts to UAE exactly once: 09:50 UTC is 13:50 in the UAE, not 17:50.
        Assert.Equal("19 Nov 2026 13:50", TicketDisplay.UaeDateTime(start.Value));
    }

    [Theory]
    [InlineData("2026-11-19T13:50:26", "Zoneless")]      // local wall-clock without a zone: read as UTC -> 4 hours in the future
    [InlineData("2026-11-19T13:50:26Z", "Utc")]          // local wall-clock mislabelled as UTC
    [InlineData("2026-11-19T17:50:26+04:00", "Offset")]  // an offset-bearing value that is really 13:50 UTC... still in the future
    public void AnImpossibleStart_IsRejectedNotConverted_AndAuditedWithItsWireForm(string wire, string expectedWireForm)
    {
        var (start, ctx) = CreateAsReceived(wire);

        Assert.Null(start);                                          // not stored; no four hours subtracted, nothing guessed
        var finding = Assert.Single(ctx.Findings);
        Assert.Equal("RejectedFuture", finding.Action);
        Assert.Equal(expectedWireForm, finding.WireForm);
        Assert.Equal("inquiry.startedAtUtc", finding.Field);
        Assert.Equal(Received, finding.ReceivedAtUtc);
        Assert.Contains(expectedWireForm, ctx.Describe());
    }

    [Fact]
    public void ASmallLeadWithinTheTolerance_IsClockSkew_AndIsKept()
    {
        var (start, ctx) = CreateAsReceived("2026-11-19T09:54:00Z");   // 2m56s ahead of receipt

        Assert.Equal(new DateTime(2026, 11, 19, 9, 54, 0, DateTimeKind.Utc), start);
        Assert.False(ctx.HasFindings);
    }

    [Fact]
    public void ATicketUpdate_ThroughTheRealContract_StoresStartAndEndAsUtc_AndLimitsAnEndInTheFuture()
    {
        var request = JsonSerializer.Deserialize<GenesysTicketUpdateRequest>(Update("2026-11-19T13:50:26+04:00", "2026-11-19T14:13:31"), Web)!;
        var ctx = Context();
        var dto = GenesysTimestamps.Normalize(GenesysContractMapper.Map(request), ctx);

        Assert.Equal(TrueStart, dto.StartedAtUtc);
        // 14:13:31 zoneless = read as UTC, which is AFTER this request's receipt (09:51:04): limited to the receipt, never stored as a future end.
        Assert.Equal(Received, dto.Ended!.EndedAtUtc);
        var finding = Assert.Single(ctx.Findings);
        Assert.Equal(("ended.endedAtUtc", "Zoneless", "ClampedToReceipt"), (finding.Field, finding.WireForm, finding.Action));
    }

    [Fact]
    public void TheWithoutClockOverloads_OnlyNormalizeTheZone_AndNeverReject()
    {
        var request = JsonSerializer.Deserialize<GenesysInquiryRequest>(Inquiry("2030-01-01T00:00:00"), Web)!;
        Assert.True(GenesysContractMapper.TryMap(request, out var dto, out _));

        Assert.Equal(new DateTime(2030, 1, 1, 0, 0, 0, DateTimeKind.Utc), GenesysTimestamps.Normalize(dto).StartedAtUtc);
    }
}
