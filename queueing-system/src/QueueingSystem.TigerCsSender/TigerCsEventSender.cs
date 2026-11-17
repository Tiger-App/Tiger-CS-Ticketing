using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

namespace QueueingSystem.TigerCsSender;

/// <summary>What the caller must do with an event after <see cref="TigerCsEventSender.SendAsync"/>.</summary>
public enum SendDisposition
{
    /// <summary>TigerCS has it (HTTP 200 / 201: applied, a duplicate, superseded by a newer event, or rejected as not applicable). Mark it sent. Never resend.</summary>
    Delivered,

    /// <summary>TigerCS stored it (HTTP 202) but cannot apply it yet - its ticket does not exist yet, or a mapping / employee is not set up on the TigerCS side. TigerCS retries it itself: mark it sent and do <b>not</b> resend. <see cref="SendResult.Response"/> says why.</summary>
    Accepted,

    /// <summary>A transient failure (network, timeout, HTTP 408 / 425 / 429 / 5xx incl. 503) that survived <c>MaxAttempts</c>. Keep the event in the outbox and resend it later, <b>unchanged</b> (same EventId, same body).</summary>
    RetryLater,

    /// <summary>The key was refused (HTTP 401 / 403). Nothing will succeed until the credentials are fixed: stop sending, alert, keep the events. Do not drop them.</summary>
    Unauthorized,

    /// <summary>TigerCS refused the event for a reason resending cannot fix (HTTP 400 validation, 409 EventId reused with different content, another 4xx such as a wrong URL). Move it to a dead-letter store and alert; do not resend as is.</summary>
    PermanentFailure
}

/// <summary>The outcome of sending one event.</summary>
public sealed record SendResult(SendDisposition Disposition, HttpStatusCode? StatusCode, QueueingEventResponse? Response, int Attempts, string? Error)
{
    /// <summary>Delivered or Accepted: the event is off the sender's outbox.</summary>
    public bool IsDone => Disposition is SendDisposition.Delivered or SendDisposition.Accepted;
}

/// <summary>
/// Sends one event to <c>POST {BaseUrl}/api/integrations/queueing/events</c> and applies the retry rules of the contract:
/// <list type="bullet">
/// <item>200 / 201 -> Delivered. 202 -> Accepted (TigerCS retries; never resend).</item>
/// <item>400, 409 and any other 4xx -> PermanentFailure (resending the same event cannot succeed). 401 / 403 -> Unauthorized (halt and alert).</item>
/// <item>Network errors, timeouts, 408, 425, 429 and every 5xx (503 honours <c>Retry-After</c>) -> retried here with exponential backoff and jitter, the <b>same</b> EventId and body each time,
/// up to <c>MaxAttempts</c>; then RetryLater - the caller's durable outbox resends it, unchanged, later.</item>
/// </list>
/// Resending is always safe: TigerCS is idempotent on EventId.
/// </summary>
public sealed class TigerCsEventSender
{
    public const string EndpointPath = "api/integrations/queueing/events";

    private readonly HttpClient _http;
    private readonly TigerCsSenderOptions _options;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly Random _random = new();

    public TigerCsEventSender(HttpClient http, TigerCsSenderOptions options, Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(options);
        options.EnsureValid();
        _http = http;
        _options = options;
        _delay = delay ?? Task.Delay;
    }

    public async Task<SendResult> SendAsync(QueueingEventRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var problems = request.Validate();
        if (problems.Count > 0)
        {
            // Never sent: TigerCS would answer 400 for exactly this.
            return new SendResult(SendDisposition.PermanentFailure, null, null, 0, "Invalid event: " + string.Join(" ", problems.Values));
        }

        var body = request.ToJson();
        var uri = new Uri(new Uri(_options.BaseUrl.TrimEnd('/') + "/"), EndpointPath);
        string? lastError = null;
        HttpStatusCode? lastStatus = null;

        for (var attempt = 1; attempt <= _options.MaxAttempts; attempt++)
        {
            TimeSpan? serverAskedFor = null;
            try
            {
                using var message = new HttpRequestMessage(HttpMethod.Post, uri) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
                message.Headers.TryAddWithoutValidation(_options.ApiKeyHeaderName, _options.ApiKey);
                message.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(TimeSpan.FromSeconds(_options.RequestTimeoutSeconds));
                using var response = await _http.SendAsync(message, timeout.Token);
                var parsed = await TryReadAsync(response, cancellationToken);
                lastStatus = response.StatusCode;

                switch (Classify(response.StatusCode))
                {
                    case Kind.Delivered:
                        return new SendResult(SendDisposition.Delivered, response.StatusCode, parsed, attempt, null);
                    case Kind.Accepted:
                        return new SendResult(SendDisposition.Accepted, response.StatusCode, parsed, attempt, null);
                    case Kind.Unauthorized:
                        return new SendResult(SendDisposition.Unauthorized, response.StatusCode, parsed, attempt, $"TigerCS refused the API key (HTTP {(int)response.StatusCode}).");
                    case Kind.Permanent:
                        return new SendResult(SendDisposition.PermanentFailure, response.StatusCode, parsed, attempt,
                            parsed?.Detail ?? $"TigerCS refused the event (HTTP {(int)response.StatusCode}).");
                    default:
                        lastError = parsed?.Detail ?? $"HTTP {(int)response.StatusCode}";
                        serverAskedFor = RetryAfter(response);
                        break;
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or IOException)
            {
                lastError = ex is OperationCanceledException ? "The request timed out." : ex.Message;
            }

            if (attempt < _options.MaxAttempts)
            {
                await _delay(Backoff(attempt, serverAskedFor), cancellationToken);
            }
        }

        return new SendResult(SendDisposition.RetryLater, lastStatus, null, _options.MaxAttempts, lastError);
    }

    private enum Kind { Delivered, Accepted, Unauthorized, Permanent, Transient }

    private static Kind Classify(HttpStatusCode status) => (int)status switch
    {
        200 or 201 => Kind.Delivered,
        202 => Kind.Accepted,
        401 or 403 => Kind.Unauthorized,
        408 or 425 or 429 => Kind.Transient,
        >= 500 => Kind.Transient,
        >= 400 => Kind.Permanent,
        // 3xx and any other 2xx are not part of the contract: do not guess that it worked.
        _ => Kind.Permanent
    };

    private TimeSpan Backoff(int attempt, TimeSpan? serverAskedFor)
    {
        var cap = TimeSpan.FromSeconds(_options.MaxDelaySeconds);
        if (serverAskedFor is { } asked)
        {
            return asked > cap ? cap : asked;
        }

        var exponential = TimeSpan.FromMilliseconds(_options.BaseDelayMilliseconds * Math.Pow(2, attempt - 1));
        var jitter = TimeSpan.FromMilliseconds(_random.NextDouble() * _options.BaseDelayMilliseconds);
        var total = exponential + jitter;
        return total > cap ? cap : total;
    }

    private static TimeSpan? RetryAfter(HttpResponseMessage response)
    {
        var header = response.Headers.RetryAfter;
        if (header?.Delta is { } delta)
        {
            return delta;
        }

        return header?.Date is { } date && date > DateTimeOffset.UtcNow ? date - DateTimeOffset.UtcNow : null;
    }

    private static async Task<QueueingEventResponse?> TryReadAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.Content.Headers.ContentType?.MediaType?.Contains("json", StringComparison.OrdinalIgnoreCase) != true)
        {
            return null;
        }

        try
        {
            return await response.Content.ReadFromJsonAsync<QueueingEventResponse>(QueueingEventResponse.Options, cancellationToken);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
