using TigerCS.Domain.Modules.Ticketing;

namespace TigerCS.Application.Modules.Ticketing.Services;

/// <summary>
/// ISSUE-011's approved decision (Executive-Decisions.md row 17): a ticket may
/// be reopened within a fixed window — 7 days by default, configurable
/// (<c>Ticketing:ReopenWindowDays</c>, bound at DI registration), never
/// hard-coded at call sites. Beyond the window a new linked ticket is created
/// instead (BR-020) — the window check therefore yields a distinct outcome,
/// never a generic "not eligible".
///
/// <para>
/// <b>The window is measured from closure, not from resolution.</b> Under the
/// approved Closed-only rule the reopenable event is the close, so the clock
/// starts at the moment the ticket reached <see cref="TicketStatus.Closed"/>.
/// A Closed ticket carries no closure timestamp of its own, so callers read it
/// from the lifecycle history that Close already writes — the
/// <c>TicketStatusHistory</c> row for the transition into Closed
/// (<c>ITicketStatusHistoryRepository.GetLatestTransitionIntoAsync</c>) —
/// rather than substituting the resolution timestamp, which for a ticket
/// resolved long before it was closed would silently shorten the window.
/// </para>
/// </summary>
/// <param name="WindowDays">How many days after closure a reopen stays allowed.</param>
public sealed record ReopenPolicy(int WindowDays)
{
    public const int DefaultWindowDays = 7;

    public static readonly ReopenPolicy Default = new(DefaultWindowDays);

    public TimeSpan Window => TimeSpan.FromDays(WindowDays);

    /// <summary>
    /// The window rule on its own, given the moment the ticket was closed.
    /// Inclusive at the boundary: the last allowed day still reopens.
    /// </summary>
    public bool IsWithinWindow(DateTime closedAtUtc, DateTime nowUtc) => closedAtUtc + Window >= nowUtc;

    /// <summary>
    /// The single lifecycle-eligibility rule every Reopen surface shares —
    /// the authoritative check in <c>TicketLifecycleAppService.ReopenAsync</c>
    /// and the display-eligibility flags on detail/history DTOs both call
    /// this, so the button and the action can never disagree on the rule.
    ///
    /// <para>
    /// Three conditions, all lifecycle: the ticket is <b>Closed</b> (the
    /// approved rule's only reopenable status — Resolved is not), it was
    /// closed as <see cref="ResolutionOutcome.Resolved"/> (Cancelled,
    /// Rejected and Duplicate are terminal dispositions — except a Cancelled
    /// that is the chatbot inactivity closure, which the customer did not
    /// choose and an agent may reopen), and closure falls
    /// inside the window. Permission (<c>TicketRoleSets.Reopen</c> plus the
    /// caller's access to the ticket) and the request type's
    /// <c>AllowReopen</c> capability are deliberately not part of it — they
    /// are checked by the endpoint, never predicted on a read.
    /// </para>
    /// </summary>
    /// <param name="status">The ticket's current status.</param>
    /// <param name="resolutionOutcome">The ticket's live <c>ResolutionOutcome</c> byte, as set when it was resolved.</param>
    /// <param name="closedAtUtc">When the ticket reached Closed, from lifecycle history; null when that is unknown or it is not closed.</param>
    /// <param name="nowUtc">The evaluation moment.</param>
    /// <param name="closedForCustomerInactivity">
    /// Whether the ticket's <i>current</i> resolution is the chatbot
    /// inactivity closure (<c>TicketResolution.ClosedForCustomerInactivity</c>).
    /// It widens the rule by exactly one case — Cancelled <i>and</i> closed for
    /// inactivity — and nothing else: the Closed status, the window and the
    /// permission checks all still apply.
    /// </param>
    public bool IsReopenEligible(
        TicketStatus status, byte? resolutionOutcome, DateTime? closedAtUtc, DateTime nowUtc,
        bool closedForCustomerInactivity = false) =>
        status is TicketStatus.Closed
        && IsReopenableOutcome(resolutionOutcome, closedForCustomerInactivity)
        && closedAtUtc is { } closedAt
        && IsWithinWindow(closedAt, nowUtc);

    /// <summary>
    /// <b>Closed is terminal on every channel (PR #292).</b> No outcome is reopenable any more: not Resolved, not the chatbot-inactivity Cancelled, and not through a
    /// Reopen Approval. A customer who writes again after closure gets a new linked ticket (BR-020) - the closed ticket keeps its owner, ClosedBy, ClosedAt and SLA result.
    /// Every reopen surface (direct Reopen, Reopen Approval request/grant, the display flags) funnels through this one rule, so lifting the restriction later is a one-line change here.
    /// The parameters are kept so callers and the persisted outcome vocabulary are untouched.
    /// </summary>
    public static bool IsReopenableOutcome(byte? resolutionOutcome, bool closedForCustomerInactivity) => false;
}
