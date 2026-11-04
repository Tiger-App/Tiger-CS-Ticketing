using TigerCS.Application.Modules.CustomerVerification.CrmIntegration;
using TigerCS.Application.Modules.CustomerVerification.Dto;
using TigerCS.Application.Modules.CustomerVerification.PactIntegration;
using TigerCS.Application.Modules.GenesysIntegration.Dto;
using TigerCS.Tests.CustomerVerification.Fakes;
using TigerCS.Tests.GenesysIntegration.Fakes;

namespace TigerCS.Tests.GenesysIntegration.Services;

/// <summary>
/// The customer lookup Genesys ingestion runs for an inquiry's phone number records exactly what happened: found, not found, ambiguous, or
/// the lookup FAILED (and how). A failed lookup is never recorded as "NotFound" - that would label a registered customer as unregistered -
/// and the audit text never contains the phone number or any customer data.
/// </summary>
public class GenesysCrmLookupStatusTests
{
    private static readonly Guid ServiceAccount = Guid.NewGuid();
    private const string Phone = "+971500000777";

    private static async Task<string> LookupStatusAsync(Action<GenesysServiceFixture> arrange)
    {
        var f = new GenesysServiceFixture();
        var (department, _) = f.SeedGenesysDepartment("Customer Service", "CS");
        arrange(f);

        var result = await f.Ingestion.IngestAsync(
            ServiceAccount, new GenesysInquiryDto("conv-lookup", GenesysChannel.WebsiteChat, CustomerPhone: Phone, DepartmentId: department.DepartmentId));

        Assert.Equal(GenesysIngestionOutcome.TicketCreated, result.Outcome);   // a lookup problem never costs the inquiry
        var audit = Assert.Single(f.Audit.Entries, a => a.Action == "GenesysInquiryIngested");
        var after = audit.AfterValue!;
        Assert.DoesNotContain(Phone, after, StringComparison.Ordinal);          // no customer data in the audit text
        var start = after.IndexOf("CustomerLookup=", StringComparison.Ordinal) + "CustomerLookup=".Length;
        return after[start..after.IndexOf(";Classification=", start, StringComparison.Ordinal)];
    }

    private static CrmBuyerMatchDto Buyer(int id, string name) =>
        new(new CrmCustomerDto(id, name, null, Phone, null),
            [new CrmBuyerUnitDto(id, 4, "Contract", 100 + id, "1506", 1, 2, 15, 10, "Tiger Tower", null, 1, "Buyer")]);

    [Fact]
    public async Task CustomerFound() =>
        Assert.Equal("Found", await LookupStatusAsync(f => f.CrmBuyers.Returns(CrmBuyerLookupResult.Success([Buyer(1, "Sami Nasser")]))));

    [Fact]
    public async Task CustomerNotFound_OnlyWhenEverySourceActuallyAnswered() =>
        Assert.Equal("NotFound(Crm=NotFound)", await LookupStatusAsync(f => f.CrmBuyers.Returns(CrmBuyerLookupResult.NotFound())));

    [Fact]
    public async Task ConflictingCustomers_AreAmbiguous_NotNotFound() =>
        Assert.Equal("Ambiguous(Crm=AmbiguousMatch)", await LookupStatusAsync(f =>
            f.CrmBuyers.Returns(CrmBuyerLookupResult.Success([Buyer(1, "Sami"), Buyer(2, "Mona")]))));

    [Fact]
    public async Task CrmTimedOut_IsALookupFailure_NeverNotFound() =>
        Assert.Equal("LookupFailed(Crm=TimedOut)", await LookupStatusAsync(f =>
            f.CrmBuyers.Returns(CrmBuyerLookupResult.Unavailable("CRM request timed out.", CrmLookupFailureKind.TimedOut))));

    [Fact]
    public async Task CrmUnavailable_IsALookupFailure() =>
        Assert.Equal("LookupFailed(Crm=Unreachable)", await LookupStatusAsync(f =>
            f.CrmBuyers.Returns(CrmBuyerLookupResult.Unavailable("CRM could not be reached."))));

    [Fact]
    public async Task CrmServerError_IsALookupFailure() =>
        Assert.Equal("LookupFailed(Crm=UnexpectedStatus)", await LookupStatusAsync(f =>
            f.CrmBuyers.Returns(CrmBuyerLookupResult.Unavailable("CRM returned unexpected status 503.", CrmLookupFailureKind.UnexpectedStatus))));

    [Fact]
    public async Task CrmMisconfigured_KeyRejected_IsALookupFailure() =>
        Assert.Equal("LookupFailed(Crm=Misconfigured)", await LookupStatusAsync(f =>
            f.CrmBuyers.Returns(CrmBuyerLookupResult.Unauthorized("rejected"))));

    [Fact]
    public async Task CrmMalformedReply_IsALookupFailure() =>
        Assert.Equal("LookupFailed(Crm=InvalidResponse)", await LookupStatusAsync(f =>
            f.CrmBuyers.Returns(CrmBuyerLookupResult.InvalidResponse("bad body"))));

    [Fact]
    public async Task CrmNotFound_ButPactDown_IsInconclusive_NotUnregistered() =>
        Assert.Equal("LookupFailed(Pact=Failed)", await LookupStatusAsync(f =>
        {
            f.CrmBuyers.Returns(CrmBuyerLookupResult.NotFound());
            f.Pact.ForcedOutcome = PactCustomerLookupOutcome.Unavailable;
        }));

    [Fact]
    public async Task BothDown_NamesBoth() =>
        Assert.Equal("LookupFailed(Crm=TimedOut;Pact=Failed)", await LookupStatusAsync(f =>
        {
            f.CrmBuyers.Returns(CrmBuyerLookupResult.Unavailable("t", CrmLookupFailureKind.TimedOut));
            f.Pact.ForcedOutcome = PactCustomerLookupOutcome.Unavailable;
        }));

    [Fact]
    public async Task CrmDown_ButPactFound_StillFound() =>
        Assert.Equal("Found", await LookupStatusAsync(f =>
        {
            f.CrmBuyers.Returns(CrmBuyerLookupResult.Unavailable("down"));
            f.Pact.Seed(Phone, new PactCustomerMatchDto("77", "Layla PACT", Phone, null, null, []));
        }));
}
