using TigerCS.Application.Modules.Collections.Abstractions;
using TigerCS.Application.Modules.Collections;
using TigerCS.Application.Modules.Collections.Review;
using TigerCS.Application.Modules.Collections.Services;
using TigerCS.Domain.Modules.Collections;

namespace TigerCS.Tests.Collections.Review;

/// <summary>
/// Builds review records the way production does now that the campaign mobile comes from CRM alone: when a test gives the builder no CRM, every seeded PACT unit gets one eligible CRM buyer
/// carrying the seeded phone / name / e-mail (an empty or invalid seeded phone is then an empty or invalid CRM mobile). A test that passes its own CRM links is left untouched.
/// </summary>
internal static class MirrorCrmBuild
{
    public static CrmLinkSet Mirror(IEnumerable<PactReceivableInstalment> rows)
    {
        var links = new Dictionary<(int, string), CrmUnitLink>();
        foreach (var r in rows)
        {
            if (CollectionsUnitKey.Normalize(r.UnitCode) is not { } key || links.ContainsKey((r.CompanyId, key))) continue;
            links[(r.CompanyId, key)] = new CrmUnitLink(CrmLinkStatus.Single, 1, 9000 + (r.UnitId ?? 0), r.UnitId ?? 0, r.UnitId ?? 0,
                new SourceContact(r.FullName, CollectionsContactNormalizer.NormalizePhone(r.Mobile), CollectionsContactNormalizer.NormalizeEmail(r.Email)));
        }
        return new CrmLinkSet(new CrmAvailability(CrmFeedState.Ready), links);
    }

    /// <summary>The CRM link service for a campaign preview whose test seeds PACT only (see the class summary).</summary>
    public static CollectionsUnitLinkService Links(Func<IEnumerable<PactReceivableInstalment>> items, DateTime now) =>
        new(new TigerCS.Tests.Collections.Crm.PactMirrorCrmOwnerStore(items), new CollectionsCrmOwnersOptions { Enabled = true },
            new TigerCS.Tests.Notifications.Fakes.FakeTimeProvider(now), Microsoft.Extensions.Logging.Abstractions.NullLogger<CollectionsUnitLinkService>.Instance);

    public static ReviewBuildResult Build(IEnumerable<PactReceivableInstalment> source, ReviewBuildContext context)
    {
        var rows = source.ToList();
        return ReviewRecordBuilder.Build(rows, context.Crm is null ? context with { Crm = Mirror(rows) } : context);
    }
}
