using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using TigerCS.Application.Authorization;
using TigerCS.Application.Modules.Collections.Dto;
using TigerCS.Application.Modules.IdentityAndAccess.Abstractions;
using TigerCS.Domain.Modules.Collections;

namespace TigerCS.Application.Modules.Collections.Services;

/// <summary>Who is calling: the employee, their roles and department memberships, read from the token by the API edge.</summary>
public sealed record CollectionsCaller(Guid EmployeeId, IReadOnlyCollection<string> Roles, IReadOnlyCollection<int> DepartmentIds);

/// <summary>Outcomes, named after the specification's error codes (§11) where one applies.</summary>
public enum CollectionsOutcome
{
    Success,

    /// <summary>Queued, or recorded with a ticket still pending — 202.</summary>
    Accepted,

    /// <summary>A replay of an earlier request with the same Idempotency-Key and body — the original answer, 200.</summary>
    Replayed,

    Disabled,
    Forbidden,
    InvalidRequest,
    AccountNotFound,
    ReminderNotFound,
    FinanceUnavailable,
    CandidateChanged,
    IdempotencyConflict,
    NoEligibleContact,
    ChannelNotEnabled,
    ReminderSuppressed,

    /// <summary>The customer has no verified PACT company/tenant mapping, so no EDSM figure can be returned.</summary>
    NotMapped,

    /// <summary>The approved list no longer matches the stored review data or a dispatch changed it: review again (409).</summary>
    ReviewRequired,

    /// <summary>A record is already part of a pending or sent dispatch, or the same key was used for a different list (409).</summary>
    DuplicateDispatch,

    NotFound,

    /// <summary>The request conflicts with the current state (for example a reconciliation is already running) - 409.</summary>
    Conflict
}

public sealed record CollectionsResult<T>(
    CollectionsOutcome Outcome,
    T? Value = default,
    string? Detail = null,
    CollectionsReminderCandidateDto? Replacement = null)
{
    public bool IsSuccess => Outcome is CollectionsOutcome.Success or CollectionsOutcome.Accepted or CollectionsOutcome.Replayed;

    public static CollectionsResult<T> Ok(T value, CollectionsOutcome outcome = CollectionsOutcome.Success) => new(outcome, value);

    public static CollectionsResult<T> Fail(CollectionsOutcome outcome, string? detail = null, CollectionsReminderCandidateDto? replacement = null) =>
        new(outcome, default, detail, replacement);
}

public sealed record CollectionsPermissions(bool CanReadFinancials, bool CanSendReminders, bool CanReportOutcomes, bool IsIntegration);

/// <summary>
/// Explicit financial authorization, separate from ticket permissions (which
/// this module does not touch): viewing payments, queueing reminders and
/// reporting delivery are three grants (<see cref="CollectionsAuthorizationOptions"/>).
/// The System Administrator override (ADR-0024) applies through
/// <see cref="AuthorizationGate"/>, as everywhere else.
/// </summary>
public sealed class CollectionsAuthorizationService(CollectionsOptions options, IDepartmentRepository departmentRepository)
{
    public async Task<CollectionsPermissions> ResolveAsync(CollectionsCaller caller, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(caller);
        var auth = options.Authorization;

        var isIntegration = auth.IntegrationEmployeeIds.Contains(caller.EmployeeId);
        bool HoldsAny(IEnumerable<string> roles) => caller.Roles.Any(r => roles.Contains(r, StringComparer.Ordinal));

        var isCollectionsMember = false;
        if (HoldsAny(auth.CollectionsDepartmentRoles) && caller.DepartmentIds.Count > 0)
        {
            var departments = await departmentRepository.ListAsync(activeOnly: true, cancellationToken);
            var collections = departments.FirstOrDefault(d =>
                string.Equals(d.Code, options.CollectionsDepartmentCode, StringComparison.OrdinalIgnoreCase));
            isCollectionsMember = collections is not null && caller.DepartmentIds.Contains(collections.DepartmentId);
        }

        var canSend = AuthorizationGate.Evaluate(caller.Roles, () => isIntegration || isCollectionsMember || HoldsAny(auth.ReminderSendRoles));
        var canRead = AuthorizationGate.Evaluate(caller.Roles, () => canSend || HoldsAny(auth.FinancialReadRoles));
        var canReport = AuthorizationGate.Evaluate(caller.Roles, () => isIntegration);

        return new CollectionsPermissions(canRead, canSend, canReport, isIntegration);
    }
}

/// <summary>The Dubai business date, freshness and rule settings every Collections decision shares.</summary>
public sealed class CollectionsClock(CollectionsOptions options, TimeProvider timeProvider)
{
    private readonly Lazy<TimeZoneInfo> _zone = new(() => TimeZoneInfo.FindSystemTimeZoneById(options.TimeZoneId));

