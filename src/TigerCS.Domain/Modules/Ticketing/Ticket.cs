using TigerCS.Domain.Modules.SlaAndEscalation;

// Alias needed because this type's own ResolutionOutcome *property* (byte?)
// shadows the ResolutionOutcome *enum* by simple name inside instance
// methods (C# prefers the instance member over the type in that position) —
// this gives instance methods below an unambiguous way to reference the
// enum's members.
using ResolutionOutcomeValue = TigerCS.Domain.Modules.Ticketing.ResolutionOutcome;

namespace TigerCS.Domain.Modules.Ticketing;

/// <summary>
/// MVP-ERD.md §2.10 / MVP-Data-Dictionary.md §2.10 — the ticket aggregate
/// root and its five independent lifecycle dimensions (ADR-0008): creation,
/// assignment/transfer, the status sub-machine, resolve/close, and — added
/// by the SLA and Escalation increment — first-response recording, the
/// <see cref="EscalationLevel"/> dimension, and the <see cref="SlaState"/>
/// projection of the two independent SLA clocks. Priority change
/// (MVP-Implementation-Backlog.md S-14) is still not exposed by any method
/// below.
///
/// <para>
/// <b>UnitReferenceId/ContactReferenceId/RequesterSnapshot are optional —
/// a customer match is enrichment, never a Ticket creation gate.</b>
/// Customer lookup (CRM/PACT/Tasleeh, <c>CustomerLookupAppService</c>) is
/// enrichment/identification, not a promotion gate: whether the lookup finds
/// a match, finds nothing, or a source fails to answer, ticket creation
/// proceeds the same way (<see cref="CreateUnverified"/>). Only when the
/// caller already has a resolved local unit/contact pair — because the agent
/// selected a lookup match — is <see cref="CreateVerified"/> used instead,
/// and both fields are populated together, never one without the other. A
/// ticket that started unverified is not stuck that way: it may later be
/// enriched or linked once customer/unit information becomes available,
/// via <see cref="ReconcileVerification"/>.
/// </para>
/// </summary>
public class Ticket
{
    public long TicketId { get; private set; }
    public string TicketNumber { get; private set; } = string.Empty;
    public int OriginatingDepartmentId { get; private set; }
    public int CurrentDepartmentId { get; private set; }
    public Guid? CurrentOwnerEmployeeId { get; private set; }

    /// <summary>
    /// The employee who created the ticket (the account that called ticket creation — for a Genesys inquiry that is the integration
    /// account). Write-once and never moved by assignment or department transfer, which is what lets a creator keep following their ticket
    /// <b>read-only</b> after it has been handed to another department. Null on tickets that predate the column and have no creation audit.
    /// </summary>
    public Guid? CreatedByEmployeeId { get; private set; }

    /// <summary>Records who created the ticket. Write-once: a second, different creator is ignored (returns false).</summary>
    public bool RecordCreator(Guid employeeId)
    {
        if (employeeId == Guid.Empty)
        {
            throw new ArgumentException("CreatedByEmployeeId must be a real employee.", nameof(employeeId));
        }

        if (CreatedByEmployeeId is not null)
        {
            return false;
        }

        CreatedByEmployeeId = employeeId;
        return true;
    }

    /// <summary>
    /// The first eligible Human Agent who owned the ticket: the agent who created it manually, or — for a ticket created by Genesys AI / a
    /// system integration, which starts with null — the first Call Center CS Agent who accepted or was assigned it. <b>Write-once</b>: department
    /// transfers, reassignment, repeated customer calls and background jobs never change it. It is what a ticket returning to the Call Center
    /// is routed back to. Distinct from <see cref="CreatedByEmployeeId"/> (for a Genesys ticket that is the integration account, not a person)
    /// and from <see cref="CurrentOwnerEmployeeId"/> (which moves with every assignment).
    /// </summary>
    public Guid? OriginalHumanAgentId { get; private set; }

    /// <summary>Records the Original Human Agent. Write-once: returns false and changes nothing when one is already recorded.</summary>
    public bool RecordOriginalHumanAgent(Guid employeeId)
    {
        if (employeeId == Guid.Empty)
        {
            throw new ArgumentException("OriginalHumanAgentId must be a real employee.", nameof(employeeId));
        }

        if (OriginalHumanAgentId is not null)
        {
            return false;
        }

        OriginalHumanAgentId = employeeId;
        return true;
    }
    public int? UnitReferenceId { get; private set; }
    public int? ContactReferenceId { get; private set; }
    /// <summary>
    /// The ticket's business classification — <b>null while the ticket is
    /// Unclassified</b>.
    ///
    /// <para>
    /// A ticket may legitimately exist before anyone knows what the customer
    /// is asking for: a Genesys inquiry reaches an agent with the department
    /// known (the queue, or the customer's own website-chat choice) but the
    /// actual request still unread. The ticket must be created immediately so
    /// the inquiry is never lost, and inventing a placeholder category to
    /// satisfy a NOT NULL column would be worse than null — it would feed
    /// real business machinery (routing, reporting, and anything later keyed
    /// on category) with a value nobody chose.
    /// </para>
    ///
    /// <para>
    /// Set exactly once, by <see cref="Classify"/>, when an agent reads the
    /// inquiry and selects the real category. Tickets created through the
    /// classified factories carry it from the start and are never Unclassified.
    /// </para>
    /// </summary>
    public int? CategoryId { get; private set; }

    /// <summary>
    /// The ticket's priority tier — <b>null while the ticket is
    /// Unclassified</b>, for the same reason <see cref="CategoryId"/> is.
    ///
    /// <para>
    /// A provisional priority is not harmless. Priority drives the dashboard
    /// KPI counts, queue sorting, the Tickets Requiring Attention ranking and
    /// the badge on every list row, so a defaulted Medium would place a
    /// ticket nobody has read yet among tickets whose urgency a human
    /// actually judged. Null says "not judged yet" and every one of those
    /// readers now handles it explicitly — never by coalescing it into a
    /// tier, and never by letting SQL's NULLs-sort-first put an unclassified
    /// ticket above Critical.
    /// </para>
    ///
    /// <para>
    /// Set exactly once, together with <see cref="CategoryId"/>, by
    /// <see cref="Classify"/>. That is also the point at which an SLA policy
    /// becomes selectable, since the policy is keyed on this value.
    /// </para>
    /// </summary>
    public byte? PriorityId { get; private set; }

    /// <summary>
    /// Whether the ticket carries a real business classification. False only
    /// for a ticket created from an inquiry whose request had not yet been
    /// read — see <see cref="CategoryId"/>.
    /// </summary>
    public bool IsClassified => CategoryId is not null;

    /// <summary>
    /// Workflow/SLA Configuration phase 2 — which configured
    /// <c>RequestType</c> this ticket follows, resolving its workflow
    /// template, capabilities, assignment rule, and request-type SLA layer.
    /// Nullable on purpose: tickets created before this phase (and callers
    /// that don't yet classify) carry null and keep the exact pre-phase-2
    /// behavior — workflow capabilities are only enforced when a request
    /// type is present, never guessed. Set once at creation via
    /// <see cref="ClassifyRequestType"/>; validation that it belongs to the
    /// ticket's department is the creating application service's job.
    /// </summary>
    public int? RequestTypeId { get; private set; }

    /// <summary>
    /// Administration / Workflow Designer phase — the exact workflow VERSION
    /// (<c>WorkflowTemplates</c> row) that was Published for the ticket's
    /// request type when the ticket was created. Write-once via
    /// <see cref="PinWorkflowVersion"/>: publishing a later version never
    /// changes it, so the ticket keeps following the flow it started on.
    /// Nullable: tickets without a request type carry null (no workflow
    /// governs them, exactly as before), and pre-versioning tickets were
    /// backfilled with the single template that governed them at the time —
    /// never a fabricated later version.
    /// </summary>
    public int? WorkflowTemplateId { get; private set; }

