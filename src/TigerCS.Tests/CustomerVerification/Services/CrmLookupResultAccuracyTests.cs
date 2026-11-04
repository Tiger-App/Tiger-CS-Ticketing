using System.Net;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TigerCS.Application.Modules.CustomerVerification.CrmIntegration;
using TigerCS.Application.Modules.CustomerVerification.Dto;
using TigerCS.Application.Modules.CustomerVerification.Services;
using TigerCS.Application.Modules.GenesysIntegration.Dto;
using TigerCS.Application.Modules.Ticketing.Dto;
using TigerCS.Application.Modules.Ticketing.Services;
using TigerCS.Domain.Modules.Ticketing;
using TigerCS.Integrations.Modules.CrmIntegration;
using TigerCS.Tests.CustomerVerification.Fakes;
using TigerCS.Tests.GenesysIntegration.Fakes;
using TigerCS.Tests.Ticketing.Fakes;

namespace TigerCS.Tests.CustomerVerification.Services;

/// <summary>
/// A CRM lookup that FAILED must never read as a customer that is NOT REGISTERED. These tests pin the five states the lookup can end in
/// - found, not found, CRM unavailable, request timed out, any other error - at the gateway, in the customer search that feeds the
/// Customer Workspace and Genesys, and in the record Genesys ingestion keeps of the lookup; and that none of it writes customer data to logs.
/// </summary>
public class CrmLookupResultAccuracyTests
{
    private const string BaseUrl = "https://crm.example.test/";
    private const string SecretKey = "test-only-secret-key";
    private const string Phone = "+971500000321";

    private sealed class CapturingLogger<T> : ILogger<T>
    {
        public List<string> Lines { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Lines.Add($"{logLevel}: {formatter(state, exception)} | {exception}");
    }

    private static CrmBuyerHttpGateway CreateGateway(
        StubHttpMessageHandler handler, CapturingLogger<CrmBuyerHttpGateway>? logger = null, string? secretKey = SecretKey,
        string? baseUrl = BaseUrl, TimeSpan? timeout = null)
    {
        var client = new HttpClient(handler) { BaseAddress = new Uri(BaseUrl), Timeout = timeout ?? TimeSpan.FromSeconds(5) };
        return new CrmBuyerHttpGateway(
            client, Options.Create(new CrmGatewayOptions { BaseUrl = baseUrl, SecretKey = secretKey }),
            logger ?? new CapturingLogger<CrmBuyerHttpGateway>());
    }

    private static StubHttpMessageHandler Responding(HttpStatusCode status, string json = "{}") =>
        new((_, _) => Task.FromResult(new HttpResponseMessage(status)
        {
            Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json")
        }));

    // -----------------------------------------------------------------
    // Gateway: every state is its own result
    // -----------------------------------------------------------------

    [Fact]
    public async Task Gateway_CustomerFound_IsSuccess_WithNoFailureReason()
    {
        var json = """{"success":true,"found":true,"buyers":[{"customer":{"customerId":7,"fullNameEnglish":"A B"},"units":[]}]}""";

        var result = await CreateGateway(Responding(HttpStatusCode.OK, json)).GetBuyerByPhoneAsync(Phone);

        Assert.Equal(CrmBuyerLookupOutcome.Success, result.Outcome);
        Assert.Null(result.FailureReason);
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound, "{}")]
    [InlineData(HttpStatusCode.OK, """{"success":true,"found":false,"buyers":[]}""")]
    public async Task Gateway_CustomerNotFound_IsNotFound_NeverAFailure(HttpStatusCode status, string json)
    {
        var result = await CreateGateway(Responding(status, json)).GetBuyerByPhoneAsync(Phone);

        Assert.Equal(CrmBuyerLookupOutcome.NotFound, result.Outcome);
        Assert.Null(result.FailureReason);
    }

    [Fact]
    public async Task Gateway_RequestTimedOut_IsUnavailableWithReasonTimeout()
    {
        var handler = new StubHttpMessageHandler(async (_, ct) =>
        {
            await Task.Delay(TimeSpan.FromSeconds(30), ct);
            return new HttpResponseMessage(HttpStatusCode.OK);
        });

        var result = await CreateGateway(handler, timeout: TimeSpan.FromMilliseconds(100)).GetBuyerByPhoneAsync(Phone);

        Assert.Equal(CrmBuyerLookupOutcome.Unavailable, result.Outcome);
        Assert.Equal(CrmBuyerLookupFailureReason.Timeout, result.FailureReason);
    }

