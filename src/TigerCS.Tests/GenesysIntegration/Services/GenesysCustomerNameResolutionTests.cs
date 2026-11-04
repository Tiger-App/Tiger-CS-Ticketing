using TigerCS.Application.Modules.CustomerVerification.CrmIntegration;
using TigerCS.Application.Modules.CustomerVerification.Dto;
using TigerCS.Application.Modules.GenesysIntegration.Dto;
using TigerCS.Tests.GenesysIntegration.Fakes;

namespace TigerCS.Tests.GenesysIntegration.Services;

/// <summary>
/// Which name Genesys ingestion stores on the originating interaction (the name Ticket Details later shows). The name the channel
/// collected wins, but only when it is a real name: a placeholder the flow sent for an unregistered caller must not shut out a valid
/// name the lookup found, and a failed or empty lookup must never replace a name the customer submitted.
/// </summary>
public class GenesysCustomerNameResolutionTests
{
    private static readonly Guid ServiceAccount = Guid.NewGuid();
    private const string Phone = "+971500000778";

    private static CrmBuyerMatchDto Buyer(string name) =>
        new(new CrmCustomerDto(1, name, null, Phone, null),
            [new CrmBuyerUnitDto(1, 4, "Contract", 101, "1506", 1, 2, 15, 10, "Tiger Tower", null, 1, "Buyer")]);

    private static async Task<string?> StoredNameAsync(string? formName, Action<GenesysServiceFixture>? arrange = null)
    {
        var f = new GenesysServiceFixture();
        var (department, _) = f.SeedGenesysDepartment("Customer Service", "CS");
        arrange?.Invoke(f);

        var result = await f.Ingestion.IngestAsync(
            ServiceAccount,
            new GenesysInquiryDto("conv-name", GenesysChannel.WebsiteChat, CustomerPhone: Phone, CustomerName: formName, DepartmentId: department.DepartmentId));

        Assert.Equal(GenesysIngestionOutcome.TicketCreated, result.Outcome);
        return Assert.Single(f.Interactions.All).CustomerName;
    }

    [Fact]
    public async Task FormName_IsStored_WhenTheLookupFoundNothing() =>
        Assert.Equal("Ahmed Hassan", await StoredNameAsync("Ahmed Hassan", f => f.CrmBuyers.Returns(CrmBuyerLookupResult.NotFound())));

    [Fact]
    public async Task FormName_IsStored_WhenTheLookupFailed() =>
        Assert.Equal("Ahmed Hassan", await StoredNameAsync("Ahmed Hassan", f => f.CrmBuyers.Returns(CrmBuyerLookupResult.Unavailable("down"))));

    [Fact]
    public async Task FormName_StillWinsOverALookupName() =>
        Assert.Equal("Ahmed Hassan", await StoredNameAsync("Ahmed Hassan", f => f.CrmBuyers.Returns(CrmBuyerLookupResult.Success([Buyer("Sami Nasser")]))));

    [Fact]
    public async Task NoFormName_UsesTheLookupName() =>
        Assert.Equal("Sami Nasser", await StoredNameAsync(null, f => f.CrmBuyers.Returns(CrmBuyerLookupResult.Success([Buyer("Sami Nasser")]))));

    [Theory]
    [InlineData("NOT_REGISTERED")]
    [InlineData("unknown")]
    [InlineData("N/A")]
    public async Task PlaceholderFormName_DoesNotShutOutAValidLookupName(string placeholder) =>
        Assert.Equal("Sami Nasser", await StoredNameAsync(placeholder, f => f.CrmBuyers.Returns(CrmBuyerLookupResult.Success([Buyer("Sami Nasser")]))));

    [Fact]
    public async Task PlaceholderFormName_WithNothingBetter_IsKeptVerbatimForAudit_NotInventedAway() =>
        Assert.Equal("NOT_REGISTERED", await StoredNameAsync("NOT_REGISTERED", f => f.CrmBuyers.Returns(CrmBuyerLookupResult.NotFound())));
}
