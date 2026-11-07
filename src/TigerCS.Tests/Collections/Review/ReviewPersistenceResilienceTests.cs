using System.Reflection;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using TigerCS.Application.Modules.Collections.Review;
using TigerCS.Domain.Modules.Collections.Review;
using TigerCS.Infrastructure.Modules.Collections;

namespace TigerCS.Tests.Collections.Review;

/// <summary>
/// A refresh that loses its database connection mid-save (SQL transport error 10060 / -1) must end as a clean Failed run or converge on exactly the right records:
/// bounded batches, idempotent retries, no duplicates, no half-published data, a visible reason.
/// </summary>
public sealed class ReviewPersistenceResilienceTests
{
    private static ReviewHarness Seeded(int rows, int batch = 500)
    {
        var h = new ReviewHarness();
        h.Review.PersistBatchSize = batch;
        for (var n = 1; n <= rows; n++) h.Source.Items.Add(ReviewHarness.Row(n, 100m + n));
        return h;
    }

    private static async Task<(ReviewRunDto Run, int Stored)> RunAsync(ReviewHarness h)
    {
        using var scope = h.NewScope();
        var started = await scope.Refresh.StartAsync(h.Manager, null, CancellationToken.None);
        await scope.Refresh.RunAsync(started.Value!.RunId, CancellationToken.None);
        var run = (await scope.Query.GetRunAsync(h.Manager, started.Value.RunId, CancellationToken.None)).Value!;
        using var context = h.CreateContext();
        return (run, await context.CollectionsReviewRecords.CountAsync(r => r.CollectionsReviewRunId == run.RunId));
    }

    [Fact]
    public async Task RecordsAreWrittenInBoundedBatches_NotOneHugeSave()
    {
        using var h = Seeded(1300, batch: 500);
        var (run, stored) = await RunAsync(h);
        Assert.Equal("Completed", run.Status);
        Assert.Equal(run.RecordCount, stored);
        Assert.True(run.RecordCount > 1300);                                    // a source row can yield several records (one per reminder type), like 6,077 from 4,916 rows
        Assert.Equal((int)Math.Ceiling(run.RecordCount / 500.0), h.Faults.RecordSaves);   // bounded batches of at most 500
    }

    [Fact]
    public async Task ATransientFailureOnABatch_IsRetriedOnFreshState_AndTheRunStillCompletesWithExactlyTheRightRecords()
    {
        using var h = Seeded(1200, batch: 500);
        h.Faults.FailBeforeSave = 1;
        var (run, stored) = await RunAsync(h);
        Assert.Equal("Completed", run.Status);
        Assert.Equal(run.RecordCount, stored);
        Assert.Equal((int)Math.Ceiling(run.RecordCount / 500.0) + 1, h.Faults.RecordSaves);   // every batch once + the one retry
    }

    [Fact]
    public async Task ALostAnswerAfterTheBatchReallyCommitted_IsRetriedWithoutDuplicatingOrLosingRecords()
    {
        using var h = Seeded(900, batch: 500);
        h.Faults.FailAfterCommit = 1;                                           // the commit happened, the caller saw a connection error
        var (run, stored) = await RunAsync(h);
        Assert.Equal("Completed", run.Status);
        Assert.Equal(run.RecordCount, stored);
        using var context = h.CreateContext();
        Assert.Equal(run.RecordCount, await context.CollectionsReviewRecords.Where(r => r.CollectionsReviewRunId == run.RunId).Select(r => r.RecordKey).Distinct().CountAsync());
    }

    [Fact]
    public async Task AFailurePastTheRetryBudget_ReportsFailedWithAClearReason_PublishesNothing_AndKeepsThePreviousData()
    {
        using var h = Seeded(10);
        var first = (await RunAsync(h)).Run;                                    // a good published run exists
        Assert.Equal("Completed", first.Status);
        h.Faults.Persistent = true;
        var (run, stored) = await RunAsync(h);
        Assert.Equal("Failed", run.Status);
        Assert.Contains("database connection was lost or timed out after 3 attempt(s)", run.Error);
        Assert.Contains("previous review data is still in use", run.Error);
        Assert.Equal(0, stored);                                                // no half-written records of the failed run
        Assert.Equal(3, h.Faults.RecordSaves - 1);                              // exactly the bounded attempts (minus the first run's one save)
        using var scope = h.NewScope();
        var current = (await scope.Query.GetCurrentRunAsync(h.Manager, CancellationToken.None)).Value!;
        Assert.Equal("Failed", current.Status);                                 // the failure is visible, not hidden behind the old run
        Assert.Equal(first.RunId, (await scope.Store.GetCurrentRunAsync(CancellationToken.None))!.CollectionsReviewRunId);   // and the previous data is still the published one
    }

