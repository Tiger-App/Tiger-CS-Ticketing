namespace TigerCS.Domain.Modules.Ticketing;

/// <summary>
/// The one canonical form of a customer phone number, and the only place
/// TigerCS is allowed to decide that two differently written numbers belong
/// to the same person.
///
/// <para>
/// A number is captured exactly as the agent typed it — "+971501234567",
/// "971501234567", "+971 50 123 4567", "+971-50-123-4567" are all the same
/// customer, and every one of them is persisted verbatim
/// (<c>IntakeRecord.PhoneNumber</c> is never rewritten; see
/// <c>IIntakeRecordRepository</c>). Identity, grouping and matching
/// therefore cannot compare the stored strings: they compare
/// <see cref="Normalize"/>'s output.
/// </para>
///
/// <para>
/// This is a read-side rule only. Nothing here reformats what is stored or
/// what an integration is sent — <see cref="WithoutPlus"/> is the separate,
/// narrower rule the PACT request path has always applied to its own URLs.
/// </para>
/// </summary>
public static class CustomerPhoneNumber
{
    /// <summary>
    /// The separator characters a phone number is written with, which
    /// <see cref="Normalize"/> drops: '+', space, hyphen, parentheses, dot
    /// and slash.
    ///
    /// <para>
    /// A database query cannot run <see cref="Normalize"/>, so the Customers
    /// directory and the ticket queue drop exactly these in SQL with chained
    /// <c>REPLACE()</c>. For a value made of digits and separators — which is
    /// what a phone number is — the two agree exactly, and
    /// <see cref="Normalize"/> stays the authority: it is applied again, in
    /// memory, to every directory key. Keep this set and those SQL mirrors in
    /// step; a test asserts they match character for character.
    /// </para>
    ///
    /// <para>
    /// <b>Known limit.</b> <see cref="Normalize"/> drops every non-digit,
    /// while the SQL mirror drops only these. A stored number carrying
    /// something else entirely — a letter, as in a free-typed
    /// "0501234567 (mobile)" — therefore canonicalizes differently on the two
    /// sides, and the Customers directory can group it as its own customer
    /// while emitting the same phone key as the clean one. Widening this set
    /// shrinks that window to values a phone number would not realistically
    /// contain; closing it entirely needs the grouping itself to be
    /// digits-only, which SQL cannot express without a computed column.
    /// </para>
    /// </summary>
    public const string SeparatorCharacters = "+ -().\u002F";

    /// <summary>
    /// The canonical form: the number's digits, in order, and nothing else.
    /// Surrounding whitespace, '+', spaces, hyphens and parentheses all
    /// disappear, so "+971 50 123 4567", "971-50-123-4567" and
    /// "+971501234567" all normalize to "971501234567". A number with no
    /// digits at all (null, blank, "+") normalizes to the empty string,
    /// which is not an identity.
    /// </summary>
    public static string Normalize(string? phoneNumber)
    {
        if (string.IsNullOrWhiteSpace(phoneNumber))
        {
            return string.Empty;
        }

        var digits = new char[phoneNumber.Length];
        var length = 0;
        foreach (var character in phoneNumber)
        {
            if (char.IsAsciiDigit(character))
            {
                digits[length++] = character;
            }
        }

        return length == 0 ? string.Empty : new string(digits, 0, length);
    }

    /// <summary>
    /// True when a search term is shaped like a phone number — it has at
    /// least one digit and contains nothing but digits and
    /// <see cref="SeparatorCharacters"/>.
    ///
    /// <para>
    /// This is the gate for matching a free-text search term canonically.
    /// <see cref="Normalize"/> alone is not that test: it strips every
    /// non-digit, so it happily reduces the unit code "M-401" to "401" and
    /// would then match it against any caller whose number merely contains
    /// those digits. A term is only matched as a phone number when it
    /// actually looks like one; everything else is matched as the text it is.
    /// </para>
    /// </summary>
    public static bool LooksLikeNumber(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var hasDigit = false;
        foreach (var character in value.Trim())
        {
            if (char.IsAsciiDigit(character))
            {
                hasDigit = true;
            }
            else if (!SeparatorCharacters.Contains(character))
            {
                return false;
            }
        }

        return hasDigit;
    }

