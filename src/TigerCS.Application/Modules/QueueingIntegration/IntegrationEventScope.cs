namespace TigerCS.Application.Modules.QueueingIntegration;

/// <summary>
/// The ambient context of one integration event being applied. While it is open (a) the clock every TigerCS service reads reports the event's
/// <b>original</b> time, so ticket creation, SLA periods, assignment rows, status history and audit entries carry the moment the thing happened in
/// the Queueing System rather than the moment it was delivered; and (b) the manual-change guard knows the change is the integration's own.
/// An <see cref="AsyncLocal{T}"/>, so concurrent requests never see each other's scope.
/// </summary>
public static class IntegrationEventScope
{
    private static readonly AsyncLocal<ScopeState?> Current = new();

    public static bool IsActive => Current.Value is not null;

    /// <summary>The event's effective time, or null outside a scope.</summary>
    public static DateTime? EffectiveAtUtc => Current.Value?.EffectiveAtUtc;

    /// <summary>The real time the scope was opened, or null outside a scope. For the integration's own bookkeeping (when it was processed, when to retry), which must never be stamped with the event's original time.</summary>
    public static DateTime? RealAtUtc => Current.Value?.RealAtUtc;

    public static IDisposable Begin(DateTime effectiveAtUtc, DateTime realAtUtc)
    {
        var previous = Current.Value;
        Current.Value = new ScopeState(DateTime.SpecifyKind(effectiveAtUtc, DateTimeKind.Utc), DateTime.SpecifyKind(realAtUtc, DateTimeKind.Utc));
        // The domain's own last line of defence (Ticket.EnsureQueueMutationAllowed) opens for exactly as long as this scope does.
        var domain = TigerCS.Domain.Modules.QueueingIntegration.QueueingMutationScope.Open();
        return new Releaser(previous, domain);
    }

    private sealed record ScopeState(DateTime EffectiveAtUtc, DateTime RealAtUtc);

    private sealed class Releaser(ScopeState? previous, IDisposable domainScope) : IDisposable
    {
        public void Dispose()
        {
            Current.Value = previous;
            domainScope.Dispose();
        }
    }
}

/// <summary>
/// The application clock, with one addition: inside an <see cref="IntegrationEventScope"/> it answers the event's original time. Everywhere else it is
/// exactly the wrapped provider, so no other flow (Genesys included) is affected.
/// </summary>
public sealed class IntegrationAwareTimeProvider(TimeProvider inner) : TimeProvider
{
    public override DateTimeOffset GetUtcNow() =>
        IntegrationEventScope.EffectiveAtUtc is { } effective ? new DateTimeOffset(effective, TimeSpan.Zero) : inner.GetUtcNow();

    public override TimeZoneInfo LocalTimeZone => inner.LocalTimeZone;

    public override long GetTimestamp() => inner.GetTimestamp();

    public override long TimestampFrequency => inner.TimestampFrequency;
}

/// <summary>The one rule behind refusing a person's manual change to a queue-managed ticket.</summary>
public static class QueueManagedTicketGuard
{
    /// <summary>
    /// True when the change must be refused: the ticket belongs to the Queueing System and the caller is not the integration applying an event. Always - the
    /// former <c>ManualChangePolicy: Allow</c> is ignored (TigerCS is a one-way mirror; <paramref name="options"/> is kept only so existing call sites compile).
    /// </summary>
    public static bool Blocks(TigerCS.Domain.Modules.Ticketing.Ticket ticket, QueueingIntegrationOptions? options) =>
        ticket.IsQueueManaged && !IntegrationEventScope.IsActive;
}