    /// <summary>
    /// Business-rule change: the real CRM Buyer Lookup match the agent
    /// selected (<c>GET /api/crm/buyers</c> — phone search only, never a
    /// Unit/Project search). A different identifier space from
    /// <see cref="UnitReferenceId"/>/<see cref="ContactReferenceId"/> (that
    /// pair is the older CRM-unit-number cache, ICrmGateway — see that
    /// interface's remarks); the four CRM Buyer ids are always set together
    /// or not at all, mirroring the Unit/Contact pair's own invariant. Set
    /// only when the agent explicitly selected one of the matched Buyer's
    /// eligible (Sold/Contract) units — CRM never auto-selects one.
    /// </summary>
    public int? CrmBuyerCustomerId { get; private set; }
    public int? CrmBuyerLeadId { get; private set; }
    public int? CrmBuyerUnitId { get; private set; }
    public int? CrmBuyerProjectId { get; private set; }

    /// <summary>Immutable, ticket-time snapshot of the selected CRM Buyer match's display text — never re-read from CRM afterward, same ADR-0007 reasoning as <see cref="TicketRequesterSnapshot"/>.</summary>
    public string? CrmBuyerCustomerName { get; private set; }
    public string? CrmBuyerProjectName { get; private set; }
    public string? CrmBuyerUnitNumber { get; private set; }

    /// <summary>
    /// Business-rule change: when CRM Buyer Lookup found no match (or CRM was
    /// unavailable), the agent manually enters Project and Unit Number
    /// instead — both required together, and never used to run another CRM
    /// lookup (CRM is searched by phone number only). Mutually exclusive with
    /// the CrmBuyer* fields above: a ticket carries one or the other, never
    /// both.
    /// </summary>
    public string? ManualProjectName { get; private set; }
    public string? ManualUnitNumber { get; private set; }

    /// <summary>
    /// Which external customer-lookup source verified the customer identity
    /// this ticket was created against, when it was not the real CRM Buyer
    /// Lookup — e.g. "Pact" (a <c>CustomerLookupSource</c> name, stored as a
    /// string so future sources need no schema change). A matched PACT/
    /// Tasleeh customer IS verified — against that source — even though no
    /// local UnitReference/ContactReference exists for it; what stays
    /// CRM-scoped is <see cref="VerificationStatus"/> (see
    /// <see cref="CreateFromExternalLookup"/>'s remarks). Null for CRM Buyer
    /// tickets (their source is the CrmBuyer* fields themselves) and for
    /// plain manual entry.
    /// </summary>
    public string? CustomerVerificationSource { get; private set; }

    /// <summary>
    /// The source's own identifier for the verified customer (for PACT, its
    /// tenantID) — an external identifier only, stored for auditability,
    /// reconciliation, and finding this customer's other tickets; never a
    /// foreign key, and never resolved through a local cache table (none
    /// exists for these sources). Travels with
    /// <see cref="CustomerVerificationSource"/>, never alone.
    /// </summary>
    public string? ExternalCustomerId { get; private set; }

    /// <summary>The source's own identifier for the selected unit (for PACT, its unitID) — same external-identifier-only discipline as <see cref="ExternalCustomerId"/>.</summary>
    public string? ExternalUnitId { get; private set; }

    /// <summary>
    /// Immutable, ticket-time snapshot of the verified external customer's
    /// own name and email, exactly as the source returned them at selection
    /// — the external counterpart of <see cref="CrmBuyerCustomerName"/>, and
    /// never re-read from the source afterward (ADR-0007, the same reasoning
    /// as <see cref="TicketRequesterSnapshot"/>).
    ///
    /// <para>
    /// PACT's <c>GET v1/contracts/{mobile}</c> returns exactly three customer
    /// identity fields — <c>customerName</c>, <c>customerEmail</c> and
    /// <c>customerMobile</c> — so these two are all there is to snapshot.
    /// <b>There is deliberately no Arabic-name column here</b>: PACT has no
    /// Arabic (or English/first/last) name field, and a column that no source
    /// can fill would only invite the English name being copied into it.
    /// Arabic names come from CRM alone, live, through
    /// <c>CustomerProfileDto.FullNameArabic</c>.
    /// </para>
    ///
    /// <para>
    /// Snapshot only: neither participates in customer identity. Two external
    /// customers are the same customer when
    /// <see cref="CustomerVerificationSource"/> + <see cref="ExternalCustomerId"/>
    /// match — never because they share a name, an email or a phone.
    /// </para>
    /// </summary>
    public string? ExternalCustomerName { get; private set; }
    public string? ExternalCustomerEmail { get; private set; }

    /// <summary>Column widths for the external snapshot — the same limits <see cref="TicketInteraction"/> holds externally-supplied customer text to.</summary>
    public const int ExternalCustomerNameMaxLength = 200;
    public const int ExternalCustomerEmailMaxLength = 256;

    public TicketStatus TicketStatus { get; private set; }
    public CrmVerificationStatus VerificationStatus { get; private set; }
    public EscalationLevel EscalationLevel { get; private set; }
    public SlaState SlaState { get; private set; }
    public byte? ResolutionOutcome { get; private set; }
    public long? DuplicateOfTicketId { get; private set; }

    public string RequestSummary { get; private set; } = string.Empty;
    public DateTime? FirstHumanResponseAtUtc { get; private set; }
    public DateTime? AcknowledgementSentAtUtc { get; private set; }
    public int ReopenCount { get; private set; }
    public DateTime CreatedAtUtc { get; private set; }

    /// <summary>
    /// MVP-Data-Dictionary.md §2.10 — optimistic concurrency token. Deferred
    /// by the ticket-creation increment ("no endpoint in this increment
    /// mutates an existing Ticket") to the increment that adds assignment/
    /// transfer/status-change — this one. EF Core maps this as SQL Server
    /// `rowversion`; never set from application code.
    /// </summary>
    public byte[] RowVersion { get; private set; } = [];

    private Ticket() { }

    /// <summary>The customer-match path: the caller already has a resolved local unit/contact pair (the agent selected a CRM lookup match). Fully verified from the moment it exists.</summary>
    public static Ticket CreateVerified(
        string ticketNumber,
        int departmentId,
        int unitReferenceId,
        int contactReferenceId,
        int categoryId,
        byte priorityId,
        string requestSummary,
        DateTime createdAtUtc)
    {
        var ticket = CreateCore(ticketNumber, departmentId, categoryId, priorityId, requestSummary, createdAtUtc);
        ticket.UnitReferenceId = unitReferenceId;
        ticket.ContactReferenceId = contactReferenceId;
        ticket.VerificationStatus = CrmVerificationStatus.Verified;
        ticket.SlaState = SlaState.Running;
        return ticket;
    }

