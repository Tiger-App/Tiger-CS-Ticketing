using TigerCS.Domain.Modules.Collections;

namespace TigerCS.Tests.Collections.Crm;

/// <summary>Phone formatting differences between CRM and PACT are harmless; a different number is a real identity conflict that is flagged and never merged.</summary>
public sealed class ContactFormattingVsIdentityTests
{
    private static LinkedContact Link(string crmPhone, string pactPhone, string crmEmail = "", string pactEmail = "") => CollectionsContactLinker.Link(
        new SourceContact("Pact Name", CollectionsContactNormalizer.NormalizePhone(pactPhone), CollectionsContactNormalizer.NormalizeEmail(pactEmail)),
        new SourceContact("Crm Name", CollectionsContactNormalizer.NormalizePhone(crmPhone), CollectionsContactNormalizer.NormalizeEmail(crmEmail)), CrmLinkStatus.Single);

    [Theory]
    [InlineData("+971501234567", "0501234567")]
    [InlineData("971501234567", "050 123 4567")]
    [InlineData("00971 50 123 4567", "+971-50-123-4567")]
    [InlineData("0501234567", "501234567")]               // leading zero lost to a spreadsheet
    [InlineData("+9710501234567", "(050) 123-4567")]      // stray trunk zero
    [InlineData("٠٥٠١٢٣٤٥٦٧", "+971501234567")]          // Arabic-Indic digits
    public void FormattingDifferences_AreTheSamePerson_NoConflict(string crm, string pact)
    {
        var linked = Link(crm, pact);
        Assert.Equal("+971501234567", linked.Phone);
        Assert.Equal("Crm", linked.PhoneSource);
    }

    [Fact]
    public void ADifferentNumber_CrmWins_AndIsNotAConflict()
    {
        var linked = Link("0501234567", "0559876543", pactEmail: "other@example.ae");
        Assert.Equal("+971501234567", linked.Phone);
        Assert.Equal(CollectionsCampaignFlags.None, CollectionsContactLinker.Flags(linked, true));
    }

    [Fact]
    public void AMissingCrmPhone_IsFilledFromPact_WhenNothingContradictsIt()
    {
        var linked = Link("", "0501234567", crmEmail: "a@example.ae", pactEmail: "a@example.ae");
        Assert.Equal(("+971501234567", "Pact"), (linked.Phone, linked.PhoneSource));
    }

    [Fact]
    public void AnInvalidPactNumber_IsNeverInvented_AndDoesNotCreateAConflict()
    {
        var linked = Link("0501234567", "12345");
        Assert.Equal("+971501234567", linked.Phone);
    }
}
