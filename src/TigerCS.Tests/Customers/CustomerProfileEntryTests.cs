using TigerCS.Domain.Modules.Ticketing;

namespace TigerCS.Tests.Customers;

/// <summary>The rules for what may be typed into the Customers directory's Edit form: phone normalization, placeholder text, and the override row.</summary>
public class CustomerProfileEntryTests
{
    [Theory]
    [InlineData("+971501234567", "+971501234567")]
    [InlineData("+971 50 123 4567", "+971501234567")]
    [InlineData("  +971-50-123-4567 ", "+971501234567")]
    [InlineData("00971501234567", "+971501234567")]
    [InlineData("00 971 (50) 123.4567", "+971501234567")]
    [InlineData("971501234567", "+971501234567")]           // a UAE number written without the plus is still a UAE number
    // A UAE mobile or landline written locally is unmistakably UAE and is saved internationally.
    [InlineData("0501234567", "+971501234567")]
    [InlineData("050 123 4567", "+971501234567")]
    [InlineData("041234567", "+97141234567")]
    [InlineData("+97141234567", "+97141234567")]
    [InlineData("0097141234567", "+97141234567")]
    [InlineData("97141234567", "+97141234567")]
    [InlineData("02 123 4567", "+97121234567")]
    [InlineData("03 123 4567", "+97131234567")]
    [InlineData("06 123 4567", "+97161234567")]
    [InlineData("07 123 4567", "+97171234567")]
    [InlineData("09 123 4567", "+97191234567")]
    [InlineData("+44 20 7946 0958", "+442079460958")]
    [InlineData("0044 20 7946 0958", "+442079460958")]
    [InlineData("+1 (202) 555-0143", "+12025550143")]
    public void A_valid_international_number_is_normalized(string input, string expected)
    {
        Assert.True(CustomerProfileEntry.TryNormalizePhone(input, out var normalized, out var error), error);
        Assert.Equal(expected, normalized);
        Assert.Null(error);

        // Whatever was accepted is a number the directory can place: it is never Unknown.
        Assert.NotEqual(CustomerLocation.Unknown, CustomerPhoneLocation.Classify(normalized));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void A_blank_number_is_refused(string? input)
    {
        Assert.False(CustomerProfileEntry.TryNormalizePhone(input, out var normalized, out var error));
        Assert.Equal(string.Empty, normalized);
        Assert.False(string.IsNullOrWhiteSpace(error));
    }

    [Theory]
    [InlineData("501234567")]             // no leading 0 and no country code: no country is ever guessed
    [InlineData("41234567")]
    [InlineData("0112345678")]            // a national number that is not a UAE mobile or landline
    [InlineData("081234567")]             // 08 is not a UAE area code
    [InlineData("0412345678")]            // a UAE landline has 8 digits after the 0, not 9
    [InlineData("+971 12345")]            // a UAE number is +971 and a 9-digit mobile or 8-digit landline
    [InlineData("+971412345678")]         // 9 digits starting 4 is neither
    [InlineData("+9715012345678")]
    [InlineData("971501234")]
    [InlineData("+0 1234567890")]         // a country code never starts with 0
    [InlineData("+1234567")]              // fewer than 8 digits
    [InlineData("+1234567890123456")]     // more than 15 digits
    [InlineData("+971abc501234567")]
    [InlineData("0501234567 ext 12")]
    [InlineData("++971501234567")]
    [InlineData("971+501234567")]
    [InlineData("+")]
    [InlineData("00")]
    public void An_invalid_or_ambiguous_number_is_refused_with_a_reason(string input)
    {
        Assert.False(CustomerProfileEntry.TryNormalizePhone(input, out var normalized, out var error));
        Assert.Equal(string.Empty, normalized);
        Assert.False(string.IsNullOrWhiteSpace(error));
    }

    [Fact]
    public void A_national_number_is_told_how_to_write_it()
    {
        Assert.False(CustomerProfileEntry.TryNormalizePhone("0112345678", out _, out var error));
        Assert.Contains("country code", error, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("NOT_REGISTERED")]
    [InlineData("not registered")]
    [InlineData("Not-Registered")]
    [InlineData("UNKNOWN")]
    [InlineData("N/A")]
    [InlineData("n.a.")]
    [InlineData("NULL")]
    [InlineData("  none  ")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    [InlineData("---")]
    public void A_placeholder_or_blank_value_is_missing_data(string? value) => Assert.Null(CustomerFieldText.Clean(value));

    [Theory]
    [InlineData("Tiger Bay Towers")]
    [InlineData("1105")]
    [InlineData("M-401")]
    [InlineData("+971501234567")]
    public void A_real_value_is_kept_trimmed(string value) => Assert.Equal(value, CustomerFieldText.Clean($"  {value} "));

    [Fact]
    public void The_fallback_text_is_friendly_and_is_not_the_source_placeholder()
    {
        Assert.Equal("Not provided", CustomerFieldText.NotProvided);

        // The fallback is itself treated as "no value", so cleaning an already-cleaned value never turns it back into text.
        Assert.Null(CustomerFieldText.Clean(CustomerFieldText.NotProvided));
    }

    private static CustomerProfileOverride NewOverride() =>
        new("crm:9101", identityKind: 1, crmBuyerCustomerId: 9101, externalSource: null, externalCustomerId: null, identityPhone: null,
            updatedByEmployeeId: Guid.NewGuid(), nowUtc: DateTime.UtcNow);

    [Fact]
    public void The_override_places_the_edited_phone_once_by_the_directorys_own_rule()
    {
        var row = NewOverride();

        row.Set("+971501234567", null, null, Guid.NewGuid(), DateTime.UtcNow);
        Assert.Equal(CustomerLocation.WithinUae, row.PhoneLocation);

        row.Set("+442079460958", "Tiger Bay Towers", "1105", Guid.NewGuid(), DateTime.UtcNow);
        Assert.Equal(CustomerLocation.OutsideUae, row.PhoneLocation);
        Assert.Equal("Tiger Bay Towers", row.ProjectName);
        Assert.Equal("1105", row.UnitNumber);

        row.Set(null, "Tiger Bay Towers", "1105", Guid.NewGuid(), DateTime.UtcNow);
        Assert.Null(row.PhoneNumber);
        Assert.Null(row.PhoneLocation);   // no phone edit: the customer is placed by their tickets
    }

    [Fact]
    public void An_override_with_nothing_left_is_empty_so_the_service_can_delete_it()
    {
        var row = NewOverride();
        row.Set("+971501234567", "P", "U", Guid.NewGuid(), DateTime.UtcNow);
        Assert.False(row.IsEmpty);

        row.Set("  ", null, "", Guid.NewGuid(), DateTime.UtcNow);
        Assert.True(row.IsEmpty);
    }

    [Fact]
    public void The_override_records_who_changed_it_and_when_and_guards_its_columns()
    {
        var row = NewOverride();
        var editor = Guid.NewGuid();
        var at = new DateTime(2026, 11, 18, 9, 30, 0, DateTimeKind.Utc);

        row.Set("+971501234567", null, null, editor, at);

        Assert.Equal(editor, row.UpdatedByEmployeeId);
        Assert.Equal(at, row.UpdatedAtUtc);
        Assert.Throws<ArgumentException>(() => row.Set(null, new string('x', CustomerProfileOverride.ProjectNameMaxLength + 1), "U", editor, at));
        Assert.Throws<ArgumentException>(() => row.Set(null, "P", new string('x', CustomerProfileOverride.UnitNumberMaxLength + 1), editor, at));
        Assert.Throws<ArgumentException>(() => row.Set("+971501234567", null, null, Guid.Empty, at));
    }
}