    /// <summary>
    /// The default path whenever no resolved unit/contact pair is available
    /// at creation time — whether the intake is not unit-related, the
    /// customer lookup found no match, a lookup source failed to answer, or
    /// the agent simply proceeded without selecting one. Customer lookup is
    /// enrichment, never a Ticket creation gate (see this type's remarks), so
    /// none of those reasons changes this factory's behavior: no Unit/Contact
    /// reference and no requester snapshot exist yet, and the SLA clock still
    /// starts immediately — nothing here is pending on an external system.
    /// <see cref="VerificationStatus"/> stays <see cref="CrmVerificationStatus.Unverified"/>
    /// until (and unless) <see cref="ReconcileVerification"/> later links a
    /// unit/contact once one becomes available.
    /// </summary>
    /// <param name="ticketNumber">The generated, unique ticket number.</param>
    /// <param name="departmentId">The originating (and initial current) department.</param>
    /// <param name="categoryId">The selected, active Ticket Category.</param>
    /// <param name="priorityId">1=Critical, 2=High, 3=Medium, 4=Low.</param>
    /// <param name="requestSummary">The caller's request, in the agent's words.</param>
    /// <param name="createdAtUtc">Creation time, in UTC — also the SLA clock-start event.</param>
    /// <param name="manualProjectName">
    /// Business-rule change: required together with <paramref name="manualUnitNumber"/>
    /// when the CRM Buyer Lookup found no match for the intake's phone number
    /// (or CRM was unavailable) — the caller enforces the pairing before this
    /// factory runs (<c>TicketCreationAppService.CreateAsync</c>), same
    /// division of responsibility as <see cref="CreateVerifiedFromCrmBuyer"/>.
    /// </param>
    /// <param name="manualUnitNumber">See <paramref name="manualProjectName"/>.</param>
    public static Ticket CreateUnverified(
        string ticketNumber,
        int departmentId,
        int categoryId,
        byte priorityId,
        string requestSummary,
        DateTime createdAtUtc,
        string? manualProjectName = null,
        string? manualUnitNumber = null)
    {
        var ticket = CreateCore(ticketNumber, departmentId, categoryId, priorityId, requestSummary, createdAtUtc);
        ticket.VerificationStatus = CrmVerificationStatus.Unverified;
        ticket.SlaState = SlaState.Running;
        ticket.ManualProjectName = manualProjectName;
        ticket.ManualUnitNumber = manualUnitNumber;
        return ticket;
    }

    /// <summary>
    /// The real CRM Buyer Lookup match path: the agent explicitly selected
    /// one of a matched Buyer's eligible (Sold/Contract) units from
    /// <c>GET /api/crm/buyers</c>'s results. A different identifier space
    /// from <see cref="CreateVerified"/>'s Unit/Contact reference pair (that
    /// factory is the older CRM-unit-number cache path, ICrmGateway) — this
    /// one never touches <see cref="UnitReferenceId"/>/<see cref="ContactReferenceId"/>.
    /// Fully verified from the moment it exists, same as <see cref="CreateVerified"/>:
    /// a real CRM Buyer match is exactly the kind of confirmed customer
    /// identification <see cref="CrmVerificationStatus.Verified"/> means.
    /// </summary>
    public static Ticket CreateVerifiedFromCrmBuyer(
        string ticketNumber,
        int departmentId,
        int crmBuyerCustomerId,
        int crmBuyerLeadId,
        int crmBuyerUnitId,
        int crmBuyerProjectId,
        string? crmBuyerCustomerName,
        string? crmBuyerProjectName,
        string? crmBuyerUnitNumber,
        int categoryId,
        byte priorityId,
        string requestSummary,
        DateTime createdAtUtc)
    {
        var ticket = CreateCore(ticketNumber, departmentId, categoryId, priorityId, requestSummary, createdAtUtc);
        ticket.CrmBuyerCustomerId = crmBuyerCustomerId;
        ticket.CrmBuyerLeadId = crmBuyerLeadId;
        ticket.CrmBuyerUnitId = crmBuyerUnitId;
        ticket.CrmBuyerProjectId = crmBuyerProjectId;
        ticket.CrmBuyerCustomerName = crmBuyerCustomerName;
        ticket.CrmBuyerProjectName = crmBuyerProjectName;
        ticket.CrmBuyerUnitNumber = crmBuyerUnitNumber;
        ticket.VerificationStatus = CrmVerificationStatus.Verified;
        ticket.SlaState = SlaState.Running;
        return ticket;
    }

    /// <summary>
    /// The external-lookup path: the agent explicitly selected one customer's
    /// one unit from a matched PACT/Tasleeh result in the department-aware
    /// customer lookup. The customer identity IS verified — against that
    /// external source — and this factory records exactly which source and
    /// which of its own customer/unit identifiers
    /// (<see cref="CustomerVerificationSource"/>/<see cref="ExternalCustomerId"/>/
    /// <see cref="ExternalUnitId"/> — external identifiers only, never
    /// foreign keys and never a local cache row), alongside the same
    /// human-readable Project/Unit snapshot the manual path stores.
    ///
    /// <para>
    /// <see cref="VerificationStatus"/> deliberately stays
    /// <see cref="CrmVerificationStatus.Unverified"/>: that enum is the
    /// CRM-named concept the rest of the system keys on (reconciliation
    /// still may link a local CRM Unit/Contact reference later; a PACT match
    /// carries no such pair), so external verification is expressed by the
    /// source fields — display and reporting derive "Verified via PACT" from
    /// them — rather than by widening a CRM-scoped status.
    /// </para>
    ///
    /// <para>
    /// <paramref name="externalCustomerName"/>/<paramref name="externalCustomerEmail"/>
    /// snapshot the customer's own name and email as the source returned them
    /// (for PACT, <c>customerName</c> and <c>customerEmail</c>) — see
    /// <see cref="ExternalCustomerName"/>. Both are optional and default to
    /// null: a source may hold no name or no email for a customer it has
    /// verified, and neither is ever invented to fill the field.
    /// </para>
    /// </summary>
    public static Ticket CreateFromExternalLookup(
        string ticketNumber,
        int departmentId,
        string customerVerificationSource,
        string? externalCustomerId,
        string? externalUnitId,
        string? manualProjectName,
        string? manualUnitNumber,
        int categoryId,
        byte priorityId,
        string requestSummary,
        DateTime createdAtUtc,
        string? externalCustomerName = null,
        string? externalCustomerEmail = null)
    {
        if (string.IsNullOrWhiteSpace(customerVerificationSource))
        {
            throw new ArgumentException(
                "CustomerVerificationSource is required — external identifiers never travel without their source.",
                nameof(customerVerificationSource));
        }

        var ticket = CreateUnverified(
            ticketNumber, departmentId, categoryId, priorityId, requestSummary, createdAtUtc,
            manualProjectName, manualUnitNumber);
        ticket.CustomerVerificationSource = customerVerificationSource;
        ticket.ExternalCustomerId = externalCustomerId;
        ticket.ExternalUnitId = externalUnitId;
        // Blank is the same as absent: a source that returned "" or "   " has
        // no name/email on file, and an empty snapshot must not out-rank the
        // fallbacks the Customers directory would otherwise use. Over-long
        // text is truncated rather than allowed to fail the write — a
        // corporate tenant's name-plus-trade-licence can exceed the column,
        // and losing the tail of a display snapshot beats losing the ticket
        // (the same call TicketInteraction makes for the same two fields).
        ticket.ExternalCustomerName = Truncate(externalCustomerName, ExternalCustomerNameMaxLength);
        ticket.ExternalCustomerEmail = Truncate(externalCustomerEmail, ExternalCustomerEmailMaxLength);
        return ticket;
    }

    private static string? Truncate(string? value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var trimmed = value.Trim();
        return trimmed.Length <= maxLength ? trimmed : trimmed[..maxLength];
    }

