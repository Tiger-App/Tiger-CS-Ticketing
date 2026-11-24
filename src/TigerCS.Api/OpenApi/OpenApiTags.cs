namespace TigerCS.Api.OpenApi;

/// <summary>
/// The canonical Swagger/OpenAPI tag names for this API, plus the
/// description shown next to each group in Swagger UI.
///
/// <para>
/// Tags are a <b>documentation grouping only</b>: they change nothing about
/// routing, authorization, or behaviour. They are declared here (rather than
/// as loose strings on each action) so that a tag is spelled exactly one way
/// across every controller, and so <see cref="Ordered"/> can fix the order
/// the groups appear in — Swagger UI otherwise sorts them alphabetically,
/// which would interleave the intake/ticket flow with unrelated groups.
/// </para>
///
/// <para>
/// Descriptions restate what the endpoints in the group already do; they
/// deliberately do not introduce business rules that are not already
/// implemented in the code they describe.
/// </para>
/// </summary>
public static class OpenApiTags
{
    public const string Health = "Health";
    public const string Authentication = "Authentication";
    public const string Users = "Users";
    public const string Roles = "Roles";
    public const string Departments = "Departments";
    public const string Categories = "Categories";
    public const string CrmLookup = "CRM Lookup";
    public const string CustomerVerification = "Customer Verification";
    public const string Channels = "Channels";
    public const string Intake = "Intake";
    public const string CustomerLookup = "Customer Lookup";
    public const string CustomerSearch = "Customer Search";
    public const string Dashboard = "Dashboard";
    public const string Reports = "Reports";
    public const string Tickets = "Tickets";
    public const string CustomerHistory = "Customer History";
    public const string CustomerDirectory = "Customer Directory";
    public const string CustomerProfile = "Customer Profile";
    public const string Assignment = "Assignment";
    public const string Transfer = "Transfer";
    public const string TicketLifecycle = "Ticket Lifecycle";
    public const string Notes = "Notes";
    public const string Approvals = "Approvals and Dependencies";
    public const string CrmReconciliation = "CRM Reconciliation";
    public const string SlaAndEscalation = "SLA and Escalation";
    public const string RequestTypes = "Request Types";
    public const string Genesys = "Genesys";
    public const string QueueingIntegration = "Queueing Integration";
    public const string CustomerReviews = "Customer Reviews";
    public const string EmployeeReviews = "Employee Reviews";
    public const string Collections = "Collections";
    public const string PendingCustomerInteractions = "Pending Customer Interactions";
    public const string Administration = "Administration";

