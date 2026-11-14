using TigerCS.Application.Modules.Collections.Abstractions;
using TigerCS.Domain.Modules.Collections;

namespace TigerCS.Tests.Collections.Crm;

/// <summary>In-memory CRM owner store: stores the rows the refresh service hands it and answers link reads exactly like fn_Collections_CrmUnitLinks (distinct customers per company + unit key).</summary>
public sealed class FakeCrmOwnerStore : ICollectionsCrmOwnerStore
{
    public List<CrmOwnerRow> Rows { get; private set; } = [];
    public Dictionary<string, int> CompanyOfTower { get; } = new() { ["140"] = 4, ["124"] = 4, ["127"] = 32 };
    public int LinkReads { get; private set; }
    public int Replaces { get; private set; }
    public string? Failure { get; private set; }
    public bool Throw { get; set; }
    public bool ThrowOnState { get; set; }
    public string? LastNote { get; private set; }
    /// <summary>When the stored run was loaded (tests pin it next to their clock, so the data is neither stale nor from the future).</summary>
    public DateTime? LastSuccessUtc { get; set; } = new DateTime(2026, 10, 14, 7, 30, 0, DateTimeKind.Utc);
    public bool LastAttemptFailed { get; set; }
    /// <summary>Company per unit key for owners that are tied by their EXACT unit (the PACT snapshot), as V012 does for towers that exist in no / both companies.</summary>
    public HashSet<(int CompanyId, string UnitKey)> AmbiguousUnits { get; } = [];

    public Task<int> ReplaceAsync(IReadOnlyList<CrmOwnerRow> rows, CancellationToken cancellationToken, string? note = null)
    {
        Rows = [.. rows]; Replaces++; Failure = null; LastNote = note;
        return Task.FromResult(rows.Count(r => CompanyOfTower.ContainsKey(CollectionsTowerNumber.Parse(r.UnitKey) ?? "")));
    }

    public Task RecordFailureAsync(string message, CancellationToken cancellationToken) { Failure = message; return Task.CompletedTask; }

    public Task<CrmOwnerState> GetStateAsync(CancellationToken cancellationToken)
    {
        if (ThrowOnState) throw new InvalidOperationException("store down");
        return Task.FromResult(new CrmOwnerState(Rows.Count > 0, Rows.Count > 0 ? LastSuccessUtc : null, null, Failure is not null || LastAttemptFailed ? "Failed" : "Succeeded", Failure, Rows.Count, 0, 0, LastNote));
    }

    public Task<IReadOnlyDictionary<(int CompanyId, string UnitKey), CrmUnitLink>> GetLinksAsync(IReadOnlyList<(int CompanyId, string UnitKey)> units, CancellationToken cancellationToken)
    {
        LinkReads++;
        if (Throw) throw new InvalidOperationException("store down");
        var wanted = units.ToHashSet();
        IReadOnlyDictionary<(int, string), CrmUnitLink> result = Rows
            .Select(r => (Row: r, Company: CompanyOfTower.TryGetValue(CollectionsTowerNumber.Parse(r.UnitKey) ?? "", out var c) ? c : 0))
            .Where(x => x.Company != 0).GroupBy(x => (x.Company, x.Row.UnitKey)).Where(g => wanted.Contains(g.Key))
            .ToDictionary(g => g.Key, g =>
            {
                var customers = g.Select(x => x.Row.CustomerId).Distinct().Count();
                var first = g.OrderBy(x => x.Row.CustomerId).First().Row;
                string Pick(Func<CrmOwnerRow, string> f) => g.Select(x => f(x.Row)).FirstOrDefault(v => v.Length > 0) ?? "";
                var companyAmbiguous = AmbiguousUnits.Contains(g.Key);
                return new CrmUnitLink(customers > 1 ? CrmLinkStatus.Ambiguous : companyAmbiguous ? CrmLinkStatus.CompanyAmbiguous : CrmLinkStatus.Single, customers,
                    customers > 1 || companyAmbiguous ? 0 : first.CustomerId, first.UnitId, first.LeadId,
                    customers > 1 || companyAmbiguous ? SourceContact.Empty : new SourceContact(Pick(r => r.FullName), Pick(r => r.PhoneNorm), Pick(r => r.EmailNorm)));
            });
        return Task.FromResult(result);
    }
}