    /// <summary>
    /// The <b>Unclassified</b> path: an inquiry reached an agent and must
    /// become a ticket immediately, but nobody has read the request yet, so
    /// its Category — and usually its Request Type — are genuinely unknown.
    /// Today's only producer is Genesys inquiry ingestion, where the
    /// department is known (the customer's website-chat choice, or the queue
    /// mapping) well before the request is.
    ///
    /// <para>
    /// <b>Nothing about the request is guessed.</b>
    /// <see cref="CategoryId"/>, <see cref="RequestTypeId"/> and
    /// <see cref="PriorityId"/> are all null, and no SLA period exists —
    /// <see cref="SlaState"/> is <see cref="SlaState.NotApplicable"/>. The
    /// SLA policy is selected by priority, and a priority nobody has judged
    /// would measure the wrong target and breach against a deadline nobody
    /// set. <see cref="Classify"/> is what supplies all three and starts the
    /// clock, from the moment the agent actually classifies.
    /// </para>
    ///
    /// <para>
    /// Everything else behaves exactly as any other new ticket: it is Open,
    /// it belongs to a real department, it is assignable, notable and
    /// transferable, its first human response is recordable, and its customer
    /// identification follows the same enrichment-never-a-gate rule as
    /// <see cref="CreateUnverified"/>.
    /// </para>
    /// </summary>
    /// <param name="ticketNumber">The generated, unique ticket number.</param>
    /// <param name="departmentId">The resolved (and initial current) department — known, never guessed.</param>
    /// <param name="requestSummary">What is known of the request — for a raw inquiry, how it arrived.</param>
    /// <param name="createdAtUtc">Creation time, in UTC.</param>
    /// <param name="manualProjectName">Optional project/tower snapshot the channel collected, same as <see cref="CreateUnverified"/>.</param>
    /// <param name="manualUnitNumber">Optional unit-number snapshot the channel collected.</param>
    /// <param name="defaultPriorityId">
    /// The configured default priority (Genesys tickets: the Normal tier). It
    /// is a default, not a judgement — category and request type stay null —
    /// but it selects the SLA policy, so the SLA clock starts at creation.
    /// Null keeps the original behavior: no priority and no SLA period.
    /// </param>
    public static Ticket CreateUnclassified(
        string ticketNumber,
        int departmentId,
        string requestSummary,
        DateTime createdAtUtc,
        string? manualProjectName = null,
        string? manualUnitNumber = null,
        byte? defaultPriorityId = null)
    {
        var ticket = CreateCore(ticketNumber, departmentId, categoryId: null, defaultPriorityId, requestSummary, createdAtUtc);
        ticket.VerificationStatus = CrmVerificationStatus.Unverified;
        // A defaulted priority selects the SLA policy, so the clock runs from
        // creation (the caller opens the period); without one there is no
        // policy to measure against and nothing starts.
        ticket.SlaState = defaultPriorityId is null ? SlaState.NotApplicable : SlaState.Running;
        ticket.ManualProjectName = manualProjectName;
        ticket.ManualUnitNumber = manualUnitNumber;
        return ticket;
    }

    private static Ticket CreateCore(
        string ticketNumber, int departmentId, int? categoryId, byte? priorityId, string requestSummary, DateTime createdAtUtc)
    {
        if (string.IsNullOrWhiteSpace(ticketNumber))
        {
            throw new ArgumentException("TicketNumber is required.", nameof(ticketNumber));
        }

        if (string.IsNullOrWhiteSpace(requestSummary))
        {
            throw new ArgumentException("RequestSummary is required.", nameof(requestSummary));
        }

        return new Ticket
        {
            TicketNumber = ticketNumber,
            // Write-once (MVP-Data-Dictionary.md §2.3/§2.10) — no method on
            // this class ever changes OriginatingDepartmentId after
            // construction; only CurrentDepartmentId moves, and only via a
            // transfer operation this increment does not implement.
            OriginatingDepartmentId = departmentId,
            CurrentDepartmentId = departmentId,
            CategoryId = categoryId,
            PriorityId = priorityId,
            RequestSummary = requestSummary,
            CreatedAtUtc = createdAtUtc,
            TicketStatus = TicketStatus.Open,
            EscalationLevel = EscalationLevel.None,
            ReopenCount = 0
        };
    }

    /// <summary>
    /// Links a unit/contact pair onto a ticket that did not have one at
    /// creation — the "later enrichment" story for a ticket that started
    /// <see cref="CrmVerificationStatus.Unverified"/> (business-rule change:
    /// customer lookup no longer gates creation, so this is the normal way a
    /// ticket picks up a customer match afterward, not a rare recovery path).
    ///
    /// <para>
    /// <b>Deliberately a bare state transition, not full orchestration —
    /// same division of responsibility as <see cref="CreateVerified"/>.</b>
    /// This method does not itself create a <c>TicketRequesterSnapshot</c>;
    /// sourcing a genuinely resolved <c>unitReferenceId</c>/
    /// <c>contactReferenceId</c> pair and constructing the resulting
    /// <c>TicketRequesterSnapshot</c> is the calling application service's
    /// job, done alongside this call in one transaction — see
    /// <c>TicketCreationAppService.CreateAsync</c> for the pattern this
    /// method's caller mirrors. A caller that invokes this with unvalidated
    /// IDs, or that skips writing the snapshot, produces a
    /// <see cref="CrmVerificationStatus.Verified"/> ticket that violates
    /// ADR-0007 — that correctness is this method's caller's responsibility,
    /// not something its own signature enforces.
    /// </para>
    /// </summary>
    public void ReconcileVerification(int unitReferenceId, int contactReferenceId)
    {
        if (VerificationStatus == CrmVerificationStatus.Verified)
        {
            throw new TicketAlreadyVerifiedException(TicketId);
        }

        UnitReferenceId = unitReferenceId;
        ContactReferenceId = contactReferenceId;
        VerificationStatus = CrmVerificationStatus.Verified;
    }

    /// <summary>
    /// Records which configured RequestType this ticket follows — write-once,
    /// at creation, by the creating application service (the same bare
    /// state-transition division of responsibility as
    /// <see cref="ReconcileVerification"/>: department/active validation
    /// happens before this is called).
    /// </summary>
    /// <summary>
    /// Records the real business classification an agent selected after
    /// reading the inquiry — the Unclassified → Classified transition of the
    /// lifecycle. <b>The same ticket is classified in place; classification
    /// never creates a second ticket.</b>
    ///
    /// <para>
    /// <b>Initial classification only — a Phase 1 safeguard, not a settled
    /// business rule.</b> A ticket that already carries a category is
    /// refused, because <i>re</i>classification has consequences nobody has
    /// specified yet: whether the SLA period is recomputed or restarted, what
    /// happens to a pinned workflow version mid-flight, and what happens to
    /// approvals already raised under the old request type. Until those are
    /// decided, refusing is the safe answer — it is not a statement that a
    /// classification may never change. <see cref="ClassifyRequestType"/>
    /// already guards its own field the same way, for the same reason.
    /// </para>
    ///
    /// <para>
    /// Priority is set rather than replaced: an unclassified ticket has none
    /// (<see cref="PriorityId"/> is null), and this is the first judgement
    /// anyone has made about the ticket's urgency.
    /// </para>
    ///
    /// <para>
    /// <b>A bare state transition, deliberately.</b> Validating that the
    /// category is active and belongs to the ticket's department, classifying
    /// the request type, pinning its workflow version, and starting the SLA
    /// clock are the calling application service's job, done in one
    /// transaction — the same division of responsibility as
    /// <see cref="CreateVerified"/> and <see cref="ReconcileVerification"/>.
    /// </para>
    /// </summary>
    public void Classify(int categoryId, byte priorityId)
    {
        EnsureNotClosed();

        if (CategoryId is { } already)
        {
            throw new TicketAlreadyClassifiedException(TicketId, already);
        }

        CategoryId = categoryId;
        PriorityId = priorityId;
    }

