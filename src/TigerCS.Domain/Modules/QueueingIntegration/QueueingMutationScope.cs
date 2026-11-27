namespace TigerCS.Domain.Modules.QueueingIntegration;

/// <summary>
/// The domain's last line of defence for the one-way mirror: a queue-managed ticket's lifecycle and assignment may only change while the Queueing System
/// integration is applying one of its events. The application layer opens this scope together with its own <c>IntegrationEventScope</c>; every lifecycle /
/// assignment mutator on <see cref="Ticketing.Ticket"/> calls <see cref="Ticketing.Ticket.EnsureQueueMutationAllowed"/> first, so a job, a service or a screen that
/// forgot its own guard still cannot change a mirrored ticket. An <see cref="AsyncLocal{T}"/>, so concurrent requests never see each other's scope.
/// </summary>
public static class QueueingMutationScope
{
    private static readonly AsyncLocal<int> Depth = new();

    /// <summary>True only while the integration is applying a Queueing System event on this logical call chain.</summary>
    public static bool IsOpen => Depth.Value > 0;

    /// <summary>Opens the scope; dispose to close it (scopes nest).</summary>
    public static IDisposable Open()
    {
        Depth.Value++;
        return new Releaser();
    }

    private sealed class Releaser : IDisposable
    {
        private bool _disposed;

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            Depth.Value = Math.Max(0, Depth.Value - 1);
        }
    }
}