    [Fact]
    public async Task Gateway_HostUnreachable_IsUnavailableWithReasonUnreachable()
    {
        var handler = new StubHttpMessageHandler((_, _) => throw new HttpRequestException("Simulated connection failure."));

        var result = await CreateGateway(handler).GetBuyerByPhoneAsync(Phone);

        Assert.Equal(CrmBuyerLookupOutcome.Unavailable, result.Outcome);
        Assert.Equal(CrmBuyerLookupFailureReason.Unreachable, result.FailureReason);
    }

    [Fact]
    public async Task Gateway_CrmAnswersWithAnErrorStatus_IsUnavailableWithReasonUnexpectedStatus()
    {
        var result = await CreateGateway(Responding(HttpStatusCode.ServiceUnavailable)).GetBuyerByPhoneAsync(Phone);

        Assert.Equal(CrmBuyerLookupOutcome.Unavailable, result.Outcome);
        Assert.Equal(CrmBuyerLookupFailureReason.UnexpectedStatus, result.FailureReason);
    }

    [Fact]
    public async Task Gateway_NotConfigured_IsUnavailableWithReasonNotConfigured_AndSendsNothing()
    {
        var handler = Responding(HttpStatusCode.OK);

        var result = await CreateGateway(handler, secretKey: null).GetBuyerByPhoneAsync(Phone);

        Assert.Equal(CrmBuyerLookupOutcome.Unavailable, result.Outcome);
        Assert.Equal(CrmBuyerLookupFailureReason.NotConfigured, result.FailureReason);
        Assert.Equal(0, handler.CallCount);
    }

