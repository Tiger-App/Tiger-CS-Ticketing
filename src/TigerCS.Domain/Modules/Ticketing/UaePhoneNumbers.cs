namespace TigerCS.Domain.Modules.Ticketing;

/// <summary>
/// The UAE numbering plan, in one place, so the phone rules (classification, editing, matching, Genesys addresses) cannot
/// disagree about what a UAE number is.
///
/// <para>
/// A UAE number is the country code <c>971</c> followed by a <b>national significant number</b> of one of two shapes:
/// a <b>mobile</b> - 9 digits starting with 5 (<c>50 / 52 / 54 / 55 / 56 / 58 ...</c>) - or a <b>landline</b> - 8 digits
/// whose first digit is the area code <c>2</c> (Abu Dhabi), <c>3</c> (Al Ain), <c>4</c> (Dubai), <c>6</c> (Sharjah, Ajman, Umm Al Quwain),
/// <c>7</c> (Ras Al Khaimah) or <c>9</c> (Fujairah). Written nationally each carries one leading trunk <c>0</c>:
/// <c>0501234567</c> and <c>041234567</c>.
/// </para>
///
/// <para>
/// <b>Not an emirate.</b> Area codes are shared (6 covers three emirates) and mobile prefixes say nothing about where the
/// subscriber lives, so nothing here ever derives an emirate. A UAE number says only that the <i>phone number</i> is Emirati;
/// where the customer actually lives is a separate fact the customer or an agent states.
/// </para>
/// </summary>
public static class UaePhoneNumbers
{
    public const string CountryCode = "971";

    /// <summary>Area digits of a UAE landline: 2 Abu Dhabi, 3 Al Ain, 4 Dubai, 6 Sharjah/Ajman/UAQ, 7 Ras Al Khaimah, 9 Fujairah.</summary>
    public const string LandlineAreaDigits = "234679";

    public const int MobileNationalLength = 9;
    public const int LandlineNationalLength = 8;

    /// <summary>True for a UAE mobile's national significant number: 9 digits starting with 5.</summary>
    public static bool IsMobileNational(string? nsn) =>
        nsn is { Length: MobileNationalLength } && nsn[0] == '5' && nsn.All(char.IsAsciiDigit);

    /// <summary>True for a UAE landline's national significant number: 8 digits starting with an area digit of 2, 3, 4, 6, 7 or 9.</summary>
    public static bool IsLandlineNational(string? nsn) =>
        nsn is { Length: LandlineNationalLength } && LandlineAreaDigits.Contains(nsn[0]) && nsn.All(char.IsAsciiDigit);

    /// <summary>True when <paramref name="nsn"/> (the digits after 971) is a valid UAE mobile or landline number.</summary>
    public static bool IsValidNational(string? nsn) => IsMobileNational(nsn) || IsLandlineNational(nsn);

    /// <summary>
    /// The national significant number of a UAE number written in national form: one leading 0 and then a mobile or landline
    /// (<c>0501234567</c>, <c>041234567</c>). Null for anything else - including a number with no leading 0, which states no country.
    /// </summary>
    public static string? NationalSignificantFromLocal(string? digits)
    {
        if (digits is { Length: > 1 } && digits[0] == '0' && digits[1] != '0')
        {
            var nsn = digits[1..];
            return IsValidNational(nsn) ? nsn : null;
        }

        return null;
    }

    /// <summary>True when <paramref name="digits"/> (digits only, no 00 / +) is <c>971</c> followed by a valid UAE mobile or landline.</summary>
    public static bool IsInternationalUae(string? digits) =>
        digits is { Length: > 3 } && digits.StartsWith(CountryCode, StringComparison.Ordinal) && IsValidNational(digits[3..]);

    /// <summary>The national spelling (<c>0</c> + national number) of a UAE number given as digits in any written form; null when it is not a UAE number.</summary>
    public static string? ToLocalDigits(string? digits) =>
        ToInternationalDigits(digits) is { } international ? "0" + international[CountryCode.Length..] : null;

    /// <summary>
    /// The extra digit strings a phone-shaped search term must also be tried as, so searching "0501234567" finds a customer stored as
    /// "+971501234567" and the other way round. For anything that is not unmistakably UAE both are the term itself - nothing is guessed.
    /// </summary>
    public static (string Alternative1, string Alternative2) SearchVariants(string searchDigits) =>
        (ToInternationalDigits(searchDigits) ?? searchDigits, ToLocalDigits(searchDigits) ?? searchDigits);

    /// <summary>
    /// The one international UAE form (<c>971</c> + national number, digits only) of a UAE number written in any of the three ways:
    /// local (<c>041234567</c>), international (<c>+97141234567</c> / <c>97141234567</c>) or with the 00 prefix (<c>0097141234567</c>).
    /// Null when the digits are not a recognisable UAE number - a foreign or malformed number is never rewritten.
    /// </summary>
    public static string? ToInternationalDigits(string? digits)
    {
        if (string.IsNullOrEmpty(digits) || !digits.All(char.IsAsciiDigit))
        {
            return null;
        }

        var core = digits.StartsWith("00", StringComparison.Ordinal) ? digits[2..] : digits;
        if (IsInternationalUae(core))
        {
            return core;
        }

        return !digits.StartsWith("00", StringComparison.Ordinal) && NationalSignificantFromLocal(digits) is { } nsn ? CountryCode + nsn : null;
    }
}
