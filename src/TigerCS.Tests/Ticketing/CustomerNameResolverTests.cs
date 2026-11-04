using TigerCS.Application.Modules.Ticketing.CustomerNames;

namespace TigerCS.Tests.Ticketing;

/// <summary>
/// The single customer-name rule shared by Ticket Details, the Customers directory and the Customer Profile: CRM snapshot, then the
/// verified external snapshot, then any submitted name — and a placeholder, blank or failed-lookup value never replaces or hides a
/// valid name from another source.
/// </summary>
public class CustomerNameResolverTests
{
    [Fact]
    public void SubmittedName_IsKnown_ButNotVerified()
    {
        var r = CustomerNameResolver.Resolve(null, null, "  Ahmed Hassan ");

        Assert.Equal("Ahmed Hassan", r.Name);
        Assert.Equal(CustomerNameSource.Submitted, r.Source);
        Assert.True(r.HasName);
        Assert.False(r.IsVerified);
    }

    [Fact]
    public void CrmName_WinsOverEverything_AndIsVerified()
    {
        var r = CustomerNameResolver.Resolve("Mariam Al Falasi", "Omar Saleh", "Typed By Customer");

        Assert.Equal("Mariam Al Falasi", r.Name);
        Assert.Equal(CustomerNameSource.VerifiedCrm, r.Source);
        Assert.True(r.IsVerified);
    }

    [Fact]
    public void ExternalName_BeatsSubmitted_AndIsVerified()
    {
        var r = CustomerNameResolver.Resolve(null, "Omar Saleh", "Typed By Customer");

        Assert.Equal("Omar Saleh", r.Name);
        Assert.Equal(CustomerNameSource.VerifiedExternal, r.Source);
        Assert.True(r.IsVerified);
    }

    [Theory]
    [InlineData("NOT_REGISTERED")]
    [InlineData("Not Registered")]
    [InlineData("UNKNOWN")]
    [InlineData("N/A")]
    [InlineData("null")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    [InlineData("12345")]
    public void PlaceholderInAHigherSource_NeverHidesAValidSubmittedName(string? placeholder)
    {
        var r = CustomerNameResolver.Resolve(placeholder, placeholder, placeholder, "Sara Khalid");

        Assert.Equal("Sara Khalid", r.Name);
        Assert.Equal(CustomerNameSource.Submitted, r.Source);
    }

    [Fact]
    public void PlaceholderCrmName_FallsThroughToTheExternalName()
    {
        var r = CustomerNameResolver.Resolve("NOT_REGISTERED", "Omar Saleh");

        Assert.Equal("Omar Saleh", r.Name);
        Assert.Equal(CustomerNameSource.VerifiedExternal, r.Source);
    }

    [Fact]
    public void FirstValidSubmittedName_Wins_InOrder() =>
        Assert.Equal("Second Name", CustomerNameResolver.Resolve(null, null, "UNKNOWN", "Second Name", "Third Name").Name);

    [Fact]
    public void NoValidName_AnywhereIsNone()
    {
        var r = CustomerNameResolver.Resolve("NOT_REGISTERED", null, "UNKNOWN", "", null);

        Assert.Null(r.Name);
        Assert.Equal(CustomerNameSource.None, r.Source);
        Assert.False(r.HasName);
        Assert.False(r.IsVerified);
    }

    [Fact]
    public void ArabicName_IsAValidName() =>
        Assert.Equal("أحمد حسن", CustomerNameResolver.Resolve(null, null, "أحمد حسن").Name);
}
