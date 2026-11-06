using TigerCS.Application.Modules.Collections.Abstractions;
using TigerCS.Infrastructure.Modules.Collections;

namespace TigerCS.Tests.Collections.Rcv;

/// <summary>
/// The snapshot procedure returns a row's tower NUMBER and name, not the tower's local id; the review records (the Manual list) filter by that id. The RCV source resolves the id from
/// <c>dbo.CollectionsTowers</c> by company and trimmed number, never guesses, and leaves a row alone when it already has an id or its tower is unknown.
/// </summary>
public sealed class RcvTowerIdResolutionTests
{
    private static PactReceivableInstalment Row(int company, string? towerNumber, int? towerId = null) =>
        new(company, "T0001", "Customer", "971500000001", "", 1001, "TP140-1", "TP140", "INV-1", "", new DateTime(2026, 10, 20), 500m, "Installment", towerNumber, towerId, null);

    private static readonly Dictionary<(int Company, string Number), int> Towers = new() { [(4, "140")] = 7, [(32, "140")] = 9, [(4, "124")] = 3 };

    [Fact]
    public void TheIdIsResolvedByCompanyAndTrimmedNumber()
    {
        var rows = RcvReceivablesSource.ResolveTowerIds([Row(4, "140"), Row(32, " 140 "), Row(4, "124")], Towers);
        Assert.Equal([7, 9, 3], rows.Select(r => r.TowerId));
        Assert.All(rows, r => Assert.NotNull(r.TowerNumber));                               // the number and name stay as the procedure returned them
    }

    [Fact]
    public void AnUnknownTower_NoNumber_OrNoTowersAtAll_LeavesTheIdNull_AndARowWithAnIdIsLeftAlone()
    {
        var rows = RcvReceivablesSource.ResolveTowerIds([Row(4, "999"), Row(4, null), Row(4, ""), Row(4, "140", towerId: 55)], Towers);
        Assert.Equal([null, null, null, 55], rows.Select(r => r.TowerId));
        Assert.All(RcvReceivablesSource.ResolveTowerIds([Row(4, "140")], new Dictionary<(int, string), int>()), r => Assert.Null(r.TowerId));
    }
}
