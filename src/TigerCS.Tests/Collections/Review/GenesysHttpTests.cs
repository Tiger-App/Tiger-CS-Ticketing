using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using TigerCS.Application.Modules.Collections.Review;
using TigerCS.Integrations.Modules.CollectionsIntegration;
using TigerCS.Tests.Notifications.Fakes;

namespace TigerCS.Tests.Collections.Review;

/// <summary>The real Genesys HTTP client and token provider against a scripted handler. No network, no real credentials.</summary>
public sealed class GenesysHttpTests
{
    private const string ClientId = "test-client-id";
    private const string ClientSecret = "test-secret-value-DO-NOT-LOG";
    private static readonly DateTime Start = new(2026, 10, 14, 8, 0, 0, DateTimeKind.Utc);
    private const string ListId = "79e5ae74-ea6e-4941-b76d-45ddf487d8d1";

    private sealed class Handler : HttpMessageHandler
    {
        public List<(HttpRequestMessage Request, string Body)> Requests { get; } = [];
        public Queue<Func<HttpRequestMessage, HttpResponseMessage>> Responses { get; } = new();
        public int TokenSeq;
        public int TokenLifetime = 1800;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
            Requests.Add((request, body));
            if (request.RequestUri!.AbsolutePath == "/oauth/token")
                return Json(HttpStatusCode.OK, $"{{\"access_token\":\"token-{++TokenSeq}\",\"token_type\":\"bearer\",\"expires_in\":{TokenLifetime}}}");
            return Responses.Count > 0 ? Responses.Dequeue()(request) : Json(HttpStatusCode.OK, "[{\"id\":\"c-1\"},{\"id\":\"c-2\"}]");
        }

