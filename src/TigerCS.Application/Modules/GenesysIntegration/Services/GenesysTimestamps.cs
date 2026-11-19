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

    // ---- Without a clock: zone normalization only (kept for callers that have no receipt time). ----

    public static GenesysInquiryDto Normalize(GenesysInquiryDto inquiry) => Normalize(inquiry, null);
    public static GenesysConversationEndDto Normalize(GenesysConversationEndDto end) => Normalize(end, null);
    public static GenesysHandoffRequestDto Normalize(GenesysHandoffRequestDto request) => Normalize(request, null);
    public static GenesysHandoffAssignmentDto Normalize(GenesysHandoffAssignmentDto assignment) => Normalize(assignment, null);
    public static GenesysTicketUpdateDto Normalize(GenesysTicketUpdateDto request) => Normalize(request, null);

    // ---- With the receipt clock: zone normalization, then the "an event cannot be in the future" guard. ----

    public static GenesysInquiryDto Normalize(GenesysInquiryDto inquiry, GenesysTimestampContext? context) =>
        inquiry with { StartedAtUtc = Resolve(context, "inquiry.startedAtUtc", inquiry.StartedAtUtc, FuturePolicy.Reject) };

    public static GenesysConversationEndDto Normalize(GenesysConversationEndDto end, GenesysTimestampContext? context) =>
        end with
        {
            EndedAtUtc = Resolve(context, "ended.endedAtUtc", end.EndedAtUtc, FuturePolicy.ClampToReceipt),
            Transcript = Normalize(end.Transcript, context, "ended.transcript")
        };

    public static GenesysHandoffRequestDto Normalize(GenesysHandoffRequestDto request, GenesysTimestampContext? context) =>
        request with { RequestedAtUtc = Resolve(context, "handoff.requestedAtUtc", request.RequestedAtUtc, FuturePolicy.ClampToReceipt) };

    public static GenesysHandoffAssignmentDto Normalize(GenesysHandoffAssignmentDto assignment, GenesysTimestampContext? context) =>
        assignment with { AssignedAtUtc = Resolve(context, "handoff.assignedAtUtc", assignment.AssignedAtUtc, FuturePolicy.ClampToReceipt) };

    /// <summary>Every instant of a ticket update, as UTC. Returns the same shape; nothing else is touched.</summary>
    public static GenesysTicketUpdateDto Normalize(GenesysTicketUpdateDto request, GenesysTimestampContext? context) => request with
    {
        StartedAtUtc = Resolve(context, "update.startedAtUtc", request.StartedAtUtc, FuturePolicy.Reject),
        Routing = request.Routing is null
            ? null
            : request.Routing with { ConnectedAtUtc = Resolve(context, "routing.connectedAtUtc", request.Routing.ConnectedAtUtc, FuturePolicy.ClampToReceipt) },
        // Compared by value on a resend (a redelivery must find its own event), so it is zone-normalized but never moved.
        CustomerConfirmation = request.CustomerConfirmation is null
            ? null
            : request.CustomerConfirmation with { ConfirmedAtUtc = Resolve(context, "customerConfirmation.confirmedAtUtc", request.CustomerConfirmation.ConfirmedAtUtc, FuturePolicy.ObserveOnly) },
        Ended = request.Ended is null
            ? null
            : request.Ended with
            {
                EndedAtUtc = Resolve(context, "ended.endedAtUtc", request.Ended.EndedAtUtc, FuturePolicy.ClampToReceipt),
                Transcript = Normalize(request.Ended.Transcript, context, "ended.transcript")
            },
        Transcript = Normalize(request.Transcript, context, "transcript")
    };

    private static IReadOnlyList<GenesysTranscriptMessageDto>? Normalize(
        IReadOnlyList<GenesysTranscriptMessageDto>? transcript, GenesysTimestampContext? context, string field) =>
        transcript?.Select(m => m with { SentAtUtc = Resolve(context, field + ".sentAtUtc", m.SentAtUtc, FuturePolicy.ObserveOnly)!.Value }).ToList();

    private static DateTime? Resolve(GenesysTimestampContext? context, string field, DateTime? raw, FuturePolicy policy) =>
        context is null ? ToUtc(raw) : context.Resolve(field, raw, policy);
}

/// <summary>What to do with an instant that lies in the future of the moment TigerCS received it.</summary>
public enum FuturePolicy
{
    /// <summary>Keep it (it is a value other code compares by identity - a transcript line, a customer confirmation); the anomaly is only reported.</summary>
    ObserveOnly = 0,