    [Fact]
    public async Task ANonTransientError_IsNeverRetried()
    {
        using var h = Seeded(5);
        h.Faults.Make = () => new InvalidOperationException("constraint");
        h.Faults.Persistent = true;
        var (run, _) = await RunAsync(h);
        Assert.Equal("Failed", run.Status);
        Assert.Contains("InvalidOperationException", run.Error);
        Assert.Equal(1, h.Faults.RecordSaves);                                  // one attempt only
    }

    [Fact]
    public async Task Retries_DoNotWeakenTheFinancialReconciliationGate()
    {
        using var h = Seeded(5);
        h.Campaign.FinancialSourceValidated = false;
        h.Faults.FailBeforeSave = 1;
        var (run, stored) = await RunAsync(h);
        Assert.Equal("Completed", run.Status);
        Assert.False(run.SourceReconciled);
        using var scope = h.NewScope();
        var summary = (await scope.Query.SummarizeAsync(h.Manager, new SelectionRequest(new ReviewFilter(ReminderType: "CurrentMonth"), ReviewQueryService.ModeAllMatching), CancellationToken.None)).Value!;
        Assert.False(summary.SourceReconciled);                                 // the gate is recorded on the run; the approval refuses on it
        var refused = await scope.Dispatch.ConfirmAsync(h.Manager, new ConfirmDispatchRequest(new SelectionRequest(new ReviewFilter(ReminderType: "CurrentMonth"), ReviewQueryService.ModeAllMatching), summary.Count, summary.Fingerprint, "retry-gate", true), CancellationToken.None);
        Assert.Equal(TigerCS.Application.Modules.Collections.Services.CollectionsOutcome.InvalidRequest, refused.Outcome); Assert.Contains("not reconciled", refused.Detail);
    }

    [Fact]
    public void OnlyConnectionLevelSqlErrors_AreTransient()
    {
        var policy = new ReviewWriteRetryPolicy();
        foreach (var number in new[] { -1, -2, 10053, 10054, 10060, 233, 1205, 40613 }) Assert.True(policy.IsTransient(SqlError(number)), number.ToString());
        foreach (var number in new[] { 2601, 2627, 515, 547, 8152 }) Assert.False(policy.IsTransient(SqlError(number)), number.ToString());
        Assert.True(policy.IsTransient(new DbUpdateException("x", SqlError(10060))));        // wrapped by EF
        Assert.True(policy.IsTransient(new TimeoutException()));
        Assert.False(policy.IsTransient(new InvalidOperationException()));
        Assert.Equal(10060, ReviewWriteRetryPolicy.NumberOf(new DbUpdateException("x", SqlError(10060))));
    }

    /// <summary>A real SqlException with the given number (the type has no public constructor).</summary>
    private static SqlException SqlError(int number)
    {
        var flags = BindingFlags.NonPublic | BindingFlags.Instance;
        var errorCtor = typeof(SqlError).GetConstructors(flags).OrderByDescending(c => c.GetParameters().Length).First();
        var args = errorCtor.GetParameters().Select(p => p.ParameterType == typeof(int) ? (object)number
            : p.ParameterType == typeof(string) ? "test" : p.ParameterType.IsValueType ? Activator.CreateInstance(p.ParameterType)! : null!).ToArray();
        // The first int parameter is the error number; later ints (line number) stay 0.
        var firstInt = errorCtor.GetParameters().ToList().FindIndex(p => p.ParameterType == typeof(int));
        for (var i = 0; i < args.Length; i++) if (errorCtor.GetParameters()[i].ParameterType == typeof(int) && i != firstInt) args[i] = 0;
        var error = (SqlError)errorCtor.Invoke(args);
        var collection = (SqlErrorCollection)Activator.CreateInstance(typeof(SqlErrorCollection), true)!;
        typeof(SqlErrorCollection).GetMethod("Add", flags)!.Invoke(collection, [error]);
        var create = typeof(SqlException).GetMethods(BindingFlags.NonPublic | BindingFlags.Static).First(m => m.Name == "CreateException" && m.GetParameters().Length == 2 && m.GetParameters()[1].ParameterType == typeof(string));
        return (SqlException)create.Invoke(null, [collection, "16.0"])!;
    }
}