        public IEnumerable<(HttpRequestMessage Request, string Body)> Token => Requests.Where(r => r.Request.RequestUri!.AbsolutePath == "/oauth/token");
        public IEnumerable<(HttpRequestMessage Request, string Body)> Upload => Requests.Where(r => r.Request.RequestUri!.AbsolutePath != "/oauth/token");
    }

    private static HttpResponseMessage Json(HttpStatusCode status, string json) =>
        new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private sealed class CapturingLogger<T> : ILogger<T>
    {
        public List<string> Lines { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Lines.Add(formatter(state, exception) + exception?.ToString());
    }

    private static GenesysOutboundOptions Options() => new() { Enabled = true, ClientId = ClientId, ClientSecret = ClientSecret };

    private static (GenesysOutboundHttpClient Client, GenesysTokenProvider Tokens, Handler Handler, FakeTimeProvider Time, CapturingLogger<GenesysTokenProvider> TokenLog, CapturingLogger<GenesysOutboundHttpClient> ClientLog)
        Build(GenesysOutboundOptions? options = null)
    {
        options ??= Options();
        var handler = new Handler();
        var time = new FakeTimeProvider(Start);
        var tokenLog = new CapturingLogger<GenesysTokenProvider>();
        var clientLog = new CapturingLogger<GenesysOutboundHttpClient>();
        var tokens = new GenesysTokenProvider(new HttpClient(handler), options, time, tokenLog);
        return (new GenesysOutboundHttpClient(new HttpClient(handler), tokens, options, clientLog), tokens, handler, time, tokenLog, clientLog);
    }

    private static GenesysContactPayload Contact(int n = 1, string listId = ListId) => new(listId, $"+97150000{n:D4}", $"Customer {n}", $"c{n}@example.test",
        "Current Month", "1234.50", "2026-10-20", true);

    // ------------------------------------------------------------ token

    [Fact]
    public async Task TokenRequest_IsServerSideBasicAuthWithClientCredentialsGrant()
    {
        var (_, tokens, handler, _, _, _) = Build();
        Assert.Equal("token-1", await tokens.GetTokenAsync(CancellationToken.None));
        var (request, body) = Assert.Single(handler.Token);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("https://login.mypurecloud.de/oauth/token", request.RequestUri!.ToString());
        Assert.Equal("Basic", request.Headers.Authorization!.Scheme);
        Assert.Equal($"{ClientId}:{ClientSecret}", Encoding.UTF8.GetString(Convert.FromBase64String(request.Headers.Authorization.Parameter!)));
        Assert.Equal("application/x-www-form-urlencoded", request.Content!.Headers.ContentType!.MediaType);
        Assert.Equal("grant_type=client_credentials", body);
    }

    [Fact]
    public async Task TokenIsCachedUntilItsExpiry_ThenRenewed()
    {
        var (_, tokens, handler, time, _, _) = Build();
        handler.TokenLifetime = 600;
        Assert.Equal("token-1", await tokens.GetTokenAsync(CancellationToken.None));
        time.Advance(TimeSpan.FromSeconds(300));
        Assert.Equal("token-1", await tokens.GetTokenAsync(CancellationToken.None));          // still valid: no second request
        Assert.Single(handler.Token);
        time.Advance(TimeSpan.FromSeconds(300));                                               // 600 s elapsed; the 60 s safety margin has passed
        Assert.Equal("token-2", await tokens.GetTokenAsync(CancellationToken.None));
        Assert.Equal(2, handler.Token.Count());
    }

    [Fact]
    public async Task ConcurrentCallers_ShareOneTokenRequest()
    {
        var (_, tokens, handler, _, _, _) = Build();
        var all = await Task.WhenAll(Enumerable.Range(0, 20).Select(_ => tokens.GetTokenAsync(CancellationToken.None)));
        Assert.All(all, t => Assert.Equal("token-1", t));
        Assert.Single(handler.Token);
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("id", " ")]
    public async Task MissingCredentials_FailWithoutAnyNetworkCall(string? id, string? secret)
    {
        var (client, _, handler, _, _, _) = Build(new GenesysOutboundOptions { Enabled = true, ClientId = id, ClientSecret = secret });
        var result = await client.UploadContactsAsync(ListId, [Contact()], CancellationToken.None);
        Assert.Equal(GenesysUploadOutcome.Rejected, result.Outcome);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task SecretsAndTokensNeverAppearInLogsOrErrors()
    {
        var (client, _, handler, _, tokenLog, clientLog) = Build();
        handler.Responses.Enqueue(_ => Json(HttpStatusCode.InternalServerError, "{\"message\":\"boom token-1\"}"));
        var failed = await client.UploadContactsAsync(ListId, [Contact()], CancellationToken.None);
        var (badClient, _, badHandler, _, badTokenLog, _) = Build();
        badHandler.TokenSeq = 0;
        var rejected = new GenesysOutboundHttpClient(new HttpClient(new RefusingTokenHandler()),
            new GenesysTokenProvider(new HttpClient(new RefusingTokenHandler()), Options(), TimeProvider.System, badTokenLog), Options(), NullLogger<GenesysOutboundHttpClient>.Instance);
        var refused = await rejected.UploadContactsAsync(ListId, [Contact()], CancellationToken.None);

        var everything = string.Join('\n', tokenLog.Lines.Concat(clientLog.Lines).Concat(badTokenLog.Lines)) + failed.Error + refused.Error;
        Assert.DoesNotContain(ClientSecret, everything);
        Assert.DoesNotContain(Convert.ToBase64String(Encoding.UTF8.GetBytes($"{ClientId}:{ClientSecret}")), everything);
        Assert.DoesNotContain("token-1", everything);
        Assert.Equal(GenesysUploadOutcome.Rejected, refused.Outcome);
    }

    private sealed class RefusingTokenHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized));
    }

    [Theory]
    [InlineData("{\"error\":\"invalid_client\",\"description\":\"client authentication failed\"}", "HTTP 400, invalid_client")]
    [InlineData("{\"error\":\"unsupported_grant_type\"}", "HTTP 400, unsupported_grant_type")]
    [InlineData("{\"error\":\"<script>alert(1)</script>\"}", "HTTP 400).")]
    [InlineData("not json", "HTTP 400).")]
    [InlineData("", "HTTP 400).")]
    public async Task ARefusedTokenRequest_NamesTheOAuthErrorCode_AndNothingElseFromTheBody(string body, string expected)
    {
        var log = new CapturingLogger<GenesysTokenProvider>();
        var tokens = new GenesysTokenProvider(new HttpClient(new BadRequestTokenHandler(body)), Options(), TimeProvider.System, log);
        var ex = await Assert.ThrowsAsync<GenesysAuthenticationException>(() => tokens.GetTokenAsync(CancellationToken.None));
        Assert.Contains(expected, ex.Message);
        Assert.DoesNotContain("client authentication failed", ex.Message); Assert.DoesNotContain("<script>", ex.Message);
        Assert.DoesNotContain(ClientSecret, ex.Message + string.Join('\n', log.Lines));
        Assert.Contains(log.Lines, l => l.Contains("HTTP 400"));
    }

    private sealed class BadRequestTokenHandler(string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(Json(HttpStatusCode.BadRequest, body));
    }

    // ------------------------------------------------------------ request shape

    [Fact]
    public async Task UploadRequest_HasTheExactUrlAndBodyShape()
    {
        var (client, _, handler, _, _, _) = Build();
        var result = await client.UploadContactsAsync(ListId, [Contact(1), Contact(2)], CancellationToken.None);
        Assert.Equal(GenesysUploadOutcome.Accepted, result.Outcome);
        Assert.Equal(["c-1", "c-2"], result.ContactIds);

        var (request, body) = Assert.Single(handler.Upload);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal($"https://api.mypurecloud.de/api/v2/outbound/contactlists/{ListId}/contacts", request.RequestUri!.ToString());
        Assert.Equal(new AuthenticationHeaderValue("Bearer", "token-1"), request.Headers.Authorization);

        using var json = JsonDocument.Parse(body);
        var contacts = json.RootElement.EnumerateArray().ToList();
        Assert.Equal(2, contacts.Count);
        foreach (var contact in contacts)
        {
            Assert.Equal(["contactListId", "data", "callable"], contact.EnumerateObject().Select(p => p.Name));
            Assert.Equal(ListId, contact.GetProperty("contactListId").GetString());           // body id == URL id
            Assert.True(contact.GetProperty("callable").GetBoolean());
            var data = contact.GetProperty("data");
            Assert.Equal(["Phone", "CustomerName", "Email Address", "ReminderType", "AmountDue", "DueDate"], data.EnumerateObject().Select(p => p.Name));
            Assert.All(data.EnumerateObject(), p => Assert.Equal(JsonValueKind.String, p.Value.ValueKind));   // every value is a string
        }
        var first = contacts[0].GetProperty("data");
        Assert.Equal("+971500000001", first.GetProperty("Phone").GetString());
        Assert.Equal("Customer 1", first.GetProperty("CustomerName").GetString());
        Assert.Equal("c1@example.test", first.GetProperty("Email Address").GetString());
        Assert.Equal("Current Month", first.GetProperty("ReminderType").GetString());
        Assert.Equal("1234.50", first.GetProperty("AmountDue").GetString());
        Assert.Equal("2026-10-20", first.GetProperty("DueDate").GetString());
    }

    [Fact]
    public async Task ABodyListIdThatDiffersFromTheUrlIsRefusedBeforeAnyRequest()
    {
        var (client, _, handler, _, _, _) = Build();
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            client.UploadContactsAsync(ListId, [Contact(1), Contact(2, "3a91c06e-47ab-4a5e-a720-004bd5cf5bba")], CancellationToken.None));
        Assert.Empty(handler.Requests);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1001)]
    public async Task MoreThan1000OrZeroContactsIsNeverSent(int count)
    {
        var (client, _, handler, _, _, _) = Build();
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            client.UploadContactsAsync(ListId, Enumerable.Range(1, count).Select(n => Contact(n)).ToList(), CancellationToken.None));
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task Exactly1000ContactsIsAllowed()
    {
        var (client, _, handler, _, _, _) = Build();
        handler.Responses.Enqueue(_ => Json(HttpStatusCode.OK, "[" + string.Join(",", Enumerable.Range(1, 1000).Select(n => $"{{\"id\":\"c{n}\"}}")) + "]"));
        var result = await client.UploadContactsAsync(ListId, Enumerable.Range(1, 1000).Select(n => Contact(n)).ToList(), CancellationToken.None);
        Assert.Equal(1000, result.ContactIds.Count);
    }

    // ------------------------------------------------------------ campaigns

    private static GenesysCampaignOptions Template() => new()
    {
        Enabled = true, AllowCreate = true, QueueId = "q-1", DialingMode = "agentless", CallerName = "Tiger", CallerAddress = "+971400000000", ScriptId = "s-1"
    };

    [Fact]
    public async Task CampaignLookup_IsAReadOnlyGetByEncodedName_AndReturnsTheCampaignsFound()
    {
        var (client, _, handler, _, _, _) = Build();
        handler.Responses.Enqueue(_ => Json(HttpStatusCode.OK,
            "{\"entities\":[{\"id\":\"cmp-1\",\"name\":\"Legal notice - days 12 and 14\",\"contactList\":{\"id\":\"" + ListId + "\"},\"campaignStatus\":\"off\"}],\"pageCount\":1}"));
        var result = await client.FindCampaignsByNameAsync("Legal notice - days 12 and 14", CancellationToken.None);

        var (request, body) = Assert.Single(handler.Upload);
        Assert.Equal(HttpMethod.Get, request.Method);
        Assert.Equal("", body);
        Assert.Equal("/api/v2/outbound/campaigns", request.RequestUri!.AbsolutePath);
        Assert.Contains("name=Legal%20notice%20-%20days%2012%20and%2014", request.RequestUri.Query);
        Assert.Equal(GenesysUploadOutcome.Accepted, result.Outcome);
        var found = Assert.Single(result.Matches);
        Assert.Equal(("cmp-1", "Legal notice - days 12 and 14", ListId, "off"), (found.Id, found.Name, found.ContactListId, found.Status));
    }

    [Fact]
    public async Task CampaignLookup_ServerError_IsUnknown_NotAnEmptyAnswer()
    {
        var (client, _, handler, _, _, _) = Build();
        handler.Responses.Enqueue(_ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
        var result = await client.FindCampaignsByNameAsync("Stage", CancellationToken.None);
        Assert.Equal(GenesysUploadOutcome.Unknown, result.Outcome);
        Assert.Empty(result.Matches);
    }

    [Fact]
    public async Task CampaignCreate_PostsTheExactName_OffStatus_AndTheTemplate()
    {
        var (client, _, handler, _, _, _) = Build();
        handler.Responses.Enqueue(_ => Json(HttpStatusCode.OK, "{\"id\":\"cmp-9\",\"name\":\"Legal notice - days 12 and 14\",\"campaignStatus\":\"off\"}"));
        var result = await client.CreateCampaignAsync("Legal notice - days 12 and 14", ListId, Template(), CancellationToken.None);

        var (request, body) = Assert.Single(handler.Upload);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("/api/v2/outbound/campaigns", request.RequestUri!.AbsolutePath);
        using var json = JsonDocument.Parse(body);
        var root = json.RootElement;
        Assert.Equal("Legal notice - days 12 and 14", root.GetProperty("name").GetString());
        Assert.Equal(ListId, root.GetProperty("contactList").GetProperty("id").GetString());
        Assert.Equal("q-1", root.GetProperty("queue").GetProperty("id").GetString());
        Assert.Equal("s-1", root.GetProperty("script").GetProperty("id").GetString());
        Assert.Equal("off", root.GetProperty("campaignStatus").GetString());
        Assert.False(root.TryGetProperty("callAnalysisResponseSet", out _));
        Assert.Equal(GenesysUploadOutcome.Accepted, result.Outcome);
        Assert.Equal("cmp-9", result.Campaign!.Id);
    }

    [Fact]
    public async Task CampaignCreate_WithAnIncompleteTemplate_SendsNothing()
    {
        var (client, _, handler, _, _, _) = Build();
        var template = Template(); template.QueueId = " ";
        var result = await client.CreateCampaignAsync("Stage", ListId, template, CancellationToken.None);
        Assert.Equal(GenesysUploadOutcome.Rejected, result.Outcome);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task CampaignCreate_OkWithoutAnId_IsUnknown()
    {
        var (client, _, handler, _, _, _) = Build();
        handler.Responses.Enqueue(_ => Json(HttpStatusCode.OK, "{}"));
        Assert.Equal(GenesysUploadOutcome.Unknown, (await client.CreateCampaignAsync("Stage", ListId, Template(), CancellationToken.None)).Outcome);
    }

    // ------------------------------------------------------------ outcomes

    [Fact]
    public async Task ARejectedToken_IsRefreshedOnce_AndTheRequestRetriedSafely()
    {
        var (client, _, handler, _, _, _) = Build();
        handler.Responses.Enqueue(_ => new HttpResponseMessage(HttpStatusCode.Unauthorized));
        var result = await client.UploadContactsAsync(ListId, [Contact()], CancellationToken.None);
        Assert.Equal(GenesysUploadOutcome.Accepted, result.Outcome);
        Assert.Equal(2, handler.Token.Count());
        Assert.Equal(["token-1", "token-2"], handler.Upload.Select(r => r.Request.Headers.Authorization!.Parameter));
    }

    [Fact]
    public async Task ASecond401_IsADefiniteRejection()
    {
        var (client, _, handler, _, _, _) = Build();
        handler.Responses.Enqueue(_ => new HttpResponseMessage(HttpStatusCode.Unauthorized));
        handler.Responses.Enqueue(_ => new HttpResponseMessage(HttpStatusCode.Unauthorized));
        var result = await client.UploadContactsAsync(ListId, [Contact()], CancellationToken.None);
        Assert.Equal(GenesysUploadOutcome.Rejected, result.Outcome);
        Assert.Equal(2, handler.Upload.Count());
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest)]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    public async Task ClientErrors_AreDefiniteRejections_AndNeverEchoTheResponseBody(HttpStatusCode status)
    {
        var (client, _, handler, _, _, _) = Build();
        handler.Responses.Enqueue(_ => Json(status, "{\"message\":\"Customer 1 +971500000001 is invalid\"}"));
        var result = await client.UploadContactsAsync(ListId, [Contact()], CancellationToken.None);
        Assert.Equal(GenesysUploadOutcome.Rejected, result.Outcome);
        Assert.Equal((int)status, result.HttpStatus);
        Assert.DoesNotContain("+971500000001", result.Error);
    }

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.BadGateway)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    [InlineData(HttpStatusCode.GatewayTimeout)]
    public async Task ServerErrors_AreAnUnknownOutcome_NotAFailure(HttpStatusCode status)
    {
        var (client, _, handler, _, _, _) = Build();
        handler.Responses.Enqueue(_ => new HttpResponseMessage(status));
        var result = await client.UploadContactsAsync(ListId, [Contact()], CancellationToken.None);
        Assert.Equal(GenesysUploadOutcome.Unknown, result.Outcome);
        Assert.Single(handler.Upload);                                   // and it was not retried
    }

    [Fact]
    public async Task ATimeoutAfterSending_IsUnknown_AndNotRetried()
    {
        var (client, _, handler, _, _, _) = Build();
        handler.Responses.Enqueue(_ => throw new TaskCanceledException("The request was canceled due to the configured HttpClient.Timeout"));
        var result = await client.UploadContactsAsync(ListId, [Contact()], CancellationToken.None);
        Assert.Equal(GenesysUploadOutcome.Unknown, result.Outcome);
        Assert.Single(handler.Upload);
    }

    [Fact]
    public async Task AConnectionFailureBeforeSending_IsAKnownNonDelivery()
    {
        var (client, _, handler, _, _, _) = Build();
        handler.Responses.Enqueue(_ => throw new HttpRequestException(HttpRequestError.ConnectionError, "refused"));
        var result = await client.UploadContactsAsync(ListId, [Contact()], CancellationToken.None);
        Assert.Equal(GenesysUploadOutcome.Rejected, result.Outcome);
    }

    [Fact]
    public async Task AConnectionDroppedMidRequest_IsUnknown()
    {
        var (client, _, handler, _, _, _) = Build();
        handler.Responses.Enqueue(_ => throw new HttpRequestException(HttpRequestError.ResponseEnded, "connection reset"));
        Assert.Equal(GenesysUploadOutcome.Unknown, (await client.UploadContactsAsync(ListId, [Contact()], CancellationToken.None)).Outcome);
    }

    [Fact]
    public async Task AnUnreadableSuccessBody_StillCountsAsAccepted_WithoutIds()
    {
        var (client, _, handler, _, _, _) = Build();
        handler.Responses.Enqueue(_ => Json(HttpStatusCode.OK, "not json"));
        var result = await client.UploadContactsAsync(ListId, [Contact()], CancellationToken.None);
        Assert.Equal(GenesysUploadOutcome.Accepted, result.Outcome);
        Assert.Empty(result.ContactIds);          // the dispatch service then marks the unmatched contacts as unconfirmed
    }

    // ------------------------------------------------------------ suppression: PUT callable=false

    [Fact]
    public async Task SuppressionIsAPutToTheContactWithCallableFalseAndTheOriginalData()
    {
        var (client, _, handler, _, _, _) = Build();
        handler.Responses.Enqueue(_ => Json(HttpStatusCode.OK, "{\"id\":\"abc-123\",\"callable\":false}"));
        var result = await client.SetNotCallableAsync(ListId, "abc-123", Contact(), CancellationToken.None);
        Assert.Equal(GenesysUploadOutcome.Accepted, result.Outcome);
        Assert.False(result.CallableAfter);
        Assert.False(result.ContactMissing);

        var (request, body) = Assert.Single(handler.Upload);
        Assert.Equal(HttpMethod.Put, request.Method);
        Assert.Equal($"https://api.mypurecloud.de/api/v2/outbound/contactlists/{ListId}/contacts/abc-123", request.RequestUri!.ToString());
        Assert.Equal("Bearer", request.Headers.Authorization!.Scheme);
        using var json = JsonDocument.Parse(body);
        var root = json.RootElement;
        Assert.Equal("abc-123", root.GetProperty("id").GetString());
        Assert.Equal(ListId, root.GetProperty("contactListId").GetString());
        Assert.False(root.GetProperty("callable").GetBoolean());                    // never true, whatever the stored payload says
        Assert.Equal(["Phone", "CustomerName", "Email Address", "ReminderType", "AmountDue", "DueDate"], root.GetProperty("data").EnumerateObject().Select(p => p.Name));
        Assert.Equal("1234.50", root.GetProperty("data").GetProperty("AmountDue").GetString());
    }

    [Fact]
    public async Task ASuppressionAnswerThatDoesNotSayCallableFalse_IsReportedAsSuch()
    {
        var (client, _, handler, _, _, _) = Build();
        handler.Responses.Enqueue(_ => Json(HttpStatusCode.OK, "{\"id\":\"abc\",\"callable\":true}"));
        Assert.True((await client.SetNotCallableAsync(ListId, "abc", Contact(), CancellationToken.None)).CallableAfter);
        handler.Responses.Enqueue(_ => Json(HttpStatusCode.OK, "{}"));
        Assert.Null((await client.SetNotCallableAsync(ListId, "abc", Contact(), CancellationToken.None)).CallableAfter);
    }

    [Fact]
    public async Task AMissingContactCannotBeDialled_SoItCountsAsSuppressed()
    {
        var (client, _, handler, _, _, _) = Build();
        handler.Responses.Enqueue(_ => Json(HttpStatusCode.NotFound, "{}"));
        var result = await client.SetNotCallableAsync(ListId, "gone", Contact(), CancellationToken.None);
        Assert.Equal(GenesysUploadOutcome.Accepted, result.Outcome);
        Assert.True(result.ContactMissing);
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest, GenesysUploadOutcome.Rejected)]
    [InlineData(HttpStatusCode.Forbidden, GenesysUploadOutcome.Rejected)]
    [InlineData(HttpStatusCode.TooManyRequests, GenesysUploadOutcome.Rejected)]
    [InlineData(HttpStatusCode.InternalServerError, GenesysUploadOutcome.Unknown)]
    [InlineData(HttpStatusCode.ServiceUnavailable, GenesysUploadOutcome.Unknown)]
    public async Task SuppressionFailuresAreClassifiedLikeUploads(HttpStatusCode status, GenesysUploadOutcome expected)
    {
        var (client, _, handler, _, _, _) = Build();
        handler.Responses.Enqueue(_ => new HttpResponseMessage(status));
        Assert.Equal(expected, (await client.SetNotCallableAsync(ListId, "abc", Contact(), CancellationToken.None)).Outcome);
        Assert.Single(handler.Upload);
    }

    [Fact]
    public async Task ASuppressionTimeoutIsUnknown_AndA401RefreshesTheTokenOnce()
    {
        var (client, _, handler, _, _, _) = Build();
        handler.Responses.Enqueue(_ => throw new TaskCanceledException("timeout"));
        Assert.Equal(GenesysUploadOutcome.Unknown, (await client.SetNotCallableAsync(ListId, "abc", Contact(), CancellationToken.None)).Outcome);
        handler.Responses.Enqueue(_ => new HttpResponseMessage(HttpStatusCode.Unauthorized));
        handler.Responses.Enqueue(_ => Json(HttpStatusCode.OK, "{\"callable\":false}"));
        var ok = await client.SetNotCallableAsync(ListId, "abc", Contact(), CancellationToken.None);
        Assert.Equal(GenesysUploadOutcome.Accepted, ok.Outcome);
        Assert.Equal(2, handler.Token.Count());
    }

    [Theory]
    [InlineData("../other")]
    [InlineData("a/b")]
    [InlineData("")]
    [InlineData("id?x=1")]
    public async Task AContactIdThatCouldAlterTheUrlIsRefusedBeforeAnyRequest(string contactId)
    {
        var (client, _, handler, _, _, _) = Build();
        await Assert.ThrowsAnyAsync<ArgumentException>(() => client.SetNotCallableAsync(ListId, contactId, Contact(), CancellationToken.None));
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task ASuppressionForAnotherListIsRefused()
    {
        var (client, _, handler, _, _, _) = Build();
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.SetNotCallableAsync(ListId, "abc", Contact(1, "3a91c06e-47ab-4a5e-a720-004bd5cf5bba"), CancellationToken.None));
        Assert.Empty(handler.Requests);
    }

    // ------------------------------------------------------------ diagnostics of a rejected request: what failed, where, with which status, never a secret

    private const string NotFoundBody = "{\"message\":\"The contact list could not be found.\",\"code\":\"bridge.entity.not.found\",\"status\":404,\"contextId\":\"0b1c2d3e-0000-4000-8000-000000000001\",\"details\":[],\"errors\":[]}";

    [Fact]
    public async Task A404OnTheContactListRead_NamesTheOperation_TheEndpoint_TheHost_TheStatus_AndGenesysOwnCode_WithoutSecrets()
    {
        var (client, _, handler, _, _, clientLog) = Build();
        handler.Responses.Enqueue(_ => Json(HttpStatusCode.NotFound, NotFoundBody));
        var result = await client.GetContactListAsync(ListId, CancellationToken.None);
        Assert.Equal((GenesysUploadOutcome.Rejected, 404), (result.Outcome, result.HttpStatus));
        var error = result.Error!;
        Assert.Contains("rejected the contact list lookup (GET /api/v2/outbound/contactlists/{contactListId} at api.mypurecloud.de)", error);
        Assert.Contains("HTTP 404 Not Found", error); Assert.Contains("code bridge.entity.not.found", error); Assert.Contains("correlation id 0b1c2d3e-0000-4000-8000-000000000001", error);
        Assert.Contains("organization this OAuth client belongs to", error); Assert.Contains("ApiBaseUrl and LoginBaseUrl", error); Assert.EndsWith("Nothing was changed.", error);
        Assert.DoesNotContain(ListId, error);                                                   // the endpoint is a template, never a concrete id
        Assert.DoesNotContain("could not be found", error);                                     // Genesys' free text is not copied
        foreach (var secret in new[] { ClientId, ClientSecret, "token-1", "Bearer" })
        {
            Assert.DoesNotContain(secret, error);
            Assert.All(clientLog.Lines, l => Assert.DoesNotContain(secret, l));
        }
        Assert.Contains(clientLog.Lines, l => l.Contains("contact list lookup answered HTTP 404") && l.Contains("api.mypurecloud.de") && l.Contains("bridge.entity.not.found"));
        Assert.Single(handler.Upload);                                                          // one GET, no second attempt, no write
        Assert.Equal(HttpMethod.Get, handler.Upload.Single().Request.Method);
    }

    [Fact]
    public async Task APermissionRefusal_NamesThePermissionTheOperationNeeds()
    {
        var (client, _, handler, _, _, _) = Build();
        handler.Responses.Enqueue(_ => Json(HttpStatusCode.Forbidden, "{\"code\":\"missing.permissions\"}"));
        var read = await client.GetContactListAsync(ListId, CancellationToken.None);
        Assert.Contains("HTTP 403 Forbidden", read.Error); Assert.Contains("code missing.permissions", read.Error); Assert.Contains("outbound:contactList:view", read.Error);
        handler.Responses.Enqueue(_ => Json(HttpStatusCode.Forbidden, "{}"));
        var upload = await client.UploadContactsAsync(ListId, [Contact(1)], CancellationToken.None);
        Assert.Equal(GenesysUploadOutcome.Rejected, upload.Outcome);
        Assert.Contains("rejected the upload (POST /api/v2/outbound/contactlists/{contactListId}/contacts at api.mypurecloud.de)", upload.Error); Assert.Contains("outbound:contact:add", upload.Error);
        Assert.DoesNotContain(ListId, upload.Error);
    }

    [Fact]
    public async Task TheEndpointTemplate_NeverCarriesAContactIdACampaignNameOrAQueryString()
    {
        var (client, _, handler, _, _, _) = Build();
        handler.Responses.Enqueue(_ => Json(HttpStatusCode.Forbidden, "{}"));
        var campaigns = await client.FindCampaignsByNameAsync("Confidential Campaign Name", CancellationToken.None);
        Assert.Contains("(GET /api/v2/outbound/campaigns at api.mypurecloud.de)", campaigns.Error); Assert.DoesNotContain("Confidential", campaigns.Error); Assert.DoesNotContain("?", campaigns.Error);
        handler.Responses.Enqueue(_ => Json(HttpStatusCode.BadRequest, "{}"));
        var suppress = await client.SetNotCallableAsync(ListId, "c-123", Contact(1), CancellationToken.None);
        Assert.Contains("(PUT /api/v2/outbound/contactlists/{contactListId}/contacts/{contactId} at api.mypurecloud.de)", suppress.Error); Assert.DoesNotContain("c-123", suppress.Error);
        Assert.Equal("/api/v2/outbound/contactlists/{contactListId}/contacts", GenesysClientTemplate($"api/v2/outbound/contactlists/{ListId}/contacts?x=1"));
    }

    private static string GenesysClientTemplate(string path) => (string)typeof(GenesysOutboundHttpClient).GetMethod("PathTemplate", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!.Invoke(null, [path])!;

    [Theory]
    [InlineData("{\"code\":\"<script>alert(1)</script>\",\"contextId\":\"not-a-guid\"}")]
    [InlineData("not json at all")]
    [InlineData("")]
    [InlineData("[1,2,3]")]
    public async Task AnUnreadableOrHostileErrorBody_ContributesNothing_ButTheStatusAndTheOperationStillShow(string body)
    {
        var (client, _, handler, _, _, _) = Build();
        handler.Responses.Enqueue(_ => Json(HttpStatusCode.NotFound, body));
        var result = await client.GetContactListAsync(ListId, CancellationToken.None);
        Assert.StartsWith("Genesys rejected the contact list lookup (GET /api/v2/outbound/contactlists/{contactListId} at api.mypurecloud.de): HTTP 404 Not Found.", result.Error);
        Assert.DoesNotContain("script", result.Error); Assert.DoesNotContain("code ", result.Error); Assert.DoesNotContain("correlation", result.Error);
    }

    [Fact]
    public async Task TheHostInTheMessageIsTheConfiguredApiHost_SoAWrongRegionIsVisible()
    {
        var options = Options(); options.ApiBaseUrl = "https://api.mec1.pure.cloud";
        var (client, _, handler, _, _, _) = Build(options);
        handler.Responses.Enqueue(_ => Json(HttpStatusCode.NotFound, "{}"));
        var result = await client.GetContactListAsync(ListId, CancellationToken.None);
        Assert.Contains("at api.mec1.pure.cloud)", result.Error);
        Assert.Equal("api.mec1.pure.cloud", handler.Upload.Single().Request.RequestUri!.Host);
    }

    [Fact]
    public async Task AServerErrorKeepsItsUnknownOutcome_AndNamesTheOperation()
    {
        var (client, _, handler, _, _, _) = Build();
        handler.Responses.Enqueue(_ => Json(HttpStatusCode.ServiceUnavailable, "{}"));
        var result = await client.GetContactListAsync(ListId, CancellationToken.None);
        Assert.Equal((GenesysUploadOutcome.Unknown, 503), (result.Outcome, result.HttpStatus));
        Assert.Contains("HTTP 503 to the contact list lookup (GET /api/v2/outbound/contactlists/{contactListId} at api.mypurecloud.de)", result.Error);
        handler.Responses.Enqueue(_ => Json((HttpStatusCode)429, "{}"));
        Assert.Equal("Genesys rate limit reached; nothing was changed.", (await client.GetContactListAsync(ListId, CancellationToken.None)).Error);
    }

    [Fact]
    public async Task TheOrganizationRead_IsAGet_ReturnsTheOrganization_AndFailsWithItsOwnOperationName()
    {
        var (client, _, handler, _, _, _) = Build();
        handler.Responses.Enqueue(_ => Json(HttpStatusCode.OK, "{\"id\":\"1f2e3d4c-0000-4000-8000-000000000009\",\"name\":\"Tiger Group\",\"domain\":\"tiger.example\"}"));
        var org = await client.GetOrganizationAsync(CancellationToken.None);
        Assert.Equal((GenesysUploadOutcome.Accepted, "1f2e3d4c-0000-4000-8000-000000000009", "Tiger Group"), (org.Outcome, org.Id, org.Name));
        var (request, body) = Assert.Single(handler.Upload);
        Assert.Equal((HttpMethod.Get, "https://api.mypurecloud.de/api/v2/organizations/me", ""), (request.Method, request.RequestUri!.ToString(), body));
        handler.Responses.Enqueue(_ => Json(HttpStatusCode.NotFound, "{}"));
        var failed = await client.GetOrganizationAsync(CancellationToken.None);
        Assert.Equal((GenesysUploadOutcome.Rejected, 404), (failed.Outcome, failed.HttpStatus)); Assert.Contains("organization lookup (GET /api/v2/organizations/me at api.mypurecloud.de)", failed.Error);
    }
}
