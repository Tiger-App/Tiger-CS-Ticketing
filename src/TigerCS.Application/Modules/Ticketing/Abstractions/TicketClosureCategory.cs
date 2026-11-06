namespace TigerCS.Application.Modules.Ticketing.Abstractions;

/// <summary>
/// The four Dashboard closure KPIs, which are also the ticket queue's
/// <c>closureCategory</c> filter — so a card and its drill-down list are
/// computed by one predicate. Every category covers currently
/// <b>Closed</b> tickets only, counted once per ticket.
///
/// <para>
/// <b>Reviewed / NotReviewed</b> split the closed tickets by whether the
/// customer submitted a satisfaction review (<c>TicketReviews</c>) for the
/// ticket's <i>current</i> cycle (<c>ReopenCycle = Ticket.ReopenCount</c>).
/// Reviews flagged <c>IsTest</c> (Test-mode links) are not customer feedback
/// and are ignored, exactly as production satisfaction reporting ignores
/// them. An invitation without a submission is not a review.
/// </para>
///
/// <para>
/// <b>ClosedByAi / ClosedByAgent</b> are decided by the ticket's most recent
/// closing audit entry (see <see cref="TicketClosureAuditActions"/>):
/// <c>AutoCloseByAi</c> is the Genesys AI closure, <c>Close</c> is a person
/// closing the ticket. Every other closure — chatbot inactivity, document
/// delivery — is neither, and a reopened ticket is judged by how it was
/// closed the last time.
/// </para>
/// </summary>
public enum TicketClosureCategory
{
    Reviewed = 1,
    NotReviewed = 2,
    ClosedByAi = 3,
    ClosedByAgent = 4
}

/// <summary>
/// The <c>AuditEntries.Action</c> values the ticket lifecycle writes when a
/// ticket reaches Closed (<c>EntityType</c> = <see cref="EntityType"/>,
/// <c>EntityId</c> = the ticket id). These are the values
/// <c>TicketLifecycleAppService</c> already writes; nothing new is recorded.
/// </summary>
public static class TicketClosureAuditActions
{
    public const string EntityType = "Ticket";

    /// <summary>A person closed the ticket (CS-layer role, <c>CloseAsync</c>).</summary>
    public const string ClosedByAgent = "Close";

    /// <summary>The Genesys AI finished the conversation (<c>closedByAi</c>).</summary>
    public const string ClosedByAi = "AutoCloseByAi";

    /// <summary>Chatbot customer-inactivity closure — a system closure, never attributed to the AI.</summary>
    public const string ClosedForCustomerInactivity = "AutoCloseCustomerInactivity";

    /// <summary>Document-delivery workflow closure — a system closure, never attributed to the AI.</summary>
    public const string ClosedForDocumentDelivery = "AutoCloseDocumentDelivery";

    /// <summary>AI-only time-limit closure (no human ever involved, open longer than the configured hours) — a system closure, never attributed to the AI's own completion.</summary>
    public const string ClosedForAiOnlyTimeout = "AutoCloseAiOnlyTimeout";

    /// <summary>Every action that closes a ticket, so "the latest closure" considers all of them.</summary>
    public static readonly IReadOnlyList<string> AllClosures =
        [ClosedByAgent, ClosedByAi, ClosedForCustomerInactivity, ClosedForDocumentDelivery, ClosedForAiOnlyTimeout];
}