    [Fact]
    public async Task Gateway_AnUnexpectedFault_IsClassifiedAsAnError_NotThrownToTheCaller()
    {
        var handler = new StubHttpMessageHandler((_, _) => throw new FormatException("boom"));

        var result = await CreateGateway(handler).GetBuyerByPhoneAsync(Phone);

        Assert.Equal(CrmBuyerLookupOutcome.Unavailable, result.Outcome);
        Assert.Equal(CrmBuyerLookupFailureReason.Error, result.FailureReason);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, CrmBuyerLookupOutcome.Unauthorized)]
    [InlineData(HttpStatusCode.BadRequest, CrmBuyerLookupOutcome.InvalidResponse)]
    public async Task Gateway_RejectedRequests_KeepTheirExistingDistinctOutcomes(HttpStatusCode status, CrmBuyerLookupOutcome expected)
    {
        var result = await CreateGateway(Responding(status)).GetBuyerByPhoneAsync(Phone);

        Assert.Equal(expected, result.Outcome);
    }

    /// <summary>The caller giving up is not a CRM failure - it still propagates, exactly as before.</summary>
    [Fact]
    public async Task Gateway_CallerCancellation_StillPropagates()
    {
        var handler = new StubHttpMessageHandler(async (_, ct) =>
        {
            await Task.Delay(TimeSpan.FromSeconds(30), ct);
            return new HttpResponseMessage(HttpStatusCode.OK);
        });
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => CreateGateway(handler).GetBuyerByPhoneAsync(Phone, cts.Token));
    }

    // -----------------------------------------------------------------
    // No customer data in logs
    // -----------------------------------------------------------------

    public static TheoryData<string> FailureScenarios => new() { "timeout", "unreachable", "status", "unexpected", "unauthorized", "notconfigured" };

    [Theory]
    [MemberData(nameof(FailureScenarios))]
    public async Task EveryFailurePath_LogsNeitherThePhoneNumberNorTheSecretKey(string scenario)
    {
        var logger = new CapturingLogger<CrmBuyerHttpGateway>();
        const string leakyMessage = "lookup of +971500000321 for Jane Customer failed";
        var handler = scenario switch
        {
            "timeout" => new StubHttpMessageHandler(async (_, ct) =>
            {
                await Task.Delay(TimeSpan.FromSeconds(30), ct);
                return new HttpResponseMessage(HttpStatusCode.OK);
            }),
            "unreachable" => new StubHttpMessageHandler((_, _) => throw new HttpRequestException("Simulated connection failure.")),
            "status" => Responding(HttpStatusCode.BadGateway),
            "unauthorized" => Responding(HttpStatusCode.Unauthorized),
            // An exception whose MESSAGE carries customer data: only its type may be logged.
            "unexpected" => new StubHttpMessageHandler((_, _) => throw new FormatException(leakyMessage)),
            _ => Responding(HttpStatusCode.OK)
        };

        await CreateGateway(
                handler, logger, secretKey: scenario == "notconfigured" ? null : SecretKey,
                timeout: scenario == "timeout" ? TimeSpan.FromMilliseconds(100) : null)
            .GetBuyerByPhoneAsync(Phone);

        Assert.NotEmpty(logger.Lines);
        Assert.All(logger.Lines, line =>
        {
            Assert.DoesNotContain("971500000321", line, StringComparison.Ordinal);
            Assert.DoesNotContain("Jane Customer", line, StringComparison.Ordinal);
            Assert.DoesNotContain(SecretKey, line, StringComparison.Ordinal);
        });
        Assert.Contains(logger.Lines, line => line.Contains("0321", StringComparison.Ordinal) || scenario == "unauthorized");
    }

    // -----------------------------------------------------------------
    // Customer search: Failed is never NotFound, and says what kind of failure it was
    // -----------------------------------------------------------------

    private static async Task<CustomerSearchResultDto> SearchResultFor(CrmBuyerLookupResult? crmResult, Exception? crmFault = null)
    {
        var crmGateway = new FakeCrmBuyerLookupGateway { ThrowOnCall = crmFault };
        if (crmResult is not null)
        {
            crmGateway.Returns(crmResult);
        }

        var customerLookup = new CustomerLookupAppService(
            new FakeIntakeRecordRepository(), new FakeDepartmentCustomerLookupSourceRepository(), new FakeCrmCustomerLookupGateway(),
            new FakePactCustomerLookupGateway(), new FakeTasleehGateway(),
            new CrmUnitLookupAppService(
                new FakeCrmGateway(), new FakeUnitReferenceRepository(), new FakeContactReferenceRepository(),
                new FakeCustomerVerificationUnitOfWork(), TimeProvider.System),
            TimeProvider.System);
        var service = new CustomerSearchAppService(
            new CrmBuyerLookupAppService(crmGateway, NullLogger<CrmBuyerLookupAppService>.Instance), customerLookup);

        return await service.SearchByPhoneAsync(Phone);
    }

    [Fact]
    public async Task Search_CustomerFound_IsFound_WithNoFailureKind()
    {
        var buyer = new CrmBuyerMatchDto(
            new CrmCustomerDto(9001, "Sami", null, Phone, null),
            [new CrmBuyerUnitDto(1, 4, "Contract", 101, "1506", 1, 2, 15, 10, "Tower", null, 1, "Buyer")]);

        var result = await SearchResultFor(CrmBuyerLookupResult.Success([buyer]));

        Assert.Equal("Found", result.CrmStatus);
        Assert.Null(result.CrmFailureKind);
    }

    [Fact]
    public async Task Search_CustomerNotFound_IsNotFound_WithNoFailureKind()
    {
        var result = await SearchResultFor(CrmBuyerLookupResult.NotFound());

        Assert.Equal("NotFound", result.CrmStatus);
        Assert.Null(result.CrmFailureKind);
    }

    [Fact]
    public async Task Search_AmbiguousMatch_IsNotAFailure()
    {
        var result = await SearchResultFor(CrmBuyerLookupResult.AmbiguousCustomerMatch());

        Assert.Equal("AmbiguousMatch", result.CrmStatus);
        Assert.Null(result.CrmFailureKind);
    }

    [Theory]
    [InlineData(CrmBuyerLookupOutcome.Unavailable, null, "Unavailable")]
    [InlineData(CrmBuyerLookupOutcome.Unavailable, CrmBuyerLookupFailureReason.NotConfigured, "Unavailable")]
    [InlineData(CrmBuyerLookupOutcome.Unavailable, CrmBuyerLookupFailureReason.Unreachable, "Unavailable")]
    [InlineData(CrmBuyerLookupOutcome.Unavailable, CrmBuyerLookupFailureReason.UnexpectedStatus, "Unavailable")]
    [InlineData(CrmBuyerLookupOutcome.Unavailable, CrmBuyerLookupFailureReason.Timeout, "Timeout")]
    [InlineData(CrmBuyerLookupOutcome.Unavailable, CrmBuyerLookupFailureReason.Error, "Error")]
    [InlineData(CrmBuyerLookupOutcome.Unauthorized, null, "Error")]
    [InlineData(CrmBuyerLookupOutcome.InvalidResponse, null, "Error")]
    public async Task Search_ACrmFailure_StaysFailed_AndNamesItsKind(
        CrmBuyerLookupOutcome outcome, CrmBuyerLookupFailureReason? reason, string expectedKind)
    {
        var result = await SearchResultFor(new CrmBuyerLookupResult(outcome, FailureReason: reason));

        // "Failed" is unchanged for every existing consumer (Genesys Architect flows, the workspace); the kind is additive.
        Assert.Equal("Failed", result.CrmStatus);
        Assert.Equal(expectedKind, result.CrmFailureKind);
        Assert.Empty(result.CrmBuyers);
    }

    [Fact]
    public async Task Search_AnUnexpectedFaultInTheCrmLeg_IsReportedAsAFailedLookup_NotAsNoCustomer_AndNotRethrown()
    {
        var result = await SearchResultFor(null, crmFault: new InvalidOperationException("boom"));

        Assert.Equal("Failed", result.CrmStatus);
        Assert.Equal("Error", result.CrmFailureKind);
        Assert.NotEqual("NotFound", result.CrmStatus);
    }

    // -----------------------------------------------------------------
    // Genesys: the data-action contract is unchanged - Failed is still Failed, and found stays false
    // -----------------------------------------------------------------

    [Fact]
    public async Task Genesys_CrmDown_ReportsFailed_NotNotFound_AndNeverClaimsTheCallerIsUnknown()
    {
        var f = new GenesysServiceFixture();
        f.CrmBuyers.Returns(CrmBuyerLookupResult.Unavailable("CRM request timed out.", CrmBuyerLookupFailureReason.Timeout));

        var r = (await f.CustomerLookup.LookUpAsync(Phone))!;

        Assert.Equal("Failed", r.CrmStatus);
        Assert.False(r.Found);
        Assert.Empty(r.CrmBuyers);
    }

    // -----------------------------------------------------------------
    // Genesys ingestion: the lookup record names a failed lookup as failed
    // -----------------------------------------------------------------

    private static async Task<string> IngestAndReadLookupRecordAsync(CrmBuyerLookupResult crm)
    {
        var f = new GenesysServiceFixture();
        var (department, _) = f.SeedGenesysDepartment("Customer Service", "CS");
        f.CrmBuyers.Returns(crm);

        await f.Ingestion.IngestAsync(
            Guid.NewGuid(),
            new GenesysInquiryDto(
                "conv-crm-" + Guid.NewGuid().ToString("N"), GenesysChannel.Phone,
                CustomerPhone: Phone, CustomerName: null, QueueId: null, DepartmentId: department.DepartmentId, DepartmentCode: null,
                TowerName: null, UnitNumber: null, Subject: null));

        return Assert.Single(f.Audit.Entries, a => a.Action == "GenesysInquiryIngested").AfterValue!;
    }

    [Fact]
    public async Task Ingestion_CustomerNotFound_IsRecordedAsNotFound()
    {
        var record = await IngestAndReadLookupRecordAsync(CrmBuyerLookupResult.NotFound());

        Assert.Contains("CustomerLookup=NotFound(Crm=NotFound);", record, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(null, "Crm:Unavailable")]
    [InlineData(CrmBuyerLookupFailureReason.Timeout, "Crm:Timeout")]
    [InlineData(CrmBuyerLookupFailureReason.Error, "Crm:Error")]
    public async Task Ingestion_CrmFailure_IsRecordedAsALookupFailure_NeverAsNotFound(CrmBuyerLookupFailureReason? reason, string expected)
    {
        var record = await IngestAndReadLookupRecordAsync(CrmBuyerLookupResult.Unavailable("CRM is down", reason));

        Assert.Contains($"CustomerLookup=LookupFailed({expected});", record, StringComparison.Ordinal);
        Assert.DoesNotContain("CustomerLookup=NotFound", record, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Ingestion_CustomerFound_IsRecordedAsFound()
    {
        var buyer = new CrmBuyerMatchDto(
            new CrmCustomerDto(9001, "Sami Nasser", null, Phone, null),
            [new CrmBuyerUnitDto(1, 4, "Contract", 101, "1506", 1, 2, 15, 10, "Tower", null, 1, "Buyer")]);

        var record = await IngestAndReadLookupRecordAsync(CrmBuyerLookupResult.Success([buyer]));

        Assert.Contains("CustomerLookup=Found;", record, StringComparison.Ordinal);
    }
}
