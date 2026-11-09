using Microsoft.Extensions.Logging;
using TigerCS.Application.Modules.Collections.Services;
using TigerCS.Domain.Modules.Collections.Review;

namespace TigerCS.Application.Modules.Collections.Review;

/// <summary>One finding of the connection check. <c>Status</c> is Ok, Failed, Skipped or Info.</summary>
public sealed record GenesysCheckLineDto(string Name, string Status, string Detail);

/// <summary>The result of the read-only Genesys connection check: <c>Ok</c> only when no line failed. Hosts only, never a credential, token or URL path.</summary>
public sealed record GenesysConnectionCheckDto(DateTime CheckedAtUtc, bool Ok, string LoginHost, string ApiHost, IReadOnlyList<GenesysCheckLineDto> Lines);

/// <summary>
/// A READ-ONLY check of the Genesys integration as configured here, for the moment a send stops with "could not be checked in Genesys". It uploads nothing, creates nothing and changes nothing: it only
/// reads configuration and issues GET requests (the organization the OAuth client belongs to, then each configured contact list). It answers the questions a 404 or 403 leaves open:
/// <list type="bullet">
/// <item>do <c>LoginBaseUrl</c> and <c>ApiBaseUrl</c> name the same region (a token from one is not valid at the other)?</item>
/// <item>does the OAuth client authenticate, and in which organization does it act?</item>
/// <item>does each configured <c>ContactListIds:*</c> id exist in that organization, is it readable by the client, and does it carry the six template columns?</item>
/// </list>
/// It is independent of <c>AutoSendingEnabled</c> and of the live-dispatch switch, never contacts a customer, and never changes which list a send uses.
/// </summary>
public sealed class GenesysConnectionCheckService(
    CollectionsOptions options, GenesysOutboundOptions genesys, CollectionsAuthorizationService authorization, CollectionsClock clock, IGenesysOutboundClient client,
    ILogger<GenesysConnectionCheckService> logger)
{
    private static readonly SemaphoreSlim OneAtATime = new(1, 1);

    public async Task<CollectionsResult<GenesysConnectionCheckDto>> CheckAsync(CollectionsCaller caller, CancellationToken ct)
    {
        if (!(await authorization.ResolveAsync(caller, ct)).CanSendReminders) return CollectionsResult<GenesysConnectionCheckDto>.Fail(CollectionsOutcome.Forbidden);
        if (!options.Enabled) return CollectionsResult<GenesysConnectionCheckDto>.Fail(CollectionsOutcome.Disabled);
        if (!await OneAtATime.WaitAsync(0, ct)) return CollectionsResult<GenesysConnectionCheckDto>.Fail(CollectionsOutcome.FinanceUnavailable, "A Genesys connection check is already running. Try again in a moment.");
        try { return CollectionsResult<GenesysConnectionCheckDto>.Ok(await RunAsync(caller, ct)); }
        finally { OneAtATime.Release(); }
    }

    private async Task<GenesysConnectionCheckDto> RunAsync(CollectionsCaller caller, CancellationToken ct)
    {
        var lines = new List<GenesysCheckLineDto>();
        void Add(string name, string status, string detail) => lines.Add(new(name, status, detail));
        var loginHost = HostOf(genesys.LoginBaseUrl); var apiHost = HostOf(genesys.ApiBaseUrl);
        GenesysConnectionCheckDto Done()
        {
            var ok = lines.All(l => l.Status != "Failed");
            logger.LogInformation("Genesys connection check by {EmployeeId}: {Result} ({Failed} failed of {Lines} lines).", caller.EmployeeId, ok ? "ok" : "failed", lines.Count(l => l.Status == "Failed"), lines.Count);
            return new(clock.UtcNow, ok, loginHost, apiHost, lines);
        }

        // 1. Configuration, offline: nothing is called when the integration cannot work at all.
        if (!genesys.Enabled) { Add("Collections:GenesysOutbound:Enabled", "Failed", "The Genesys outbound integration is switched off, so nothing can be read or uploaded."); return Done(); }
        Add("Collections:GenesysOutbound:Enabled", "Ok", "On.");
        var credentials = !string.IsNullOrWhiteSpace(genesys.ClientId) && !string.IsNullOrWhiteSpace(genesys.ClientSecret);
        Add("ClientId / ClientSecret", credentials ? "Ok" : "Failed", credentials ? "Both are configured (their values are never shown)." : "The OAuth client id or secret is not configured (supplied as secrets).");
        var urlsValid = true;
        foreach (var (key, value) in new[] { ("LoginBaseUrl", genesys.LoginBaseUrl), ("ApiBaseUrl", genesys.ApiBaseUrl) })
        {
            if (Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps) continue;
            urlsValid = false; Add($"Collections:GenesysOutbound:{key}", "Failed", "Not a valid https URL.");
        }
        if (urlsValid) Add("Region", RegionLine(loginHost, apiHost).Status, RegionLine(loginHost, apiHost).Detail);
        if (!credentials || !urlsValid) { Add("Genesys requests", "Skipped", "Not sent: fix the configuration above first."); return Done(); }

        // 2. The OAuth client and its organization (read only).
        GenesysOrganizationInfo org;
        try { org = await client.GetOrganizationAsync(ct); }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        { org = new(GenesysUploadOutcome.Unknown, null, null, null, $"The check could not be completed ({ex.GetType().Name})."); }
        if (org.Outcome == GenesysUploadOutcome.Accepted)
            Add("OAuth client and organization", "Ok", $"Authenticated at {loginHost}; the client acts in organization \"{org.Name ?? "(no name)"}\" (id {org.Id ?? "unknown"}) at {apiHost}. Every contact list id below is looked up in THIS organization.");
        else
        {
            Add("OAuth client and organization", "Failed", org.Error ?? "Genesys did not answer.");
            // Without a token, or without an answer from the API, a list read would only repeat the same failure.
            if (org.HttpStatus is null) { Add("Contact lists", "Skipped", "Not read: the OAuth client could not be authenticated or Genesys could not be reached."); return Done(); }
        }

        // 3. Every configured contact list, once per distinct id (read only).
        var byId = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var type in Enum.GetValues<CampaignReminderType>())
        {
            string id;
            try { id = genesys.ContactListIdFor(type); }
            catch (InvalidOperationException) { Add($"ContactListIds:{type}", "Failed", "No valid contact list id (a GUID) is configured."); continue; }
            if (!byId.TryGetValue(id, out var types)) byId[id] = types = [];
            types.Add(type.ToString());
        }
        // The dedicated Manual list is read like the others; a missing, invalid or duplicated id is reported by name.
        try
        {
            var manualId = genesys.ManualContactListId;
            if (!byId.TryGetValue(manualId, out var manualTypes)) byId[manualId] = manualTypes = [];
            manualTypes.Add(CampaignReminderTypes.ManualListKey);
        }
        catch (InvalidOperationException ex) { Add($"ContactListIds:{CampaignReminderTypes.ManualListKey}", "Failed", ex.Message); }
        foreach (var (id, types) in byId)
        {
            var name = string.Join(" / ", types.Select(t => $"ContactListIds:{t}"));
            if (types.Count > 1) Add(name, "Info", $"These settings share one list id ({id}); that is allowed but means the same Genesys list receives all of those campaigns.");
            GenesysContactListInfo list;
            try { list = await client.GetContactListAsync(id, ct); }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            { list = new(GenesysUploadOutcome.Unknown, null, [], $"The check could not be completed ({ex.GetType().Name})."); }
            if (list.Outcome != GenesysUploadOutcome.Accepted)
            {
                var why = list.HttpStatus switch
                {
                    404 => $"HTTP 404: list {id} does not exist in organization \"{org.Name ?? "(unknown)"}\" at {apiHost}. The id is wrong, belongs to another organization or region, or was deleted: correct Collections:GenesysOutbound:{name}.",
                    403 => $"HTTP 403: the OAuth client may not read this list. Grant it outbound:contactList:view in Genesys Admin. {list.Error}",
                    _ => list.Error ?? "Genesys did not answer."
                };
                Add(name, "Failed", why);
                continue;
            }
            // The dedicated Manual list is checked against its eleven official columns; every Auto list against the six.
            var isManual = types.Contains(CampaignReminderTypes.ManualListKey);
            var fit = GenesysContactTemplate.CheckHeader(list.Columns, isManual);
            Add(name, fit.Fits ? "Ok" : "Failed", fit.Fits
                ? $"List {id} exists and carries the {(isManual ? "eleven" : "six")} template columns."
                : $"List {id} exists but does not fit the template: {string.Join("; ", fit.Missing.Select(m => $"missing column {m}").Concat(fit.Misnamed.Select(m => $"misnamed column {m}")))}.");
        }
        return Done();
    }

    /// <summary>A token is issued per region: <c>login.&lt;domain&gt;</c> and <c>api.&lt;domain&gt;</c> must share the domain (for example mypurecloud.de, or mec1.pure.cloud for the Middle East).</summary>
    private static (string Status, string Detail) RegionLine(string loginHost, string apiHost)
    {
        static string? Domain(string host, string prefix) => host.StartsWith(prefix + ".", StringComparison.OrdinalIgnoreCase) ? host[(prefix.Length + 1)..] : null;
        var login = Domain(loginHost, "login"); var api = Domain(apiHost, "api");
        if (login is null || api is null) return ("Info", $"LoginBaseUrl ({loginHost}) and ApiBaseUrl ({apiHost}) do not follow the usual login.<region> / api.<region> pattern, so their regions were not compared.");
        return string.Equals(login, api, StringComparison.OrdinalIgnoreCase)
            ? ("Ok", $"Both name the region {login}. This must be the region of the organization that owns the contact lists.")
            : ("Failed", $"LoginBaseUrl names {login} but ApiBaseUrl names {api}: a token issued in one region is not valid in the other. Set both to the organization's region.");
    }

    private static string HostOf(string url) => Uri.TryCreate(url, UriKind.Absolute, out var uri) ? uri.Host : "(not a valid URL)";
}