    /// <summary>
    /// The UAE-aware matching form: <see cref="Normalize"/>'s digits, except that a UAE mobile or landline written locally
    /// (<c>0501234567</c>, <c>041234567</c>), internationally (<c>+97141234567</c>) or with the 00 prefix (<c>0097141234567</c>) always
    /// becomes <c>971</c> + its national number. So all three spellings of one UAE number match.
    ///
    /// <para>
    /// This is a matching rule, not a storage rule: stored keys (<c>CustomerKey</c>, <c>CustomerContactDetails.PhoneKey</c>) keep
    /// <see cref="Normalize"/>'s form so no existing row changes meaning. A lookup by a stored key therefore tries both
    /// (<see cref="LookupKeys"/>). Only a number that is unmistakably UAE is rewritten: a foreign number, a malformed one or a bare
    /// "501234567" (country not stated) is returned unchanged, so two different customers can never be merged by a guess.
    /// </para>
    /// </summary>
    public static string MatchKey(string? phoneNumber)
    {
        var digits = Normalize(phoneNumber);
        return UaePhoneNumbers.ToInternationalDigits(digits) ?? digits;
    }

    /// <summary>
    /// Every stored-key spelling a lookup of <paramref name="phoneNumber"/> must try: the stored form (<see cref="Normalize"/>) and the
    /// UAE-aware form (<see cref="MatchKey"/>), de-duplicated. Empty when the number has no digits.
    /// </summary>
    public static IReadOnlyList<string> LookupKeys(string? phoneNumber)
    {
        var stored = Normalize(phoneNumber);
        if (stored.Length == 0)
        {
            return [];
        }

        var match = MatchKey(phoneNumber);
        return match == stored ? [stored] : [stored, match];
    }

    /// <summary>True when both numbers name the same number once written the same way (UAE spellings included); false when either has no digits.</summary>
    public static bool AreSameNumber(string? left, string? right)
    {
        var canonicalLeft = MatchKey(left);
        return canonicalLeft.Length > 0 && string.Equals(canonicalLeft, MatchKey(right), StringComparison.Ordinal);
    }

    /// <summary>
    /// A telephony address — what a contact-center platform reports as the
    /// caller's number (Genesys' <c>Call.Ani</c>) — turned into the form an
    /// agent would have typed: "+" followed by the number's digits.
    ///
    /// <para>
    /// Telephony platforms do not report a number the way a person writes
    /// it: "tel:+971501234567", "sip:+971501234567@host;user=phone",
    /// "+971501234567" and "971501234567" are the same caller. Passing the
    /// raw value on would break every source behind the lookup — CRM
    /// receives the number verbatim, and <see cref="WithoutPlus"/> would
    /// turn "tel:+971…" into "tel:971…" for PACT — so the address is reduced
    /// to its digits with <see cref="Normalize"/> (after dropping any
    /// "@host" and ";parameters" part, whose digits are not the number's)
    /// and written back in the international "+" form the rest of TigerCS
    /// already uses. A "00" international prefix becomes "+". A number in
    /// national form ("0501234567") is returned as its digits unchanged:
    /// its country is not stated, and none is guessed.
    /// </para>
    ///
    /// <para>
    /// Returns null when the address carries no number at all — a withheld
    /// caller id ("tel:anonymous", "sip:anonymous@…") is a missing number,
    /// never an identity.
    /// </para>
    /// </summary>
    public static string? FromTelephonyAddress(string? address)
    {
        if (string.IsNullOrWhiteSpace(address))
        {
            return null;
        }

        var value = address.Trim();

        // "tel:+971…;phone-context=…" / "sip:+971…@host;user=phone": only the
        // user part before '@' and ';' is the number.
        var cut = value.IndexOfAny(['@', ';']);
        if (cut >= 0)
        {
            value = value[..cut];
        }

        var digits = Normalize(value);
        if (digits.Length == 0)
        {
            return null;
        }

        if (digits.StartsWith("00", StringComparison.Ordinal))
        {
            return digits.Length > 2 ? "+" + digits[2..] : null;
        }

        // A UAE mobile or landline in national form (0501234567, 041234567) is unmistakably UAE - the trunk 0 plus a 9-digit 5xx or an
        // 8-digit area-coded number exists nowhere else with that shape - so it is written internationally. Any other national number
        // (a foreign or malformed one) still states no country and none is guessed.
        if (UaePhoneNumbers.NationalSignificantFromLocal(digits) is { } uaeNational)
        {
            return "+" + UaePhoneNumbers.CountryCode + uaeNational;
        }

        return digits.StartsWith('0') ? digits : "+" + digits;
    }

    /// <summary>
    /// PACT's request form — trimmed, with every '+' removed, and otherwise
    /// untouched. Deliberately <b>not</b> <see cref="Normalize"/>: PACT
    /// matches on the number as it stores it (without the '+' prefix), and
    /// the rest of the string is passed through as typed. Applied to the
    /// PACT request path only; CRM and persistence keep the number exactly
    /// as entered.
    /// </summary>
    public static string WithoutPlus(string? phoneNumber) =>
        string.IsNullOrWhiteSpace(phoneNumber) ? string.Empty : phoneNumber.Trim().Replace("+", string.Empty);
}
