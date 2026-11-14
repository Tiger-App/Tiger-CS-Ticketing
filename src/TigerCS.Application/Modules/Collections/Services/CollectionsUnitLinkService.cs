using System.Data.Common;
using Microsoft.Extensions.Logging;
using TigerCS.Application.Modules.Collections.Abstractions;
using TigerCS.Domain.Modules.Collections;

namespace TigerCS.Application.Modules.Collections.Services;

/// <summary>A unit to link: its company and PACT unit code, plus the contact PACT has for it (may be empty).</summary>
public sealed record UnitToLink(int CompanyId, string UnitCode, string PactName, string PactMobile, string PactEmail);

/// <summary>Whether CRM contact data could be used right now. <c>Disabled</c> is a deliberate setting; <c>NotLoaded</c>, <c>Stale</c> and <c>ReadFailed</c> are service problems.</summary>
public enum CrmFeedState { Disabled, Ready, NotLoaded, Stale, ReadFailed }

/// <summary>
/// The state of the CRM owner data as the pages must report it. A service problem (<see cref="Blocks"/>) is never presented as "CRM has no value": the contact of every unit
/// is then unconfirmed, so the unit is flagged <c>CrmUnavailable</c> and nothing is exported to Genesys until it is back.
/// </summary>
public sealed record CrmAvailability(CrmFeedState State, DateTime? LastSuccessUtc = null, bool LastRefreshFailed = false, string? Detail = null, int UnlinkedRows = 0, int AmbiguousRows = 0)
{
    public static readonly CrmAvailability Disabled = new(CrmFeedState.Disabled);
    public bool IsEnabled => State != CrmFeedState.Disabled;
    public bool Blocks => State is CrmFeedState.NotLoaded or CrmFeedState.Stale or CrmFeedState.ReadFailed;
    /// <summary>Short text for the pages; null when everything is fine. A switched-off feed is reported too: CRM was not consulted, which is not the same as CRM holding nothing.</summary>
    public string? Message => State switch
    {
        CrmFeedState.Disabled => "CRM contact linking is switched off: contacts shown come from PACT only and CRM was not checked, so a mobile or email may exist in CRM.",
        CrmFeedState.NotLoaded => "CRM contact data has not been loaded yet. Contacts shown come from PACT only and are unconfirmed; nothing can be exported to Genesys.",
        CrmFeedState.Stale => $"CRM contact data is out of date (last loaded {LastSuccessUtc:yyyy-MM-dd HH:mm} UTC). Contacts are unconfirmed; nothing can be exported to Genesys.",
        CrmFeedState.ReadFailed => "CRM contact data could not be read. Contacts shown come from PACT only and are unconfirmed; nothing can be exported to Genesys.",
        CrmFeedState.Ready when LastRefreshFailed => "The latest CRM refresh failed; the previous CRM data is still being used.",
        _ => null
    };
}

/// <summary>The CRM link of each requested unit and the state of the CRM data they were read from.</summary>
public sealed record CrmLinkSet(CrmAvailability Availability, IReadOnlyDictionary<(int CompanyId, string UnitKey), CrmUnitLink> Links)
{
    public static readonly CrmLinkSet None = new(CrmAvailability.Disabled, new Dictionary<(int, string), CrmUnitLink>());

    public CrmUnitLink? Find(int companyId, string? unitCode) =>
        CollectionsUnitKey.Normalize(unitCode) is { } key && Links.TryGetValue((companyId, key), out var link) ? link : null;

    /// <summary>The one combination of PACT and CRM contact for a unit: CRM first, PACT completing the missing fields only; never two people merged.</summary>
    public LinkedContact Link(int companyId, string? unitCode, SourceContact pact)
    {
        var link = Find(companyId, unitCode);
        return CollectionsContactLinker.Link(pact, link?.Contact ?? SourceContact.Empty, link?.Status ?? CrmLinkStatus.None);
    }

    /// <summary>The contact of a campaign unit: the mobile comes from CRM only (never PACT, not even as a fallback); name and e-mail as <see cref="Link"/>.</summary>
    public LinkedContact LinkForCampaign(int companyId, string? unitCode, SourceContact pact)
    {
        var link = Find(companyId, unitCode);
        return CollectionsContactLinker.LinkForCampaign(pact, link?.Contact ?? SourceContact.Empty, link?.Status ?? CrmLinkStatus.None);
    }

    /// <summary>The review flags of a linked unit, including <c>CrmUnavailable</c> when CRM could not be used at all.</summary>
    public CollectionsCampaignFlags Flags(LinkedContact linked, bool contactRequired)
    {
        var flags = CollectionsContactLinker.Flags(linked, contactRequired);
        if (Availability.Blocks) flags |= CollectionsCampaignFlags.CrmUnavailable;
        return flags;
    }
}

