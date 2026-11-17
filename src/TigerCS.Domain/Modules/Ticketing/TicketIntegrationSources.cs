namespace TigerCS.Domain.Modules.Ticketing;

/// <summary>The external systems that can own a ticket's lifecycle. Stable codes: they are stored on <see cref="Ticket.IntegrationSource"/>.</summary>
public static class TicketIntegrationSources
{
    /// <summary>The Queueing System (QR-code queue tickets). It is the source of truth for the lifecycle of the tickets it originates.</summary>
    public const string QueueingSystem = "QueueingSystem";
}
