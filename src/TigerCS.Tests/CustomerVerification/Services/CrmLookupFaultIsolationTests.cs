using System.Net;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TigerCS.Application.Modules.CustomerVerification.CrmIntegration;
using TigerCS.Application.Modules.CustomerVerification.Services;
using TigerCS.Application.Modules.Ticketing.Services;
using TigerCS.Integrations.Modules.CrmIntegration;
using TigerCS.Tests.CustomerVerification.Fakes;
using TigerCS.Tests.Ticketing.Fakes;

namespace TigerCS.Tests.CustomerVerification.Services;

/// <summary>
/// An UNEXPECTED fault in the CRM leg is a failed lookup (kind Error), never a thrown 500 and never "no customer": the customer search keeps
/// the PACT/Tasleeh answers, and no log line carries the phone number or the exception's message.
/// </summary>
public class CrmLookupFaultIsolationTests
{
    private const string Phone = "+971500000321";

    private sealed class CapturingLogger<T> : ILogger<T>
    {
        public List<string> Lines { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Lines.Add($"{logLevel}: {formatter(state, exception)} | {exception}");
    }

    [Fact]
    public async Task Gateway_AnUnexpectedFault_IsClassifiedAsUnexpectedError_AndLogsOnlyTheExceptionType()
    {
        var logger = new CapturingLogger<CrmBuyerHttpGateway>();
        var handler = new StubHttpMessageHandler((_, _) => throw new FormatException("lookup of +971500000321 for Jane Customer failed"));
        var client = new HttpClient(handler) { BaseAddress = new Uri("https://crm.example.test/") };
        var gateway = new CrmBuyerHttpGateway(
            client, Options.Create(new CrmGatewayOptions { BaseUrl = "https://crm.example.test/", SecretKey = "secret-key-value" }), logger);

        var result = await gateway.GetBuyerByPhoneAsync(Phone);

        Assert.Equal(CrmBuyerLookupOutcome.Unavailable, result.Outcome);
        Assert.Equal(CrmLookupFailureKind.UnexpectedError, result.FailureKind);
        Assert.Contains(logger.Lines, l => l.Contains("FormatException", StringComparison.Ordinal));
        Assert.All(logger.Lines, l =>
        {
            Assert.DoesNotContain("971500000321", l, StringComparison.Ordinal);
            Assert.DoesNotContain("Jane Customer", l, StringComparison.Ordinal);
            Assert.DoesNotContain("secret-key-value", l, StringComparison.Ordinal);
        });
    }

    [Fact]
    public async Task Gateway_CallerCancellation_StillPropagates()
    {
        var handler = new StubHttpMessageHandler(async (_, ct) =>
        {
            await Task.Delay(TimeSpan.FromSeconds(30), ct);
            return new HttpResponseMessage(HttpStatusCode.OK);
        });
        var gateway = new CrmBuyerHttpGateway(
            new HttpClient(handler) { BaseAddress = new Uri("https://crm.example.test/") },
            Options.Create(new CrmGatewayOptions { BaseUrl = "https://crm.example.test/", SecretKey = "k" }), NullLogger<CrmBuyerHttpGateway>.Instance);
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => gateway.GetBuyerByPhoneAsync(Phone, cts.Token));
    }

    [Fact]
    public async Task Search_AnUnexpectedFaultInTheCrmLeg_IsFailed_KindError_AndKeepsTheOtherSourcesAnswers()
    {
        var crm = new FakeCrmBuyerLookupGateway { ThrowOnCall = new InvalidOperationException("boom") };
        var pact = new FakePactCustomerLookupGateway();
        var service = new CustomerSearchAppService(
            new CrmBuyerLookupAppService(crm, NullLogger<CrmBuyerLookupAppService>.Instance),
            new CustomerLookupAppService(
                new FakeIntakeRecordRepository(), new FakeDepartmentCustomerLookupSourceRepository(), new FakeCrmCustomerLookupGateway(),
                pact, new FakeTasleehGateway(),
                new CrmUnitLookupAppService(
                    new FakeCrmGateway(), new FakeUnitReferenceRepository(), new FakeContactReferenceRepository(),
                    new FakeCustomerVerificationUnitOfWork(), TimeProvider.System),
                TimeProvider.System));

        var result = await service.SearchByPhoneAsync(Phone);

        Assert.Equal("Failed", result.CrmStatus);
        Assert.Equal("Error", result.CrmFailureKind);
        Assert.NotEmpty(result.ExternalSources);
    }
}
