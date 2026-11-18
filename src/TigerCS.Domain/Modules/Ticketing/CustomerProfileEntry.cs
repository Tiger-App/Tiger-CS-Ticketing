namespace TigerCS.Domain.Modules.Ticketing;

/// <summary>
/// The rules for what may be typed into the Customers directory's Edit form. Pure, so the Api, the Web and the tests share one answer.
/// </summary>
public static class CustomerProfileEntry
{
    /// <summary>
    /// Normalizes a phone number typed by a person to its international form (<c>+971501234567</c>) or says why it cannot be saved.
    ///
    /// <para>
    /// The separators <see cref="CustomerPhoneNumber.SeparatorCharacters"/> are dropped; anything else that is not a digit (a letter,
    /// "ext") is refused. The number must state its country: a leading "+" or "00" ("00971..." becomes "+971..."), or "971" followed
    /// by the nine digits of a UAE number. No country is ever guessed, so a national form ("0501234567") is refused with a message that
    /// says how to write it, exactly as <see cref="CustomerPhoneLocation"/> places such a number in Unknown. Validity itself is
    /// delegated to <see cref="CustomerPhoneLocation.Classify"/> (8-15 digits, not starting 0, a UAE number is "971" + 9 digits), so what is
    /// accepted here is exactly what the directory can place.
    /// </para>
    /// </summary>
    public static bool TryNormalizePhone(string? input, out string normalized, out string? error)
    {
        normalized = string.Empty;
        error = null;

        if (string.IsNullOrWhiteSpace(input))
        {
            error = "Enter a phone number.";
            return false;
        }

        var raw = input.Trim();
        var plus = raw[0] == '+';
        if (raw.IndexOf('+', 1) >= 0)
        {
            error = "A + may only start the number, as in +971 50 123 4567.";
            return false;
        }

        var stripped = new string(raw.Where(c => !CustomerPhoneNumber.SeparatorCharacters.Contains(c)).ToArray());
        if (stripped.Length == 0 || !stripped.All(char.IsAsciiDigit))
        {
            error = "A phone number may contain only digits, spaces and the characters + - ( ) . /";
            return false;
        }

        string digits;
        if (plus)
        {
            digits = stripped;
        }
        else if (stripped.StartsWith("00", StringComparison.Ordinal))
        {
            digits = stripped[2..];
        }
        else if (stripped.StartsWith(CustomerPhoneLocation.UaeCountryCode, StringComparison.Ordinal)
                 && stripped.Length == CustomerPhoneLocation.UaeCountryCode.Length + CustomerPhoneLocation.UaeNationalNumberLength)
        {
            digits = stripped;
        }
        else
        {
            error = "Write the number with its country code, for example +971 50 123 4567. A number without one is not guessed.";
            return false;
        }

        var candidate = "+" + digits;
        if (CustomerPhoneLocation.Classify(candidate) == CustomerLocation.Unknown)
        {
            error = digits.StartsWith(CustomerPhoneLocation.UaeCountryCode, StringComparison.Ordinal)
                ? "A UAE number is +971 followed by 9 digits."
                : $"A phone number has {CustomerPhoneLocation.MinInternationalDigits} to {CustomerPhoneLocation.MaxInternationalDigits} digits including its country code, and the country code does not start with 0.";
            return false;
        }

        normalized = candidate;
        return true;
    }
}

/// <summary>
/// Placeholder text a source system writes for "no value" (<c>NOT_REGISTERED</c>, <c>UNKNOWN</c>, <c>N/A</c>...). A project, unit or phone
/// that is only a placeholder is missing data and must be shown as missing, never printed. (Names have their own, wider rule in
/// <c>CustomerNameResolver</c>.)
/// </summary>
public static class CustomerFieldText
{
    /// <summary>What the Customers pages print for a missing project, unit or phone.</summary>
    public const string NotProvided = "Not provided";

    private static readonly HashSet<string> Placeholders = new(StringComparer.OrdinalIgnoreCase)
    {
        "NOTREGISTERED", "UNREGISTERED", "UNKNOWN", "NA", "NONE", "NULL", "NIL", "NAN", "EMPTY",
        "NOTAVAILABLE", "NOTPROVIDED", "NOTSPECIFIED", "UNDEFINED", "TBD"
    };

    /// <summary>The trimmed value, or null when it is blank or only a placeholder ("NOT_REGISTERED", "not registered", "N/A", "n.a." all collapse to the same key).</summary>
    public static string? Clean(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var trimmed = value.Trim();
        var key = new string(trimmed.Where(char.IsLetterOrDigit).ToArray());
        return key.Length == 0 || Placeholders.Contains(key) ? null : trimmed;
    }
}
