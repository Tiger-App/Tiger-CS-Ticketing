using TigerCS.Domain.Modules.Ticketing;

namespace TigerCS.Tests.Ticketing.Domain;

/// <summary>The phone → location rule behind the Dashboard's Customers by Location card. Nationality plays no part; only the country code does.</summary>
public sealed class CustomerPhoneLocationTests
{
    [Theory]
    [InlineData("+971501234567")]
    [InlineData("971501234567")]
    [InlineData("+971 50 123 4567")]
    [InlineData("+971-50-123-4567")]
    [InlineData("(+971) 50.123.4567")]
    [InlineData("00971501234567")]
    [InlineData("00 971 50 123 4567")]
    [InlineData("  +971501234567  ")]
    [InlineData("+971412345678")]       // a landline: 971 + 9 digits
    public void WithinUae_ForAValidUaeNumber_InAnyWrittenForm(string phone) =>
        Assert.Equal(CustomerLocation.WithinUae, CustomerPhoneLocation.Classify(phone));

    [Theory]
    [InlineData("+447911123456")]
    [InlineData("+44 7911 123456")]
    [InlineData("0044 7911 123456")]
    [InlineData("+14155552671")]
    [InlineData("+919876543210")]
    [InlineData("+966501234567")]
    [InlineData("+1 (415) 555-2671")]
    public void OutsideUae_ForAValidInternationalNumberWithAnotherCountryCode(string phone) =>
        Assert.Equal(CustomerLocation.OutsideUae, CustomerPhoneLocation.Classify(phone));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("+")]
    [InlineData("0501234567")]          // UAE national form: no country code stated, none is guessed
    [InlineData("501234567")]
    [InlineData("447911123456")]        // a foreign number with no '+'/'00' marker is indistinguishable from a national number
    [InlineData("+97141234567")]        // 971 + 8 digits
    [InlineData("+97150123456")]        // 971 + 8 digits: an invalid UAE number, not "outside"
    [InlineData("+9715012345678")]      // 971 + 10 digits
    [InlineData("+971501234567 ext 12")] // letters: invalid
    [InlineData("call me")]
    [InlineData("+0441234567")]         // country codes never start with 0
    [InlineData("+1234567")]            // 7 digits: below the E.164 minimum used here
    [InlineData("+1234567890123456")]   // 16 digits: above E.164's 15
    [InlineData("00")]
    public void Unknown_ForMissingInvalidOrAmbiguousNumbers(string? phone) =>
        Assert.Equal(CustomerLocation.Unknown, CustomerPhoneLocation.Classify(phone));

    [Fact]
    public void Resolve_RequiresEveryLocatedNumberToAgree_AndIgnoresUnknownOnes()
    {
        Assert.Equal(CustomerLocation.WithinUae, CustomerPhoneLocation.Resolve([CustomerLocation.WithinUae, CustomerLocation.Unknown, CustomerLocation.WithinUae]));
        Assert.Equal(CustomerLocation.OutsideUae, CustomerPhoneLocation.Resolve([CustomerLocation.Unknown, CustomerLocation.OutsideUae]));
        Assert.Equal(CustomerLocation.Unknown, CustomerPhoneLocation.Resolve([CustomerLocation.WithinUae, CustomerLocation.OutsideUae]));
        Assert.Equal(CustomerLocation.Unknown, CustomerPhoneLocation.Resolve([]));
        Assert.Equal(CustomerLocation.Unknown, CustomerPhoneLocation.Resolve([CustomerLocation.Unknown]));
    }

    [Theory]
    [InlineData(null, CustomerPhoneLocationReason.Missing)]
    [InlineData("  ", CustomerPhoneLocationReason.Missing)]
    [InlineData("+", CustomerPhoneLocationReason.Missing)]
    [InlineData("call me", CustomerPhoneLocationReason.InvalidCharacters)]
    [InlineData("+971501234567 ext 12", CustomerPhoneLocationReason.InvalidCharacters)]
    [InlineData("+1234567", CustomerPhoneLocationReason.InternationalWrongLength)]
    [InlineData("00", CustomerPhoneLocationReason.InternationalWrongLength)]
    [InlineData("+1234567890123456", CustomerPhoneLocationReason.InternationalWrongLength)]
    [InlineData("+0441234567", CustomerPhoneLocationReason.InternationalLeadingZero)]
    [InlineData("+97141234567", CustomerPhoneLocationReason.UaeWrongLength)]
    [InlineData("00971501234", CustomerPhoneLocationReason.UaeWrongLength)]
    [InlineData("97150123", CustomerPhoneLocationReason.UaeWrongLength)]
    [InlineData("0501234567", CustomerPhoneLocationReason.UaeLocalFormat)]      // UAE mobile, national form
    [InlineData("04 123 4567", CustomerPhoneLocationReason.UaeLocalFormat)]     // UAE landline (Dubai), national form
    [InlineData("0112345678", CustomerPhoneLocationReason.NationalFormatNoCountryCode)]
    [InlineData("501234567", CustomerPhoneLocationReason.NoCountryCode)]
    [InlineData("447911123456", CustomerPhoneLocationReason.NoCountryCode)]
    public void Unknown_NamesExactlyWhy_AndNothingIsGuessed(string? phone, CustomerPhoneLocationReason reason)
    {
        var diagnosis = CustomerPhoneLocation.Diagnose(phone);
        Assert.Equal(CustomerLocation.Unknown, diagnosis.Location);
        Assert.Equal(reason, diagnosis.Reason);
    }

    [Theory]
    [InlineData("+971501234567", CustomerLocation.WithinUae)]
    [InlineData("971501234567", CustomerLocation.WithinUae)]
    [InlineData("00971501234567", CustomerLocation.WithinUae)]
    [InlineData("+447911123456", CustomerLocation.OutsideUae)]
    [InlineData("0044 7911 123456", CustomerLocation.OutsideUae)]
    public void APlacedNumber_HasNoReason_AndClassifyIsTheDiagnosedLocation(string phone, CustomerLocation location)
    {
        var diagnosis = CustomerPhoneLocation.Diagnose(phone);
        Assert.Equal(location, diagnosis.Location);
        Assert.Equal(CustomerPhoneLocationReason.None, diagnosis.Reason);
        Assert.Equal(location, CustomerPhoneLocation.Classify(phone));
    }
}
