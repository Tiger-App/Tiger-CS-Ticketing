namespace TigerCS.Application.Modules.Ticketing.Abstractions;

/// <summary>
/// Makes sure only ONE reconciliation run is active at a time across every process that shares the database (IIS overlapped recycle,
/// two web-garden workers, a second server). Disposing the returned handle releases the lock.
/// </summary>
public interface ICallCenterReconciliationRunLock
{
    /// <summary>The lock handle, or null when another run holds it (the caller skips this run; it never waits).</summary>
    Task<IAsyncDisposable?> TryAcquireAsync(CancellationToken cancellationToken = default);
}