public sealed record UnitLinkBatch(CrmAvailability Availability, IReadOnlyDictionary<(int CompanyId, string UnitCode), LinkedContact> Contacts);

/// <summary>
/// THE shared way to attach CRM owners to units outside the SQL engines (Campaigns, the review records behind the Genesys lists, Receivables rows, exports, Payment Summary):
/// ONE set-based read of the local CRM owner data for any number of units - never a CRM or PACT request per unit - then <see cref="CollectionsContactLinker"/>.
/// It also reports the state of that data, so a CRM service problem is never mistaken for "CRM has no mobile".
/// </summary>
public sealed class CollectionsUnitLinkService(
    ICollectionsCrmOwnerStore store, CollectionsCrmOwnersOptions options, TimeProvider timeProvider, ILogger<CollectionsUnitLinkService> logger)
{
    public async Task<CrmAvailability> GetAvailabilityAsync(CancellationToken cancellationToken)
    {
        if (!options.Enabled) return CrmAvailability.Disabled;
        try { return Evaluate(await store.GetStateAsync(cancellationToken)); }
        catch (Exception ex) when (ex is DbException or InvalidOperationException)
        {
            logger.LogWarning("CRM owner state could not be read ({ExceptionType}).", ex.GetType().Name);
            return new CrmAvailability(CrmFeedState.ReadFailed, Detail: "The CRM owner data could not be read.");
        }
    }

    internal CrmAvailability Evaluate(CrmOwnerState state)
    {
        if (!state.Loaded) return new CrmAvailability(CrmFeedState.NotLoaded, state.LastSuccessUtc, state.LastAttemptStatus == "Failed", state.LastError, state.UnlinkedRows, state.AmbiguousRows);
        var age = state.LastSuccessUtc is { } last ? timeProvider.GetUtcNow().UtcDateTime - last : TimeSpan.MaxValue;
        var failed = state.LastAttemptStatus == "Failed";
        return age > TimeSpan.FromMinutes(Math.Max(1, options.MaxAgeMinutes))
            ? new CrmAvailability(CrmFeedState.Stale, state.LastSuccessUtc, failed, state.LastError, state.UnlinkedRows, state.AmbiguousRows)
            : new CrmAvailability(CrmFeedState.Ready, state.LastSuccessUtc, failed, failed ? state.LastError : null, state.UnlinkedRows, state.AmbiguousRows);
    }

    /// <summary>The CRM links of the given (company, unit code) pairs and the availability of the data, in one bulk read. A read failure is reported as <c>ReadFailed</c>, never as "no CRM data".</summary>
    public async Task<CrmLinkSet> GetLinksAsync(IEnumerable<(int CompanyId, string UnitCode)> units, CancellationToken cancellationToken)
    {
        var availability = await GetAvailabilityAsync(cancellationToken);
        if (!availability.IsEnabled || availability.State is CrmFeedState.NotLoaded or CrmFeedState.ReadFailed) return new CrmLinkSet(availability, new Dictionary<(int, string), CrmUnitLink>());
        var keys = units.Select(u => (u.CompanyId, Key: CollectionsUnitKey.Normalize(u.UnitCode))).Where(k => k.Key is not null)
            .Select(k => (k.CompanyId, k.Key!)).Distinct().ToList();
        try { return new CrmLinkSet(availability, await store.GetLinksAsync(keys, cancellationToken)); }
        catch (Exception ex) when (ex is DbException or InvalidOperationException)
        {
            logger.LogWarning("CRM owner links could not be read ({ExceptionType}).", ex.GetType().Name);
            return new CrmLinkSet(new CrmAvailability(CrmFeedState.ReadFailed, availability.LastSuccessUtc, availability.LastRefreshFailed, "The CRM owner links could not be read."),
                new Dictionary<(int, string), CrmUnitLink>());
        }
    }

    public async Task<UnitLinkBatch> LinkAsync(IReadOnlyList<UnitToLink> units, CancellationToken cancellationToken)
    {
        var set = await GetLinksAsync(units.Select(u => (u.CompanyId, u.UnitCode)), cancellationToken);
        var result = new Dictionary<(int, string), LinkedContact>();
        foreach (var unit in units)
            result[(unit.CompanyId, unit.UnitCode)] = set.Link(unit.CompanyId, unit.UnitCode,
                new SourceContact(unit.PactName.Trim(), CollectionsContactNormalizer.NormalizePhone(unit.PactMobile), CollectionsContactNormalizer.NormalizeEmail(unit.PactEmail)));
        return new UnitLinkBatch(set.Availability, result);
    }
}