    /// <summary>It cannot be trusted and there is nothing better to substitute: it is not stored at all (TigerCS falls back to its own recorded time).</summary>
    Reject = 1,

    /// <summary>The event happened no later than it was received: the receipt time is the latest it can be.</summary>
    ClampToReceipt = 2
}

/// <summary>One instant that was not taken as sent: what arrived (and in which wire form), and what TigerCS did with it.</summary>
/// <param name="Field">The contract field.</param>
/// <param name="RawValue">The value exactly as the JSON reader produced it, before any conversion.</param>
/// <param name="WireForm">How the sender wrote the zone: "Utc" (<c>…Z</c>), "Offset" (<c>…+04:00</c>) or "Zoneless" (nothing - which TigerCS reads as UTC, per the contract).</param>
/// <param name="ReceivedAtUtc">The server clock when the request arrived.</param>
/// <param name="Action">"RejectedFuture", "ClampedToReceipt" or "ObservedFuture".</param>
/// <param name="AppliedUtc">What was stored instead (null for a rejection / an observation).</param>
public sealed record GenesysTimestampFinding(string Field, DateTime RawValue, string WireForm, DateTime ReceivedAtUtc, string Action, DateTime? AppliedUtc)
{
    public override string ToString() =>
        $"{Field}: sent {RawValue:O} ({WireForm}), received {ReceivedAtUtc:O}, {Action}" + (AppliedUtc is { } a ? $" -> {a:O}" : string.Empty);
}

/// <summary>
/// <b>One request's view of "what time is it, and which instants can be believed".</b>
///
/// <para>
/// Zone normalization (<see cref="GenesysTimestamps.ToUtc(DateTime)"/>) can only be right when the sender's text says what zone it is in. A
/// zoneless string is read as UTC because the contract says so, but a sender that writes its LOCAL wall-clock without an offset (or labels
/// local time with <c>Z</c>) is indistinguishable from a correct one by the text alone - it produced a conversation "started" four hours in the
/// future (ticket TG-REG-20261119-0027: created 13:51, conversation start 17:50). What <i>is</i> knowable: an event cannot be later than the
/// moment TigerCS received it. So the guard compares every instant with the server's own clock - no zone is assumed and no offset is
/// subtracted - and an impossible one is rejected (a start time), clamped to the receipt time (an acceptance / end time) or reported (a value
/// that other code compares by identity), always with the raw value and its wire form recorded for the audit trail.
/// </para>
/// </summary>
public sealed class GenesysTimestampContext(DateTime receivedAtUtc, TimeSpan futureTolerance)
{
    private readonly List<GenesysTimestampFinding> _findings = [];

    public DateTime ReceivedAtUtc { get; } = GenesysTimestamps.ToUtc(receivedAtUtc);

    public IReadOnlyList<GenesysTimestampFinding> Findings => _findings;

    public bool HasFindings => _findings.Count > 0;

    public static string WireFormOf(DateTime raw) => raw.Kind switch
    {
        DateTimeKind.Utc => "Utc",
        DateTimeKind.Local => "Offset",
        _ => "Zoneless"
    };

    public DateTime? Resolve(string field, DateTime? raw, FuturePolicy policy)
    {
        if (raw is not { } value)
        {
            return null;
        }

        var utc = GenesysTimestamps.ToUtc(value);
        if (utc <= ReceivedAtUtc + futureTolerance)
        {
            return utc;
        }

        switch (policy)
        {
            case FuturePolicy.Reject:
                _findings.Add(new GenesysTimestampFinding(field, value, WireFormOf(value), ReceivedAtUtc, "RejectedFuture", null));
                return null;
            case FuturePolicy.ClampToReceipt:
                _findings.Add(new GenesysTimestampFinding(field, value, WireFormOf(value), ReceivedAtUtc, "ClampedToReceipt", ReceivedAtUtc));
                return ReceivedAtUtc;
            default:
                // One finding per field is enough to expose the defect; a 200-line transcript must not write 200 of them.
                if (!_findings.Any(f => f.Field == field))
                {
                    _findings.Add(new GenesysTimestampFinding(field, value, WireFormOf(value), ReceivedAtUtc, "ObservedFuture", null));
                }

                return utc;
        }
    }

    /// <summary>The audit text for every finding, or null when everything was taken as sent.</summary>
    public string? Describe() => _findings.Count == 0 ? null : string.Join(" | ", _findings);
}
