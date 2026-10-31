using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using TigerCS.Application.Modules.Administration.Dto;
using TigerCS.Application.Modules.ClassificationAndRouting.Dto;
using TigerCS.Application.Modules.IdentityAndAccess.Dto;
using TigerCS.Application.Modules.Notifications.Services;
using TigerCS.Application.Modules.SlaAndEscalation.Dto;
using TigerCS.Application.Modules.Ticketing.Dto;
using TigerCS.Application.Modules.Ticketing.Services;
using TigerCS.Domain.Modules.SlaAndEscalation;
using TigerCS.Domain.Modules.Ticketing;
using TigerCS.Web.Models;
using TigerCS.Web.Services;
using TigerCS.Web.Services.Api;
using TigerCS.Web.Services.Auth;

namespace TigerCS.Web.Pages;

/// <summary>
/// One entry in the Activity feed — built from real, separately-fetched
/// facts: the ticket's own CreatedAtUtc, its notes, its escalations and, since
/// the approved Reopen rule, its lifecycle history
/// (<c>GET /api/tickets/{id}/history</c>) and typed workflow events. Nothing
/// here is synthesized.
/// </summary>
/// <param name="Detail">A second line of already-resolved context (e.g. a reopen's department move and new SLA deadline). Never parsed by the view.</param>
public sealed record ActivityEntry(
    DateTime TimestampUtc, string Kind, string Actor, string Description, string? Note, bool IsHuman, string? Detail = null);

