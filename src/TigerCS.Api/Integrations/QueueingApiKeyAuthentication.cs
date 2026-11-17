using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace TigerCS.Api.Integrations;

/// <summary>
/// The Queueing System's own authentication, deliberately independent of staff sign-in: a presented API key (header <c>X-Queueing-Api-Key</c>) is
/// hashed with SHA-256 and compared, in constant time, against the hashes configured under <c>QueueingIntegration:Authentication:ApiKeys</c>. Only
/// hashes are configured, so a leaked configuration file is not a leaked key, and several can be listed at once to rotate a key without downtime.
/// A key authenticates <b>only</b> the scheme named here, so it opens the one Queueing endpoint and nothing else: it cannot call any staff endpoint,
/// and a staff JWT cannot call this one.
/// </summary>
public static class QueueingApiKeyDefaults
{
    public const string Scheme = "QueueingApiKey";
    public const string ClaimType = "tigercs:integration";
    public const string ClaimValue = "queueing";
    public const string DefaultHeaderName = "X-Queueing-Api-Key";

    /// <summary>The SHA-256 of a key, lower-case hex - what is configured. Used by the documented one-liner and by tests.</summary>
    public static string Hash(string apiKey) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(apiKey))).ToLowerInvariant();
}

public sealed class QueueingApiKeyOptions
{
    public const string SectionName = "QueueingIntegration:Authentication";

    public string HeaderName { get; set; } = QueueingApiKeyDefaults.DefaultHeaderName;

    public List<QueueingApiKeyEntry> ApiKeys { get; set; } = [];
}

/// <summary>One configured key: a label and the hash of the key. Never the key itself.</summary>
public sealed class QueueingApiKeyEntry
{
    /// <summary>A label for the key (who holds it) - appears in logs and as the caller's identity, never the key.</summary>
    public string KeyId { get; set; } = string.Empty;

    /// <summary>Lower- or upper-case hex SHA-256 of the key.</summary>
    public string Sha256 { get; set; } = string.Empty;
}

public sealed class QueueingApiKeyAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> schemeOptions,
    IOptionsMonitor<QueueingApiKeyOptions> keyOptions,
    ILoggerFactory logger,
    UrlEncoder encoder)
    : AuthenticationHandler<AuthenticationSchemeOptions>(schemeOptions, logger, encoder)
{
    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var configured = keyOptions.CurrentValue;
        if (!Request.Headers.TryGetValue(configured.HeaderName, out var presented) || string.IsNullOrWhiteSpace(presented.ToString()))
        {
            return Task.FromResult(AuthenticateResult.NoResult());
        }

        var presentedHash = SHA256.HashData(Encoding.UTF8.GetBytes(presented.ToString()));

        // Every configured key is compared, with no early exit, so the time taken does not reveal which (if any) matched.
        string? matchedKeyId = null;
        foreach (var entry in configured.ApiKeys)
        {
            if (TryDecode(entry.Sha256, out var expected) && CryptographicOperations.FixedTimeEquals(presentedHash, expected))
            {
                matchedKeyId ??= string.IsNullOrWhiteSpace(entry.KeyId) ? "unnamed" : entry.KeyId.Trim();
            }
        }

        if (matchedKeyId is null)
        {
            return Task.FromResult(AuthenticateResult.Fail("Invalid API key."));
        }

        var identity = new ClaimsIdentity(
            [
                new Claim(ClaimTypes.NameIdentifier, $"queueing-integration:{matchedKeyId}"),
                new Claim(ClaimTypes.Name, $"Queueing System ({matchedKeyId})"),
                new Claim(QueueingApiKeyDefaults.ClaimType, QueueingApiKeyDefaults.ClaimValue)
            ],
            Scheme.Name);
        return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), Scheme.Name)));
    }

    protected override Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        Response.StatusCode = StatusCodes.Status401Unauthorized;
        Response.Headers.WWWAuthenticate = "ApiKey";
        return Task.CompletedTask;
    }

    private static bool TryDecode(string hex, out byte[] bytes)
    {
        try
        {
            bytes = Convert.FromHexString(hex.Trim());
            return bytes.Length == SHA256.HashSizeInBytes;
        }
        catch (FormatException)
        {
            bytes = [];
            return false;
        }
    }
}
