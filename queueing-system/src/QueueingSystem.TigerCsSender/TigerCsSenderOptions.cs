namespace QueueingSystem.TigerCsSender;

/// <summary>
/// Configuration of the sender (bind it from the <c>TigerCs</c> section). The API key is a secret: supply it from the Queueing System's secret store or an
/// environment variable (<c>TigerCs__ApiKey</c>), never from a committed file.
/// </summary>
public sealed class TigerCsSenderOptions
{
    public const string SectionName = "TigerCs";

    /// <summary>The TigerCS API base address, e.g. <c>https://tigercs.example.com</c>. Required. The endpoint is <c>{BaseUrl}/api/integrations/queueing/events</c>.</summary>
    public string BaseUrl { get; set; } = string.Empty;

    /// <summary>The API key TigerCS gave the Queueing System. Required. Sent as-is in <see cref="ApiKeyHeaderName"/>.</summary>
    public string ApiKey { get; set; } = string.Empty;

    /// <summary>The request header that carries the key. Default <c>X-Queueing-Api-Key</c> (it is configurable on the TigerCS side, <c>QueueingIntegration:Authentication:HeaderName</c>).</summary>
    public string ApiKeyHeaderName { get; set; } = DefaultApiKeyHeaderName;

    public const string DefaultApiKeyHeaderName = "X-Queueing-Api-Key";

    /// <summary>Timeout of one HTTP attempt, in seconds. Default 30.</summary>
    public int RequestTimeoutSeconds { get; set; } = 30;

    /// <summary>How many HTTP attempts one <see cref="TigerCsEventSender.SendAsync"/> call makes before it hands the event back to the caller's outbox as <see cref="SendDisposition.RetryLater"/>. Default 5.</summary>
    public int MaxAttempts { get; set; } = 5;

    /// <summary>The first backoff, in milliseconds; it doubles each attempt (with jitter). Default 1000.</summary>
    public int BaseDelayMilliseconds { get; set; } = 1000;

    /// <summary>The longest single backoff, in seconds. Default 60.</summary>
    public int MaxDelaySeconds { get; set; } = 60;

    /// <summary>Throws when the configuration cannot work, naming the key.</summary>
    public void EnsureValid()
    {
        if (!Uri.TryCreate(BaseUrl, UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp))
        {
            throw new InvalidOperationException("TigerCs:BaseUrl must be an absolute http(s) address.");
        }

        if (string.IsNullOrWhiteSpace(ApiKey))
        {
            throw new InvalidOperationException("TigerCs:ApiKey is required.");
        }

        if (string.IsNullOrWhiteSpace(ApiKeyHeaderName))
        {
            throw new InvalidOperationException("TigerCs:ApiKeyHeaderName must not be empty.");
        }

        if (MaxAttempts < 1 || RequestTimeoutSeconds < 1 || BaseDelayMilliseconds < 0 || MaxDelaySeconds < 0)
        {
            throw new InvalidOperationException("TigerCs:MaxAttempts and RequestTimeoutSeconds must be at least 1; the delays cannot be negative.");
        }
    }
}
