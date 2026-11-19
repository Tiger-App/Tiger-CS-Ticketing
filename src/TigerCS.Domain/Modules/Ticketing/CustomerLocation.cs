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
/// "971" followed by a valid UAE mobile (9 digits, 5…) or landline (8 digits, area digit 2/3/4/6/7/9) is UAE (see
/// <see cref="UaePhoneNumbers"/>); any other "971…" is an invalid UAE number (<see cref="CustomerLocation.Unknown"/>);
/// every other valid number is Outside UAE.</item>
/// <item>Written without either marker, "971" + a valid UAE number is placed (UAE), and so is a UAE mobile or landline in national
/// form ("0501234567", "041234567"): the trunk 0 plus that shape exists nowhere else. Any other national form or a bare foreign
/// number states no country, so it is <see cref="CustomerLocation.Unknown"/>: no country is ever guessed.</item>
/// <item>This is the country of the <i>phone number</i>. It says nothing about the customer's emirate (area codes are shared) or
/// where they actually live.</item>
/// </list>
/// </summary>
/// <summary>Why a phone number (or a customer's phones) is not placed in a location.</summary>
public enum CustomerPhoneLocationReason
{
    /// <summary>Placed: there is nothing to explain.</summary>
    None = 0,

    /// <summary>No phone number is recorded (or it is only separators).</summary>
    Missing = 1,

    /// <summary>The number contains letters or symbols other than the separators (" ", "-", "(", ")", ".", "/", "+").</summary>
    InvalidCharacters = 2,

    /// <summary>Written with "+" or "00" but not 8–15 digits.</summary>
    InternationalWrongLength = 3,

    /// <summary>Written with "+" or "00" and a country code starting with 0.</summary>
    InternationalLeadingZero = 4,

    /// <summary>Starts with 971 (+971 / 00971 / 971) but is not followed by a valid UAE mobile (9 digits, 5…) or landline (8 digits, area digit 2/3/4/6/7/9).</summary>
    UaeWrongLength = 5,

    /// <summary>Retired: a UAE-style national number (05x mobile / 0[234679] landline) is now placed Within UAE. Kept only so stored values keep their meaning.</summary>
    UaeLocalFormat = 6,

    /// <summary>Starts with a single 0 (national format) and is not a UAE-style local number: the country is not stated.</summary>
    NationalFormatNoCountryCode = 7,

    /// <summary>Digits with no "+", "00" or 971 prefix: the country is not stated.</summary>
    NoCountryCode = 8,

    /// <summary>The customer's tickets carry both a UAE and a foreign number: ambiguous, so not placed.</summary>
    ConflictingCountries = 9,
}

/// <summary>A phone's location and, when Unknown, the reason.</summary>
public readonly record struct PhoneLocationDiagnosis(CustomerLocation Location, CustomerPhoneLocationReason Reason);

public static class CustomerPhoneLocation
{
    /// <summary>The UAE's calling code.</summary>
    public const string UaeCountryCode = "971";

    /// <summary>The digits after "971" in a UAE <i>mobile</i> number. A landline has 8 (<see cref="UaePhoneNumbers.LandlineNationalLength"/>).</summary>
    public const int UaeNationalNumberLength = 9;

    /// <summary>E.164 allows at most 15 digits and, in practice, no fewer than 8 including the country code.</summary>
    public const int MinInternationalDigits = 8;

    public const int MaxInternationalDigits = 15;

    public static CustomerLocation Classify(string? phoneNumber) => Diagnose(phoneNumber).Location;

    /// <summary>
    /// The one rule, with its reason: where <paramref name="phoneNumber"/> sits and — when it sits nowhere — exactly why. <see cref="Classify"/> is this
    /// method's <see cref="PhoneLocationDiagnosis.Location"/>, so the Dashboard, the Customers directory and the Unknown explanations can never disagree.
    /// Nothing is guessed and nothing is rewritten: a local number such as "0501234567" is reported as <see cref="CustomerPhoneLocationReason.UaeLocalFormat"/>
    /// (it looks UAE but states no country) and stays Unknown.
    /// </summary>
    public static PhoneLocationDiagnosis Diagnose(string? phoneNumber)
    {
        if (string.IsNullOrWhiteSpace(phoneNumber))
        {
            return new(CustomerLocation.Unknown, CustomerPhoneLocationReason.Missing);
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
                    return new(CustomerLocation.Unknown, CustomerPhoneLocationReason.InvalidCharacters);
                }

                stripped.Append(character);
            }
        }

        var digits = stripped.ToString();
        if (digits.Length == 0)
        {
            return new(CustomerLocation.Unknown, CustomerPhoneLocationReason.Missing);
        }

        var international = hasPlus || digits.StartsWith("00", StringComparison.Ordinal);
        var core = international && !hasPlus ? digits[2..] : digits;

        if (international)
        {
            if (core.Length is < MinInternationalDigits or > MaxInternationalDigits)
            {
                return new(CustomerLocation.Unknown, CustomerPhoneLocationReason.InternationalWrongLength);
            }

            if (core[0] == '0')
            {
                return new(CustomerLocation.Unknown, CustomerPhoneLocationReason.InternationalLeadingZero);
            }

            if (core.StartsWith(UaeCountryCode, StringComparison.Ordinal))
            {
                return UaePhoneNumbers.IsInternationalUae(core)
                    ? new(CustomerLocation.WithinUae, CustomerPhoneLocationReason.None)
                    : new(CustomerLocation.Unknown, CustomerPhoneLocationReason.UaeWrongLength);
            }

            return new(CustomerLocation.OutsideUae, CustomerPhoneLocationReason.None);
        }

        if (core.StartsWith(UaeCountryCode, StringComparison.Ordinal))
        {
            return UaePhoneNumbers.IsInternationalUae(core)
                ? new(CustomerLocation.WithinUae, CustomerPhoneLocationReason.None)
                : new(CustomerLocation.Unknown, CustomerPhoneLocationReason.UaeWrongLength);
        }

        if (core[0] == '0')
        {
            // A UAE mobile (05x xxx xxxx) or landline (0[234679] xxx xxxx) in national form is unmistakably a UAE number: it is placed
            // Within UAE, exactly as +971… would be. Any other national number states no country and stays Unknown.
            return UaePhoneNumbers.NationalSignificantFromLocal(core) is not null
                ? new(CustomerLocation.WithinUae, CustomerPhoneLocationReason.None)
                : new(CustomerLocation.Unknown, CustomerPhoneLocationReason.NationalFormatNoCountryCode);
        }

        return new(CustomerLocation.Unknown, CustomerPhoneLocationReason.NoCountryCode);
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
