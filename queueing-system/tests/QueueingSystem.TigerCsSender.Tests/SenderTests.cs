using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace QueueingSystem.TigerCsSender.Tests;

public sealed class QueueingEventFactoryTests
{
    private static readonly DateTime Now = new(2026, 11, 10, 9, 0, 0, DateTimeKind.Utc);

    private static QueueingEventFactory Factory(IEventSequenceProvider? sequences = null) =>
        new(sequences ?? new InMemoryEventSequenceProvider(), () => Now, () => Guid.NewGuid().ToString());

    [Fact]
    public async Task EventSequence_StartsAtOne_AndGrowsByOneForEveryEventOfTheSameQueueTicket()
    {
        var factory = Factory();
        var created = await factory.CreatedAsync("QT-1", "A-017", "company-a", "type-complaint", "+971500000123");
        var assigned = await factory.AssignedAsync("QT-1", "queue-user-a", "counter-3");
        var comment = await factory.CommentAddedAsync("QT-1", "note");
        var closed = await factory.ClosedAsync("QT-1");
        var other = await factory.CreatedAsync("QT-2", "A-018", "company-a", "type-complaint", "+971500000124");

        Assert.Equal([1L, 2L, 3L, 4L], new[] { created, assigned, comment, closed }.Select(e => e.EventSequence));
        Assert.Equal(1, other.EventSequence);                                        // every queue ticket has its own sequence
        Assert.Equal(4, new[] { created, assigned, comment, closed, other }.Select(e => e.EventId).Distinct().Count() - 1);
    }

    [Fact]
    public async Task TicketCreated_MustBeTheFirstEvent()
    {
        var sequences = new InMemoryEventSequenceProvider();
        await sequences.NextAsync("QT-1");                                           // something was already sent for this queue ticket
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Factory(sequences).CreatedAsync("QT-1", "A-017", "company-a", "type-complaint", "+971500000123"));
    }

    [Fact]
    public async Task EveryEventTypeIsBuiltValid_WithItsRequiredMembers()
    {
        var factory = Factory();
        var events = new[]
        {
            await factory.CreatedAsync("QT-9", "A-017", "company-a", "type-complaint", "+971500000123", "Aisha Khan", "aisha@example.test"),
            await factory.AssignedAsync("QT-9", "queue-user-a"),
            await factory.ReassignedAsync("QT-9", "queue-user-b", "Specialist required"),
            await factory.ReturnedAsync("QT-9"),
            await factory.CommentAddedAsync("QT-9", "A comment"),
            await factory.RecalledAsync("QT-9"),
            await factory.ClosedAsync("QT-9"),
            await factory.CancelledAsync("QT-9", "left")
        };

        Assert.Equal(Enum.GetValues<QueueingEventType>().Order(), events.Select(e => e.EventType).Order());
        Assert.All(events, e => Assert.Empty(e.Validate(Now)));
        Assert.All(events, e => Assert.Equal(DateTimeKind.Utc, e.OccurredAtUtc.Kind));
    }

    [Theory]
    [InlineData(QueueingEventType.TicketCreated, "QueueNumber,CompanyId,TicketTypeId")]
    [InlineData(QueueingEventType.TicketAssigned, "AssignedUserId")]
    [InlineData(QueueingEventType.TicketReassigned, "AssignedUserId")]
    [InlineData(QueueingEventType.TicketCommentAdded, "Comment")]
    [InlineData(QueueingEventType.TicketReturned, "")]
    [InlineData(QueueingEventType.TicketRecalled, "")]
    [InlineData(QueueingEventType.TicketClosed, "")]
    [InlineData(QueueingEventType.TicketCancelled, "")]
    public void RequiredMembers_AreExactlyTheContractsTable(QueueingEventType type, string expected)
    {
        Assert.Equal(expected.Length == 0 ? [] : expected.Split(','), QueueingEventRequest.RequiredMembers(type));

        var bare = new QueueingEventRequest { EventId = "e", EventType = type, ExternalTicketId = "QT-1", EventSequence = 1, OccurredAtUtc = Now };
        var expectedErrors = expected.Length == 0 ? [] : expected.Split(',');
        Assert.Equal(expectedErrors.Order(), bare.Validate(Now).Keys.Order());
    }

    [Fact]
    public void Validation_CatchesTheEnvelopeProblems_BeforeAnythingIsSent()
    {
        var bad = new QueueingEventRequest { EventId = " ", EventType = (QueueingEventType)99, ExternalTicketId = "", EventSequence = 0, OccurredAtUtc = Now.AddHours(1) };
        Assert.Equal(["EventId", "EventSequence", "EventType", "ExternalTicketId", "OccurredAtUtc"], bad.Validate(Now).Keys.Order());
        Assert.Contains("CustomerPhone",
            new QueueingEventRequest { EventId = "e", EventType = QueueingEventType.TicketCreated, ExternalTicketId = "QT-1", EventSequence = 1, OccurredAtUtc = Now, CustomerPhone = "abc",
                QueueNumber = "A", CompanyId = "c", TicketTypeId = "t" }.Validate(Now).Keys);
    }

    [Fact]
    public void ATicketCreated_WithNeitherPhoneNorEmail_IsValid_AndTheNamesAreNeverRequired()
    {
        // The queue number, company and ticket type identify the customer; TigerCS never needs (or invents) a phone, and CompanyName / TicketTypeName are optional.
        var created = new QueueingEventRequest
        {
            EventId = "e", EventType = QueueingEventType.TicketCreated, ExternalTicketId = "QT-1", EventSequence = 1, OccurredAtUtc = Now,
            QueueNumber = "A-017", CompanyId = "c", TicketTypeId = "t"
        };

        Assert.Empty(created.Validate(Now));
        Assert.DoesNotContain("CustomerPhone", QueueingEventRequest.RequiredMembers(QueueingEventType.TicketCreated));
        Assert.DoesNotContain("CustomerEmail", QueueingEventRequest.RequiredMembers(QueueingEventType.TicketCreated));
        Assert.DoesNotContain("CompanyName", QueueingEventRequest.RequiredMembers(QueueingEventType.TicketCreated));
        Assert.DoesNotContain("TicketTypeName", QueueingEventRequest.RequiredMembers(QueueingEventType.TicketCreated));
        Assert.DoesNotContain("CustomerPhone", created.ToJson());   // nothing is invented: the member is simply omitted
    }

    [Fact]
    public void OnTheWire_MemberNamesArePascalCase_TheEventTypeIsItsName_AndEmptyMembersAreOmitted()
    {
        var json = JsonNode.Parse(new QueueingEventRequest
        {
            EventId = "e-1", EventType = QueueingEventType.TicketAssigned, ExternalTicketId = "QT-1", EventSequence = 2,
            OccurredAtUtc = new DateTime(2026, 11, 10, 8, 34, 0, DateTimeKind.Utc), AssignedUserId = "queue-user-a"
        }.ToJson())!.AsObject();

        Assert.Equal(["EventId", "EventType", "ExternalTicketId", "EventSequence", "OccurredAtUtc", "AssignedUserId"], json.Select(p => p.Key));
        Assert.Equal("TicketAssigned", json["EventType"]!.GetValue<string>());
        Assert.Equal(2, json["EventSequence"]!.GetValue<long>());
        Assert.Equal("2026-11-10T08:34:00Z", json["OccurredAtUtc"]!.GetValue<string>());
    }
}