    public DateTime UtcNow => timeProvider.GetUtcNow().UtcDateTime;

    public DateOnly BusinessDate => DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(UtcNow, _zone.Value));

    public string TimeZoneId => options.TimeZoneId;

    /// <summary>The start of today's Dubai business day, as a UTC instant (the beginning of the contact-frequency day).</summary>
    public DateTime BusinessDayStartUtc =>
        TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(BusinessDate.ToDateTime(TimeOnly.MinValue), DateTimeKind.Unspecified), _zone.Value);

    /// <summary>The Dubai calendar date of a UTC instant.</summary>
    public DateOnly ToBusinessDate(DateTime utc) =>
        DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), _zone.Value));

    public bool IsStale(DateTime asOfUtc) => UtcNow - asOfUtc > TimeSpan.FromMinutes(Math.Max(1, options.StaleAfterMinutes));

    /// <summary>
    /// Staleness of a figure that was read from the LOCAL receivables snapshot: never stricter than that snapshot's own freshness limit. The snapshot refreshes every two hours,
    /// so judging it by <see cref="CollectionsOptions.StaleAfterMinutes"/> (live reads, 60 minutes) would call it stale for half of every cycle.
    /// </summary>
    public bool IsStale(DateTime asOfUtc, int snapshotMaxAgeMinutes) =>
        UtcNow - asOfUtc > TimeSpan.FromMinutes(Math.Max(1, Math.Max(options.StaleAfterMinutes, snapshotMaxAgeMinutes)));

    public ReminderRuleSettings Rules => options.Rules.ToSettings();
}

public static class CollectionsEnums
{
    /// <summary>Strict, name-only parsing: a number or an unknown name is refused rather than coerced.</summary>
    public static bool TryParse<TEnum>(string? value, out TEnum result) where TEnum : struct, Enum
    {
        result = default;
        return !string.IsNullOrWhiteSpace(value)
            && !char.IsDigit(value.Trim()[0])
            && !value.Trim().StartsWith('-')
            && Enum.TryParse(value.Trim(), ignoreCase: true, out result)
            && Enum.IsDefined(result);
    }

    public static string Names<TEnum>() where TEnum : struct, Enum => string.Join(", ", Enum.GetNames<TEnum>());
}

/// <summary>Opaque list cursors (the offset of the next page). A malformed cursor is a 400, never a silent restart.</summary>
public static class CollectionsCursor
{
    public const int DefaultPageSize = 50;
    public const int MaxPageSize = 100;

    public static string? Next(int offset, int pageSize, bool hasMore) =>
        hasMore ? Base64Url(Encoding.UTF8.GetBytes($"v1:{(offset + pageSize).ToString(CultureInfo.InvariantCulture)}")) : null;

    public static bool TryRead(string? cursor, int? pageSize, out int offset, out int size, out string? error)
    {
        offset = 0;
        size = pageSize ?? DefaultPageSize;
        error = null;

        if (size is < 1 or > MaxPageSize)
        {
            error = $"pageSize must be between 1 and {MaxPageSize}.";
            return false;
        }

        if (string.IsNullOrWhiteSpace(cursor))
        {
            return true;
        }

        try
        {
            var text = Encoding.UTF8.GetString(FromBase64Url(cursor.Trim()));
            if (text.StartsWith("v1:", StringComparison.Ordinal)
                && int.TryParse(text[3..], NumberStyles.None, CultureInfo.InvariantCulture, out offset))
            {
                return true;
            }
        }
        catch (FormatException)
        {
        }

        error = "cursor is not valid.";
        return false;
    }

    internal static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    internal static byte[] FromBase64Url(string text)
    {
        var s = text.Replace('-', '+').Replace('_', '/');
        return Convert.FromBase64String(s.PadRight(s.Length + ((4 - (s.Length % 4)) % 4), '='));
    }
}

/// <summary>
/// The opaque <c>candidateId</c>: what was offered (account, type, cycle,
/// amount, currency, qualifying instalments) and until when. It is not a
/// credential and grants nothing — queueing re-reads the account and
/// accepts the candidate only if a fresh evaluation reproduces it exactly,
/// so an altered or stale candidate can only ever be refused
/// (409 CandidateChanged).
/// </summary>
public sealed record CollectionsCandidate(
    long CrmCustomerId,
    string AccountId,
    ReminderType Type,
    string CycleKey,
    decimal Amount,
    string Currency,
    IReadOnlyList<string> InstalmentIds,
    DateTime ExpiresAtUtc)
{
    private const string Prefix = "CAND-";

    public string Encode() =>
        Prefix + CollectionsCursor.Base64Url(JsonSerializer.SerializeToUtf8Bytes(this));

    public static bool TryDecode(string? candidateId, out CollectionsCandidate? candidate)
    {
        candidate = null;
        if (string.IsNullOrWhiteSpace(candidateId) || !candidateId.Trim().StartsWith(Prefix, StringComparison.Ordinal))
        {
            return false;
        }

        try
        {
            candidate = JsonSerializer.Deserialize<CollectionsCandidate>(CollectionsCursor.FromBase64Url(candidateId.Trim()[Prefix.Length..]));
            return candidate is { CrmCustomerId: > 0, AccountId.Length: > 0, CycleKey.Length: > 0, InstalmentIds: not null }
                && Enum.IsDefined(candidate.Type);
        }
        catch (Exception ex) when (ex is FormatException or JsonException)
        {
            return false;
        }
    }

    /// <summary>Whether a fresh evaluation still offers exactly this.</summary>
    public bool Matches(CollectionsCandidate fresh) =>
        CrmCustomerId == fresh.CrmCustomerId && AccountId == fresh.AccountId && Type == fresh.Type && CycleKey == fresh.CycleKey
        && Amount == fresh.Amount && Currency == fresh.Currency && InstalmentIds.SequenceEqual(fresh.InstalmentIds);
}