public sealed class TicketDetailsModel(
    TicketsApiClient ticketsApiClient,
    TicketSlaApiClient slaApiClient,
    UsersApiClient usersApiClient,
    TicketNameResolver nameResolver,
    CategoriesApiClient? categoriesApiClient = null,
    RequestTypesApiClient? requestTypesApiClient = null) : PageModel
{
    public long TicketId { get; private set; }

    /// <summary>
    /// The Tickets view (tab, filters, page) the agent came from, as the
    /// Tickets page remembered it — Ticket Details is a child workspace of
    /// Tickets, and its breadcrumb and back links lead back to that exact
    /// list. Falls back to the bare Tickets workspace when nothing is
    /// remembered (a deep link, a fresh session).
    /// </summary>
    public TicketsContext ReturnContext { get; private set; } = TicketsContext.Default;
    public ApiOutcome Outcome { get; private set; }
    public TicketDetailDto? Ticket { get; private set; }
    public TicketSlaSummaryResponseDto? Sla { get; private set; }
    public IReadOnlyList<TicketNoteResponseDto> Notes { get; private set; } = [];
    public IReadOnlyList<TicketEscalationResponseDto> Escalations { get; private set; } = [];

    /// <summary>
    /// Customer History — this ticket's customer's other tickets, verified
    /// (CrmBuyerCustomerId) or unverified (phone-snapshot fallback), the
    /// current ticket already excluded. Null only when the call itself
    /// failed; an empty <see cref="CustomerHistoryDto.Tickets"/> is a normal,
    /// successful "no previous tickets" result. Never a live CRM call.
    /// </summary>
    public CustomerHistoryDto? CustomerHistory { get; private set; }

    /// <summary>Approvals / Dependencies (Workflow/Automation phase 3) — cycles, requestable requirements, and the derived maintenance/prerequisite states. Null when the call failed; a view with empty lists is the normal "nothing configured" result and renders no section.</summary>
    public TicketApprovalsViewDto? Approvals { get; private set; }

    /// <summary>
    /// Conversation History (Genesys integration phase 1) — every call/chat
    /// recorded against this ticket, oldest first, each with its transcript.
    /// Empty is the normal result for a ticket created by hand; null only
    /// when the call itself failed.
    /// </summary>
    public IReadOnlyList<TicketInteractionDto> Interactions { get; private set; } = [];

    /// <summary>True when the interactions call failed, so the tab says so instead of implying the ticket has no conversations.</summary>
    public bool InteractionsUnavailable { get; private set; }

    /// <summary>Customer satisfaction review cycles; empty when none was requested or the viewer may not see them.</summary>
    public IReadOnlyList<TigerCS.Application.Modules.CustomerReviews.Dto.TicketReviewCycleDto> Reviews { get; private set; } = [];

    public bool ReviewsUnavailable { get; private set; }

    // ---- The customer behind this ticket (the Customer summary card). ----
    // Identity comes from the ticket's own persisted facts as the Api's
    // customer-history read resolves them: CRM Buyer id, else the external
    // verification pair, else the intake phone — the same key the Customers
    // directory uses, so the card's link opens exactly that customer.

    /// <summary>The Customers directory key of this ticket's customer, or null when the ticket carries no customer identity at all.</summary>
    public string? CustomerKey { get; private set; }

    /// <summary>The Customer Profile for this ticket's customer, carrying this ticket so the profile can lead back — null when there is no identity to open.</summary>
    public string? CustomerProfileHref =>
        CustomerKey is null ? null : $"/Customers/{Uri.EscapeDataString(CustomerKey)}?fromTicket={TicketId}";

    /// <summary>The best persisted customer name: the CRM Buyer snapshot, else what the originating call/chat reported, else the history's own name.</summary>
    public string? CustomerDisplayName { get; private set; }

    /// <summary>The customer's phone: the intake snapshot the history read reports, else the originating interaction's caller number.</summary>
    public string? CustomerPhone { get; private set; }
    public string? DepartmentName { get; private set; }
    public string? OwnerName { get; private set; }

    /// <summary>Names for the people the handling summary and transfer entries mention (first handler, resolver, receivers); anyone not resolvable falls back to a short id.</summary>
    public IReadOnlyDictionary<Guid, string> EmployeeNames { get; private set; } = new Dictionary<Guid, string>();

    public string? FirstHandledByName => Ticket?.FirstHandledByName ?? NameFor(Ticket?.FirstHandledByEmployeeId);

    public string? ResolvedByName => Ticket?.ResolvedByName ?? NameFor(Ticket?.ResolvedByEmployeeId);

    private string? NameFor(Guid? employeeId) =>
        employeeId is { } id ? (EmployeeNames.TryGetValue(id, out var name) ? name : $"Employee #{id.ToString()[..8]}") : null;
    public IReadOnlyList<DepartmentUserDto> AssignableEmployees { get; private set; } = [];

    /// <summary>
    /// How the current department's member list loaded. Only
    /// <see cref="ApiOutcome.Success"/> with an empty list means "this
    /// department has no active members"; anything else is a failed request
    /// and is said so — never shown as an empty directory.
    /// </summary>
    public ApiOutcome AssignableEmployeesOutcome { get; private set; } = ApiOutcome.Unknown;

    /// <summary>Whether the viewer may assign outside the ticket's department (CS Manager / override) and is therefore offered the whole directory.</summary>
    public bool CanAssignAcrossDepartments { get; private set; }

    /// <summary>The cross-department directory (loaded only for <see cref="CanAssignAcrossDepartments"/> viewers), one row per employee with all their departments.</summary>
    public IReadOnlyList<AssignableUserDto> AssignableUsers { get; private set; } = [];

    /// <summary>How the cross-department directory loaded — distinguishes "nobody" from "could not load" / "not permitted".</summary>
    public ApiOutcome AssignableUsersOutcome { get; private set; } = ApiOutcome.Unknown;

    /// <summary>
    /// The departments the directory is grouped by for the assign control:
    /// the ticket's current department first, then every other department
    /// that has at least one assignable member, by name.
    /// </summary>
    public IReadOnlyList<(int DepartmentId, string Name, IReadOnlyList<AssignableUserDto> Members)> AssignableByDepartment { get; private set; } = [];

    /// <summary>
    /// Whether the viewer holds transfer authority (the Api's own CS Manager
    /// role set, or the System Administrator override) — the only case in
    /// which a Transfer control, and any department to transfer to, is shown.
    /// </summary>
    public bool CanTransfer { get; private set; }

    /// <summary>
    /// The departments the Transfer picker offers, by name: the ACTIVE
    /// directory minus the ticket's current department (the Api rejects both
    /// an inactive target and a no-op transfer), and only ever populated for a
    /// viewer who <see cref="CanTransfer"/>. The picker binds each name to its
    /// existing DepartmentId, so nobody types a number and the transfer
    /// contract is unchanged.
    /// </summary>
    public IReadOnlyList<DepartmentDto> TransferTargets { get; private set; } = [];

    /// <summary>True when the viewer may transfer but the department directory could not be loaded — the form says so instead of offering an empty picker.</summary>
    public bool TransferTargetsUnavailable { get; private set; }

    /// <summary>
    /// Departments the Reopen form offers. Unlike <see cref="TransferTargets"/>
    /// this deliberately INCLUDES the ticket's current department: reopening
    /// into the department that closed it is a legitimate choice the approved
    /// rule allows, and the API accepts it. Populated only for a viewer who
    /// <see cref="CanReopen"/> and only when the ticket is reopen-eligible.
    /// </summary>
    public IReadOnlyList<DepartmentDto> ReopenTargets { get; private set; } = [];

    /// <summary>True when the Reopen form should be offered but the department directory could not be loaded — it says so rather than offering an empty picker.</summary>
    public bool ReopenTargetsUnavailable { get; private set; }

    /// <summary>Whether the viewer's roles allow Reopen at all — the Api still decides, including whether they may act on this ticket.</summary>
    public bool CanReopen { get; private set; }

    /// <summary>
    /// The viewer-and-ticket facts every remaining action affordance is
    /// decided from — built once per render, in <see cref="LoadActionAffordances"/>,
    /// from the viewer's own claims and the server-supplied department
    /// memberships. Null when the ticket could not be loaded or nobody is
    /// signed in, which makes every check below false.
    /// </summary>
    public TicketActionContext? ActionContext { get; private set; }

    /// <summary>Whether an Assign control is worth rendering — mirrors TicketAssignmentAppService.AssignAsync's permission rule.</summary>
    public bool CanAssign { get; private set; }

    /// <summary>
    /// Whether a Change Status control is worth rendering: the permission
    /// half — TicketLifecycleAppService's IsCurrentOwnerOrDepartmentAuthorityAsync,
    /// mirrored by <see cref="TicketActions.CanChangeStatus"/> — AND the
    /// ticket having somewhere to go.
    ///
    /// <para>
    /// The lifecycle half is here rather than in <see cref="TicketActions"/>
    /// because it is not a permission: on a Resolved or Closed ticket the
    /// working sub-machine has no target at all, so the control would open a
    /// picker with nothing legal in it. It is read straight off the domain's
    /// own transition table (<see cref="StatusTargets"/>), not re-derived —
    /// a second copy of the rule is exactly what let the picker offer Open
    /// and Pending Third Party while the domain refused both.
    /// </para>
    /// </summary>
    public bool CanChangeStatus { get; private set; }

    /// <summary>
    /// Exactly the statuses <c>POST /api/tickets/{id}/status</c> would accept
    /// from this ticket's current status, from the domain's single transition
    /// table. Empty on a Resolved or Closed ticket — and on a status the
    /// client does not recognize — which hides the Change Status action
    /// entirely.
    ///
    /// <para>
    /// A ticket sitting in the legacy PendingThirdParty status therefore
    /// offers In Progress and nothing else: its one sanctioned way out.
    /// Nothing offers PendingThirdParty itself, because no arm of the table
    /// produces it.
    /// </para>
    /// </summary>
    public IReadOnlyList<TicketStatus> StatusTargets { get; private set; } = [];

    /// <summary>Whether a Resolve control is worth rendering — mirrors TicketLifecycleAppService.IsResolveAuthorizedAsync, role set AND the ownership/department half.</summary>
    public bool CanResolve { get; private set; }

    /// <summary>Whether a Close control is worth rendering — mirrors TicketLifecycleAppService.CloseAsync's role-only rule. The ticket being Resolved is a separate, lifecycle condition.</summary>
    public bool CanClose { get; private set; }

    /// <summary>Whether an Escalate control is worth rendering — true when either escalation tier is open to the viewer.</summary>
    public bool CanEscalate { get; private set; }

    /// <summary>Whether Level 4 may be OFFERED inside that control — CS Manager/GM only, per MVP-ERD.md §2.17.</summary>
    public bool CanEscalateToLevel4 { get; private set; }

    /// <summary>Whether the "Request priority downgrade" control is worth rendering - mirrors PriorityDowngradeAppService.RequestAsync.</summary>
    public bool CanRequestDowngrade { get; private set; }

    /// <summary>The ticket's Pending downgrade request, if any (a downgrade never takes effect until it is approved).</summary>
    public PriorityDowngradeRequestResponseDto? PendingDowngrade { get; private set; }

    /// <summary>Whether the viewer may approve/reject the pending request: a Department Head of this department (or above) who is not the requester.</summary>
    public bool CanDecideDowngrade { get; private set; }

    /// <summary>Priorities lower than the ticket's current one - the only valid downgrade targets.</summary>
    public IReadOnlyList<(byte PriorityId, string Label)> DowngradeTargets { get; private set; } = [];

    /// <summary>The customer's name as people read it; a missing or placeholder name is stated, never printed.</summary>
    public string CustomerNameLabel => CustomerDisplayName
        ?? (CustomerKey is null ? "Customer not identified"
            : CustomerKey.StartsWith("phone:", StringComparison.Ordinal) ? "Unnamed caller"
            : $"{TicketDisplay.LookupSourceLabel(Ticket?.CustomerVerificationSource ?? "Crm")} customer");

    /// <summary>How the customer's identity was established, in user-facing words.</summary>
    public string CustomerVerificationLabel
    {
        get
        {
            if (Ticket is not { } t)
            {
                return "Customer not identified";
            }

            if (t.CrmBuyerUnitId is not null)
            {
                return "Verified via Tiger CRM";
            }

            if (!string.IsNullOrWhiteSpace(t.CustomerVerificationSource))
            {
                return $"Verified via {TicketDisplay.LookupSourceLabel(t.CustomerVerificationSource)}";
            }

            return !string.IsNullOrWhiteSpace(t.ManualProjectName) || !string.IsNullOrWhiteSpace(t.ManualUnitNumber)
                ? "Manual entry / Not externally verified"
                : TicketDisplay.VerificationStatusLabel(t.VerificationStatus);
        }
    }

    private TicketGuidance? guidance;

    /// <summary>
    /// The "What needs your attention?" card: the next step for this viewer, urgent conditions
    /// and the reasons an action is unavailable — all worded from state and permission flags
    /// this page already derives (no new rule). Built once per render, after the page has loaded.
    /// </summary>
    public TicketGuidance? Guidance => Ticket is null ? null : guidance ??= BuildGuidance(Ticket);

    private TicketGuidance BuildGuidance(TicketDetailDto t)
    {
        const string reopenApproval = "ReopenApproval";
        var now = DateTime.UtcNow;
        var closed = t.TicketStatus == TicketNextAction.Closed;
        var approvals = Approvals;

        var sla = Sla;
        var slaBreached = sla is not null && TicketDisplay.SlaBadgeText(sla, now).CssKey == "breached";
        string? slaText = null;
        if (slaBreached && sla is not null)
        {
            var (kind, due) = sla.ResolutionBreached ? ("Resolution", sla.ResolutionDueAtUtc) : ("First-response", sla.FirstResponseDueAtUtc);
            slaText = due is { } dueAt
                ? $"{kind} SLA is overdue by {TicketDisplay.FormatDuration(now - dueAt)}."
                : $"{kind} SLA has been breached.";
        }

        var clockRunning = sla?.Explanation is not { HasActivePeriod: false };
        var firstResponsePending = sla is not null && sla.FirstHumanResponseAtUtc is null && clockRunning;

        // Approval cycles of a closed ticket matter only for the reopen request; on an open
        // ticket the reopen type is not part of day-to-day work.
        bool Relevant(string type) => closed == (type == reopenApproval);
        var pending = approvals?.Approvals
            .Where(a => a.IsCurrent && a.Status == "Pending" && Relevant(a.ApprovalType))
            .Select(a => new PendingApprovalFact(TicketDisplay.ApprovalTypeLabel(a.ApprovalType), a.TargetSummary, a.CallerCanDecide))
            .ToList() ?? [];
        var required = approvals?.RequestableApprovals
            .Where(a => Relevant(a.ApprovalType))
            .Select(a => new RequiredApprovalFact(TicketDisplay.ApprovalTypeLabel(a.ApprovalType), a.TargetSummary, a.BlocksWorkUntilApproved, a.CallerCanRequest))
            .ToList() ?? [];
        var reopenApproved = approvals?.Approvals.Any(a => a.ApprovalType == reopenApproval && a.IsCurrent && a.Status == "Approved") ?? false;

        var handling = Interactions.LastOrDefault(i => i.HandlingState is not null)?.HandlingState;

        return TicketNextAction.Build(new TicketGuidanceFacts(
            TicketStatus: t.TicketStatus,
            IsClassified: t.CategoryId is not null,
            HasOwner: t.CurrentOwnerEmployeeId is not null,
            ViewerIsOwner: ActionContext?.IsCurrentOwner ?? false,
            OwnerName: OwnerName,
            DepartmentName: TicketDisplay.AssignedDepartmentLabel(t.CurrentDepartmentId, DepartmentName),
            ReopenCount: t.ReopenCount,
            IsReopenEligible: t.IsReopenEligible,
            PendingCrmVerification: t.VerificationStatus == "PendingCrmVerification",
            HandoffState: t.HandoffState,
            HandoffRequestedAtUtc: t.HandoffRequestedAtUtc,
            HandlingState: handling,
            SlaBreached: slaBreached,
            SlaBreachText: slaText,
            FirstResponsePending: firstResponsePending,
            ResolutionClockPaused: sla?.IsCurrentlyPaused ?? false,
            ViewerIsReadOnly: ActionContext?.IsReadOnlyViewer ?? false,
            CanClassify: CanClassify,
            CanAssign: CanAssign,
            CanTransfer: CanTransfer,
            CanChangeStatus: CanChangeStatus,
            CanResolve: CanResolve,
            CanClose: CanClose,
            CanEscalate: CanEscalate,
            CanReopen: CanReopen,
            CanRequestDowngrade: CanRequestDowngrade,
            CanRecordFirstResponse: CanRecordFirstResponse,
            HasPendingDowngrade: PendingDowngrade is not null,
            CanDecideDowngrade: CanDecideDowngrade,
            PendingApprovals: pending,
            RequiredApprovals: required,
            ReopenApproved: reopenApproved,
            NowUtc: now,
            ClassifyNotice: ClassifyNotice));
    }

    /// <summary>
    /// True when the viewer created this ticket but it belongs to another department and they hold no other grant on it: they may follow it
    /// and change nothing. Every action control is hidden — the Api refuses each modification (403) regardless; this only keeps the page
    /// from offering what would be refused.
    /// </summary>
    public bool IsReadOnlyAccess => Ticket?.IsReadOnlyAccess == true;

    private void ApplyReadOnlyAccess(TicketDetailDto ticket)
    {
        if (!ticket.IsReadOnlyAccess)
        {
            return;
        }

        CanAssign = CanAssignAcrossDepartments = CanChangeStatus = CanResolve = CanClose = CanEscalate = CanEscalateToLevel4 = false;
        CanRequestDowngrade = CanDecideDowngrade = CanClassify = CanTransfer = CanReopen = false;
        StatusTargets = [];
        DowngradeTargets = [];
        TransferTargets = [];
        ReopenTargets = [];
    }

    /// <summary>The existing condition for the "Record first response" button: the clock is running, no human has replied, and the viewer may resolve or escalate.</summary>
    public bool CanRecordFirstResponse =>
        !IsReadOnlyAccess && Sla is { FirstHumanResponseAtUtc: null } sla && sla.Explanation is not { HasActivePeriod: false } && (CanResolve || CanEscalate);

    public CurrentUser? Viewer { get; private set; }
    public TicketNameResolver NameResolver => nameResolver;
    public IReadOnlyList<ActivityEntry> ActivityFeed { get; private set; } = [];

    public string? ActionError { get; private set; }
    public string? OpenSection { get; private set; }

    [TempData]
    public string? ActionSuccess { get; set; }

    [BindProperty] public AssignInput Assign { get; set; } = new();
    [BindProperty] public TransferInput Transfer { get; set; } = new();
    [BindProperty] public ClassifyInput Classify { get; set; } = new();

    /// <summary>
    /// Whether the Classify control is offered: the ticket has no Category yet (the Api's own "already classified" test), is not closed, and
    /// the viewer is not a read-only role. Display only — the Api still decides (department visibility, role set) and can answer 403/422.
    /// Classifying never creates a human handoff.
    /// </summary>
    public bool CanClassify { get; private set; }
    public IReadOnlyList<CategoryDto> ClassifyCategories { get; private set; } = [];
    public IReadOnlyList<RequestTypeOptionDto> ClassifyRequestTypes { get; private set; } = [];

    /// <summary>The Classify form's choices with each category's own request types — the dependency the form follows.</summary>
    public TicketClassifyOptions ClassifyOptions { get; private set; } = TicketClassifyOptions.Failed;

    /// <summary>Set when Classify is offered but cannot be completed (nothing configured, or the lists failed to load); the form is then replaced by this explanation.</summary>
    public ClassifyNotice? ClassifyNotice { get; private set; }

    /// <summary>The ticket's category by name, for a classified ticket — never the raw id.</summary>
    public string? CategoryName { get; private set; }

    /// <summary>The priorities the Classify form offers: the ticket's current one and anything more urgent — a lower urgency is a downgrade, which only an approved request may do.</summary>
    public IReadOnlyList<byte> ClassifyPriorities { get; private set; } = [];
    public bool ClassifyOptionsUnavailable { get; private set; }
    [BindProperty] public StatusInput Status { get; set; } = new();
    [BindProperty] public ResolveInput Resolve { get; set; } = new();
    [BindProperty] public RowVersionInput Close { get; set; } = new();
    [BindProperty] public ReopenInput Reopen { get; set; } = new();
    [BindProperty] public EscalateInput Escalate { get; set; } = new();
    [BindProperty] public DowngradeRequestInput DowngradeRequest { get; set; } = new();
    [BindProperty] public DowngradeDecisionInput DowngradeDecision { get; set; } = new();
    [BindProperty] public NoteInput Note { get; set; } = new();
    [BindProperty] public ApprovalRequestInput ApprovalRequest { get; set; } = new();
    [BindProperty] public ApprovalDecisionInput ApprovalDecision { get; set; } = new();
    [BindProperty] public WorkflowEventInput WorkflowEvent { get; set; } = new();

    public async Task<IActionResult> OnGetAsync(long id, int? reopen, CancellationToken cancellationToken)
    {
        TicketId = id;
        // ?reopen=1 arrives from a customer-workspace "Reopen" link — open
        // the reopen panel so the agent lands directly on the action.
        if (reopen == 1)
        {
            OpenSection = "reopen";
        }

        await LoadAsync(cancellationToken);
        if (Ticket is null && Outcome == ApiOutcome.NotFound)
        {
            return NotFound();
        }

        return Page();
    }

    public async Task<IActionResult> OnPostAssignAsync(long id, CancellationToken cancellationToken)
    {
        TicketId = id;
        if (!TryDecodeRowVersion(Assign.RowVersionBase64, out var rowVersion))
        {
            return await ReloadWithErrorAsync("assign", "Could not read the ticket's current version. Reloading.", cancellationToken);
        }

        // The selection is "employeeId|departmentId" — the (user, department)
        // pair the viewer picked, because a user in several departments can be
        // placed in any of them. Same department as the ticket → a plain
        // Assign. Another department → transfer-and-assign, which needs a
        // reason exactly like a standalone Transfer; the Api enforces that the
        // assignee belongs to the target department.
        if (!TryParseAssignTarget(Assign.Target, out var employeeId, out var departmentId))
        {
            return await ReloadWithErrorAsync("assign", "Choose who to assign the ticket to.", cancellationToken);
        }

        var current = await ticketsApiClient.GetByIdAsync(id, cancellationToken);
        var currentDepartmentId = current.IsSuccess && current.Value is not null ? current.Value.CurrentDepartmentId : (int?)null;

        // Handing an OWNED ticket to someone else (or to another department) must say why; taking one out of the queue
        // stays a plain assignment. The Api enforces the same rule — this just asks before posting.
        var currentOwner = current.IsSuccess ? current.Value?.CurrentOwnerEmployeeId : null;
        var isHandOver = (currentOwner is { } owner && owner != employeeId) || (departmentId is not null && departmentId != currentDepartmentId);
        if (isHandOver && Assign.ReasonCode is (null or TicketTransferReason.Unspecified or TicketTransferReason.SystemRouting))
        {
            return await ReloadWithErrorAsync("assign", "Choose why the ticket is changing hands (for example Unable to resolve or Needs escalation).", cancellationToken);
        }

        if (departmentId is null || departmentId == currentDepartmentId)
        {
            var result = await ticketsApiClient.AssignAsync(
                id, new AssignTicketRequestDto(employeeId, rowVersion, Assign.ReasonCode, string.IsNullOrWhiteSpace(Assign.Reason) ? null : Assign.Reason.Trim()),
                cancellationToken);
            return await HandleMutationAsync(result, "assign", "Ticket assigned.", cancellationToken);
        }

        if (string.IsNullOrWhiteSpace(Assign.Reason))
        {
            return await ReloadWithErrorAsync(
                "assign", "A reason is required when assigning to someone in another department (the ticket is transferred there).", cancellationToken);
        }

        var transferred = await ticketsApiClient.TransferAsync(
            id, new TransferTicketRequestDto(departmentId.Value, Assign.Reason.Trim(), rowVersion, employeeId, Assign.ReasonCode), cancellationToken);
        return await HandleMutationAsync(transferred, "assign", "Ticket transferred and assigned.", cancellationToken);
    }

    /// <summary>Parses the assign control's "employeeId|departmentId" value (departmentId optional for the single-department control).</summary>
    internal static bool TryParseAssignTarget(string? target, out Guid employeeId, out int? departmentId)
    {
        employeeId = Guid.Empty;
        departmentId = null;
        if (string.IsNullOrWhiteSpace(target))
        {
            return false;
        }

        var parts = target.Split('|', 2);
        if (!Guid.TryParse(parts[0], out employeeId) || employeeId == Guid.Empty)
        {
            return false;
        }

        if (parts.Length == 2)
        {
            if (!int.TryParse(parts[1], out var parsed) || parsed <= 0)
            {
                return false;
            }

            departmentId = parsed;
        }

        return true;
    }

    public async Task<IActionResult> OnPostTransferAsync(long id, CancellationToken cancellationToken)
    {
        TicketId = id;
        if (!TryDecodeRowVersion(Transfer.RowVersionBase64, out var rowVersion))
        {
            return await ReloadWithErrorAsync("transfer", "Could not read the ticket's current version. Reloading.", cancellationToken);
        }

        if (Transfer.ReasonCode is null or TicketTransferReason.Unspecified or TicketTransferReason.SystemRouting)
        {
            return await ReloadWithErrorAsync("transfer", "Choose why the ticket is being transferred.", cancellationToken);
        }

        var result = await ticketsApiClient.TransferAsync(
            id, new TransferTicketRequestDto(Transfer.TargetDepartmentId, Transfer.Reason, rowVersion, null, Transfer.ReasonCode), cancellationToken);
        return await HandleMutationAsync(result, "transfer", "Ticket transferred.", cancellationToken);
    }

    public async Task<IActionResult> OnPostClassifyAsync(long id, CancellationToken cancellationToken)
    {
        TicketId = id;
        if (!TryDecodeRowVersion(Classify.RowVersionBase64, out var rowVersion))
        {
            return await ReloadWithErrorAsync("classify", "Could not read the ticket's current version. Reloading.", cancellationToken);
        }

        if (Classify.CategoryId < 1 || Classify.PriorityId < 1)
        {
            return await ReloadWithErrorAsync("classify", "Choose a category and a priority.", cancellationToken);
        }

        // The request type must belong under the chosen category (the dependency the form follows), and is required whenever that
        // category has request types of its own. Re-read from configuration, never trusted from the posted form.
        var ticketResult = await ticketsApiClient.GetByIdAsync(id, cancellationToken);
        if (ticketResult.IsSuccess && ticketResult.Value is { } ticketToClassify
            && categoriesApiClient is not null && requestTypesApiClient is not null)
        {
            var categoriesTask = categoriesApiClient.GetCategoriesAsync(ticketToClassify.CurrentDepartmentId, cancellationToken);
            var requestTypesTask = requestTypesApiClient.GetOptionsAsync(ticketToClassify.CurrentDepartmentId, cancellationToken);
            await Task.WhenAll(categoriesTask, requestTypesTask);
            if (categoriesTask.Result.IsSuccess && requestTypesTask.Result.IsSuccess)
            {
                var options = TicketClassifyOptions.Build(
                    ticketToClassify.CurrentDepartmentId, categoriesTask.Result.Value ?? [], requestTypesTask.Result.Value ?? []);
                if (!Classify.RequestTypeAlreadySet)
                {
                    if (options.CategoryNeedsRequestType(Classify.CategoryId) && Classify.RequestTypeId < 1)
                    {
                        return await ReloadWithErrorAsync("classify", "Choose a request type for that category.", cancellationToken);
                    }

                    if (Classify.RequestTypeId > 0 && !options.Allows(Classify.CategoryId, Classify.RequestTypeId))
                    {
                        return await ReloadWithErrorAsync("classify", "That request type does not belong under the chosen category. Choose a request type from the list for that category.", cancellationToken);
                    }
                }
            }
        }

        var result = await ticketsApiClient.ClassifyAsync(
            id, new ClassifyTicketRequestDto(
                Classify.CategoryId, Classify.PriorityId, Classify.RequestTypeAlreadySet || Classify.RequestTypeId < 1 ? null : Classify.RequestTypeId, rowVersion), cancellationToken);
        return await HandleMutationAsync(result, "classify", "Ticket classified.", cancellationToken);
    }

    public async Task<IActionResult> OnPostChangeStatusAsync(long id, CancellationToken cancellationToken)
    {
        TicketId = id;
        if (!TryDecodeRowVersion(Status.RowVersionBase64, out var rowVersion))
        {
            return await ReloadWithErrorAsync("status", "Could not read the ticket's current version. Reloading.", cancellationToken);
        }

        // The pending reason travels with the status change (required by the
        // API for either Pending target since the structured-pending phase).
        var result = await ticketsApiClient.ChangeStatusAsync(
            id, new ChangeStatusRequestDto(Status.NewStatus, rowVersion, Status.PendingReason), cancellationToken);
        return await HandleMutationAsync(result, "status", "Status updated.", cancellationToken);
    }

    public async Task<IActionResult> OnPostRequestApprovalAsync(long id, CancellationToken cancellationToken)
    {
        TicketId = id;
        var result = await ticketsApiClient.RequestApprovalAsync(
            id, new RequestApprovalRequestDto(ApprovalRequest.ApprovalType, ApprovalRequest.Comment), cancellationToken);
        return await HandleApprovalActionAsync(result.IsSuccess, result.Outcome, result.Detail, "Approval requested.", cancellationToken);
    }

    public async Task<IActionResult> OnPostDecideApprovalAsync(long id, CancellationToken cancellationToken)
    {
        TicketId = id;
        var result = await ticketsApiClient.DecideApprovalAsync(
            id, ApprovalDecision.ApprovalId,
            new DecideApprovalRequestDto(ApprovalDecision.Decision, ApprovalDecision.Comment), cancellationToken);
        var message = string.Equals(ApprovalDecision.Decision, "Reject", StringComparison.OrdinalIgnoreCase)
            ? "Approval rejected."
            : "Approval granted.";
        return await HandleApprovalActionAsync(result.IsSuccess, result.Outcome, result.Detail, message, cancellationToken);
    }

    public async Task<IActionResult> OnPostRecordEventAsync(long id, CancellationToken cancellationToken)
    {
        TicketId = id;
        var result = await ticketsApiClient.RecordWorkflowEventAsync(
            id, new RecordWorkflowEventRequestDto(WorkflowEvent.EventType, WorkflowEvent.Note), cancellationToken);
        return await HandleApprovalActionAsync(result.IsSuccess, result.Outcome, result.Detail, "Recorded.", cancellationToken);
    }

    private async Task<IActionResult> HandleApprovalActionAsync(
        bool isSuccess, ApiOutcome outcome, string? detail, string successMessage, CancellationToken cancellationToken)
    {
        if (isSuccess)
        {
            ActionSuccess = successMessage;
            return RedirectToPage(new { id = TicketId });
        }

        ActionError = DescribeError(outcome, detail);
        OpenSection = "approvals";
        await LoadAsync(cancellationToken);
        return Page();
    }

    public async Task<IActionResult> OnPostResolveAsync(long id, CancellationToken cancellationToken)
    {
        TicketId = id;
        if (!TryDecodeRowVersion(Resolve.RowVersionBase64, out var rowVersion))
        {
            return await ReloadWithErrorAsync("resolve", "Could not read the ticket's current version. Reloading.", cancellationToken);
        }

        var result = await ticketsApiClient.ResolveAsync(
            id,
            new ResolveTicketRequestDto(Resolve.ResolutionOutcome, Resolve.ResolutionNote, null, Resolve.DuplicateOfTicketId, rowVersion),
            cancellationToken);
        return await HandleMutationAsync(result, "resolve", "Ticket resolved.", cancellationToken);
    }

    public async Task<IActionResult> OnPostCloseAsync(long id, CancellationToken cancellationToken)
    {
        TicketId = id;
        if (!TryDecodeRowVersion(Close.RowVersionBase64, out var rowVersion))
        {
            return await ReloadWithErrorAsync("close", "Could not read the ticket's current version. Reloading.", cancellationToken);
        }

        var result = await ticketsApiClient.CloseAsync(id, new CloseTicketRequestDto(rowVersion), cancellationToken);
        return await HandleMutationAsync(result, "close", "Ticket closed.", cancellationToken);
    }

    public async Task<IActionResult> OnPostReopenAsync(long id, CancellationToken cancellationToken)
    {
        TicketId = id;
        if (string.IsNullOrWhiteSpace(Reopen.Reason))
        {
            return await ReloadWithErrorAsync("reopen", "Enter a reason before reopening.", cancellationToken);
        }

        if (Reopen.TargetDepartmentId <= 0)
        {
            return await ReloadWithErrorAsync("reopen", "Choose the department that will take this ticket on.", cancellationToken);
        }

        if (!TryDecodeRowVersion(Reopen.RowVersionBase64, out var rowVersion))
        {
            return await ReloadWithErrorAsync("reopen", "Could not read the ticket's current version. Reloading.", cancellationToken);
        }

        var result = await ticketsApiClient.ReopenAsync(
            id, new ReopenTicketRequestDto(Reopen.Reason, Reopen.TargetDepartmentId, rowVersion), cancellationToken);

        // Stays on this page: the redirect re-reads the ticket, so the agent
        // sees In Progress, the department that now owns it, the resulting
        // assignee (or the department queue), the new Resolution SLA deadline
        // and the reopen in Activity — all as served facts, not as a message.
        return await HandleMutationAsync(result, "reopen", "Ticket reopened — it is In Progress again.", cancellationToken);
    }

    /// <summary>
    /// Records the first human response on the ticket (Source = Manual). The server decides who may
    /// (current owner, supervisory roles, Department Head of the ticket's department) and refuses a
    /// second write, so this handler only relays and reports.
    /// </summary>
    public async Task<IActionResult> OnPostRecordFirstResponseAsync(long id, CancellationToken cancellationToken)
    {
        TicketId = id;
        if (!TryDecodeRowVersion(Escalate.RowVersionBase64, out var rowVersion))
        {
            return await ReloadWithErrorAsync("sla", "Could not read the ticket's current version. Reloading.", cancellationToken);
        }

        var result = await slaApiClient.RecordFirstResponseAsync(
            id, new RecordFirstResponseRequestDto("Manual", null, rowVersion), cancellationToken);

        if (result.IsSuccess)
        {
            ActionSuccess = "First response recorded.";
            return RedirectToPage(new { id });
        }

        // Idempotent from the user's side: a double click (or a second agent) finds it already recorded.
        if (result.Outcome == ApiOutcome.Conflict
            && result.ProblemType?.EndsWith("first-response-already-recorded", StringComparison.Ordinal) == true)
        {
            ActionSuccess = "First response was already recorded.";
            return RedirectToPage(new { id });
        }

        ActionError = DescribeError(result.Outcome, result.Detail);
        OpenSection = "sla";
        await LoadAsync(cancellationToken);
        return Page();
    }

    public async Task<IActionResult> OnPostEscalateAsync(long id, CancellationToken cancellationToken)
    {
        TicketId = id;
        if (!TryDecodeRowVersion(Escalate.RowVersionBase64, out var rowVersion))
        {
            return await ReloadWithErrorAsync("escalate", "Could not read the ticket's current version. Reloading.", cancellationToken);
        }

        var triggerType = Escalate.Level == 4 ? "ManualLevel4" : "ManualFlag";
        var result = await slaApiClient.EscalateAsync(
            id, new ManualEscalationRequestDto(Escalate.Level, triggerType, Escalate.Note, rowVersion), cancellationToken);

        if (result.IsSuccess)
        {
            ActionSuccess = $"Escalated to level {Escalate.Level}.";
            return RedirectToPage(new { id });
        }

        ActionError = DescribeError(result.Outcome, result.Detail);
        OpenSection = "escalate";
        await LoadAsync(cancellationToken);
        return Page();
    }

    public async Task<IActionResult> OnPostRequestDowngradeAsync(long id, CancellationToken cancellationToken)
    {
        TicketId = id;
        if (string.IsNullOrWhiteSpace(DowngradeRequest.Reason))
        {
            ActionError = "Enter a reason for the downgrade.";
            OpenSection = "downgrade";
            await LoadAsync(cancellationToken);
            return Page();
        }

        var result = await slaApiClient.RequestPriorityDowngradeAsync(
            id, new CreateDowngradeRequestRequestDto(DowngradeRequest.NewPriorityId, DowngradeRequest.Reason), cancellationToken);

        if (result.IsSuccess)
        {
            ActionSuccess = "Downgrade requested. The priority stays unchanged until a Department Head approves it.";
            return RedirectToPage(new { id });
        }

        ActionError = DescribeDowngradeError(result.Outcome, result.Detail);
        OpenSection = "downgrade";
        await LoadAsync(cancellationToken);
        return Page();
    }

    public async Task<IActionResult> OnPostApproveDowngradeAsync(long id, CancellationToken cancellationToken)
    {
        TicketId = id;
        TryDecodeRowVersion(DowngradeDecision.RowVersionBase64, out var rowVersion);

        var result = await slaApiClient.ApprovePriorityDowngradeAsync(
            DowngradeDecision.RequestId, new ApproveDowngradeRequestRequestDto(rowVersion), cancellationToken);

        if (result.IsSuccess)
        {
            ActionSuccess = "Downgrade approved. The ticket now follows the lower priority's SLA; earlier breaches stay on record.";
            return RedirectToPage(new { id });
        }

        ActionError = DescribeDowngradeError(result.Outcome, result.Detail);
        OpenSection = "downgrade";
        await LoadAsync(cancellationToken);
        return Page();
    }

    public async Task<IActionResult> OnPostRejectDowngradeAsync(long id, CancellationToken cancellationToken)
    {
        TicketId = id;
        if (string.IsNullOrWhiteSpace(DowngradeDecision.DecisionNote))
        {
            ActionError = "Enter a note explaining the rejection.";
            OpenSection = "downgrade";
            await LoadAsync(cancellationToken);
            return Page();
        }

        TryDecodeRowVersion(DowngradeDecision.RowVersionBase64, out var rowVersion);
        var result = await slaApiClient.RejectPriorityDowngradeAsync(
            DowngradeDecision.RequestId, new RejectDowngradeRequestRequestDto(DowngradeDecision.DecisionNote, rowVersion), cancellationToken);

        if (result.IsSuccess)
        {
            ActionSuccess = "Downgrade rejected. The priority is unchanged.";
            return RedirectToPage(new { id });
        }

        ActionError = DescribeDowngradeError(result.Outcome, result.Detail);
        OpenSection = "downgrade";
        await LoadAsync(cancellationToken);
        return Page();
    }

    private static string DescribeDowngradeError(ApiOutcome outcome, string? detail) => outcome switch
    {
        ApiOutcome.Conflict => detail ?? "A downgrade request is already pending, or it was already decided. The latest state is shown.",
        ApiOutcome.Forbidden => "You don't have permission to do that (a requester cannot approve their own request).",
        ApiOutcome.UnprocessableEntity => detail ?? "That downgrade isn't valid for this ticket right now.",
        _ => DescribeError(outcome, detail)
    };

    public async Task<IActionResult> OnPostAddNoteAsync(long id, CancellationToken cancellationToken)
    {
        TicketId = id;
        if (string.IsNullOrWhiteSpace(Note.NoteText))
        {
            ActionError = "Enter a note before saving.";
            OpenSection = "note";
            await LoadAsync(cancellationToken);
            return Page();
        }

        var result = await ticketsApiClient.AddNoteAsync(id, new CreateNoteRequestDto(Note.NoteText), cancellationToken);
        if (result.IsSuccess)
        {
            ActionSuccess = "Note added.";
            return RedirectToPage(new { id });
        }

        ActionError = DescribeError(result.Outcome, result.Detail);
        OpenSection = "note";
        await LoadAsync(cancellationToken);
        return Page();
    }

    private async Task<IActionResult> HandleMutationAsync(
        ApiResult<TicketDetailDto> result, string section, string successMessage, CancellationToken cancellationToken)
    {
        if (result.IsSuccess)
        {
            ActionSuccess = successMessage;
            return RedirectToPage(new { id = TicketId });
        }

        ActionError = DescribeError(result.Outcome, result.Detail);
        OpenSection = section;
        await LoadAsync(cancellationToken);
        return Page();
    }

    private async Task<IActionResult> ReloadWithErrorAsync(string section, string message, CancellationToken cancellationToken)
    {
        ActionError = message;
        OpenSection = section;
        await LoadAsync(cancellationToken);
        return Page();
    }

    /// <summary>
    /// Groups the directory for the assign control: one group per department,
    /// the ticket's own department first (even when empty, so the viewer sees
    /// it has nobody), the rest by name. A user in several departments
    /// appears under each of them — the viewer chooses the pair.
    /// </summary>
    internal static IReadOnlyList<(int DepartmentId, string Name, IReadOnlyList<AssignableUserDto> Members)> GroupAssignableByDepartment(
        IReadOnlyList<AssignableUserDto> users, int currentDepartmentId, string? currentDepartmentName)
    {
        var groups = users
            .SelectMany(u => u.Departments.Select(d => (Department: d, User: u)))
            .GroupBy(x => x.Department.DepartmentId)
            .Select(g => (
                DepartmentId: g.Key,
                Name: g.First().Department.Name,
                Members: (IReadOnlyList<AssignableUserDto>)g.Select(x => x.User)
                    .OrderBy(u => u.DisplayName, StringComparer.OrdinalIgnoreCase).ToList()))
            .ToList();

        if (groups.All(g => g.DepartmentId != currentDepartmentId))
        {
            groups.Add((currentDepartmentId, currentDepartmentName ?? $"Department #{currentDepartmentId}", []));
        }

        return groups
            .OrderBy(g => g.DepartmentId == currentDepartmentId ? 0 : 1)
            .ThenBy(g => g.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static bool TryDecodeRowVersion(string? base64, out byte[] rowVersion)
    {
        rowVersion = [];
        if (string.IsNullOrEmpty(base64))
        {
            return false;
        }

        try
        {
            rowVersion = Convert.FromBase64String(base64);
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    /// <summary>Loads the Classify form's choices — the ticket's own department's active categories and request types — only when the control will be shown.</summary>
    private async Task LoadClassifyOptionsAsync(TicketDetailDto ticket, CancellationToken cancellationToken)
    {
        CanClassify =
            categoriesApiClient is not null && requestTypesApiClient is not null
            && ticket.CategoryId is null
            && !string.Equals(ticket.TicketStatus, nameof(TicketStatus.Closed), StringComparison.Ordinal)
            && Viewer is not null
            && !TigerCS.Domain.Modules.IdentityAndAccess.Roles.IsReadOnlyCaller(Viewer.Roles);
        if (!CanClassify)
        {
            return;
        }

        var categoriesTask = categoriesApiClient!.GetCategoriesAsync(ticket.CurrentDepartmentId, cancellationToken);
        var requestTypesTask = requestTypesApiClient!.GetOptionsAsync(ticket.CurrentDepartmentId, cancellationToken);
        await Task.WhenAll(categoriesTask, requestTypesTask);

        ClassifyOptionsUnavailable = !categoriesTask.Result.IsSuccess || !requestTypesTask.Result.IsSuccess;
        ClassifyCategories = categoriesTask.Result.Value?.Where(c => c.DepartmentId == ticket.CurrentDepartmentId).ToList() ?? [];
        ClassifyRequestTypes = requestTypesTask.Result.Value?.Where(r => r.DepartmentId == ticket.CurrentDepartmentId && r.HasPublishedWorkflow).ToList() ?? [];
        ClassifyOptions = ClassifyOptionsUnavailable
            ? TicketClassifyOptions.Failed
            : TicketClassifyOptions.Build(ticket.CurrentDepartmentId, ClassifyCategories, ClassifyRequestTypes);
        ClassifyNotice = ClassifyNotice.For(
            ClassifyOptions,
            TicketDisplay.AssignedDepartmentLabel(ticket.CurrentDepartmentId, nameResolver.TryGetDepartmentName(ticket.CurrentDepartmentId)),
            Viewer, TicketActions.CanTransfer(Viewer?.Roles));

        var current = ticket.PriorityId ?? 4;
        ClassifyPriorities = Enumerable.Range(1, current).Select(p => (byte)p).ToList();

        // Default to the category the AI's own request type belongs under, otherwise the first one; the request type follows the category.
        var defaultCategory = ClassifyOptions.CategoryForRequestType(ticket.RequestTypeName) ?? ClassifyOptions.Categories.FirstOrDefault();
        Classify = new ClassifyInput
        {
            RowVersionBase64 = ticket.RowVersion,
            CategoryId = defaultCategory?.CategoryId ?? 0,
            PriorityId = current,
            RequestTypeId = defaultCategory?.RequestTypes.FirstOrDefault()?.RequestTypeId ?? 0,
            RequestTypeAlreadySet = ticket.RequestTypeId is not null,
        };
    }

    /// <summary>The classified ticket's category name, from the active-category directory. Absent (null) rather than an id when it cannot be resolved.</summary>
    private async Task LoadCategoryNameAsync(TicketDetailDto ticket, CancellationToken cancellationToken)
    {
        if (ticket.CategoryId is not { } categoryId || categoriesApiClient is null)
        {
            return;
        }

        var result = await categoriesApiClient.GetCategoriesAsync(null, cancellationToken);
        CategoryName = result.IsSuccess ? result.Value?.FirstOrDefault(c => c.CategoryId == categoryId)?.Name : null;
    }

    private static string DescribeError(ApiOutcome outcome, string? detail) => outcome switch
    {
        ApiOutcome.Conflict => "This ticket changed since you loaded it. The latest version is now shown — please retry.",
        ApiOutcome.UnprocessableEntity => detail ?? "That action isn't valid for this ticket right now.",
        ApiOutcome.Forbidden => "You don't have permission to do that.",
        ApiOutcome.ValidationError => detail ?? "Check the values you entered.",
        ApiOutcome.NotFound => "That reference could not be found.",
        ApiOutcome.Unreachable or ApiOutcome.BadGateway => "Tiger Ticketing System could not reach the ticketing service.",
        _ => detail ?? "Something went wrong."
    };

    private async Task LoadAsync(CancellationToken cancellationToken)
    {
        Viewer = CurrentUser.FromPrincipal(User);
        ReturnContext = TicketsContext.FromCookieValue(Request.Cookies[TicketsContext.CookieName]);
        await nameResolver.PrimeDepartmentsAsync(cancellationToken);

        var detailResult = await ticketsApiClient.GetByIdAsync(TicketId, cancellationToken);
        Outcome = detailResult.Outcome;
        if (!detailResult.IsSuccess || detailResult.Value is null)
        {
            Ticket = null;
            return;
        }

        Ticket = detailResult.Value;

        var slaTask = slaApiClient.GetSlaAsync(TicketId, cancellationToken);
        var notesTask = ticketsApiClient.GetNotesAsync(TicketId, 1, 50, cancellationToken);
        var escalationsTask = slaApiClient.GetEscalationsAsync(TicketId, cancellationToken);
        var assignableTask = usersApiClient.GetDepartmentUsersAsync(Ticket.CurrentDepartmentId, 1, 100, cancellationToken);
        // The whole directory is fetched only for viewers the Api lets assign
        // across departments; for everyone else the endpoint would answer 403
        // and the call is pointless.
        CanAssignAcrossDepartments = TicketActions.CanAssignAcrossDepartments(Viewer?.Roles);
        var assignableUsersTask = CanAssignAcrossDepartments
            ? usersApiClient.GetAssignableUsersAsync(cancellationToken)
            : Task.FromResult(ApiResult<IReadOnlyList<AssignableUserDto>>.Success([]));
        var customerHistoryTask = ticketsApiClient.GetCustomerHistoryAsync(TicketId, limit: 10, cancellationToken);
        var approvalsTask = ticketsApiClient.GetApprovalsAsync(TicketId, cancellationToken);
        var interactionsTask = ticketsApiClient.GetInteractionsAsync(TicketId, cancellationToken);
        var historyTask = ticketsApiClient.GetLifecycleHistoryAsync(TicketId, cancellationToken);
        var reviewsTask = ticketsApiClient.GetReviewsAsync(TicketId, cancellationToken);
        var downgradesTask = slaApiClient.GetPriorityDowngradeRequestsAsync(TicketId, cancellationToken);

        await Task.WhenAll(
            slaTask, notesTask, escalationsTask, assignableTask, assignableUsersTask, customerHistoryTask, approvalsTask, interactionsTask,
            historyTask, downgradesTask, reviewsTask);

        Reviews = reviewsTask.Result.IsSuccess && reviewsTask.Result.Value is not null ? reviewsTask.Result.Value.Cycles : [];
        ReviewsUnavailable = !reviewsTask.Result.IsSuccess;

        PendingDowngrade = downgradesTask.Result.IsSuccess && downgradesTask.Result.Value is not null
            ? downgradesTask.Result.Value.FirstOrDefault(r => r.Status == nameof(PriorityDowngradeRequestStatus.Pending))
            : null;

        Interactions = interactionsTask.Result.IsSuccess && interactionsTask.Result.Value is not null
            ? interactionsTask.Result.Value.Interactions
            : [];
        InteractionsUnavailable = !interactionsTask.Result.IsSuccess;

        Approvals = approvalsTask.Result.IsSuccess ? approvalsTask.Result.Value : null;
        Sla = slaTask.Result.IsSuccess ? slaTask.Result.Value : null;
        Notes = notesTask.Result.IsSuccess && notesTask.Result.Value is not null ? notesTask.Result.Value.Items : [];
        Escalations = escalationsTask.Result.IsSuccess && escalationsTask.Result.Value is not null ? escalationsTask.Result.Value : [];
        AssignableEmployees = assignableTask.Result.IsSuccess && assignableTask.Result.Value is not null ? [.. assignableTask.Result.Value.Items] : [];
        AssignableEmployeesOutcome = assignableTask.Result.Outcome;
        AssignableUsers = assignableUsersTask.Result.IsSuccess && assignableUsersTask.Result.Value is not null ? assignableUsersTask.Result.Value : [];
        AssignableUsersOutcome = assignableUsersTask.Result.Outcome;
        CustomerHistory = customerHistoryTask.Result.IsSuccess ? customerHistoryTask.Result.Value : null;

        var originating = Interactions.FirstOrDefault(i => i.IsOriginatingInteraction) ?? Interactions.FirstOrDefault();
        CustomerPhone = FirstNonEmpty(CustomerHistory?.PhoneNumberSnapshot, originating?.CustomerPhone);
        // A source system's placeholder ("NOT_REGISTERED", "UNKNOWN", "N/A") is not a name: it is treated as missing,
        // so the page says "Customer not identified" instead of printing it.
        CustomerDisplayName = FirstNonEmpty(
            CustomerNameValidator.Sanitize(Ticket.CrmBuyerCustomerName),
            CustomerNameValidator.Sanitize(originating?.CustomerName),
            CustomerNameValidator.Sanitize(CustomerHistory?.CustomerDisplayName));
        CustomerKey = CustomerHistory?.CustomerKey
            ?? CustomerIdentity.FromTicketFacts(Ticket.CrmBuyerCustomerId, Ticket.CustomerVerificationSource, Ticket.ExternalCustomerId, CustomerPhone)?.Key;

        DepartmentName = nameResolver.TryGetDepartmentName(Ticket.CurrentDepartmentId);
        AssignableByDepartment = GroupAssignableByDepartment(AssignableUsers, Ticket.CurrentDepartmentId, DepartmentName);
        OwnerName = Ticket.CurrentOwnerEmployeeId is Guid ownerId
            ? await nameResolver.ResolveOwnerNameAsync(Ticket.CurrentDepartmentId, ownerId, cancellationToken)
            : null;

        EmployeeNames = BuildEmployeeNames(Ticket, Approvals?.Events ?? []);

        LoadActionAffordances(Ticket);
        await LoadClassifyOptionsAsync(Ticket, cancellationToken);
        await LoadCategoryNameAsync(Ticket, cancellationToken);
        await LoadTransferTargetsAsync(Ticket, cancellationToken);
        await LoadReopenTargetsAsync(Ticket, cancellationToken);
        ApplyReadOnlyAccess(Ticket);

        var lifecycleHistory = historyTask.Result.IsSuccess && historyTask.Result.Value is not null
            ? historyTask.Result.Value.Entries
            : [];

        ActivityFeed = BuildActivityFeed(
            Ticket, Notes, Escalations, lifecycleHistory, Approvals?.Events ?? [], Viewer, nameResolver, EmployeeNames);

        // Pre-fill the RowVersion the forms will post back, and default the
        // employee/status pickers so an untouched form still submits something valid.
        Assign = new AssignInput
        {
            RowVersionBase64 = Ticket.RowVersion,
            Target = Ticket.CurrentOwnerEmployeeId is Guid owner ? $"{owner}|{Ticket.CurrentDepartmentId}" : string.Empty,
        };
        Transfer = new TransferInput { RowVersionBase64 = Ticket.RowVersion, TargetDepartmentId = TransferTargets.FirstOrDefault()?.DepartmentId ?? 0 };
        // Default the picker to a target it actually offers. The ticket's own
        // current status is never one of them (nothing transitions to itself),
        // so defaulting to it would submit a guaranteed 422 from an untouched
        // form.
        Status = new StatusInput
        {
            RowVersionBase64 = Ticket.RowVersion,
            NewStatus = StatusTargets.Count > 0 ? StatusTargets[0].ToString() : Ticket.TicketStatus
        };
        Resolve = new ResolveInput { RowVersionBase64 = Ticket.RowVersion, ResolutionOutcome = "Resolved" };
        Close = new RowVersionInput { RowVersionBase64 = Ticket.RowVersion };
        Reopen = new ReopenInput
        {
            RowVersionBase64 = Ticket.RowVersion,
            // Defaults to the department that closed it — the common case, and
            // an explicit choice the agent can change rather than a blank.
            TargetDepartmentId = ReopenTargets.Any(d => d.DepartmentId == Ticket.CurrentDepartmentId)
                ? Ticket.CurrentDepartmentId
                : ReopenTargets.FirstOrDefault()?.DepartmentId ?? 0
        };
        // Default to a level the picker actually offers: a viewer without
        // ManualLevel4 authority is shown levels 1-3, so the default must not
        // be the 4 this ticket's current level would otherwise suggest.
        var nextLevel = NextEscalationLevel(Ticket.EscalationLevel);
        Escalate = new EscalateInput
        {
            RowVersionBase64 = Ticket.RowVersion,
            Level = CanEscalateToLevel4 ? nextLevel : Math.Min(nextLevel, (byte)3)
        };
    }

    /// <summary>
    /// Builds the viewer-and-ticket context the action affordances are decided
    /// from, and evaluates each one against the Api's own canonical rule.
    ///
    /// <para>
    /// The department memberships come from <c>nameResolver.OwnDepartments</c>
    /// — the <c>Departments</c> collection of <c>GET /api/users/me</c>, already
    /// fetched by the <c>PrimeDepartmentsAsync</c> call at the top of
    /// <see cref="LoadAsync"/>, so mirroring the server's department-scoped
    /// rules costs no additional round trip. That endpoint reads the same
    /// UserDepartmentAssignments rows the Api's own ExistsAsync check queries.
    /// </para>
    ///
    /// <para>
    /// Everything here fails closed: no signed-in viewer, or a users/me call
    /// that failed (leaving the memberships empty), hides the
    /// department-scoped controls rather than offering them on an assumption.
    /// </para>
    /// </summary>
    private void LoadActionAffordances(TicketDetailDto ticket)
    {
        ActionContext = Viewer is null
            ? null
            : new TicketActionContext(
                Viewer.Roles,
                Viewer.EmployeeId,
                ticket.CurrentOwnerEmployeeId,
                ticket.CurrentDepartmentId,
                [.. nameResolver.OwnDepartments.Select(d => d.DepartmentId)]);

        StatusTargets = TicketStatusTransitions.AllowedTargetsFrom(ticket.TicketStatus);

        CanAssign = TicketActions.CanAssign(ActionContext);
        CanChangeStatus = TicketActions.CanChangeStatus(ActionContext) && StatusTargets.Count > 0;
        CanResolve = TicketActions.CanResolve(ActionContext);
        CanClose = TicketActions.CanCloseTicket(ActionContext);
        CanEscalate = TicketActions.CanEscalate(ActionContext);
        CanEscalateToLevel4 = TicketActions.CanEscalateToLevel4(ActionContext);

        var open = ticket.TicketStatus is not (nameof(TicketStatus.Closed) or nameof(TicketStatus.Resolved));
        DowngradeTargets = ticket.PriorityId is { } current
            ? [.. Enum.GetValues<PriorityLevel>().Where(p => (byte)p > current).Select(p => ((byte)p, p.ToString()))]
            : [];
        CanRequestDowngrade = open && PendingDowngrade is null && DowngradeTargets.Count > 0
            && TicketActions.CanRequestPriorityDowngrade(ActionContext);
        CanDecideDowngrade = PendingDowngrade is { } pending
            && Viewer?.EmployeeId != pending.RequestedByEmployeeId
            && TicketActions.CanDecidePriorityDowngrade(ActionContext);
        DowngradeRequest = new DowngradeRequestInput { NewPriorityId = DowngradeTargets.Count > 0 ? DowngradeTargets[0].PriorityId : (byte)0 };
    }

    /// <summary>
    /// Authorization first, then the directory: a viewer without transfer
    /// authority is offered no departments at all (not even their own), and
    /// an authorized viewer is offered only real, active departments other
    /// than the one the ticket is already in. The active-only directory call
    /// is skipped entirely for viewers who cannot transfer.
    /// </summary>
    private async Task LoadTransferTargetsAsync(TicketDetailDto ticket, CancellationToken cancellationToken)
    {
        CanTransfer = TicketActions.CanTransfer(Viewer?.Roles);
        TransferTargets = [];
        TransferTargetsUnavailable = false;

        if (!CanTransfer)
        {
            return;
        }

        var active = await nameResolver.GetActiveDepartmentsAsync(cancellationToken);
        if (active is null)
        {
            TransferTargetsUnavailable = true;
            return;
        }

        TransferTargets =
        [
            .. active
                .Where(d => d.DepartmentId != ticket.CurrentDepartmentId)
                .OrderBy(d => d.Name, StringComparer.OrdinalIgnoreCase)
        ];
    }

    /// <summary>
    /// The Reopen form's department picker. Role check first, then the
    /// directory — and skipped entirely when the ticket is not reopen-eligible,
    /// so a closed-but-expired ticket costs no directory call. Unlike transfer
    /// this keeps the ticket's current department in the list: reopening into
    /// the department that closed it is allowed.
    /// </summary>
    private async Task LoadReopenTargetsAsync(TicketDetailDto ticket, CancellationToken cancellationToken)
    {
        CanReopen = TicketActions.CanReopen(Viewer?.Roles);
        ReopenTargets = [];
        ReopenTargetsUnavailable = false;

        if (!CanReopen || !ticket.IsReopenEligible)
        {
            return;
        }

        var active = await nameResolver.GetActiveDepartmentsAsync(cancellationToken);
        if (active is null)
        {
            ReopenTargetsUnavailable = true;
            return;
        }

        ReopenTargets = [.. active.OrderBy(d => d.Name, StringComparer.OrdinalIgnoreCase)];
    }

    private static string? FirstNonEmpty(params string?[] values) =>
        values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v))?.Trim();

    private static IReadOnlyList<ActivityEntry> BuildActivityFeed(
        TicketDetailDto ticket,
        IReadOnlyList<TicketNoteResponseDto> notes,
        IReadOnlyList<TicketEscalationResponseDto> escalations,
        IReadOnlyList<TicketStatusHistoryEntryDto> lifecycleHistory,
        IReadOnlyList<TicketWorkflowEventDto> workflowEvents,
        CurrentUser? viewer,
        TicketNameResolver nameResolver,
        IReadOnlyDictionary<Guid, string>? employeeNames = null)
    {
        var entries = new List<ActivityEntry>
        {
            new(ticket.CreatedAtUtc, "created", "System", $"Ticket {ticket.TicketNumber} created", null, false)
        };

        foreach (var note in notes)
        {
            var author = TicketNameResolver.ResolveSelfAuthorName(note.AuthorEmployeeId, viewer) ?? $"Employee #{note.AuthorEmployeeId.ToString()[..8]}";
            entries.Add(new ActivityEntry(note.CreatedAtUtc, "note", author, "added a note", note.NoteText, true));
        }

        foreach (var escalation in escalations)
        {
            var levelLabel = TicketDisplay.EscalationLevelLabel($"Level{escalation.Level}");
            entries.Add(new ActivityEntry(
                escalation.RaisedAtUtc, "escalation", "System",
                $"Escalated to {levelLabel} ({escalation.TriggerType})",
                escalation.NotifiedRoles is null ? null : $"Notified: {escalation.NotifiedRoles}", false));
        }

        // Approval cycles read from the typed workflow events the approval
        // service writes — the request (whose note is the reason, and for a
        // Reopen Approval that reason is the whole point) and the decision.
        // Deliberately separate entries from the reopen below: an approval
        // authorizes an ask, the reopen is the state change, and merging them
        // would claim the ticket moved when it did not.
        foreach (var approvalEvent in workflowEvents)
        {
            if (approvalEvent.EventType == nameof(WorkflowEventType.TicketTransferred))
            {
                entries.Add(DescribeTransfer(approvalEvent, viewer, nameResolver, employeeNames));
                continue;
            }

            if (approvalEvent.EventType is nameof(WorkflowEventType.GenesysAgentConnected) or nameof(WorkflowEventType.GenesysCallTransferred))
            {
                entries.Add(DescribeGenesysAgentEvent(approvalEvent, viewer, employeeNames));
                continue;
            }

            if (DescribeApprovalEvent(approvalEvent.EventType) is not { } description)
            {
                continue;
            }

            var actor = approvalEvent.ActorEmployeeId is { } approvalActorId
                ? TicketNameResolver.ResolveSelfAuthorName(approvalActorId, viewer) ?? $"Employee #{approvalActorId.ToString()[..8]}"
                : "System";

            // A handoff request is worded from its stated trigger, so the feed
            // never claims "asked for a human" for a request nobody made.
            if (approvalEvent.EventType == nameof(WorkflowEventType.HandoffRequested))
            {
                description = TicketDisplay.HandoffRequestedActivity(approvalEvent.Note);
                actor = "Genesys";
            }

            entries.Add(new ActivityEntry(
                approvalEvent.OccurredAtUtc, "approval", actor, description, approvalEvent.Note, true));
        }

        // Reopens read from BOTH lifecycle stores, because each holds half of
        // what the entry has to say: the status-history row carries the agent
        // and the reason they typed, the typed Reopened event carries the
        // department move, where ownership landed and the new SLA deadline.
        // Matched by timestamp — one reopen writes both rows inside one
        // transaction at one instant.
        var reopenFacts = workflowEvents
            .Where(e => string.Equals(e.EventType, nameof(WorkflowEventType.Reopened), StringComparison.Ordinal))
            .ToDictionary(e => e.OccurredAtUtc, e => e.Note);

        foreach (var change in lifecycleHistory)
        {
            if (DescribeLifecycleChange(change, ticket) is not { } description)
            {
                continue;
            }

            var actor = change.ActorIsSystem || change.ActorEmployeeId is not { } actorId
                ? "System"
                : TicketNameResolver.ResolveSelfAuthorName(actorId, viewer) ?? $"Employee #{actorId.ToString()[..8]}";

            var isReopen = change is { Dimension: nameof(TicketStatusDimension.TicketStatus), NewValue: nameof(TicketStatus.InProgress) }
                && change.OldValue == nameof(TicketStatus.Closed);

            entries.Add(new ActivityEntry(
                change.OccurredAtUtc,
                isReopen ? "reopen" : "status",
                actor,
                description,
                change.Note,
                !change.ActorIsSystem,
                isReopen && reopenFacts.TryGetValue(change.OccurredAtUtc, out var note)
                    ? DescribeReopenFacts(note, nameResolver)
                    : null));
        }

        return [.. entries.OrderBy(e => e.TimestampUtc)];
    }

    /// <summary>
    /// A transfer / escalation as the feed reads it: who moved the ticket, to whom or which department, and the recorded
    /// reason. Escalations (Unable to Resolve / Needs Escalation) get their own kind so they stand out in the timeline.
    /// </summary>
    private static ActivityEntry DescribeTransfer(
        TicketWorkflowEventDto transfer, CurrentUser? viewer, TicketNameResolver nameResolver, IReadOnlyDictionary<Guid, string>? employeeNames)
    {
        string NameOf(Guid id) =>
            TicketNameResolver.ResolveSelfAuthorName(id, viewer)
            ?? (employeeNames is not null && employeeNames.TryGetValue(id, out var known) ? known : $"Employee #{id.ToString()[..8]}");

        string DepartmentOf(int? id) =>
            id is { } departmentId ? nameResolver.TryGetDepartmentName(departmentId) ?? $"Department #{departmentId}" : "a department";

        var actor = transfer.ActorEmployeeId is { } actorId ? NameOf(actorId) : "System";
        var isEscalation = transfer.TransferReason is nameof(TicketTransferReason.UnableToResolve) or nameof(TicketTransferReason.NeedsEscalation);
        var movedDepartment = transfer.FromDepartmentId != transfer.ToDepartmentId;

        var target = transfer.ToEmployeeId is { } toEmployeeId
            ? movedDepartment ? $"{NameOf(toEmployeeId)} in {DepartmentOf(transfer.ToDepartmentId)}" : NameOf(toEmployeeId)
            : $"the {DepartmentOf(transfer.ToDepartmentId)} queue";

        var verb = isEscalation ? "escalated the ticket to" : movedDepartment ? "transferred the ticket to" : "reassigned the ticket to";
        var detail = movedDepartment ? $"From {DepartmentOf(transfer.FromDepartmentId)} to {DepartmentOf(transfer.ToDepartmentId)}" : null;

        return new ActivityEntry(
            transfer.OccurredAtUtc,
            isEscalation ? "escalation" : "transfer",
            actor,
            $"{verb} {target} — {TicketDisplay.TransferReasonLabel(transfer.TransferReason)}",
            transfer.Note,
            transfer.ActorEmployeeId is not null,
            detail);
    }

    /// <summary>
    /// A Genesys agent taking, or handing on, the customer's conversation. Worded as handling history only — never as an
    /// escalation: a call transfer says the conversation moved, not that the first agent could not resolve the issue. The
    /// event note keeps the verbatim Genesys ids and names, including agents with no TigerCS user.
    /// </summary>
    private static ActivityEntry DescribeGenesysAgentEvent(
        TicketWorkflowEventDto agentEvent, CurrentUser? viewer, IReadOnlyDictionary<Guid, string>? employeeNames)
    {
        string NameOf(Guid id) =>
            TicketNameResolver.ResolveSelfAuthorName(id, viewer)
            ?? (employeeNames is not null && employeeNames.TryGetValue(id, out var known) ? known : $"Employee #{id.ToString()[..8]}");

        var receiver = agentEvent.ToEmployeeId is { } toId ? NameOf(toId) : null;

        if (agentEvent.EventType == nameof(WorkflowEventType.GenesysAgentConnected))
        {
            return new ActivityEntry(
                agentEvent.OccurredAtUtc, "genesys", receiver ?? "A Genesys agent",
                "was the first agent to handle the customer's conversation", null, true);
        }

        var from = agentEvent.ActorEmployeeId is { } fromId ? NameOf(fromId) : "A Genesys agent";
        return new ActivityEntry(
            agentEvent.OccurredAtUtc, "genesys", from,
            $"transferred the customer's call to {receiver ?? "another Genesys agent"}",
            agentEvent.Note, true);
    }

    /// <summary>
    /// The people the handling summary and the feed name. The Api resolves every name from the employee directory itself
    /// (<c>FirstHandledByName</c>, <c>ResolvedByName</c>, and the actor/receiver names on each event), so someone who has
    /// since left the department or been deactivated is still named; the page does not walk department member lists.
    /// Anyone the Api could not name keeps the short-id fallback.
    /// </summary>
    private static Dictionary<Guid, string> BuildEmployeeNames(TicketDetailDto ticket, IReadOnlyList<TicketWorkflowEventDto> workflowEvents)
    {
        var names = new Dictionary<Guid, string>();

        void Add(Guid? id, string? name)
        {
            if (id is { } known && !string.IsNullOrWhiteSpace(name))
            {
                names[known] = name;
            }
        }

        Add(ticket.FirstHandledByEmployeeId, ticket.FirstHandledByName);
        Add(ticket.ResolvedByEmployeeId, ticket.ResolvedByName);
        foreach (var workflowEvent in workflowEvents)
        {
            Add(workflowEvent.ActorEmployeeId, workflowEvent.ActorName);
            Add(workflowEvent.ToEmployeeId, workflowEvent.ToEmployeeName);
        }

        return names;
    }

    /// <summary>
    /// What an approval-cycle workflow event reads as in the feed, or null
    /// for the dependency events (prerequisites/maintenance), which the
    /// Approvals panel already states as a current condition rather than a
    /// moment, and for <c>Reopened</c>, which the lifecycle row below owns.
    /// </summary>
    private static string? DescribeApprovalEvent(string eventType) => eventType switch
    {
        nameof(WorkflowEventType.ApprovalRequested) => "requested an approval",
        nameof(WorkflowEventType.ApprovalReceived) => "approved the request",
        nameof(WorkflowEventType.CustomerServiceApproved) => "approved the request (Customer Service)",
        nameof(WorkflowEventType.ApprovalRejected) => "rejected the request",

        // The AI ↔ human handoff story, read beside the transcript that led
        // to it. Phrased without an actor for the Genesys-side events, whose
        // ActorEmployeeId is deliberately null — nobody in TigerCS performed
        // them.
        nameof(WorkflowEventType.HandoffRequested) => "a human agent was requested",
        nameof(WorkflowEventType.HandoffAssigned) => "was assigned a human agent",
        nameof(WorkflowEventType.HandoffStarted) => "accepted the customer interaction",
        nameof(WorkflowEventType.HandoffCompleted) => "finished the customer interaction",
        nameof(WorkflowEventType.HandoffCancelled) => "stood down the request for a human agent",

        // Record-only: the customer's word, not a status change — Resolve
        // and Close still happen through their own actions.
        nameof(WorkflowEventType.CustomerConfirmedResolved) => "recorded that the customer confirmed the issue is resolved",
        nameof(WorkflowEventType.RequestTypeReclassified) => "changed the request type",

        _ => null
    };

    /// <summary>
    /// What one lifecycle row reads as in the feed, or null for rows worth
    /// nothing to a reader: the seeding rows written at creation (already
    /// covered by the "created" entry) and SLA-breach rows (already covered by
    /// the escalation entries they raise).
    /// </summary>
    private static string? DescribeLifecycleChange(TicketStatusHistoryEntryDto change, TicketDetailDto ticket) => change switch
    {
        { OldValue: null } => null,
        { Dimension: nameof(TicketStatusDimension.SlaState) } when change.NewValue == nameof(SlaState.Breached) => null,
        { Dimension: nameof(TicketStatusDimension.TicketStatus) } when
            change.OldValue == nameof(TicketStatus.Closed) && change.NewValue == nameof(TicketStatus.InProgress) =>
            $"reopened ticket {ticket.TicketNumber} — from Closed",
        { Dimension: nameof(TicketStatusDimension.TicketStatus) } =>
            $"changed status: {TicketDisplay.TicketStatusLabel(change.OldValue!)} → {TicketDisplay.TicketStatusLabel(change.NewValue)}",
        { Dimension: nameof(TicketStatusDimension.SlaState) } =>
            $"SLA: {TicketDisplay.SlaStateLabel(change.OldValue!)} → {TicketDisplay.SlaStateLabel(change.NewValue)}",
        { Dimension: nameof(TicketStatusDimension.VerificationStatus) } =>
            $"verification: {TicketDisplay.VerificationStatusLabel(change.OldValue!)} → {TicketDisplay.VerificationStatusLabel(change.NewValue)}",
        { Dimension: nameof(TicketStatusDimension.ResolutionOutcome) } =>
            $"recorded outcome: {change.NewValue}",
        _ => null
    };

    /// <summary>
    /// The reopen entry's second line, from the typed event's facts. Read back
    /// through the same <see cref="ReopenActivityFacts"/> the server wrote them
    /// with — never hand-parsed here — and degrades to no detail line at all if
    /// the note is not one this version understands.
    /// </summary>
    private static string? DescribeReopenFacts(string? note, TicketNameResolver nameResolver)
    {
        if (!ReopenActivityFacts.TryParse(note, out var facts))
        {
            return null;
        }

        var from = nameResolver.TryGetDepartmentName(facts.FromDepartmentId) ?? $"Department #{facts.FromDepartmentId}";
        var to = nameResolver.TryGetDepartmentName(facts.ToDepartmentId) ?? $"Department #{facts.ToDepartmentId}";

        var parts = new List<string> { $"Department: {from} → {to}" };
        parts.Add(facts.ResultingOwnerEmployeeId is null ? "Assigned to: Department queue" : "Reassigned automatically");
        if (facts.ResolutionSlaDueAtUtc is { } due)
        {
            parts.Add($"Resolution SLA due: {due:yyyy-MM-dd HH:mm} UTC");
        }

        return string.Join(" · ", parts);
    }

    private static byte NextEscalationLevel(string current) => current switch
    {
        "None" => 1,
        "Level1" => 2,
        "Level2" => 3,
        _ => 4
    };

    public sealed class AssignInput
    {
        /// <summary>"employeeId|departmentId" — the (user, department) pair chosen in the assign control.</summary>
        [Required]
        public string Target { get; set; } = string.Empty;

        /// <summary>Required only when the chosen department is not the ticket's current one (it becomes the transfer reason).</summary>
        public string? Reason { get; set; }

        /// <summary>Why the ticket changes hands; optional here. Recorded by the Api only when this reassigns a ticket that already has an owner, or transfers it to another department.</summary>
        public TicketTransferReason? ReasonCode { get; set; }
        public string? RowVersionBase64 { get; set; }
    }

    public sealed class ClassifyInput
    {
        public int CategoryId { get; set; }
        public byte PriorityId { get; set; }
        public int RequestTypeId { get; set; }

        /// <summary>The ticket already has a request type (the Genesys AI set it): classification then only adds the category and priority.</summary>
        public bool RequestTypeAlreadySet { get; set; }
        public string? RowVersionBase64 { get; set; }
    }

    public sealed class TransferInput
    {
        [Required, Range(1, int.MaxValue)]
        public int TargetDepartmentId { get; set; }
        [Required]
        public string Reason { get; set; } = string.Empty;

        /// <summary>Required: a manual transfer always says why (Unable to Resolve / Needs Escalation / ...), so escalations can be reviewed.</summary>
        public TicketTransferReason? ReasonCode { get; set; }
        public string? RowVersionBase64 { get; set; }
    }

    public sealed class StatusInput
    {
        [Required]
        public string NewStatus { get; set; } = "Open";
        public string? RowVersionBase64 { get; set; }

        /// <summary>Required by the API when NewStatus is PendingCustomer — a ticket is never pending without a recorded why.</summary>
        public string? PendingReason { get; set; }
    }

    public sealed class ResolveInput
    {
        [Required]
        public string ResolutionOutcome { get; set; } = "Resolved";
        [Required]
        public string ResolutionNote { get; set; } = string.Empty;
        public long? DuplicateOfTicketId { get; set; }
        public string? RowVersionBase64 { get; set; }
    }

    public sealed class EscalateInput
    {
        [Range(1, 4)]
        public byte Level { get; set; } = 1;
        public string? Note { get; set; }
        public string? RowVersionBase64 { get; set; }
    }

    public sealed class DowngradeRequestInput
    {
        public byte NewPriorityId { get; set; }
        public string? Reason { get; set; }
    }

    public sealed class DowngradeDecisionInput
    {
        public long RequestId { get; set; }
        public string? DecisionNote { get; set; }
        public string? RowVersionBase64 { get; set; }
    }

    public sealed class RowVersionInput
    {
        public string? RowVersionBase64 { get; set; }
    }

    public sealed class ReopenInput
    {
        [Required]
        public string Reason { get; set; } = string.Empty;

        /// <summary>The department that takes the reopened work on. Required by the approved rule; may be the ticket's current department.</summary>
        [Required, Range(1, int.MaxValue)]
        public int TargetDepartmentId { get; set; }

        public string? RowVersionBase64 { get; set; }
    }

    public sealed class NoteInput
    {
        public string NoteText { get; set; } = string.Empty;
    }

    public sealed class ApprovalRequestInput
    {
        public string ApprovalType { get; set; } = string.Empty;
        public string? Comment { get; set; }
    }

    public sealed class ApprovalDecisionInput
    {
        public long ApprovalId { get; set; }
        public string Decision { get; set; } = "Approve";

        /// <summary>Optional on approval; the API requires it on rejection.</summary>
        public string? Comment { get; set; }
    }

    public sealed class WorkflowEventInput
    {
        public string EventType { get; set; } = string.Empty;
        public string? Note { get; set; }
    }
}
