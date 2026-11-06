using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using TigerCS.Application.Modules.Collections.Review;

namespace TigerCS.Integrations.Modules.CollectionsIntegration;

public interface IGenesysTokenProvider
{
    /// <summary>A valid bearer token, cached until shortly before its stated expiry.</summary>
    Task<string> GetTokenAsync(CancellationToken cancellationToken);

    /// <summary>Drops the cached token if it is still <paramref name="rejectedToken"/> (Genesys answered 401).</summary>
    void Invalidate(string rejectedToken);
}

public sealed class GenesysAuthenticationException(string message) : Exception(message);

/// <summary>
/// OAuth client-credentials token for Genesys Cloud, requested server-side and cached by its <c>expires_in</c>.
/// The client id, secret and token are never logged or included in exception messages.
/// </summary>
public sealed class GenesysTokenProvider(HttpClient http, GenesysOutboundOptions options, TimeProvider time,
    ILogger<GenesysTokenProvider> logger) : IGenesysTokenProvider
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private string? _token;
    private DateTimeOffset _expiresAt;

    public async Task<string> GetTokenAsync(CancellationToken cancellationToken)
    {
        if (TryCached(out var cached)) return cached;
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (TryCached(out cached)) return cached;
            if (string.IsNullOrWhiteSpace(options.ClientId) || string.IsNullOrWhiteSpace(options.ClientSecret))
                throw new GenesysAuthenticationException("The Genesys client credentials are not configured.");

            using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(new Uri(options.LoginBaseUrl.TrimEnd('/') + "/"), "oauth/token"));
            request.Headers.Authorization = new AuthenticationHeaderValue("Basic",
                Convert.ToBase64String(Encoding.UTF8.GetBytes($"{options.ClientId}:{options.ClientSecret}")));
            request.Content = new FormUrlEncodedContent([new KeyValuePair<string, string>("grant_type", "client_credentials")]);
            HttpResponseMessage response;
            try { response = await http.SendAsync(request, cancellationToken); }
            catch (HttpRequestException) { throw new GenesysAuthenticationException("The Genesys login service could not be reached."); }
            using (response)
            {
                if (!response.IsSuccessStatusCode)
                {
                    // The OAuth error code (invalid_client, unsupported_grant_type, ...) names the cause without exposing anything: the body's other fields and the credentials are never kept.
                    var code = await ReadOAuthErrorCodeAsync(response, cancellationToken);
                    logger.LogWarning("Genesys token request was refused with HTTP {Status} ({Code}).", (int)response.StatusCode, code ?? "no error code");
                    throw new GenesysAuthenticationException($"The Genesys login service refused the credentials (HTTP {(int)response.StatusCode}{(code is null ? "" : ", " + code)}).");
                }
                using var json = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
                var root = json.RootElement;
                var token = root.TryGetProperty("access_token", out var t) ? t.GetString() : null;
                var seconds = root.TryGetProperty("expires_in", out var e) && e.TryGetInt32(out var s) ? s : 0;
                if (string.IsNullOrEmpty(token) || seconds <= 0)
                    throw new GenesysAuthenticationException("The Genesys login service returned an unusable token response.");
                _token = token;
                // Refresh a little early; never cache longer than the stated lifetime.
                _expiresAt = time.GetUtcNow().AddSeconds(Math.Max(1, seconds - Math.Max(0, options.TokenExpirySkewSeconds)));
                return token;
            }
        }
        finally { _gate.Release(); }
    }

    public void Invalidate(string rejectedToken)
    {
        if (string.Equals(_token, rejectedToken, StringComparison.Ordinal)) { _token = null; _expiresAt = default; }
    }

    /// <summary>The RFC 6749 <c>error</c> code of a refused token response (letters, digits, <c>_</c> and <c>-</c> only, at most 40 characters), or null when there is none readable.</summary>
    internal static async Task<string?> ReadOAuthErrorCodeAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        try
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            if (string.IsNullOrWhiteSpace(body) || body.Length > 64 * 1024) return null;
            using var json = JsonDocument.Parse(body);
            if (json.RootElement.ValueKind != JsonValueKind.Object || !json.RootElement.TryGetProperty("error", out var error) || error.ValueKind != JsonValueKind.String) return null;
            var code = error.GetString() ?? "";
            return code.Length > 0 && code.Length <= 40 && code.All(ch => char.IsAsciiLetterOrDigit(ch) || ch is '_' or '-') ? code : null;
        }
        catch (Exception ex) when (ex is JsonException or IOException or HttpRequestException or InvalidOperationException) { return null; }
    }

    private bool TryCached(out string token)
    {
        var current = _token;
        if (current is not null && time.GetUtcNow() < _expiresAt) { token = current; return true; }
        token = "";
        return false;
    }
}