    /// <summary>
    /// Changes an already-classified ticket's priority.
    ///
    /// <para>
    /// <b>A bare priority decrease is never permitted</b> (ISSUE-023
    /// Option B; MVP-API-Contracts.md §3.4/§5.6): a downgrade takes effect
    /// only through an approved <c>PriorityDowngradeRequest</c>, so this
    /// throws <see cref="PriorityDowngradeRequiresApprovalException"/> unless
    /// <paramref name="downgradeApproved"/> is true, which only the approval
    /// service passes, inside the same transaction that replaces the SLA
    /// period. Priority ids rise as urgency falls (1=Critical ... 4=Low), so
    /// a "decrease" in priority is a numerically larger id.
    /// </para>
    ///
    /// <para>This method does not touch the SLA period; the caller owns that.</para>
    /// </summary>
    public void ChangePriority(byte newPriorityId, bool downgradeApproved = false)
    {
        EnsureNotClosed();

        if (PriorityId is not { } current)
        {
            throw new InvalidOperationException(
                $"Ticket {TicketId} has no priority yet; set it with Classify, which is not a downgrade.");
        }

        if (newPriorityId == current)
        {
            throw new ArgumentException("The new priority equals the current priority.", nameof(newPriorityId));
        }

        if (newPriorityId > current && !downgradeApproved)
        {
            throw new PriorityDowngradeRequiresApprovalException(TicketId, current, newPriorityId);
        }

        PriorityId = newPriorityId;
    }

    /// <summary>
    /// Gives a ticket that carries no priority the configured default. Only
    /// for an unclassified ticket created before default priorities existed
    /// (new Genesys tickets get theirs at creation); a ticket that already
    /// has a priority is never touched — a default must not overwrite a
    /// priority anyone set, and re-ingesting a conversation must not reset it.
    /// </summary>
    public void ApplyDefaultPriority(byte priorityId)
    {
        EnsureNotClosed();

        if (PriorityId is not null)
        {
            throw new InvalidOperationException(
                $"Ticket {TicketId} already has priority {PriorityId}; a default priority never replaces it.");
        }

        PriorityId = priorityId;
    }

    /// <summary>
    /// Records the approved default classification (Customer Services / General) on a ticket whose classification is missing, partial or
    /// invalid. <b>Classification only</b>: priority, SLA, status, department and owner are deliberately untouched, so applying the
    /// fallback can never restart a clock, change urgency or move work between people. Idempotent for the same values. Not guarded by
    /// <see cref="EnsureNotClosed"/>: a historical backfill records a label on a closed ticket without reopening or changing it.
    /// </summary>
    public void ApplyDefaultClassification(int categoryId, int requestTypeId, int workflowTemplateId)
    {
        CategoryId = categoryId;
        RequestTypeId = requestTypeId;
        WorkflowTemplateId = workflowTemplateId;
    }

    /// <summary>
    /// Replaces the default classification (Customer Services / General) with the real one once it is known — the explicit, authorized
    /// reclassification of a fallback ticket. The calling service decides that the current classification IS the fallback; this method
    /// only guards that the ticket is open. The request type and its pinned workflow are replaced together (or cleared together when the
    /// agent classified by category alone). Priority, SLA and ownership are the caller's concern and untouched here.
    /// </summary>
    public void ReplaceClassification(int categoryId, int? requestTypeId, int? workflowTemplateId)
    {
        EnsureNotClosed();
        if ((requestTypeId is null) != (workflowTemplateId is null))
        {
            throw new ArgumentException("A request type and its workflow version are replaced together.", nameof(workflowTemplateId));
        }

        CategoryId = categoryId;
        RequestTypeId = requestTypeId;
        WorkflowTemplateId = workflowTemplateId;
    }

    public void ClassifyRequestType(int requestTypeId)
    {
        if (RequestTypeId is not null)
        {
            throw new TicketRequestTypeAlreadySetException(TicketId, RequestTypeId.Value);
        }

        RequestTypeId = requestTypeId;
    }

    /// <summary>
    /// Records the Category that pairs with the request type the system just
    /// applied (Genesys classification). Unlike <see cref="Classify"/> this does
    /// not judge priority and may replace a category the system itself set when
    /// the AI refines the request type; the calling service decides that a
    /// person has not chosen the current category.
    /// </summary>
    public void ApplyCategoryFromRequestType(int categoryId)
    {
        EnsureNotClosed();
        CategoryId = categoryId;
    }

    /// <summary>
    /// Replaces the request type (and the workflow version pinned through it)
    /// of a ticket that already has one — the narrow, explicit exception to
    /// "classify once" that lets the Genesys AI refine its own classification
    /// while the conversation progresses. <b>Whether it is allowed is the
    /// calling service's decision</b> (no human has worked the ticket, no
    /// approval was raised under the old type); this method only guards the
    /// invariants it owns: the ticket is not closed and a request type was
    /// already set, so it can never be used to classify an unclassified ticket
    /// or to skip the write-once pin on the first classification.
    /// </summary>
    public void ReclassifyRequestType(int requestTypeId, int workflowTemplateId)
    {
        EnsureNotClosed();

        if (RequestTypeId is null)
        {
            throw new InvalidOperationException(
                $"Ticket {TicketId} has no request type to replace; use ClassifyRequestType for the first classification.");
        }

        RequestTypeId = requestTypeId;
        WorkflowTemplateId = workflowTemplateId;
    }

    /// <summary>
    /// Pins the workflow version the ticket follows — write-once, at creation,
    /// by the creating application service after it resolved the request
    /// type's currently Published version. Requires the request type to be
    /// classified first: a version is only ever pinned through a request type.
    /// </summary>
    public void PinWorkflowVersion(int workflowTemplateId)
    {
        if (RequestTypeId is null)
        {
            throw new InvalidOperationException(
                $"Ticket {TicketId} has no request type; a workflow version is only pinned through a request type.");
        }

        if (WorkflowTemplateId is not null)
        {
            throw new TicketWorkflowVersionAlreadyPinnedException(TicketId, WorkflowTemplateId.Value);
        }

        WorkflowTemplateId = workflowTemplateId;
    }

    /// <summary>MVP-API-Contracts.md §3.5 / §2.12 — sets the current owner and appends a superseding <see cref="TicketAssignment"/> row is the caller's job (this method only updates the ticket's own denormalized pointer).</summary>
    public void AssignTo(Guid employeeId)
    {
        EnsureNotClosed();

        if (employeeId == Guid.Empty)
        {
            throw new ArgumentException("AssignedEmployeeId is required.", nameof(employeeId));
        }

        CurrentOwnerEmployeeId = employeeId;
    }

    /// <summary>
    /// Clears the current owner without moving the ticket: it returns to its department's queue. Used only to release an AUTOMATIC
    /// pre-assignment (Round Robin / rule) when the person who actually answered the interaction cannot take the ticket, so it is never left
    /// with somebody who did not. The caller supersedes the assignment row and audits it. A no-op when nobody owns the ticket.
    /// </summary>
    public void ReleaseOwner()
    {
        EnsureNotClosed();
        CurrentOwnerEmployeeId = null;
    }

    /// <summary>MVP-API-Contracts.md §3.6 — moves CurrentDepartmentId, leaves OriginatingDepartmentId untouched (write-once, MVP-ERD.md §2.3), and clears the current owner: the receiving department must explicitly claim/assign it.</summary>
    public void TransferToDepartment(int targetDepartmentId, bool preserveOwner = false)
    {
        EnsureNotClosed();

        if (targetDepartmentId == CurrentDepartmentId)
        {
            throw new TicketAlreadyInTargetDepartmentException(TicketId, targetDepartmentId);
        }

        CurrentDepartmentId = targetDepartmentId;

        // The department and the CS Agent are independent fields: a business transfer keeps the agent unless the caller reassigns explicitly.
        // Only system routing that deliberately releases an automatic pre-assignment (Call Center first) clears it.
        if (!preserveOwner)
        {
            CurrentOwnerEmployeeId = null;
        }
    }

