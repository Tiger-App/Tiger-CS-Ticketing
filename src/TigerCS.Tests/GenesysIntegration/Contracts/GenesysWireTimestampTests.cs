// TigerCS.Web is referenced under an alias — see TigerCS.Tests.csproj.
extern alias TigerCsWeb;

using System.Text.Json;
using Microsoft.EntityFrameworkCore;
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
    [InlineData("2026-11-19T13:50:26")]          // a wall-clock read as UTC -> 4 hours in the future
    [InlineData("2026-11-19T13:50:26Z")]         // a wall-clock mislabelled as UTC
    [InlineData("2026-11-19T17:50:26+04:00")]    // an offset-bearing value whose real instant (13:50 UTC) is still in the future
    public void AnImpossibleStart_IsRejectedNotConverted_AndAudited(string wire)
    {
        var (start, ctx) = CreateAsReceived(wire);

        Assert.Null(start);                                          // not stored; no four hours subtracted, nothing guessed
        var finding = Assert.Single(ctx.Findings);
        Assert.Equal("RejectedFuture", finding.Action);
        Assert.Equal("Utc", finding.WireForm);                       // the boundary has already normalized it: the audit carries the UTC value that was refused
        Assert.Equal("inquiry.startedAtUtc", finding.Field);
        Assert.Equal(Received, finding.ReceivedAtUtc);
        Assert.Contains("RejectedFuture", ctx.Describe());
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
        Assert.Equal(("ended.endedAtUtc", "ClampedToReceipt"), (finding.Field, finding.Action));
    }

    [Fact]
    public void TheWithoutClockOverloads_OnlyNormalizeTheZone_AndNeverReject()
    {
        var request = JsonSerializer.Deserialize<GenesysInquiryRequest>(Inquiry("2030-01-01T00:00:00"), Web)!;
        Assert.True(GenesysContractMapper.TryMap(request, out var dto, out _));

        Assert.Equal(new DateTime(2030, 1, 1, 0, 0, 0, DateTimeKind.Utc), GenesysTimestamps.Normalize(dto).StartedAtUtc);
    }

    // ======================================================================================================================================
    // Ticket TG-REG-20261119-0027, root cause: an OFFSET-bearing UTC string ("...+00:00") was read by System.Text.Json into the HOST's local zone, so on the UTC+4
    // application server 09:50:26 UTC arrived as wall-clock 13:50:26, was persisted verbatim into a "...Utc" column, and the UI's one (correct) +4 showed 17:50:26.
    // The Genesys boundary now parses every instant straight to UTC and never consults the host zone. Run this class under TZ=UTC, TZ=Asia/Dubai and
    // TZ=America/Los_Angeles (CI does): every assertion below is zone-independent.
    // ======================================================================================================================================

    /// <summary>The conversation start of ticket 1878: 09:50:26 UTC = 13:50:26 UAE.</summary>
    private static readonly DateTime Ticket1878StartUtc = new(2026, 11, 19, 9, 50, 26, DateTimeKind.Utc);

    private sealed record Probe(
        [property: System.Text.Json.Serialization.JsonConverter(typeof(GenesysUtcDateTimeConverter))] DateTime? At,
        [property: System.Text.Json.Serialization.JsonConverter(typeof(GenesysUtcDateTimeConverter))] DateTime Required = default);

    [Theory]
    [InlineData("2026-11-19T09:50:26Z")]
    [InlineData("2026-11-19T09:50:26.000Z")]
    [InlineData("2026-11-19T09:50:26.0000000Z")]
    [InlineData("2026-11-19T09:50:26+00:00")]        // <- the production shape: UTC written with an explicit zero offset
    [InlineData("2026-11-19T09:50:26.000+00:00")]
    [InlineData("2026-11-19T09:50:26")]               // zoneless: UTC per the contract
    [InlineData("2026-11-19 09:50:26")]
    [InlineData("2026-11-19T13:50:26+04:00")]        // the same instant written as UAE wall-clock WITH its offset
    [InlineData("2026-11-19T04:50:26-05:00")]
    public void EveryRepresentationOfTheSameInstant_IsReadAsUtc_WithNoDependenceOnTheHostZone(string wire)
    {
        var probe = JsonSerializer.Deserialize<Probe>($$"""{"at":"{{wire}}","required":"{{wire}}"}""", Web)!;

        Assert.Equal(Ticket1878StartUtc, probe.At!.Value);
        Assert.Equal(DateTimeKind.Utc, probe.At.Value.Kind);
        Assert.Equal(Ticket1878StartUtc, probe.Required);
        Assert.Equal(DateTimeKind.Utc, probe.Required.Kind);
        // ...and it is displayed in the UAE exactly once: 13:50:26, never 17:50:26.
        Assert.Equal("19 Nov 2026 13:50:26", TicketDisplay.DubaiTimestamp(probe.At.Value));
    }

    [Fact]
    public void TheDefaultJsonReader_IsNotZoneIndependent_WhichIsWhyTheBoundaryNoLongerUsesIt()
    {
        // Documents the defect: with an explicit offset the stock reader yields a Local-kind value whose wall-clock is the HOST's. On a UTC host it happens to
        // equal UTC; on the UAE host it is 13:50:26 - the value ticket 1878 stored. Only the converter result is asserted to be correct everywhere.
        var stock = JsonSerializer.Deserialize<DateTime>("\"2026-11-19T09:50:26+00:00\"", Web);
        Assert.Equal(DateTimeKind.Local, stock.Kind);
        Assert.Equal(Ticket1878StartUtc, stock.ToUniversalTime());                 // the instant is right; the persisted TICKS (stock.Ticks) are the host's wall-clock
        Assert.Equal(stock.ToLocalTime().Ticks, stock.Ticks);

        var ours = JsonSerializer.Deserialize<Probe>("""{"at":"2026-11-19T09:50:26+00:00"}""", Web)!.At!.Value;
        Assert.Equal(Ticket1878StartUtc.Ticks, ours.Ticks);                        // the stored ticks ARE the UTC wall-clock, on every host
    }

    [Theory]
    [InlineData("not a time")]
    [InlineData("")]
    [InlineData("2026-13-45T99:00:00Z")]
    public void AnInvalidTimestamp_IsRejected_NotGuessed(string wire) =>
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<Probe>($$"""{"at":"{{wire}}"}""", Web));

    [Fact]
    public void ANumberOrANullRequiredTimestamp_IsRejected_AndANullOptionalOneIsAbsent()
    {
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<Probe>("""{"at":1763545826}""", Web));
        Assert.Null(JsonSerializer.Deserialize<Probe>("""{"at":null}""", Web)!.At);
    }

    [Fact]
    public void TheConverter_WritesUtcWithZ()
    {
        var json = JsonSerializer.Serialize(new Probe(Ticket1878StartUtc, Ticket1878StartUtc), Web);
        Assert.Contains("\"2026-11-19T09:50:26Z\"", json);
    }

    [Fact]
    public void EveryInboundGenesysTimestampOfTheRealContracts_ReadsOffsetBearingUtcCorrectly()
    {
        // The real contract types, the exact production shape on every timestamp Genesys sends.
        var inquiry = JsonSerializer.Deserialize<GenesysInquiryRequest>(
            """{"conversationId":"c","channel":"WhatsApp","startedAtUtc":"2026-11-19T09:50:26+00:00"}""", Web)!;
        var update = JsonSerializer.Deserialize<GenesysTicketUpdateRequest>(
            """
            {"conversationId":"c","startedAtUtc":"2026-11-19T09:50:26+00:00",
             "routing":{"agentId":"a","connectedAtUtc":"2026-11-19T09:55:00+00:00"},
             "customerConfirmation":{"confirmedResolved":true,"confirmedAtUtc":"2026-11-19T09:56:00+00:00"},
             "transcript":[{"sender":"Customer","sentAtUtc":"2026-11-19T09:50:30+00:00","body":"hi"}],
             "ended":{"endedAtUtc":"2026-11-19T10:13:31+00:00","transcript":[{"sender":"VirtualAgent","sentAtUtc":"2026-11-19T10:13:00+00:00","body":"bye"}]}}
            """, Web)!;

        Assert.Equal(Ticket1878StartUtc, inquiry.StartedAtUtc);
        Assert.Equal(Ticket1878StartUtc, update.StartedAtUtc);
        Assert.Equal(new DateTime(2026, 11, 19, 9, 55, 0, DateTimeKind.Utc), update.Routing!.ConnectedAtUtc);
        Assert.Equal(new DateTime(2026, 11, 19, 9, 56, 0, DateTimeKind.Utc), update.CustomerConfirmation!.ConfirmedAtUtc);
        Assert.Equal(new DateTime(2026, 11, 19, 9, 50, 30, DateTimeKind.Utc), update.Transcript![0].SentAtUtc);
        Assert.Equal(new DateTime(2026, 11, 19, 10, 13, 31, DateTimeKind.Utc), update.Ended!.EndedAtUtc);
        Assert.Equal(new DateTime(2026, 11, 19, 10, 13, 0, DateTimeKind.Utc), update.Ended.Transcript![0].SentAtUtc);
        Assert.All(new[] { inquiry.StartedAtUtc!.Value, update.StartedAtUtc!.Value, update.Routing.ConnectedAtUtc!.Value, update.Ended.EndedAtUtc!.Value },
            at => Assert.Equal(DateTimeKind.Utc, at.Kind));
    }

    [Fact]
    public void EveryInboundDateTimeOnTheGenesysContracts_CarriesTheUtcConverter_SoANewFieldCannotReintroduceTheHostZone()
    {
        var inbound = typeof(GenesysInquiryRequest).Assembly.GetTypes()
            .Where(t => t.Namespace == typeof(GenesysInquiryRequest).Namespace && t.Name.StartsWith("Genesys", StringComparison.Ordinal)
                && (t.Name.EndsWith("Request", StringComparison.Ordinal) || t.Name.EndsWith("Part", StringComparison.Ordinal)))
            .SelectMany(t => t.GetProperties().Select(p => (Type: t, Property: p)))
            .Where(x => x.Property.PropertyType == typeof(DateTime) || x.Property.PropertyType == typeof(DateTime?))
            .ToList();

        Assert.NotEmpty(inbound);
        foreach (var (type, property) in inbound)
        {
            var converter = property.GetCustomAttributes(typeof(System.Text.Json.Serialization.JsonConverterAttribute), inherit: true)
                .Cast<System.Text.Json.Serialization.JsonConverterAttribute>().SingleOrDefault();
            Assert.True(converter?.ConverterType == typeof(GenesysUtcDateTimeConverter), $"{type.Name}.{property.Name} must use GenesysUtcDateTimeConverter.");
        }
    }

    // ---- persistence: no server-local time is ever written ----

    [Fact]
    public async Task TheDbContext_NeverPersistsAServerLocalValue_ItStoresTheRealInstantInUtc()
    {
        var options = new DbContextOptionsBuilder<TigerCS.Infrastructure.Persistence.TigerCsDbContext>()
            .UseInMemoryDatabase("local-time-guard-" + Guid.NewGuid()).Options;
        // Built from the real instant, so the assertion holds on any host zone.
        var localKind = DateTime.SpecifyKind(Ticket1878StartUtc.ToLocalTime(), DateTimeKind.Local);

        long id;
        await using (var db = new TigerCS.Infrastructure.Persistence.TigerCsDbContext(options))
        {
            var interaction = TigerCS.Domain.Modules.Ticketing.TicketInteraction.CreateFromGenesys(
                ticketId: 1, channelId: 1, customerPhone: "+971500000000", genesysConversationId: "c-local",
                calledNumber: null, genesysQueueId: null, genesysQueueName: null, genesysAgentId: null, genesysAgentName: null,
                interactionStartedAtUtc: localKind, direction: null, createdAtUtc: Ticket1878StartUtc,
                isOriginatingInteraction: true, genesysServiceRequestId: null);
            db.TicketInteractions.Add(interaction);
            await db.SaveChangesAsync();
            id = interaction.TicketInteractionId;
        }

        await using var read = new TigerCS.Infrastructure.Persistence.TigerCsDbContext(options);
        var stored = (await read.TicketInteractions.SingleAsync(i => i.TicketInteractionId == id)).InteractionStartedAtUtc!.Value;
        Assert.Equal(Ticket1878StartUtc.Ticks, stored.Ticks);                       // the TICKS are what SQL Server persists: the UTC wall-clock, on every host
    }

    // ---- presentation: UTC to Asia/Dubai exactly once ----

    [Theory]
    [InlineData(DateTimeKind.Utc)]
    [InlineData(DateTimeKind.Unspecified)]   // what EF Core and the Api's JSON hand to the Web for every ...Utc column
    public void TheUi_ConvertsStoredUtcToUae_Once_WhateverTheKind(DateTimeKind kind)
    {
        var stored = DateTime.SpecifyKind(Ticket1878StartUtc, kind);

        Assert.Equal("19 Nov 2026 13:50:26", TicketDisplay.DubaiTimestamp(stored));
        Assert.Equal("19 Nov 2026 13:50 (UAE)", TicketDisplay.UaeTime(stored));
        Assert.Equal("19 Nov 2026 13:50", TicketDisplay.UaeDateTime(stored));
        Assert.Equal("13:50", TicketDisplay.UaeClock(stored));
    }

    [Fact]
    public void ALocalKindValue_IsConvertedByItsRealInstant_NeverRelabelledAsUtc_SoItCannotGainFourHours()
    {
        // A Local-kind DateTime (host wall-clock) used to be relabelled UTC by SpecifyKind and then shifted again: +4h on the UAE host.
        var local = DateTime.SpecifyKind(Ticket1878StartUtc.ToLocalTime(), DateTimeKind.Local);

        Assert.Equal("19 Nov 2026 13:50:26", TicketDisplay.DubaiTimestamp(local));
        Assert.Equal(Ticket1878StartUtc, TicketDisplay.AsUtc(local));
    }

    [Fact]
    public void ToUae_DoesNotDependOnTheServerZone_ItIsAFixedUtcPlusFour_AndCrossesMidnightCorrectly()
    {
        Assert.Equal(new DateTime(2026, 11, 20, 1, 30, 0), TicketDisplay.ToUae(new DateTime(2026, 11, 19, 21, 30, 0, DateTimeKind.Utc)));
        Assert.Equal(new DateTime(2026, 7, 1, 4, 0, 0), TicketDisplay.ToUae(new DateTime(2026, 7, 1, 0, 0, 0, DateTimeKind.Unspecified)));   // no daylight saving, in any month
    }

    [Fact]
    public void NoWebViewOrDisplayHelper_UsesTheServersLocalTime_ToLocalTime_IsForbiddenInTheWebProject()
    {
        var web = FindWebProject();
        var offenders = Directory.EnumerateFiles(web, "*.*", SearchOption.AllDirectories)
            .Where(f => (f.EndsWith(".cshtml", StringComparison.Ordinal) || f.EndsWith(".cs", StringComparison.Ordinal))
                && !f.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar) && !f.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar))
            .SelectMany(f => File.ReadLines(f).Select((line, n) => (File: f, Line: n + 1, Text: line)))
            .Where(x => x.Text.Contains(".ToLocalTime()", StringComparison.Ordinal) && !x.Text.TrimStart().StartsWith("///", StringComparison.Ordinal))
            .Select(x => $"{Path.GetRelativePath(web, x.File)}:{x.Line}")
            .ToList();

        Assert.True(offenders.Count == 0, "ToLocalTime() gives UAE time on the UAE host and UTC elsewhere - use TicketDisplay.ToUae: " + string.Join(", ", offenders));
    }

    private static string FindWebProject()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "src", "TigerCS.Web");
            if (Directory.Exists(candidate)) return candidate;
        }

        throw new DirectoryNotFoundException("src/TigerCS.Web");
    }
}