public sealed class TigerCsEventSenderTests
{
    private static readonly DateTime Now = DateTime.UtcNow;

    private static QueueingEventRequest Event() => new()
    {
        EventId = "0f6c1d52-3b7a-4a55-9c53-0d6a1f1b0002", EventType = QueueingEventType.TicketAssigned, ExternalTicketId = "QT-1", EventSequence = 2,
        OccurredAtUtc = Now.AddMinutes(-2), AssignedUserId = "queue-user-a", CounterId = "counter-3"
    };

    private static TigerCsSenderOptions Options() => new() { BaseUrl = "https://tigercs.example.test/", ApiKey = "the-key", MaxAttempts = 4, BaseDelayMilliseconds = 10, MaxDelaySeconds = 5 };

    private sealed class ScriptedHandler(params Func<HttpRequestMessage, HttpResponseMessage>[] script) : HttpMessageHandler
    {
        public List<(HttpRequestMessage Request, string Body)> Calls { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);
            Calls.Add((request, body));
            return script[Math.Min(Calls.Count - 1, script.Length - 1)](request);
        }
    }

    private static HttpResponseMessage Json(HttpStatusCode status, string json) =>
        new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private static (TigerCsEventSender Sender, ScriptedHandler Handler, List<TimeSpan> Delays) Build(params Func<HttpRequestMessage, HttpResponseMessage>[] script)
    {
        var handler = new ScriptedHandler(script);
        var delays = new List<TimeSpan>();
        var sender = new TigerCsEventSender(new HttpClient(handler), Options(), (d, _) => { delays.Add(d); return Task.CompletedTask; });
        return (sender, handler, delays);
    }

    [Fact]
    public async Task TheRequest_GoesToTheContractsEndpoint_WithTheApiKeyHeader_AndTheExactJson()
    {
        var (sender, handler, _) = Build(_ => Json(HttpStatusCode.OK, """{"eventId":"x","status":"Processed","outcome":"APPLIED","retryable":false}"""));
        var @event = Event();
        await sender.SendAsync(@event);

        var (request, body) = Assert.Single(handler.Calls);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("https://tigercs.example.test/api/integrations/queueing/events", request.RequestUri!.ToString());
        Assert.Equal("the-key", Assert.Single(request.Headers.GetValues("X-Queueing-Api-Key")));
        Assert.Null(request.Headers.Authorization);                                   // the key is NOT a bearer token
        Assert.Equal("application/json", request.Content!.Headers.ContentType!.MediaType);
        Assert.Equal(@event.ToJson(), body);
    }

    [Theory]
    [InlineData(HttpStatusCode.OK, """{"status":"Processed","outcome":"APPLIED"}""", SendDisposition.Delivered)]
    [InlineData(HttpStatusCode.Created, """{"status":"Processed","outcome":"TICKET_CREATED"}""", SendDisposition.Delivered)]
    [InlineData(HttpStatusCode.OK, """{"status":"Duplicate","outcome":"APPLIED"}""", SendDisposition.Delivered)]
    [InlineData(HttpStatusCode.OK, """{"status":"Superseded","outcome":"STALE_EVENT"}""", SendDisposition.Delivered)]
    [InlineData(HttpStatusCode.OK, """{"status":"Rejected","outcome":"TICKET_ALREADY_CLOSED"}""", SendDisposition.Delivered)]
    [InlineData(HttpStatusCode.Accepted, """{"status":"Pending","outcome":"USER_NOT_MAPPED","retryable":true}""", SendDisposition.Accepted)]
    [InlineData(HttpStatusCode.BadRequest, """{"status":"Invalid","outcome":"VALIDATION_FAILED"}""", SendDisposition.PermanentFailure)]
    [InlineData(HttpStatusCode.Conflict, """{"status":"Conflict","outcome":"EVENT_ID_REUSED"}""", SendDisposition.PermanentFailure)]
    [InlineData(HttpStatusCode.NotFound, "{}", SendDisposition.PermanentFailure)]
    [InlineData(HttpStatusCode.Unauthorized, "{}", SendDisposition.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden, "{}", SendDisposition.Unauthorized)]
    public async Task EveryDefinitiveAnswer_IsSentOnce_AndNeverRetried(HttpStatusCode status, string body, SendDisposition expected)
    {
        var (sender, handler, delays) = Build(_ => Json(status, body));
        var result = await sender.SendAsync(Event());

        Assert.Equal(expected, result.Disposition);
        Assert.Single(handler.Calls);
        Assert.Empty(delays);
        Assert.Equal(1, result.Attempts);
    }

    [Theory]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.BadGateway)]
    [InlineData(HttpStatusCode.GatewayTimeout)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    [InlineData(HttpStatusCode.RequestTimeout)]
    public async Task ATransientFailure_IsRetriedWithTheSameEventIdAndBody_UntilItSucceeds(HttpStatusCode transient)
    {
        var (sender, handler, delays) = Build(
            _ => Json(transient, "{}"), _ => Json(transient, "{}"), _ => Json(HttpStatusCode.OK, """{"status":"Processed","outcome":"APPLIED"}"""));
        var result = await sender.SendAsync(Event());

        Assert.Equal(SendDisposition.Delivered, result.Disposition);
        Assert.Equal(3, result.Attempts);
        Assert.Equal(2, delays.Count);
        Assert.All(delays, d => Assert.InRange(d, TimeSpan.Zero, TimeSpan.FromSeconds(5)));        // exponential with jitter, never above MaxDelaySeconds
        Assert.Single(handler.Calls.Select(c => c.Body).Distinct());                  // every attempt is byte-identical: same EventId, same content
    }

    [Fact]
    public async Task ThePersistentTransientFailure_IsHandedBackToTheOutbox_AfterMaxAttempts()
    {
        var (sender, handler, delays) = Build(_ => Json(HttpStatusCode.ServiceUnavailable, "{}"));
        var result = await sender.SendAsync(Event());

        Assert.Equal(SendDisposition.RetryLater, result.Disposition);
        Assert.Equal(4, handler.Calls.Count);
        Assert.Equal(3, delays.Count);                                                // no pause after the last attempt
        Assert.False(result.IsDone);
    }

    [Fact]
    public async Task RetryAfter_IsHonoured_UpToTheConfiguredCap()
    {
        var withHeader = Build(
            _ => { var r = Json(HttpStatusCode.ServiceUnavailable, "{}"); r.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(2)); return r; },
            _ => { var r = Json(HttpStatusCode.ServiceUnavailable, "{}"); r.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(600)); return r; },
            _ => Json(HttpStatusCode.OK, "{}"));
        await withHeader.Sender.SendAsync(Event());

        Assert.Equal(TimeSpan.FromSeconds(2), withHeader.Delays[0]);
        Assert.Equal(TimeSpan.FromSeconds(5), withHeader.Delays[1]);                  // capped at MaxDelaySeconds
    }

    [Fact]
    public async Task ANetworkError_IsRetried_LikeAnyTransientFailure()
    {
        var (sender, handler, _) = Build(_ => throw new HttpRequestException("connection refused"), _ => Json(HttpStatusCode.Created, """{"status":"Processed","outcome":"TICKET_CREATED"}"""));
        var result = await sender.SendAsync(Event());
        Assert.Equal(SendDisposition.Delivered, result.Disposition);
        Assert.Equal(2, handler.Calls.Count);
    }

    [Fact]
    public async Task AnInvalidEvent_IsNeverSent()
    {
        var (sender, handler, _) = Build(_ => Json(HttpStatusCode.OK, "{}"));
        var result = await sender.SendAsync(Event() with { AssignedUserId = null });
        Assert.Equal(SendDisposition.PermanentFailure, result.Disposition);
        Assert.Empty(handler.Calls);
        Assert.Contains("AssignedUserId", result.Error);
    }

    [Fact]
    public async Task ThePendingAnswer_ExposesWhyTigerCsHeldIt()
    {
        var (sender, _, _) = Build(_ => Json(HttpStatusCode.Accepted, """{"eventId":"e","status":"Pending","outcome":"USER_NOT_MAPPED","detail":"Queue user is not mapped.","retryable":true}"""));
        var result = await sender.SendAsync(Event());
        Assert.True(result.IsDone);                                                   // do not resend: TigerCS retries it
        Assert.Equal(QueueingResultStatus.Pending, result.Response!.Status);
        Assert.Equal("USER_NOT_MAPPED", result.Response.Outcome);
        Assert.True(result.Response.Retryable);
    }

    [Fact]
    public void TheConfiguration_IsValidatedByKeyName()
    {
        Assert.Throws<InvalidOperationException>(() => new TigerCsSenderOptions { BaseUrl = "not a url", ApiKey = "k" }.EnsureValid());
        Assert.Throws<InvalidOperationException>(() => new TigerCsSenderOptions { BaseUrl = "https://x.test", ApiKey = "" }.EnsureValid());
        new TigerCsSenderOptions { BaseUrl = "https://x.test", ApiKey = "k" }.EnsureValid();
    }
}

