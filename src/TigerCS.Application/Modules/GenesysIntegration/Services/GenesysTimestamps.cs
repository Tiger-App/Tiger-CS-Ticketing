using TigerCS.Application.Modules.GenesysIntegration.Dto;

namespace TigerCS.Application.Modules.GenesysIntegration.Services;

/// <summary>
/// <b>One rule for every instant Genesys sends: it is stored as UTC, once.</b>
///
/// <para>
/// Every Genesys contract field is documented as UTC (<c>startedAtUtc</c>, <c>sentAtUtc</c>, <c>connectedAtUtc</c>, <c>endedAtUtc</c> …), but System.Text.Json
/// hands the application a <see cref="DateTime"/> whose <see cref="DateTime.Kind"/> depends on the text that arrived: <c>…Z</c> is <c>Utc</c>,
/// <c>…+04:00</c> is converted to the <i>server's local zone</i> and arrives as <c>Local</c> (UTC+4 on the UAE application server), and a string
/// with no zone is <c>Unspecified</c>. EF Core writes the raw wall-clock ticks of whichever it got, so a value that carried an offset was
/// persisted as UAE wall-clock time while every other timestamp was UTC — and the UI, which converts stored UTC to UAE exactly once, then added
/// four hours <i>again</i> to that one value (AI conversation "started" 19:18 although the first human reply was at 15:21).
/// </para>
///
/// <para>
/// Normalizing here — before anything is compared, ordered, persisted or used for an SLA — removes the whole class: <c>Utc</c> is kept,
/// <c>Local</c> is converted to its real instant, and <c>Unspecified</c> is read as UTC (the contract's stated zone; a zoneless string is never
/// guessed to be local). The conversion to UAE time happens in exactly one place, the UI (<c>TicketDisplay</c>).
/// </para>
/// </summary>
public static class GenesysTimestamps
{
    /// <summary>The instant <paramref name="value"/> denotes, as a <see cref="DateTimeKind.Utc"/> value.</summary>
    public static DateTime ToUtc(DateTime value) => value.Kind switch
    {
        DateTimeKind.Utc => value,
        DateTimeKind.Local => value.ToUniversalTime(),
        _ => DateTime.SpecifyKind(value, DateTimeKind.Utc)
    };

    public static DateTime? ToUtc(DateTime? value) => value is { } v ? ToUtc(v) : null;

    public static GenesysTranscriptMessageDto Normalize(GenesysTranscriptMessageDto message) =>
        message with { SentAtUtc = ToUtc(message.SentAtUtc) };

    public static IReadOnlyList<GenesysTranscriptMessageDto>? Normalize(IReadOnlyList<GenesysTranscriptMessageDto>? transcript) =>
        transcript?.Select(Normalize).ToList();

    public static GenesysInquiryDto Normalize(GenesysInquiryDto inquiry) =>
        inquiry with { StartedAtUtc = ToUtc(inquiry.StartedAtUtc) };

    public static GenesysConversationEndDto Normalize(GenesysConversationEndDto end) =>
        end with { EndedAtUtc = ToUtc(end.EndedAtUtc), Transcript = Normalize(end.Transcript) };

    public static GenesysHandoffRequestDto Normalize(GenesysHandoffRequestDto request) =>
        request with { RequestedAtUtc = ToUtc(request.RequestedAtUtc) };

    public static GenesysHandoffAssignmentDto Normalize(GenesysHandoffAssignmentDto assignment) =>
        assignment with { AssignedAtUtc = ToUtc(assignment.AssignedAtUtc) };

    /// <summary>Every instant of a ticket update, as UTC. Returns the same shape; nothing else is touched.</summary>
    public static GenesysTicketUpdateDto Normalize(GenesysTicketUpdateDto request) => request with
    {
        StartedAtUtc = ToUtc(request.StartedAtUtc),
        Routing = request.Routing is null ? null : request.Routing with { ConnectedAtUtc = ToUtc(request.Routing.ConnectedAtUtc) },
        CustomerConfirmation = request.CustomerConfirmation is null
            ? null
            : request.CustomerConfirmation with { ConfirmedAtUtc = ToUtc(request.CustomerConfirmation.ConfirmedAtUtc) },
        Ended = request.Ended is null
            ? null
            : request.Ended with { EndedAtUtc = ToUtc(request.Ended.EndedAtUtc), Transcript = Normalize(request.Ended.Transcript) },
        Transcript = Normalize(request.Transcript)
    };
}