public sealed class FakeCrmUnitOwnersGateway : ICrmUnitOwnersGateway
{
    public List<CrmUnitOwnerDto> Owners { get; } = [];
    public CrmUnitOwnersOutcome Outcome { get; set; } = CrmUnitOwnersOutcome.Success;
    public int Requests { get; private set; }

    public Task<CrmUnitOwnersPage> GetPageAsync(int page, int pageSize, CancellationToken cancellationToken)
    {
        Requests++;
        if (Outcome != CrmUnitOwnersOutcome.Success) return Task.FromResult(new CrmUnitOwnersPage(Outcome, [], Message: "boom"));
        return Task.FromResult(new CrmUnitOwnersPage(CrmUnitOwnersOutcome.Success, Owners.Skip((page - 1) * pageSize).Take(pageSize).ToList(), Owners.Count));
    }

    public static CrmUnitOwnerDto Owner(int customerId, string project, string unit, string? name = "Crm Customer", string? mobile = "0501234567", string? email = "crm@example.test",
        int leadStatus = 8, string? statusName = "Sold", int customerType = 1, int leadId = 0) =>
        new(leadId == 0 ? customerId * 10 : leadId, leadStatus, statusName, customerType, customerId, name, null, mobile, email, 5000 + customerId, unit, 7, project, "Project");
}

/// <summary>
/// Test default for suites that seed customers on the PACT side only: every PACT unit gets ONE eligible CRM buyer whose mobile is the seeded phone, so campaign tests keep exercising the
/// other rules now that the campaign mobile comes from CRM alone. A unit seeded with an empty / invalid phone therefore has an empty / invalid CRM mobile (NoValidContact), exactly as before.
/// </summary>
public sealed class PactMirrorCrmOwnerStore(Func<IEnumerable<PactReceivableInstalment>> items) : ICollectionsCrmOwnerStore
{
    public Task<int> ReplaceAsync(IReadOnlyList<CrmOwnerRow> rows, CancellationToken cancellationToken, string? note = null) => Task.FromResult(0);
    public Task RecordFailureAsync(string message, CancellationToken cancellationToken) => Task.CompletedTask;
    public Task<CrmOwnerState> GetStateAsync(CancellationToken cancellationToken) =>
        Task.FromResult(new CrmOwnerState(true, TigerCS.Tests.Collections.Review.ReviewHarness.Now.AddMinutes(-20), null, "Succeeded", null, 1, 0, 0, null));

    public Task<IReadOnlyDictionary<(int CompanyId, string UnitKey), CrmUnitLink>> GetLinksAsync(IReadOnlyList<(int CompanyId, string UnitKey)> units, CancellationToken cancellationToken)
    {
        var wanted = units.ToHashSet();
        var result = new Dictionary<(int, string), CrmUnitLink>();
        foreach (var r in items())
        {
            if (CollectionsUnitKey.Normalize(r.UnitCode) is not { } key || !wanted.Contains((r.CompanyId, key)) || result.ContainsKey((r.CompanyId, key))) continue;
            result[(r.CompanyId, key)] = new CrmUnitLink(CrmLinkStatus.Single, 1, 9000 + (r.UnitId ?? 0), r.UnitId ?? 0, r.UnitId ?? 0,
                new SourceContact(r.FullName, CollectionsContactNormalizer.NormalizePhone(r.Mobile), CollectionsContactNormalizer.NormalizeEmail(r.Email)));
        }
        return Task.FromResult<IReadOnlyDictionary<(int, string), CrmUnitLink>>(result);
    }
}