/// <summary>The sample JSON in docs/QueueingIntegration/samples is the contract; the TigerCS test-suite posts the same files to the real API.</summary>
public sealed class ContractSampleTests
{
    private static string SamplesDirectory()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "docs", "QueueingIntegration", "samples");
            if (Directory.Exists(candidate))
            {
                return candidate;
            }
        }

        throw new DirectoryNotFoundException("docs/QueueingIntegration/samples was not found above the test output directory.");
    }

    [Fact]
    public void EverySampleRequest_RoundTripsThroughTheDto_ToTheSameJson()
    {
        var files = Directory.GetFiles(SamplesDirectory(), "*.request.json");
        Assert.NotEmpty(files);
        foreach (var file in files)
        {
            var original = File.ReadAllText(file);
            if (Path.GetFileName(file).StartsWith("12-Invalid"))
            {
                continue;   // deliberately incomplete (it demonstrates the 400); it still has to be well-formed JSON
            }

            var dto = JsonSerializer.Deserialize<QueueingEventRequest>(original)!;
            Assert.True(JsonNode.DeepEquals(JsonNode.Parse(original), JsonNode.Parse(dto.ToJson())), Path.GetFileName(file));
            Assert.Empty(dto.Validate(new DateTime(2026, 11, 10, 9, 0, 0, DateTimeKind.Utc)));
        }
    }

    [Fact]
    public void EverySampleResponse_ParsesIntoTheDto()
    {
        var files = Directory.GetFiles(SamplesDirectory(), "*.response.json");
        Assert.NotEmpty(files);
        foreach (var file in files)
        {
            var dto = JsonSerializer.Deserialize<QueueingEventResponse>(File.ReadAllText(file), new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
            Assert.False(string.IsNullOrEmpty(dto.EventId), Path.GetFileName(file));
            Assert.False(string.IsNullOrEmpty(dto.Outcome), Path.GetFileName(file));
        }
    }

    [Fact]
    public void TheSequenceInTheLifecycleSamples_IsStrictlyIncreasing_StartingAtOne()
    {
        var sequences = Directory.GetFiles(SamplesDirectory(), "*.request.json").Where(f => Path.GetFileName(f)[..2] is "01" or "02" or "03" or "04" or "05" or "06" or "07").Order()
            .Select(f => JsonNode.Parse(File.ReadAllText(f))!["EventSequence"]!.GetValue<long>()).ToList();
        Assert.Equal([1L, 2L, 3L, 4L, 5L, 6L, 7L], sequences);
    }
}