    /// <summary>
    /// Solution-Analysis.md §5.6's TicketStatus transition table, restricted
    /// to the "work" sub-machine this method owns: Open→InProgress
    /// (requires an already-assigned owner) and InProgress↔PendingCustomer.
    /// Resolved/Closed are reached only via <see cref="Resolve"/>/
    /// <see cref="Close"/>, and Closed returns to InProgress only via
    /// <see cref="Reopen"/> — deliberately distinct operations, not reachable
    /// through this method.
    ///
    /// <para>
    /// <b>PendingCustomer is optional.</b> A ticket may go straight from
    /// InProgress to Resolved; nothing requires it to pass through a Pending
    /// state first.
    /// </para>
    ///
    /// <para>
    /// <b><see cref="TicketStatus.PendingThirdParty"/> is legacy-readable
    /// only</b> (approved lifecycle cleanup). No arm of
    /// <see cref="TicketStatusTransitions.IsChangeStatusAllowed"/> produces
    /// it, so no ticket can enter it again through any caller — Api included
    /// — while a ticket that was already in it keeps its single escape back
    /// to InProgress.
    /// </para>
    /// </summary>
    public void ChangeStatus(TicketStatus newStatus)
    {
        EnsureNotClosed();

        if (!TicketStatusTransitions.IsChangeStatusAllowed(TicketStatus, newStatus))
        {
            throw new InvalidTicketStatusTransitionException(TicketId, TicketStatus, newStatus);
        }

        if (newStatus == TicketStatus.InProgress && TicketStatus == TicketStatus.Open && CurrentOwnerEmployeeId is null)
        {
            throw new TicketNotAssignedException(TicketId);
        }

        TicketStatus = newStatus;
    }

    /// <summary>
    /// MVP-API-Contracts.md §3.9 / Solution-Analysis.md §5.6 — marks the
    /// underlying work done. Valid only from InProgress or PendingCustomer
    /// (never directly from Open, never twice), and PendingCustomer is
    /// optional on the way: InProgress→Resolved is the ordinary path. Does
    /// not itself create the <see cref="TicketResolution"/> row or write
    /// audit/history — mirrors <see cref="CreateVerified"/>'s "bare state
    /// transition" division of responsibility; the calling application
    /// service does both in the same transaction.
    ///
    /// <para>
    /// A legacy <see cref="TicketStatus.PendingThirdParty"/> ticket is
    /// deliberately <i>not</i> resolvable in place: its one sanctioned exit is
    /// <see cref="ChangeStatus"/> back to InProgress, from where it resolves
    /// like any other ticket. That keeps the active lifecycle to a single
    /// shape rather than preserving a second, legacy-only route into
    /// Resolved.
    /// </para>
    /// </summary>
    public void Resolve(ResolutionOutcomeValue outcome, long? duplicateOfTicketId)
    {
        EnsureNotClosed();

        if (TicketStatus is not (TicketStatus.InProgress or TicketStatus.PendingCustomer))
        {
            throw new TicketNotEligibleForResolutionException(TicketId, TicketStatus);
        }

        TicketStatus = TicketStatus.Resolved;
        ResolutionOutcome = (byte)outcome;
        DuplicateOfTicketId = outcome == ResolutionOutcomeValue.Duplicate ? duplicateOfTicketId : null;
    }

    /// <summary>
    /// Chatbot inactivity closure: the customer never answered the chatbot, so
    /// the ticket is closed <b>without</b> anyone having worked or confirmed
    /// it. The lifecycle still requires Resolved before Closed, so this
    /// performs both transitions in one step and returns the status the ticket
    /// held before — the caller writes the two history rows (old→Resolved,
    /// Resolved→Closed) and the resolution row, with outcome
    /// <see cref="ResolutionOutcomeValue.Cancelled"/>: the honest outcome,
    /// because nothing here says the customer's issue was resolved.
    ///
    /// <para>
    /// Deliberately narrower than <see cref="Resolve"/> in what it will
    /// <i>not</i> do: a ticket with a human owner is refused
    /// (<see cref="TicketNotEligibleForResolutionException"/>) — the human has
    /// taken it, so a silent customer is theirs to follow up. It is the only
    /// path that resolves an <see cref="TicketStatus.Open"/> ticket, because a
    /// chatbot ticket nobody has been assigned cannot be moved to InProgress
    /// (<see cref="ChangeStatus"/> requires an owner).
    /// </para>
    /// </summary>
    public TicketStatus CloseForCustomerInactivity()
    {
        EnsureNotClosed();

        if (TicketStatus is not (TicketStatus.Open or TicketStatus.InProgress or TicketStatus.PendingCustomer)
            || CurrentOwnerEmployeeId is not null)
        {
            throw new TicketNotEligibleForResolutionException(TicketId, TicketStatus);
        }

        var previous = TicketStatus;
        TicketStatus = TicketStatus.Resolved;
        ResolutionOutcome = (byte)ResolutionOutcomeValue.Cancelled;
        DuplicateOfTicketId = null;
        TicketStatus = TicketStatus.Closed;
        ClearOwnerOnClosure();
        return previous;
    }

    /// <summary>
    /// Call Center tickets are worked from the moment they exist: a ticket that originates in the Call Center department starts
    /// <see cref="TicketStatus.InProgress"/> instead of <see cref="TicketStatus.Open"/>. Creation-time only — a persisted
    /// ticket moves through <see cref="ChangeStatus"/>, which still demands an owner for Open→InProgress. Leaves
    /// <see cref="OriginatingDepartmentId"/> (write-once) and every other field untouched.
    /// </summary>
    public void StartInProgressAtCreation()
    {
        if (TicketId != 0 || TicketStatus != TicketStatus.Open)
        {
            throw new InvalidTicketStatusTransitionException(TicketId, TicketStatus, TicketStatus.InProgress);
        }

        TicketStatus = TicketStatus.InProgress;
    }

    /// <summary>
    /// Document-delivery closure (Genesys chatbot / voicebot): the document workflow reached its end, so the ticket goes
    /// Resolved → Closed in one step with the given <paramref name="outcome"/> — <see cref="ResolutionOutcomeValue.Resolved"/>
    /// when the document was sent, <see cref="ResolutionOutcomeValue.Cancelled"/> when it could not be delivered (never "Resolved":
    /// nothing was fixed). Returns the status held before so the caller writes the history rows. A human owner does not block it:
    /// the request that ends the workflow is the customer's own document request, not an inactivity guess.
    /// </summary>
    public TicketStatus CloseForDocumentDelivery(ResolutionOutcomeValue outcome)
    {
        EnsureNotClosed();

        if (outcome is not (ResolutionOutcomeValue.Resolved or ResolutionOutcomeValue.Cancelled)
            || TicketStatus is not (TicketStatus.Open or TicketStatus.InProgress or TicketStatus.PendingCustomer))
        {
            throw new TicketNotEligibleForResolutionException(TicketId, TicketStatus);
        }

        var previous = TicketStatus;
        TicketStatus = TicketStatus.Resolved;
        ResolutionOutcome = (byte)outcome;
        DuplicateOfTicketId = null;
        TicketStatus = TicketStatus.Closed;
        ClearOwnerOnClosure();
        return previous;
    }