internal static class CollectionsHashing
{
    private static readonly JsonSerializerOptions Canonical = new(JsonSerializerDefaults.Web);

    /// <summary>SHA-256 of a request's canonical JSON — tells a replay from a conflicting reuse of an idempotency key.</summary>
    public static string Hash<T>(T request) =>
        Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(request, Canonical)));

    /// <summary>
    /// The audit entity id for a reminder channel: its de-duplication key,
    /// readable before the row has an id. AuditEntries.EntityId holds 100
    /// characters; an over-long key keeps its readable prefix plus a hash.
    /// </summary>
    public static string AuditEntityId(string key)
    {
        const int max = 100;
        if (key.Length <= max)
        {
            return key;
        }

        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)))[..16];
        return $"{key[..(max - 17)]}~{hash}";
    }
}

/// <summary>
/// Monetary values leave the API with two decimal places (44000 → 44000.00).
/// A source value with more precision is passed through unchanged — never
/// silently rounded.
/// </summary>
public static class CollectionsMoney
{
    public static decimal Two(decimal value) =>
        value == decimal.Round(value, 2)
            ? decimal.Parse(value.ToString("F2", CultureInfo.InvariantCulture), CultureInfo.InvariantCulture)
            : value;

    public static decimal? Two(decimal? value) => value is { } v ? Two(v) : null;
}

internal static class CollectionsMapper
{
    public static string JobStatus(CollectionsReminder r) =>
        r.Channels.All(c => c.Status == ChannelStatus.Suppressed) ? "Suppressed"
        : r.Channels.Any(c => c.Status == ChannelStatus.Queued) ? "Queued"
        : "Processed";

    public static CollectionsChannelStatusDto ToDto(CollectionsReminderChannel c) =>
        new(c.Channel.ToString(), c.Status.ToString(), c.LastEventAtUtc, c.Attempts, c.StatusReason);

    public static CollectionsReminderJobDto ToJobDto(CollectionsReminder r) => new(
        r.PublicId, r.CrmCustomerId, r.AccountId, r.Type.ToString(), r.CycleKey, r.Currency, CollectionsMoney.Two(r.Amount), r.AmountBasis,
        SplitIds(r.InstalmentIds), r.QueuedAtUtc, JobStatus(r),
        r.Channels.OrderBy(c => c.Channel).Select(ToDto).ToList());

    public static CollectionsReminderHistoryItemDto ToHistoryDto(CollectionsReminder r)
    {
        var responses = r.Events.Where(e => e.CustomerResponded).OrderBy(e => e.OccurredAtUtc).ThenBy(e => e.CollectionsReminderEventId).ToList();
        var latest = responses.LastOrDefault();
        var latestTicket = responses.LastOrDefault(e => e.TicketId is not null);
        return new CollectionsReminderHistoryItemDto(
            r.PublicId, r.AccountId, r.Type.ToString(), r.CycleKey, r.Currency, CollectionsMoney.Two(r.Amount), r.AmountBasis, r.QueuedAtUtc, r.Trigger.ToString(),
            r.Channels.OrderBy(c => c.Channel).Select(ToDto).ToList(),
            latest?.CustomerIntent?.ToString(),
            latestTicket?.TicketId,
            latestTicket?.TicketNumber,
            responses.Select(e => new CollectionsReminderResponseDto(
                e.ExternalEventId, e.Channel?.ToString() ?? "", e.CustomerIntent!.Value.ToString(), e.OccurredAtUtc,
                e.FollowUpRequired, e.VerificationFollowUpRequired, e.TicketResult.ToString(), e.TicketId, e.TicketNumber)).ToList());
    }

    public static IReadOnlyList<string> SplitIds(string ids) =>
        string.IsNullOrEmpty(ids) ? [] : ids.Split(',', StringSplitOptions.RemoveEmptyEntries);
}
