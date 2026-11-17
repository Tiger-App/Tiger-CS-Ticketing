namespace TigerCS.Domain.Modules.Ticketing;

/// <summary>
/// Where a customer is, judged ONLY from their phone number's country code — never from nationality, a CRM country
/// field or anything else. The three values are mutually exclusive: a customer is in exactly one of them.
/// </summary>
public enum CustomerLocation
{
    /// <summary>Missing, invalid, ambiguous (national form with no country code) or conflicting phone evidence. Not guessed.</summary>
    Unknown = 0,

    /// <summary>A valid UAE number: country code +971.</summary>
    WithinUae = 1,

    /// <summary>A valid international number carrying any other country code.</summary>
    OutsideUae = 2,
}

/// <summary>
/// The one rule that places a single phone number in a <see cref="CustomerLocation"/>. The Customers directory
/// (<c>CustomerDirectoryRepository</c>) mirrors it in SQL with chained <c>REPLACE()</c> — a database cannot run C# — and a
/// test asserts the two agree on a table of samples, so this class is the authority and the SQL the translation.
///
/// <para>
/// A number is first reduced the way <see cref="CustomerPhoneNumber"/> reduces it: the separators in
/// <see cref="CustomerPhoneNumber.SeparatorCharacters"/> are dropped. Anything left that is not a digit (a letter, "ext")
/// makes the number invalid. Then:
/// </para>
/// <list type="bullet">
/// <item>Written internationally — a leading "+" or "00" — the rest is the E.164 number: 8–15 digits, not starting with 0.
/// "971" followed by exactly 9 digits is UAE; any other "971…" is an invalid UAE number (<see cref="CustomerLocation.Unknown"/>);
/// every other valid number is Outside UAE.</item>
/// <item>Written without either marker, only "971" + 9 digits is placed (UAE) — the existing rule that "971501234567" and
/// "+971501234567" are one customer. A national form ("0501234567") or a bare foreign number states no country, so it is
/// <see cref="CustomerLocation.Unknown"/>: no country is ever guessed.</item>
/// </list>
/// </summary>
public static class CustomerPhoneLocation
{
    /// <summary>The UAE's calling code.</summary>
    public const string UaeCountryCode = "971";

    /// <summary>The digits after "971" in a UAE number (area/mobile prefix + subscriber number).</summary>
    public const int UaeNationalNumberLength = 9;

    /// <summary>E.164 allows at most 15 digits and, in practice, no fewer than 8 including the country code.</summary>
    public const int MinInternationalDigits = 8;

    public const int MaxInternationalDigits = 15;

    public static CustomerLocation Classify(string? phoneNumber)
    {
        if (string.IsNullOrWhiteSpace(phoneNumber))
        {
            return CustomerLocation.Unknown;
        }

        var raw = phoneNumber.Trim();
        var hasPlus = raw[0] == '+';

        var stripped = new System.Text.StringBuilder(raw.Length);
        foreach (var character in raw)
        {
            if (!CustomerPhoneNumber.SeparatorCharacters.Contains(character))
            {
                if (!char.IsAsciiDigit(character))
                {
                    return CustomerLocation.Unknown;
                }

                stripped.Append(character);
            }
        }

        var digits = stripped.ToString();
        var international = hasPlus || digits.StartsWith("00", StringComparison.Ordinal);
        var core = international && !hasPlus ? digits[2..] : digits;

        if (international)
        {
            if (core.Length is < MinInternationalDigits or > MaxInternationalDigits || core[0] == '0')
            {
                return CustomerLocation.Unknown;
            }

            if (core.StartsWith(UaeCountryCode, StringComparison.Ordinal))
            {
                return core.Length == UaeCountryCode.Length + UaeNationalNumberLength ? CustomerLocation.WithinUae : CustomerLocation.Unknown;
            }

            return CustomerLocation.OutsideUae;
        }

        return core.StartsWith(UaeCountryCode, StringComparison.Ordinal) && core.Length == UaeCountryCode.Length + UaeNationalNumberLength
            ? CustomerLocation.WithinUae
            : CustomerLocation.Unknown;
    }

    /// <summary>
    /// A customer's location from every phone their tickets carry: all located numbers must agree. One UAE and one foreign number
    /// is ambiguous, so the customer is <see cref="CustomerLocation.Unknown"/> — never counted twice, never counted in a guessed place.
    /// Numbers that themselves classify Unknown are ignored: they add no evidence either way.
    /// </summary>
    public static CustomerLocation Resolve(IEnumerable<CustomerLocation> perTicket)
    {
        var within = false;
        var outside = false;
        foreach (var location in perTicket)
        {
            within |= location == CustomerLocation.WithinUae;
            outside |= location == CustomerLocation.OutsideUae;
        }

        return within == outside ? CustomerLocation.Unknown : within ? CustomerLocation.WithinUae : CustomerLocation.OutsideUae;
    }
}
