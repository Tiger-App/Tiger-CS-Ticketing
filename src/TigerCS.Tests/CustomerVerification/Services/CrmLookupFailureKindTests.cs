using System.Net;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TigerCS.Application.Modules.CustomerVerification.CrmIntegration;
using TigerCS.Integrations.Modules.CrmIntegration;
using TigerCS.Tests.CustomerVerification.Fakes;

namespace TigerCS.Tests.CustomerVerification.Services;

/// <summary>
/// Every way a CRM Buyer lookup can end is told apart: found, not found, ambiguous, and each failure (timed out, unreachable, not
/// configured, key rejected, request rejected, unexpected status, malformed reply). The Outcome contract is unchanged; FailureKind is the
/// compatible extension. Logs never carry the phone number or the secret key.
/// </summary>
public class CrmLookupFailureKindTests
{
    private const string Phone = "+971500009999";
    private const string Secret = "super-secret-key-value";

    private sealed class ListLogger : ILogger<CrmBuyerHttpGateway>
    {
        public List<string> Lines { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Lines.Add(formatter(state, exception) + (exception is null ? "" : " | " + exception.Message));
    }

    private static CrmBuyerHttpGateway Gateway(
        StubHttpMessageHandler handler, ILogger<CrmBuyerHttpGateway>? logger = null, string? secret = Secret, TimeSpan? timeout = null) =>
        new(new HttpClient(handler) { BaseAddress = new Uri("https://crm.example.test/"), Timeout = timeout ?? TimeSpan.FromSeconds(5) },
            Options.Create(new CrmGatewayOptions { BaseUrl = "https://crm.example.test/", SecretKey = secret }),
            logger ?? NullLogger<CrmBuyerHttpGateway>.Instance);

    private static StubHttpMessageHandler Responding(HttpStatusCode code, string body = "") =>
        new((_, _) => Task.FromResult(new HttpResponseMessage(code) { Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json") }));

    [Fact]
    public async Task Timeout_IsUnavailable_WithKindTimedOut()
    {
        var handler = new StubHttpMessageHandler(async (_, ct) => { await Task.Delay(TimeSpan.FromSeconds(30), ct); return new HttpResponseMessage(HttpStatusCode.OK); });

        var result = await Gateway(handler, timeout: TimeSpan.FromMilliseconds(100)).GetBuyerByPhoneAsync(Phone);

        Assert.Equal(CrmBuyerLookupOutcome.Unavailable, result.Outcome);
        Assert.Equal(CrmLookupFailureKind.TimedOut, result.FailureKind);
    }

    [Fact]
    public async Task NetworkFailure_IsUnavailable_WithKindUnreachable()
    {
        var result = await Gateway(new StubHttpMessageHandler((_, _) => throw new HttpRequestException("dns"))).GetBuyerByPhoneAsync(Phone);

        Assert.Equal(CrmBuyerLookupOutcome.Unavailable, result.Outcome);
        Assert.Equal(CrmLookupFailureKind.Unreachable, result.FailureKind);
    }

    [Fact]
    public async Task MissingSecretKey_IsUnavailable_WithKindNotConfigured_AndNothingIsSent()
    {
        var handler = Responding(HttpStatusCode.OK);

        var result = await Gateway(handler, secret: null).GetBuyerByPhoneAsync(Phone);

        Assert.Equal(CrmLookupFailureKind.NotConfigured, result.FailureKind);
        Assert.Equal(0, handler.CallCount);
    }

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError, CrmBuyerLookupOutcome.Unavailable, CrmLookupFailureKind.UnexpectedStatus)]
    [InlineData(HttpStatusCode.ServiceUnavailable, CrmBuyerLookupOutcome.Unavailable, CrmLookupFailureKind.UnexpectedStatus)]
    [InlineData(HttpStatusCode.Unauthorized, CrmBuyerLookupOutcome.Unauthorized, CrmLookupFailureKind.Unauthorized)]
    [InlineData(HttpStatusCode.BadRequest, CrmBuyerLookupOutcome.InvalidResponse, CrmLookupFailureKind.RejectedRequest)]
    public async Task StatusCodes_MapToTheirOwnFailureKind(HttpStatusCode code, CrmBuyerLookupOutcome outcome, CrmLookupFailureKind kind)
    {
        var result = await Gateway(Responding(code)).GetBuyerByPhoneAsync(Phone);

        Assert.Equal(outcome, result.Outcome);
        Assert.Equal(kind, result.FailureKind);
    }

    [Fact]
    public async Task MalformedBody_IsInvalidResponse_WithKindMalformed()
    {
        var result = await Gateway(Responding(HttpStatusCode.OK, "<html>oops</html>")).GetBuyerByPhoneAsync(Phone);

        Assert.Equal(CrmBuyerLookupOutcome.InvalidResponse, result.Outcome);
        Assert.Equal(CrmLookupFailureKind.MalformedResponse, result.FailureKind);
    }

    [Theory]
    [InlineData("""{"success":true,"found":false,"buyers":[]}""")]
    public async Task ACleanNotFound_HasNoFailureKind(string body)
    {
        var result = await Gateway(Responding(HttpStatusCode.OK, body)).GetBuyerByPhoneAsync(Phone);

        Assert.Equal(CrmBuyerLookupOutcome.NotFound, result.Outcome);
        Assert.Null(result.FailureKind);
    }

    [Fact]
    public async Task Http404_IsNotFound_NotAFailure()
    {
        var result = await Gateway(Responding(HttpStatusCode.NotFound)).GetBuyerByPhoneAsync(Phone);

        Assert.Equal(CrmBuyerLookupOutcome.NotFound, result.Outcome);
        Assert.Null(result.FailureKind);
    }

    [Fact]
    public async Task Logs_NeverContainThePhoneNumberOrTheSecretKey()
    {
        var logger = new ListLogger();
        await Gateway(new StubHttpMessageHandler((_, _) => throw new HttpRequestException("dns")), logger).GetBuyerByPhoneAsync(Phone);
        await Gateway(Responding(HttpStatusCode.InternalServerError), logger).GetBuyerByPhoneAsync(Phone);
        await Gateway(Responding(HttpStatusCode.BadRequest), logger).GetBuyerByPhoneAsync(Phone);
        await Gateway(Responding(HttpStatusCode.OK, "<html>"), logger).GetBuyerByPhoneAsync(Phone);
        await Gateway(Responding(HttpStatusCode.OK), logger, secret: null).GetBuyerByPhoneAsync(Phone);

        Assert.NotEmpty(logger.Lines);
        Assert.All(logger.Lines, line =>
        {
            Assert.DoesNotContain(Phone, line, StringComparison.Ordinal);
            Assert.DoesNotContain(Secret, line, StringComparison.Ordinal);
            Assert.DoesNotContain("super-secret", line, StringComparison.Ordinal);
        });
    }
}