/// <summary>
/// Talks to Genesys outbound contact lists. Every answer is classified as accepted (Genesys did it), rejected (Genesys certainly did
/// not, so a retry is safe) or unknown (the request may have been processed: timeouts, dropped connections, 5xx). Unknown outcomes
/// must never be resent blindly.
/// </summary>
public sealed class GenesysOutboundHttpClient(HttpClient http, IGenesysTokenProvider tokens, GenesysOutboundOptions options,
    ILogger<GenesysOutboundHttpClient> logger) : IGenesysOutboundClient
{
    private sealed record Raw(GenesysUploadOutcome Outcome, int? Status, string? Body, string? Error);

    private static object DataOf(GenesysContactPayload c) => new Dictionary<string, string>
    {
        ["Phone"] = c.Phone, ["CustomerName"] = c.CustomerName, ["Email Address"] = c.EmailAddress,
        ["ReminderType"] = c.ReminderType, ["AmountDue"] = c.AmountDue, ["DueDate"] = c.DueDate
    };

    public async Task<GenesysUploadResult> UploadContactsAsync(string contactListId, IReadOnlyList<GenesysContactPayload> contacts, CancellationToken ct)
    {
        if (contacts.Count is 0 or > GenesysOutboundOptions.MaxBatchSize)
            throw new ArgumentOutOfRangeException(nameof(contacts), "A request must contain 1-1,000 contacts.");
        if (contacts.Any(c => !string.Equals(c.ContactListId, contactListId, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("Every contact must carry the same contactListId as the request URL.");

        var body = JsonSerializer.Serialize(contacts.Select(c => new Dictionary<string, object?>
        {
            ["contactListId"] = c.ContactListId, ["data"] = DataOf(c), ["callable"] = c.Callable
        }));
        var raw = await SendAsync(HttpMethod.Post, $"api/v2/outbound/contactlists/{contactListId}/contacts", body, "upload", ct);
        if (raw.Outcome != GenesysUploadOutcome.Accepted) return new(raw.Outcome, raw.Status, [], raw.Error);
        return new(GenesysUploadOutcome.Accepted, raw.Status, ReadIds(raw.Body), null);
    }

    public async Task<GenesysSuppressResult> SetNotCallableAsync(string contactListId, string contactId, GenesysContactPayload contact, CancellationToken ct)
    {
        if (!string.Equals(contact.ContactListId, contactListId, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The contact must carry the same contactListId as the request URL.");
        if (string.IsNullOrWhiteSpace(contactId) || contactId.Any(ch => !(char.IsAsciiLetterOrDigit(ch) || ch is '-' or '_')))
            throw new ArgumentException("Invalid Genesys contact id.", nameof(contactId));
        var body = JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["id"] = contactId, ["contactListId"] = contactListId, ["data"] = DataOf(contact), ["callable"] = false
        });
        var raw = await SendAsync(HttpMethod.Put, $"api/v2/outbound/contactlists/{contactListId}/contacts/{contactId}", body, "suppression", ct);
        if (raw.Status == 404) return new(GenesysUploadOutcome.Accepted, 404, null, ContactMissing: true, null);
        if (raw.Outcome != GenesysUploadOutcome.Accepted) return new(raw.Outcome, raw.Status, null, false, raw.Error);
        return new(GenesysUploadOutcome.Accepted, raw.Status, ReadCallable(raw.Body), false, null);
    }

    public async Task<GenesysCampaignLookupResult> FindCampaignsByNameAsync(string name, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("A campaign name is required.", nameof(name));
        var found = new List<GenesysCampaignRef>();
        for (var page = 1; page <= MaxLookupPages; page++)
        {
            var raw = await SendAsync(HttpMethod.Get,
                $"api/v2/outbound/campaigns?name={Uri.EscapeDataString(name)}&pageSize=100&pageNumber={page}", null, "campaign lookup", ct);
            if (raw.Outcome != GenesysUploadOutcome.Accepted) return new(raw.Outcome, raw.Status, [], raw.Error);
            try
            {
                using var doc = JsonDocument.Parse(raw.Body ?? "");
                var root = doc.RootElement;
                if (root.TryGetProperty("entities", out var entities) && entities.ValueKind == JsonValueKind.Array)
                    found.AddRange(entities.EnumerateArray().Select(ReadCampaign).OfType<GenesysCampaignRef>());
                var pageCount = root.TryGetProperty("pageCount", out var pc) && pc.TryGetInt32(out var n) ? n : 1;
                if (page >= pageCount) return new(GenesysUploadOutcome.Accepted, raw.Status, found, null);
            }
            catch (JsonException) { return new(GenesysUploadOutcome.Unknown, raw.Status, [], "Genesys returned an unreadable campaign list."); }
        }
        // More pages than we are willing to read: the answer is incomplete, so it must not be mistaken for "no such campaign".
        return new(GenesysUploadOutcome.Unknown, null, [], "The campaign lookup returned too many pages to check.");
    }

    public async Task<GenesysContactListInfo> GetContactListAsync(string contactListId, CancellationToken ct)
    {
        if (!Guid.TryParse(contactListId, out var id)) throw new ArgumentException("A contact list id is required.", nameof(contactListId));
        var raw = await SendAsync(HttpMethod.Get, $"api/v2/outbound/contactlists/{id:D}", null, "contact list lookup", ct);
        if (raw.Outcome != GenesysUploadOutcome.Accepted) return new(raw.Outcome, raw.Status, [], raw.Error);
        try
        {
            using var doc = JsonDocument.Parse(raw.Body ?? "");
            var columns = new List<string>();
            if (doc.RootElement.TryGetProperty("columnNames", out var names) && names.ValueKind == JsonValueKind.Array)
                columns.AddRange(names.EnumerateArray().Where(n => n.ValueKind == JsonValueKind.String).Select(n => n.GetString()!));
            if (doc.RootElement.TryGetProperty("phoneColumns", out var phones) && phones.ValueKind == JsonValueKind.Array)
                columns.AddRange(phones.EnumerateArray().Select(p => p.TryGetProperty("columnName", out var c) && c.ValueKind == JsonValueKind.String ? c.GetString()! : "").Where(c => c.Length > 0));
            return new(GenesysUploadOutcome.Accepted, raw.Status, columns, null);
        }
        catch (JsonException) { return new(GenesysUploadOutcome.Unknown, raw.Status, [], "Genesys returned an unreadable contact list."); }
    }

    public async Task<GenesysCampaignLookupResult> FindCampaignsByContactListAsync(string contactListId, CancellationToken ct)
    {
        if (!Guid.TryParse(contactListId, out var id)) throw new ArgumentException("A contact list id is required.", nameof(contactListId));
        var found = new List<GenesysCampaignRef>();
        for (var page = 1; page <= MaxLookupPages; page++)
        {
            var raw = await SendAsync(HttpMethod.Get, $"api/v2/outbound/campaigns?contactListId={id:D}&pageSize=100&pageNumber={page}", null, "campaign lookup by contact list", ct);
            if (raw.Outcome != GenesysUploadOutcome.Accepted) return new(raw.Outcome, raw.Status, [], raw.Error);
            try
            {
                using var doc = JsonDocument.Parse(raw.Body ?? "");
                var root = doc.RootElement;
                if (root.TryGetProperty("entities", out var entities) && entities.ValueKind == JsonValueKind.Array)
                    found.AddRange(entities.EnumerateArray().Select(ReadCampaign).OfType<GenesysCampaignRef>());
                var pageCount = root.TryGetProperty("pageCount", out var pc) && pc.TryGetInt32(out var n) ? n : 1;
                // The answer is narrowed again here: a campaign counts only when it really dials this list, whatever the query filter did.
                if (page >= pageCount)
                    return new(GenesysUploadOutcome.Accepted, raw.Status, found.Where(c => string.Equals(c.ContactListId, id.ToString("D"), StringComparison.OrdinalIgnoreCase)).ToList(), null);
            }
            catch (JsonException) { return new(GenesysUploadOutcome.Unknown, raw.Status, [], "Genesys returned an unreadable campaign list."); }
        }
        return new(GenesysUploadOutcome.Unknown, null, [], "The campaign lookup returned too many pages to check.");
    }

    public async Task<GenesysCampaignCreateResult> CreateCampaignAsync(string name, string contactListId, GenesysCampaignOptions t, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("A campaign name is required.", nameof(name));
        if (t.MissingForCreate() is { Count: > 0 } missing)
            return new(GenesysUploadOutcome.Rejected, null, null, $"The campaign template is incomplete ({string.Join(", ", missing)}).");
        var body = new Dictionary<string, object?>
        {
            ["name"] = name,
            ["contactList"] = new { id = contactListId },
            ["queue"] = new { id = t.QueueId },
            ["dialingMode"] = t.DialingMode,
            ["callerName"] = t.CallerName,
            ["callerAddress"] = t.CallerAddress,
            // Created off on purpose: nothing may dial until a person switches the campaign on in Genesys.
            ["campaignStatus"] = "off"
        };
        if (!string.IsNullOrWhiteSpace(t.ScriptId)) body["script"] = new { id = t.ScriptId };
        if (!string.IsNullOrWhiteSpace(t.CallAnalysisResponseSetId)) body["callAnalysisResponseSet"] = new { id = t.CallAnalysisResponseSetId };
        if (t.AbandonRate is { } rate) body["abandonRate"] = rate;
        var raw = await SendAsync(HttpMethod.Post, "api/v2/outbound/campaigns", JsonSerializer.Serialize(body), "campaign creation", ct);
        if (raw.Outcome != GenesysUploadOutcome.Accepted) return new(raw.Outcome, raw.Status, null, raw.Error);
        try
        {
            using var doc = JsonDocument.Parse(raw.Body ?? "");
            var created = ReadCampaign(doc.RootElement);
            if (created is not null) return new(GenesysUploadOutcome.Accepted, raw.Status, created, null);
        }
        catch (JsonException) { /* falls through */ }
        // Genesys said OK but we cannot tell which campaign it made: not safe to treat as either created or not created.
        return new(GenesysUploadOutcome.Unknown, raw.Status, null, "Genesys accepted the campaign but returned no usable id.");
    }

    private const int MaxLookupPages = 10;

    private static GenesysCampaignRef? ReadCampaign(JsonElement e)
    {
        if (e.ValueKind != JsonValueKind.Object) return null;
        var id = e.TryGetProperty("id", out var i) && i.ValueKind == JsonValueKind.String ? i.GetString() : null;
        if (string.IsNullOrEmpty(id)) return null;
        var name = e.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String ? n.GetString() ?? "" : "";
        var list = e.TryGetProperty("contactList", out var cl) && cl.ValueKind == JsonValueKind.Object
            && cl.TryGetProperty("id", out var cid) && cid.ValueKind == JsonValueKind.String ? cid.GetString() : null;
        var status = e.TryGetProperty("campaignStatus", out var st) && st.ValueKind == JsonValueKind.String ? st.GetString() : null;
        return new(id, name, list, status);
    }

    private async Task<Raw> SendAsync(HttpMethod method, string path, string? body, string what, CancellationToken ct)
    {
        for (var attempt = 0; attempt < 2; attempt++)
        {
            string token;
            try { token = await tokens.GetTokenAsync(ct); }
            catch (GenesysAuthenticationException ex) { return new(GenesysUploadOutcome.Rejected, null, null, ex.Message); }

            using var request = new HttpRequestMessage(method, new Uri(new Uri(options.ApiBaseUrl.TrimEnd('/') + "/"), path))
            { Content = body is null ? null : new StringContent(body, Encoding.UTF8, "application/json") };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

            HttpResponseMessage response;
            try { response = await http.SendAsync(request, ct); }
            catch (HttpRequestException ex) when (ex.HttpRequestError is HttpRequestError.ConnectionError or HttpRequestError.NameResolutionError or HttpRequestError.SecureConnectionError)
            { return new(GenesysUploadOutcome.Rejected, null, null, "Genesys could not be reached; nothing was sent."); }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException)
            {
                if (ct.IsCancellationRequested) throw;
                logger.LogWarning("Genesys {What} ended without a response ({ExceptionType}); the outcome is unknown.", what, ex.GetType().Name);
                return new(GenesysUploadOutcome.Unknown, null, null, "The request timed out or the connection dropped before Genesys answered.");
            }

            using (response)
            {
                var status = (int)response.StatusCode;
                if (response.StatusCode == HttpStatusCode.Unauthorized && attempt == 0) { tokens.Invalidate(token); continue; }
                if (response.IsSuccessStatusCode) return new(GenesysUploadOutcome.Accepted, status, await response.Content.ReadAsStringAsync(ct), null);
                if (status >= 500) return new(GenesysUploadOutcome.Unknown, status, null, $"Genesys answered HTTP {status}; the change may or may not have been applied.");
                var detail = status == 429 ? "Genesys rate limit reached; nothing was changed." : $"Genesys rejected the request (HTTP {status}); nothing was changed.";
                return new(GenesysUploadOutcome.Rejected, status, null, detail);
            }
        }
        return new(GenesysUploadOutcome.Rejected, 401, null, "Genesys rejected the access token twice; nothing was changed.");
    }

    private static IReadOnlyList<string> ReadIds(string? json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json ?? "");
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return [];
            return doc.RootElement.EnumerateArray().Select(e => e.TryGetProperty("id", out var id) ? id.GetString() ?? "" : "").ToList();
        }
        catch (JsonException) { return []; }
    }

    private static bool? ReadCallable(string? json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json ?? "");
            return doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty("callable", out var c)
                && c.ValueKind is JsonValueKind.True or JsonValueKind.False ? c.GetBoolean() : null;
        }
        catch (JsonException) { return null; }
    }
}