    /// <summary>
    /// Every tag, in the order Swagger UI should render them — roughly the
    /// order an agent moves through the system: sign in, look the caller up,
    /// verify them, record the intake, create the ticket, then work it.
    /// </summary>
    public static readonly IReadOnlyList<(string Name, string Description)> Ordered =
    [
        (Health, "Unauthenticated liveness probe."),
        (Authentication, "Sign in and obtain a JWT access token, and sign out."),
        (Users, "The signed-in user's own profile, and administrator-only user activation."),
        (Roles, "The fixed role catalogue used across the system."),
        (Departments, "Department membership listings."),
        (Categories, "The active Ticket Category directory, optionally scoped to one Department — what the New Ticket UI's Category dropdown reads from."),
        (CrmLookup, "Read-only lookups against Tiger CRM: units and the contacts linked to a unit."),
        (CustomerVerification, "Verification sessions — recording that a requester was confirmed against a CRM unit and contact."),
        (Channels, "The configured channel directory (Admin → Configuration → Channels) — what the New Ticket wizard's Channel picker reads. Active channels only by default."),
        (Intake, "Intake records — every customer interaction is captured before customer lookup is attempted."),
        (CustomerLookup,
            "Searches CRM, PACT, and Tasleeh for the intake's phone number and returns whatever each source found. "
            + "Enrichment/identification only — a NotFound or Failed source never blocks ticket creation."),
        (CustomerSearch,
            "The Customer Workspace's standalone customer search by phone number — the same CRM Buyer and "
            + "PACT/Tasleeh lookups the New Ticket wizard uses, before any intake record exists. Read-only and "
            + "side-effect free."),
        (Dashboard,
            "The operational dashboard: KPI counts and the Tickets Requiring Attention list, computed over the "
            + "caller's own visible-department scope."),
        (Reports,
            "Management reports. Team Performance: one row per active CS Agent (Call Center agents are CS Agents in the "
            + "Call Center department) with Currently Assigned, Tickets Worked, Completed Follow-ups and SLA Breaches, "
            + "plus the records behind any one count. CS Manager, General Manager and Chairman/CEO only."),
        (Tickets, "Ticket creation and ticket queries (queue and detail)."),
        (CustomerHistory,
            "Customer -> previous ticket history, sourced entirely from the Tickets table (never a live CRM call). "
            + "Verified history is keyed by the exact CrmBuyerCustomerId the agent selected; unverified history falls back "
            + "to the persisted phone-number snapshot and is always labelled as such."),
        (CustomerDirectory,
            "The Customers directory: the paginated list of customers TigerCS already knows, derived from its own "
            + "persisted tickets, intakes and interactions (never a CRM list call), deduplicated by CRM Buyer id, then "
            + "external-verification identity, then intake phone — and the per-customer profile behind each row."),
        (CustomerProfile,
            "Ticket-anchored, live CRM Customer Details/Profile (Overview/Contact Info/Units) for the exact "
            + "CrmBuyerCustomerId the ticket was verified against, via the same CRM Buyer Lookup service the New "
            + "Ticket wizard uses. Previous Tickets on that page reuses the Customer History endpoint above unchanged."),
        (Assignment, "Assigning a ticket to an employee."),
        (Transfer, "Transferring a ticket to another department."),
        (TicketLifecycle, "Status changes, resolution, and closing."),
        (Notes, "Ticket notes."),
        (Approvals,
            "Approval cycles (request/approve/reject/cancel) and typed dependency events (prerequisites, maintenance) "
            + "— approval state is separate from TicketStatus; decisions produce the typed trigger events the SLA "
            + "phase reads."),
        (CrmReconciliation, "Linking a confirmed CRM match onto a ticket that did not have one at creation."),
        (SlaAndEscalation,
            "SLA due dates and breach state, recording the First Human Response, and manual escalation. "
            + "Automatic Level 2 escalation on an SLA breach is system-triggered by a background job and has no endpoint. "
            + "SLA pause/resume, priority change, and escalation responses are not part of this pilot."),
        (RequestTypes,
            "The active Request Type directory scoped to one Department — what the New Ticket wizard's Request Type "
            + "picker reads from. A request type resolves the workflow version a new ticket is pinned to."),
        (CustomerReviews,
            "The public customer satisfaction review page's backend. Anonymous: the opaque review token from a Resolved/Closed "
            + "email is the only credential. Looking up a link never records anything; a review is committed only by an explicit "
            + "submission, once per ticket."),
        (EmployeeReviews,
            "Customer star ratings of employees, collected on review tablets. The tablet endpoints are anonymous: the device token "
            + "(sent in the X-Device-Token header) is the only credential and the employee is always resolved from the tablet's own "
            + "assignment. The reporting endpoints are scoped to the caller's own visibility; tablet administration is System Administrator only."),
        (Genesys,
            "The three externally consumable Genesys contracts. CREATE: one normalized endpoint every channel "
            + "(phone, website chat, chatbot, WhatsApp, social media) converges on, idempotent on the Genesys "
            + "conversation id — one inquiry produces exactly one ticket, and a retry returns that same ticket. There "
            + "is no event field: posting means create-or-reuse, and a ringing call never reaches TigerCS. New tickets may be created "
            + "Unclassified (no category, request type or priority), and a customer-lookup failure never blocks "
            + "creation. LOOK UP: the call-pickup customer lookup, reusing the same CRM Buyer and PACT/Tasleeh "
            + "services the New Ticket wizard uses — no second CRM integration, and finding nobody is a normal 200. "
            + "UPDATE: one endpoint for everything Genesys owns about a ticket afterwards — agent context, "
            + "conversation end and transcript (Customer / VirtualAgent / HumanAgent / System, deduplicated on the "
            + "supplied messageId), and human-handoff state. It carries no field for category, "
            + "priority, status, owner or closure: those move through their own TigerCS operations, and ending a "
            + "conversation never closes the ticket."),
        (QueueingIntegration,
            "The receiving API for the Queueing System (QR-code queue tickets): one authenticated endpoint takes the eight queue events "
            + "(TicketCreated, TicketAssigned, TicketReassigned, TicketReturned, TicketCommentAdded, TicketRecalled, TicketClosed, TicketCancelled) and "
            + "mirrors them onto exactly one TigerCS ticket per queue ticket. The Queueing System is the source of truth for these tickets' lifecycle. "
            + "Idempotent on EventId, ordered by EventSequence, with unresolved events kept and retried. Authenticated by the integration's own API key, "
            + "not by a staff account."),
        (Collections,
            "Collections: balances, instalments and posted payments read from the authoritative financial source (never "
            + "calculated or stored by TigerCS — 503 while no source is integrated), payment reminders with duplicate "
            + "prevention per account/type/cycle/channel, delivery outcomes and customer responses. A response given in a "
            + "conversation creates or reuses its ticket through the Genesys conversation-id ingestion; nothing here posts a "
            + "payment, resolves or closes a ticket, or sends a legal notice. Collections:Enabled gates every route."),
        (PendingCustomerInteractions,
            "Customer interactions waiting for a human agent, on every channel — the agent work list. A callback is one "
            + "possible follow-up mode here, not the concept: a website chat, a WhatsApp thread and a social-media message "
            + "each continue differently. TigerCS records and surfaces the work; Genesys still owns channel delivery, "
            + "routing and execution. Completing a work item never closes its ticket."),
        (Administration,
            "System Administrator-only management of users, departments, request types, workflows and channels, including the "
            + "Workflow Designer's versioned Draft -> Validate -> Publish lifecycle. Every endpoint here requires the "
            + "System Administrator role; nothing is hidden by UI alone.")
    ];
}
