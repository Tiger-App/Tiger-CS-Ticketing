using TigerCS.Application.Modules.Ticketing.CustomerNames;
using TigerCS.Application.Modules.Ticketing.Services;

namespace TigerCS.Tests.Ticketing.Services;

/// <summary>
/// THE rule for the name, e-mail and phone every surface shows for a customer. It is a pure function, so it is proven here directly: the
/// Customer Profile, Customer Directory, Ticket Details and Related Tickets all call it with what they know and must get the same answer.
/// </summary>
public sealed class CustomerContactResolutionTests
{
    private static readonly DateTime T0 = new(2026, 11, 19, 8, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void AnEnteredName_BeatsEverySource_AndIsNeverShownAsVerified()
    {
        var result = CustomerContactResolution.Resolve(new CustomerContactInputs(
            ProfileName: "Mohamed Ismaik", CrmName: "Mohammed Ismail", ExternalName: "M. Ismail", SubmittedNames: ["caller"]));

        Assert.Equal("Mohamed Ismaik", result.Name);
        Assert.True(result.NameIsEdited);
        Assert.False(new ResolvedCustomerName(result.Name, result.NameSource).IsVerified);   // a person typed it: known, not a verified identity
    }

    [Fact]
    public void WithoutAnEnteredName_TheEvidenceRankingIsUnchanged_CrmThenExternalThenSubmitted()
    {
        Assert.Equal(("Crm Name", CustomerNameSource.VerifiedCrm), Pair(new CustomerContactInputs(CrmName: "Crm Name", ExternalName: "Ext", SubmittedNames: ["Sub"])));
        Assert.Equal(("Ext", CustomerNameSource.VerifiedExternal), Pair(new CustomerContactInputs(CrmName: "  ", ExternalName: "Ext", SubmittedNames: ["Sub"])));
        Assert.Equal(("Sub", CustomerNameSource.Submitted), Pair(new CustomerContactInputs(CrmName: "NOT_REGISTERED", ExternalName: null, SubmittedNames: [null, "UNKNOWN", "Sub"])));
        Assert.Equal((null, CustomerNameSource.None), Pair(new CustomerContactInputs(CrmName: "N/A", SubmittedNames: ["unknown", ""])));   // honest: no name, never a placeholder
    }

    [Fact]
    public void TheMoreRecentlySavedEnteredName_Wins_WhetherItCameFromTheProfileOrTheOlderContactRecord()
    {
        var contactNewer = CustomerContactResolution.Resolve(new CustomerContactInputs(
            ProfileName: "Profile Name", ProfileUpdatedAtUtc: T0, ManualContactName: "Contact Name", ManualContactUpdatedAtUtc: T0.AddMinutes(5)));
        var profileNewer = CustomerContactResolution.Resolve(new CustomerContactInputs(
            ProfileName: "Profile Name", ProfileUpdatedAtUtc: T0.AddMinutes(5), ManualContactName: "Contact Name", ManualContactUpdatedAtUtc: T0));

        Assert.Equal("Contact Name", contactNewer.Name);
        Assert.Equal("Profile Name", profileNewer.Name);
    }

    [Fact]
    public void APlaceholderIsNeverAnEnteredName_SoItCannotHideARealOne()
    {
        var result = CustomerContactResolution.Resolve(new CustomerContactInputs(ProfileName: "UNKNOWN", ManualContactName: "Aisha Rahman"));

        Assert.Equal("Aisha Rahman", result.Name);
    }

    [Fact]
    public void TheEmailAndPhone_FollowTheSamePrecedence_AnEnteredValueThenWhatTheTicketsCaptured()
    {
        var entered = CustomerContactResolution.Resolve(new CustomerContactInputs(
            ProfileEmail: "entered@example.com", ManualContactEmail: "contact@example.com", DerivedEmails: ["ticket@example.com"],
            ProfilePhone: "+971501234567", DerivedPhones: ["+971559998888"]));
        var derived = CustomerContactResolution.Resolve(new CustomerContactInputs(
            DerivedEmails: [null, "  ", "ticket@example.com", "other@example.com"], DerivedPhones: ["", "+971559998888"]));

        Assert.Equal("entered@example.com", entered.Email);
        Assert.Equal("+971501234567", entered.Phone);
        Assert.Equal("ticket@example.com", derived.Email);
        Assert.Equal("+971559998888", derived.Phone);
    }

    [Fact]
    public void EveryNumberTheCustomerIsKnownBy_IsATryableKey_InEveryWrittenForm_SoANameSavedUnderAnyOfThemIsFound()
    {
        var keys = CustomerContactResolution.ContactKeys(["+971501234567", "050 123 4567", "041234567", "0097141234567", null, "", "+447911123456"]);

        // One UAE mobile written two ways and one landline written two ways: the stored digits AND the UAE-aware form of each are tried.
        Assert.Contains("971501234567", keys);
        Assert.Contains("0501234567", keys);
        Assert.Contains("97141234567", keys);
        Assert.Contains("041234567", keys);
        Assert.Contains("0097141234567", keys);
        Assert.Contains("447911123456", keys);
        Assert.Equal(keys.Distinct().Count(), keys.Count);
        Assert.DoesNotContain("", keys);
    }

    private static (string?, CustomerNameSource) Pair(CustomerContactInputs inputs)
    {
        var result = CustomerContactResolution.Resolve(inputs);
        return (result.Name, result.NameSource);
    }
}
