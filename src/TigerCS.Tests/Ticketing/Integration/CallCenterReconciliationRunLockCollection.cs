namespace TigerCS.Tests.Ticketing.Integration;

/// <summary>
/// <see cref="TigerCS.Infrastructure.BackgroundJobs.CallCenterReconciliationRunLock"/> has a process-wide static gate (one reconciliation at a time per process). Test classes that
/// build the real lock are placed in this collection so they run one after another; they otherwise contend for the gate and a lock the test expects to get is refused.
/// </summary>
[CollectionDefinition(Name)]
public sealed class CallCenterReconciliationRunLockCollection
{
    public const string Name = "CallCenterReconciliationRunLock";
}