    /// <summary>
    /// Genesys AI completion: the virtual agent finished the conversation (<c>closedByAi</c>), so the ticket goes Resolved → Closed in one
    /// step with outcome <see cref="ResolutionOutcomeValue.Resolved"/>. Refused for a ticket a person owns
    /// (<see cref="TicketNotEligibleForResolutionException"/>) — a human has taken it, so the AI cannot close it — and for one already closed.
    /// Returns the status held before so the caller writes the history rows.
    /// </summary>
    public TicketStatus CloseForAiCompletion()
    {
        EnsureNotClosed();

        if (TicketStatus is not (TicketStatus.Open or TicketStatus.InProgress or TicketStatus.PendingCustomer)
            || CurrentOwnerEmployeeId is not null)
        {
            throw new TicketNotEligibleForResolutionException(TicketId, TicketStatus);
        }

        return CloseForDocumentDelivery(ResolutionOutcomeValue.Resolved);
    }

    /// <summary>
    /// AI-only time limit: a ticket the AI virtual agent alone has handled and nobody finished is closed by the system through the
    /// normal Resolved → Closed transition (outcome <see cref="ResolutionOutcomeValue.Resolved"/>, never Cancelled). Same preconditions as <see cref="CloseForAiCompletion"/>:
    /// open for work and not owned by a person. Returns the status held before so the caller writes the history rows.
    /// </summary>
    public TicketStatus CloseForAiOnlyTimeout()
    {
        EnsureNotClosed();

        if (TicketStatus is not (TicketStatus.Open or TicketStatus.InProgress or TicketStatus.PendingCustomer)
            || CurrentOwnerEmployeeId is not null)
        {
            throw new TicketNotEligibleForResolutionException(TicketId, TicketStatus);
        }

        return CloseForDocumentDelivery(ResolutionOutcomeValue.Resolved);
    }

    /// <summary>MVP-API-Contracts.md §3.10 — the final, CS-layer-only close, distinct from Resolve. Requires a current TicketResolutions row (enforced by the caller — this method only enforces the TicketStatus precondition).</summary>
    public void Close()
    {
        // Closing an already-Closed ticket is closed-ticket immutability
        // (PR correction), not "not yet resolved" — a genuinely different
        // condition from the check below, which is why this method checks
        // EnsureNotClosed() explicitly first rather than letting the
        // `!= Resolved` branch (also true for Closed) catch it implicitly.
        EnsureNotClosed();

        if (TicketStatus != TicketStatus.Resolved)
        {
            throw new TicketNotYetResolvedException(TicketId);
        }

        TicketStatus = TicketStatus.Closed;
        ClearOwnerOnClosure();
    }

    /// <summary>
    /// A Closed ticket has no owner: nobody is working it any more. Called by every transition INTO <see cref="TicketStatus.Closed"/> (manual,
    /// document delivery, AI completion, customer inactivity). The caller captures the previous owner BEFORE closing - for the audit trail - and
    /// supersedes the current <see cref="TicketAssignment"/> row (history is kept). Open / InProgress / Resolved tickets are never touched by this.
    /// </summary>
    private void ClearOwnerOnClosure() => CurrentOwnerEmployeeId = null;

    /// <summary>
    /// FR-RES-04 / MVP-API-Contracts.md §3.11 — Reopen is a domain event,
    /// not a status value: a <b>Closed</b> ticket returns to InProgress in a
    /// caller-chosen department, <see cref="ReopenCount"/> increments, and
    /// the live <see cref="ResolutionOutcome"/> returns to unset
    /// (Solution-Analysis.md §5's "the field returns to unset until the
    /// ticket is resolved/closed again"). The prior outcome is preserved as
    /// history on its TicketResolutions row — flipping that row's IsCurrent,
    /// writing status history, the workflow event and audit are the calling
    /// application service's job, done in the same transaction (same bare
    /// state-transition division of responsibility as <see cref="Resolve"/>/
    /// <see cref="Close"/>).
    ///
    /// <para>
    /// <b>Closed is the only reopenable status</b> (approved business rule).
    /// Resolved is deliberately NOT reopenable: work that is merely marked
    /// done has not yet been confirmed to the customer, so it is corrected by
    /// continuing the existing work item, never by a reopen cycle. This
    /// method is therefore the single sanctioned exit from closed-ticket
    /// immutability, and is for that reason not gated on
    /// <see cref="EnsureNotClosed"/>.
    /// </para>
    ///
    /// <para>
    /// <b>Only a Resolved outcome is reopenable.</b> Cancelled, Rejected and
    /// Duplicate are terminal dispositions, not incomplete work: a customer
    /// coming back on one of those raises a new ticket. Because a Duplicate
    /// ticket can therefore never reach this method,
    /// <see cref="DuplicateOfTicketId"/> is deliberately left untouched —
    /// clearing it here would only ever be a no-op that implied otherwise.
    /// </para>
    ///
    /// <para>
    /// <b>The department moves and the owner is cleared</b>, exactly as
    /// <see cref="TransferToDepartment"/> does: the closing department's
    /// employee cannot stay accountable for work that has been handed back,
    /// and the reopening agent names the department that now owns it (which
    /// may legitimately be the same one). Re-assignment inside that
    /// department is the calling service's job, through the one existing
    /// assignment automation. The reopen window (ISSUE-011 — 7 days,
    /// configurable) is a clock-dependent business rule owned by the
    /// application service; this method carries no clock.
    /// </para>
    /// </summary>
    /// <param name="targetDepartmentId">The department that takes responsibility for the reopened work.</param>
    /// <param name="closedForCustomerInactivity">
    /// The one carve-out from the Resolved-only rule: the caller has read the
    /// current resolution and it is the chatbot inactivity closure
    /// (<see cref="TicketResolution.ClosedForCustomerInactivity"/>), so a
    /// <b>Cancelled</b> outcome is reopenable for this closure — and only this
    /// one. Rejected, Duplicate and every other Cancelled stay final.
    /// </param>
    public void Reopen(int targetDepartmentId, bool closedForCustomerInactivity = false)
    {
        if (TicketStatus is not TicketStatus.Closed)
        {
            throw new TicketNotEligibleForReopenException(TicketId, TicketStatus);
        }

        var reopenableOutcome = ResolutionOutcome == (byte)ResolutionOutcomeValue.Resolved
            || (closedForCustomerInactivity && ResolutionOutcome == (byte)ResolutionOutcomeValue.Cancelled);
        if (!reopenableOutcome)
        {
            throw new TicketResolutionOutcomeNotReopenableException(TicketId, ResolutionOutcome);
        }

        if (targetDepartmentId <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(targetDepartmentId), targetDepartmentId, "A reopened ticket must name the department that takes it on.");
        }

