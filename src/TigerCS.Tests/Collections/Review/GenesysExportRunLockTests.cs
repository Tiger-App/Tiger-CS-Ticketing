using Microsoft.Extensions.Configuration;
using TigerCS.Infrastructure.Modules.Collections;

namespace TigerCS.Tests.Collections.Review;

/// <summary>
/// The export lock: one export records its repeat check and audit at a time. Without a database connection string (tests) only the in-process gate applies; with SQL Server the same lock is also
/// held on the database as a session-scoped application lock, so two application servers cannot record at once either.
/// </summary>
public sealed class GenesysExportRunLockTests
{
    [Fact]
    public async Task OneExportRecordsAtATime_AWaiterGetsTheLockWhenTheHolderLetsGo_AndATimedOutWaiterDoesNot()
    {
        var gate = new GenesysExportRunLock(new ConfigurationBuilder().Build());                  // no connection string: the in-process gate only
        var first = await gate.TryAcquireAsync(TimeSpan.FromSeconds(1), CancellationToken.None);
        Assert.NotNull(first);
        Assert.Null(await gate.TryAcquireAsync(TimeSpan.FromMilliseconds(50), CancellationToken.None));   // the holder has it: a short wait gives up
        var waiting = gate.TryAcquireAsync(TimeSpan.FromSeconds(5), CancellationToken.None);
        await first!.DisposeAsync();                                                              // the holder finishes its audit
        var second = await waiting;
        Assert.NotNull(second);                                                                   // the waiter gets it in turn
        await second!.DisposeAsync();
        var third = await gate.TryAcquireAsync(TimeSpan.FromSeconds(1), CancellationToken.None);
        Assert.NotNull(third); await third!.DisposeAsync();
    }
}
