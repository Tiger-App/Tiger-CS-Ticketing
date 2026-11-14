using Microsoft.EntityFrameworkCore;
using TigerCS.Application.Modules.Collections;
using TigerCS.Application.Modules.Collections.Abstractions;
using TigerCS.Application.Modules.Collections.Dto;
using TigerCS.Application.Modules.Collections.Review;
using TigerCS.Application.Modules.Collections.Services;
using TigerCS.Domain.Modules.Collections;
using TigerCS.Domain.Modules.Collections.Review;

namespace TigerCS.Tests.Collections.Review;

/// <summary>
/// PACT marks a cancelled unit with an asterisk anywhere in its code (including '**'). Such a unit is dropped BEFORE matching, grouping and paging, its amounts never reach a total, and a
/// record prepared earlier for it is excluded again just before dispatch - the mark is never stripped to reach an active unit.
/// </summary>
public sealed class ReviewCancelledUnitTests
{
    private static readonly DateOnly Today = new(2026, 10, 14);

    [Theory]
    [InlineData("TP140-1*")]
    [InlineData("TP140-*1")]
    [InlineData("TP140-1**")]
    [InlineData("**")]
    [InlineData("TP1*40-1*")]
    public void TheBuilder_NeverCreatesARecord_ForACodeWithOneOrMoreAsterisks(string code)
    {
        Assert.True(ReviewRecordBuilder.IsCancelledUnit(code));
        var cancelled = ReviewHarness.Row(1) with { UnitCode = code };
        var active = ReviewHarness.Row(2);
        var context = new ReviewBuildContext(Today, ReviewHarness.Now, "PACT test", "AED", true, true, false, MoneyNormalizer.DefaultFloatTolerance, new Dictionary<string, string>(), null);

        var result = MirrorCrmBuild.Build([cancelled, active], context);

        Assert.All(result.Records, r => Assert.Equal("TP140-2", r.UnitCode));
        Assert.NotEmpty(result.Records);
        Assert.Equal(1, result.CancelledRows);
        Assert.Equal(500m * result.Records.Count, result.Records.Sum(r => r.RemainingAmount ?? 0m));   // the cancelled amount is in no total
    }

    [Fact]
    public void AnActiveCode_IsNotCancelled()
    {
        Assert.False(ReviewRecordBuilder.IsCancelledUnit("TP140-101"));
        Assert.False(ReviewRecordBuilder.IsCancelledUnit(""));
        Assert.False(ReviewRecordBuilder.IsCancelledUnit(null));
    }

    [Theory]
    [InlineData("TP140-1*")]
    [InlineData("TP140-1**")]
    public async Task ARecordPreparedBeforeTheUnitWasCancelled_IsExcludedAtDispatch_AndNoReminderIsSent(string cancelledCode)
    {
        using var h = new ReviewHarness();
        for (var n = 1; n <= 3; n++) h.Source.Items.Add(ReviewHarness.Row(n, 100m + n));
        await h.RefreshAsync();
        var (_, confirm) = await h.PrepareAsync();
        long id;
        using (var scope = h.NewScope())
        {
            Assert.Equal(CollectionsOutcome.Accepted, (await scope.Dispatch.ConfirmAsync(h.Manager, confirm, CancellationToken.None)).Outcome);
            id = h.Scheduler.Dispatches.Last();
        }
        using (var context = h.CreateContext())
            await context.CollectionsDispatchItems.Where(i => i.UnitCode == "TP140-1").ExecuteUpdateAsync(s => s.SetProperty(i => i.UnitCode, cancelledCode));   // PACT cancelled the unit after approval

        using (var scope = h.NewScope()) await scope.Dispatch.ExecuteAsync(id, CancellationToken.None);

        var sent = Assert.Single(h.Client.Calls).Contacts;
        Assert.Equal(2, sent.Count);
        Assert.DoesNotContain(sent, c => c.Phone == "+971500000001");
        using var check = h.CreateContext();
        var items = await check.CollectionsDispatchItems.AsNoTracking().ToListAsync();
        var excluded = Assert.Single(items, i => i.Status == DispatchItemStatus.Excluded);
        Assert.Equal(cancelledCode, excluded.UnitCode);
        Assert.Contains("Cancelled unit", excluded.StatusReason);
        Assert.DoesNotContain("Paid", excluded.StatusReason);
    }

    [Fact]
    public async Task WhenEveryPreparedRecordIsOfACancelledUnit_NothingIsUploaded()
    {
        using var h = new ReviewHarness();
        h.Source.Items.Add(ReviewHarness.Row(1));
        await h.RefreshAsync();
        var (_, confirm) = await h.PrepareAsync();
        long id;
        using (var scope = h.NewScope())
        {
            await scope.Dispatch.ConfirmAsync(h.Manager, confirm, CancellationToken.None);
            id = h.Scheduler.Dispatches.Last();
        }
        using (var context = h.CreateContext())
            await context.CollectionsDispatchItems.ExecuteUpdateAsync(s => s.SetProperty(i => i.UnitCode, "TP140-1*"));

        using (var scope = h.NewScope()) await scope.Dispatch.ExecuteAsync(id, CancellationToken.None);

        Assert.Empty(h.Client.Calls);
        using var check = h.CreateContext();
        var dispatch = await check.CollectionsDispatches.AsNoTracking().SingleAsync();
        Assert.Equal(DispatchStatus.Completed, dispatch.Status);
        Assert.Contains("cancelled", dispatch.StatusReason, StringComparison.OrdinalIgnoreCase);
    }
}