        TicketStatus = TicketStatus.InProgress;
        ResolutionOutcome = null;
        CurrentDepartmentId = targetDepartmentId;
        CurrentOwnerEmployeeId = null;
        ReopenCount++;
    }

    /// <summary>
    /// Reopen's SLA-dimension counterpart: a reopened ticket has a brand new
    /// Resolution SLA cycle running, so a clock that had settled on
    /// <see cref="SlaState.Met"/> is running again.
    ///
    /// <para>
    /// Deliberately narrow, mirroring <see cref="StartSlaClock"/>: it only
    /// ever makes that one transition. <see cref="SlaState.Breached"/> is
    /// sticky and is never resurrected (MVP-ERD.md §2.15's immutability
    /// rule), and <see cref="SlaState.NotApplicable"/>/<see cref="SlaState.Paused"/>
    /// are left alone — a ticket with no running clock does not acquire one
    /// by being reopened. The period rows themselves are the calling
    /// application service's job.
    /// </para>
    /// </summary>
    public void RestartSlaClockForReopen()
    {
        if (SlaState == SlaState.Met)
        {
            SlaState = SlaState.Running;
        }
    }

    /// <summary>
    /// Records that the <b>automated</b> acknowledgement (FR-NOT-01) was
    /// delivered.
    ///
    /// <para>
    /// <b>This is not, and can never become, a First Response event.</b>
    /// FR-SLA-05/ISSUE-019 exist precisely because an acknowledgement fires
    /// within seconds of creation and would otherwise "satisfy" every
    /// first-response target automatically, making the KPI meaningless. This
    /// method therefore writes <see cref="AcknowledgementSentAtUtc"/> and
    /// touches nothing else — not <see cref="FirstHumanResponseAtUtc"/>, not
    /// <see cref="SlaState"/>, not <see cref="TicketStatus"/>. The two
    /// timestamps are separate columns (MVP-Data-Dictionary.md §2.10) read by
    /// separate code paths, and <c>SlaBreachProcessor</c> resolves First
    /// Response from <see cref="FirstHumanResponseAtUtc"/> alone.
    /// </para>
    ///
    /// <para>
    /// Write-once, and deliberately <i>not</i> gated on
    /// <see cref="EnsureNotClosed"/>. Write-once is the delivery-side
    /// duplicate guard: a redelivered Outbox message reaching an
    /// already-acknowledged ticket is refused here rather than emailing the
    /// customer twice. The closed-ticket gate is omitted because an
    /// acknowledgement is an asynchronous consequence of creation, and a
    /// ticket created and closed within the dispatcher's polling interval
    /// must still be able to record what was already sent — refusing would
    /// lose the record of a delivery that genuinely happened, which is worse
    /// than recording it.
    /// </para>
    /// </summary>
    public void RecordAcknowledgementSent(DateTime sentAtUtc)
    {
        if (AcknowledgementSentAtUtc is { } already)
        {
            throw new AcknowledgementAlreadySentException(TicketId, already);
        }

        AcknowledgementSentAtUtc = sentAtUtc;
    }

    /// <summary>
    /// ISSUE-019 / MVP-API-Contracts.md §5.2 — records the first genuine,
    /// human-authored response to the customer. Write-once at the ticket
    /// level (MVP-ERD.md §2.10): a second call throws rather than
    /// overwriting, because the First Response SLA is measured against the
    /// <i>first</i> engagement and a later correction would silently move a
    /// contractual measurement.
    ///
    /// <para>
    /// The automated acknowledgement never reaches this method, on any
    /// channel — that is <see cref="AcknowledgementSentAtUtc"/>'s separate
    /// job, and keeping the two apart is the whole point of ISSUE-019.
    /// </para>
    ///
    /// <para>
    /// <paramref name="occurredAtUtc"/> may precede
    /// <see cref="CreatedAtUtc"/>: SLA-Architecture.md §16's Example E has a
    /// Genesys call answered at 08:58 satisfying a ticket created moments
    /// later at 09:00, and the actual moment of human engagement is the
    /// correct value there. No Genesys adapter ships in this increment; the
    /// method simply does not forbid the case its own approved example
    /// requires.
    /// </para>
    /// </summary>
    public void RecordFirstHumanResponse(DateTime occurredAtUtc)
    {
        EnsureNotClosed();

        if (FirstHumanResponseAtUtc is { } already)
        {
            throw new FirstResponseAlreadyRecordedException(TicketId, already);
        }

        FirstHumanResponseAtUtc = occurredAtUtc;
    }

    /// <summary>
    /// Advances the <see cref="EscalationLevel"/> dimension — and nothing
    /// else.
    ///
    /// <para>
    /// <b>Escalation is a separate dimension from <see cref="TicketStatus"/></b>
    /// (ADR-0008, Solution-Analysis.md §5.3/§7.6: "escalating a ticket never
    /// removes it from active work"). This method deliberately does not
    /// touch <see cref="TicketStatus"/>, <see cref="ResolutionOutcome"/> or
    /// <see cref="SlaState"/>, so an escalated ticket stays exactly as Open
    /// or InProgress as it was.
    /// </para>
    ///
    /// <para>
    /// The level only ever rises. ADR-0011 models it as a state rather than
    /// a counter, so an automatic Level 2 raised by a later breach must not
    /// pull a ticket back down from a Level 4 a CS Manager already set.
    /// </para>
    /// </summary>
    public void RaiseEscalationLevel(EscalationLevel newLevel)
    {
        EnsureNotClosed();

        if (newLevel <= EscalationLevel)
        {
            throw new EscalationLevelCannotBeLoweredException(TicketId, EscalationLevel, newLevel);
        }

        EscalationLevel = newLevel;
    }

    /// <summary>
    /// Projects a recorded SLA breach onto the ticket's own
    /// <see cref="SlaState"/> dimension (SLA-Architecture.md §11).
    ///
    /// <para>
    /// The two clocks breach independently and are tracked independently on
    /// <c>TicketSlaInstances</c> (ADR-0009); <see cref="SlaState"/> is a
    /// single column, so it carries the ticket-level summary: breached if
    /// <i>either</i> clock is. Sticky, matching the breach flags' own
    /// immutability rule (MVP-ERD.md §2.15) — nothing moves this back to
    /// Running or Met.
    /// </para>
    /// </summary>
    public void MarkSlaBreached() => SlaState = SlaState.Breached;

    /// <summary>
    /// Marks the SLA met once the resolution landed within its deadline.
    /// A breach already recorded wins and is never downgraded to Met, so a
    /// ticket that missed its First Response target and then resolved on
    /// time still reports Breached.
    /// </summary>
    public void MarkSlaMet()
    {
        if (SlaState != SlaState.Breached)
        {
            SlaState = SlaState.Met;
        }
    }

    /// <summary>
    /// Moves the <see cref="SlaState"/> dimension from
    /// <see cref="SlaState.NotApplicable"/> to <see cref="SlaState.Running"/>
    /// when an Unclassified ticket's first SLA period is opened at
    /// classification.
    ///
    /// <para>
    /// Deliberately narrow: it only ever makes that one transition, so it
    /// cannot resurrect a Met or Breached clock, and it is a no-op on a
    /// ticket whose clock was already running (every classified ticket).
    /// The period itself is opened by the calling application service — same
    /// bare state-transition division of responsibility as
    /// <see cref="Classify"/>.
    /// </para>
    /// </summary>
    public void StartSlaClock()
    {
        if (SlaState == SlaState.NotApplicable)
        {
            SlaState = SlaState.Running;
        }
    }

    /// <summary>
    /// SLA pause (ISSUE-018): <see cref="SlaState.Running"/> becomes
    /// <see cref="SlaState.Paused"/>. Narrow like <see cref="StartSlaClock"/>:
    /// a Breached, Met or NotApplicable ticket is left alone, so a pause can
    /// never hide a recorded breach. The pause row itself is the application
    /// service's job.
    /// </summary>
    /// <returns>True when this call changed the state.</returns>
    public bool PauseSla()
    {
        if (SlaState != SlaState.Running)
        {
            return false;
        }

        SlaState = SlaState.Paused;
        return true;
    }

    /// <summary>
    /// SLA resume: <see cref="SlaState.Paused"/> becomes
    /// <see cref="SlaState.Running"/>; every other state (notably a sticky
    /// Breached recorded during the pause by the First Response clock) is
    /// left as it is.
    /// </summary>
    /// <returns>True when this call changed the state.</returns>
    public bool ResumeSla()
    {
        if (SlaState != SlaState.Paused)
        {
            return false;
        }

        SlaState = SlaState.Running;
        return true;
    }

    /// <summary>Closed-ticket immutability (PR correction): every mutating method above calls this first — a Closed ticket accepts no further Assign/Transfer/ChangeStatus/Resolve/Close.</summary>
    private void EnsureNotClosed()
    {
        if (TicketStatus == TicketStatus.Closed)
        {
            throw new TicketClosedException(TicketId);
        }
    }
}
