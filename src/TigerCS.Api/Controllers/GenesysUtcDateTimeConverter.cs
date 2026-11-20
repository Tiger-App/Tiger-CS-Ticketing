using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using TigerCS.Application.Modules.GenesysIntegration.Services;

namespace TigerCS.Api.Controllers;

/// <summary>
/// <b>Reads a Genesys instant straight to UTC at the JSON boundary - the host's time zone is never involved.</b>
///
/// <para>
/// Every Genesys timestamp is UTC by contract. <c>System.Text.Json</c>'s own <see cref="DateTime"/> reader is not zone-independent: a value that carries an
/// explicit offset - <c>2026-11-19T09:50:26+00:00</c>, which is how some Genesys tooling writes UTC - is converted to the <i>server's local zone</i>
/// (<see cref="DateTimeKind.Local"/>), so on a UTC+4 host it arrives as wall-clock 13:50:26. EF Core persists a <c>datetime2</c> by its raw ticks, so that 13:50:26 was
/// stored in a column named <c>…Utc</c>, and the UI - which correctly converts stored UTC to Asia/Dubai exactly once - then showed 17:50:26 (ticket 1878, whose first
/// human reply-to-start order was inverted by exactly four hours). <c>…Z</c> and a zoneless string were never affected, which is why it only hit some conversations.
/// </para>
///
/// <para>
/// This converter parses the text itself with <see cref="DateTimeOffset"/>: an offset (<c>Z</c>, <c>+00:00</c>, <c>+04:00</c>, <c>-05:00</c>) names the real instant; text with
/// no offset is UTC, as the contract says. The result is always <see cref="DateTimeKind.Utc"/>, whatever zone the process runs in. It writes ISO-8601 UTC
/// with <c>Z</c>. Anything that is not an ISO-8601 instant is a 400, never a guess.
/// </para>
/// </summary>
public sealed class GenesysUtcDateTimeConverter : JsonConverter<DateTime>
{
    /// <summary>Parses <paramref name="text"/> as a Genesys instant; false when it is not one.</summary>
    public static bool TryParse(string? text, out DateTime utc)
    {
        utc = default;
        if (string.IsNullOrWhiteSpace(text)
            || !DateTimeOffset.TryParse(
                text.Trim(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AllowWhiteSpaces, out var instant))
        {
            return false;
        }

        utc = instant.UtcDateTime;   // DateTimeKind.Utc, independent of TimeZoneInfo.Local
        return true;
    }

    public override DateTime Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.String)
        {
            throw new JsonException("A Genesys timestamp must be an ISO-8601 string such as \"2026-11-19T09:50:26Z\".");
        }

        return TryParse(reader.GetString(), out var utc)
            ? utc
            : throw new JsonException($"'{reader.GetString()}' is not an ISO-8601 instant. Send UTC, e.g. \"2026-11-19T09:50:26Z\".");
    }

    public override void Write(Utf8JsonWriter writer, DateTime value, JsonSerializerOptions options) =>
        writer.WriteStringValue(GenesysTimestamps.ToUtc(value).ToString("yyyy-MM-ddTHH:mm:ss.FFFFFFFK", CultureInfo.InvariantCulture));
}
