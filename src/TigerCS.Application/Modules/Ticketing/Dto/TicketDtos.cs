namespace TigerCS.Application.Modules.Ticketing.Dto;

/// <summary>
/// Create a ticket from an IntakeRecord. Customer information from CRM,
/// PACT, or Tasleeh is attached when available; lack of a match does not
/// prevent ticket creation — Ticket Category is the only thing every ticket
/// requires (business-rule change: customer lookup is enrichment, never a
/// creation gate, for unit-related and non-unit-related intakes alike).
/// </summary>
/// <param name="IntakeRecordId">Required. The intake record to promote. It must not already be linked to a ticket.</param>
/// <param name="UnitReferenceId">
/// Optional. The local reference id of a unit the agent selected from a
/// customer-lookup match (<c>GET /api/intake-records/{id}/customer-lookup</c>).
/// Must be supplied together with <paramref name="ContactReferenceId"/> or
/// not at all — never one without the other.
/// </param>
/// <param name="ContactReferenceId">Optional. The matching contact's local reference id — see <paramref name="UnitReferenceId"/>.</param>
/// <param name="CrmBuyerCustomerId">
/// Optional. The real CRM Buyer Lookup match's customer id
/// (<c>GET /api/crm/buyers</c> — phone search only) the agent selected.
/// Must be supplied together with <paramref name="CrmBuyerLeadId"/>,
/// <paramref name="CrmBuyerUnitId"/>, and <paramref name="CrmBuyerProjectId"/>
/// or not at all. Mutually exclusive with <paramref name="ManualProjectName"/>/
/// <paramref name="ManualUnitNumber"/> — a ticket carries a real CRM Buyer
/// match or a manually-entered Project/Unit Number, never both.
/// </param>
/// <param name="CrmBuyerLeadId">Optional. See <paramref name="CrmBuyerCustomerId"/>.</param>
/// <param name="CrmBuyerUnitId">Optional. See <paramref name="CrmBuyerCustomerId"/>.</param>
/// <param name="CrmBuyerProjectId">Optional. See <paramref name="CrmBuyerCustomerId"/>.</param>
/// <param name="CrmBuyerCustomerName">Optional display snapshot of the selected Buyer's name, captured at ticket-creation time.</param>
/// <param name="CrmBuyerProjectName">Optional display snapshot of the selected unit's project name.</param>
/// <param name="CrmBuyerUnitNumber">Optional display snapshot of the selected unit's number.</param>
/// <param name="ManualProjectName">
/// Required, together with <paramref name="ManualUnitNumber"/>, whenever no
/// <paramref name="CrmBuyerUnitId"/> is supplied — i.e. CRM Buyer Lookup
/// found no match for the intake's phone number, or CRM was unavailable.
/// Never used to run another CRM lookup — CRM is searched by phone number
/// only.
/// </param>
/// <param name="ManualUnitNumber">Required together with <paramref name="ManualProjectName"/> — see that parameter.</param>
/// <param name="CategoryId">
/// The ticket's business classification, which also determines the
/// department it routes to. Optional <b>only</b> for the Unclassified path —
/// an inquiry that must become a ticket before anyone has read the request
/// (Genesys ingestion) — where <paramref name="DepartmentId"/> supplies the
/// department instead. Every agent-driven creation supplies it, and a
/// category is never invented to stand in for a missing one.
/// </param>
/// <param name="DepartmentId">
/// Required <b>only</b> when <paramref name="CategoryId"/> is omitted: the
/// already-resolved department the unclassified ticket belongs to. Ignored
/// when a category is supplied, since the category itself routes the ticket.
/// </param>
/// <param name="PriorityId">
/// 1=Critical, 2=High, 3=Medium, 4=Low. Required whenever
/// <paramref name="CategoryId"/> is supplied, and must be <b>omitted</b> when
/// it is not: an unclassified ticket carries no priority, because nobody has
/// judged its urgency yet, and a default would rank it against tickets a
/// human actually triaged.
/// </param>
/// <param name="RequestSummary">Required. The caller's request, in the agent's words.</param>
/// <param name="CustomerVerificationSource">The external lookup source that verified the customer ("Pact"/"Tasleeh") when the agent selected a matched external customer/unit. Mutually exclusive with the CrmBuyer* identifiers; accompanies (never replaces) the manual Project/Unit snapshot.</param>
/// <param name="ExternalCustomerId">The source's own customer identifier (for PACT, its tenantID) — an external identifier only, stored for audit/reconciliation; requires <paramref name="CustomerVerificationSource"/>.</param>
/// <param name="ExternalUnitId">The source's own identifier for the selected unit (for PACT, its unitID) — same rule as <paramref name="ExternalCustomerId"/>.</param>
/// <param name="ExternalCustomerName">The verified external customer's own name as the source returned it (PACT's <c>customerName</c>) — a ticket-time display snapshot, the external counterpart of <paramref name="CrmBuyerCustomerName"/>. Optional: null when the source holds no name.</param>
/// <param name="ExternalCustomerEmail">The verified external customer's own email as the source returned it (PACT's <c>customerEmail</c>) — a ticket-time display snapshot. Optional: null when the source holds no email.</param>
/// <param name="RequestTypeId">Optional (Workflow/Automation phase 2): the configured Request Type this ticket follows. Must be an active request type of the department the ticket routes to; when present it drives the workflow capabilities and the automatic assignment rule.</param>
/// <param name="AssignToDepartmentQueue">Optional. A CS Agent creating a ticket by hand is made its owner when they are eligible (an active member of the ticket's department) and no assignment rule or setting already decided; <c>true</c> is the agent explicitly choosing the Department Queue instead, and is respected. Ignored for every other caller and for Genesys-created tickets.</param>
/// <param name="SkipDefaultClassification">Set only by the Genesys ingestion when it is about to classify the new ticket with a validated request type itself: the ticket is then created without the Customer Services / General fallback. Every other caller leaves it null - a ticket with no valid classification is created as Customer Services / General.</param>
/// <param name="GenesysContext">Optional (Workflow/Automation phase 2): the Genesys-provided interaction context, persisted verbatim for traceability. Absent for Face-to-Face and every other locally-created interaction.</param>
public sealed record CreateTicketRequestDto(
    long IntakeRecordId,
    int? UnitReferenceId,
    int? ContactReferenceId,
    int? CategoryId,
    byte? PriorityId,
    string RequestSummary,
    int? CrmBuyerCustomerId = null,
    int? CrmBuyerLeadId = null,
    int? CrmBuyerUnitId = null,
    int? CrmBuyerProjectId = null,
    string? CrmBuyerCustomerName = null,
    string? CrmBuyerProjectName = null,
    string? CrmBuyerUnitNumber = null,
    string? ManualProjectName = null,
    string? ManualUnitNumber = null,
    string? CustomerVerificationSource = null,
    string? ExternalCustomerId = null,
    string? ExternalUnitId = null,
    string? ExternalCustomerName = null,
    string? ExternalCustomerEmail = null,
    int? RequestTypeId = null,
    GenesysIntegration.Dto.GenesysInteractionContextDto? GenesysContext = null,
    int? DepartmentId = null,
    bool? AssignToDepartmentQueue = null,
    bool? SkipDefaultClassification = null);

/// <summary>A newly created ticket (MVP-API-Contracts.md §3.1).</summary>
/// <param name="TicketId">The ticket.</param>
/// <param name="TicketNumber">The human-facing ticket number.</param>
/// <param name="OriginatingDepartmentId">The department the ticket was raised in.</param>
/// <param name="CurrentDepartmentId">The department that currently holds it.</param>
/// <param name="UnitReferenceId">The matched unit, or null when no customer match was linked at creation.</param>
/// <param name="ContactReferenceId">The matched contact, or null when no customer match was linked at creation.</param>
/// <param name="CategoryId">The ticket's category, or null while the ticket is Unclassified.</param>
/// <param name="PriorityId">1=Critical, 2=High, 3=Medium, 4=Low, or null while the ticket is Unclassified — no priority has been judged yet.</param>
/// <param name="TicketStatus">One of Open, InProgress, PendingCustomer, Resolved, Closed — or the legacy, no-longer-reachable PendingThirdParty on a historical ticket.</param>
/// <param name="VerificationStatus">One of Unverified, PendingCrmVerification, Verified.</param>
/// <param name="EscalationLevel">One of None, Level1, Level2, Level3, Level4.</param>
/// <param name="SlaState">One of Running, Paused, Met, Breached, NotApplicable.</param>
/// <param name="RequestSummary">The request, in the agent's words.</param>
/// <param name="CreatedAtUtc">When the ticket was created, in UTC.</param>
/// <param name="RowVersion">The concurrency token, Base64-encoded. Send it back on any subsequent write to this ticket.</param>
/// <param name="CrmBuyerCustomerId">The real CRM Buyer Lookup match's customer id, or null when no CRM Buyer match was linked at creation.</param>
/// <param name="CrmBuyerLeadId">The matched CRM Lead id, or null.</param>
/// <param name="CrmBuyerUnitId">The matched CRM unit id, or null.</param>
/// <param name="CrmBuyerProjectId">The matched CRM project id, or null.</param>
/// <param name="CrmBuyerCustomerName">Ticket-time display snapshot of the matched Buyer's name, or null.</param>
/// <param name="CrmBuyerProjectName">Ticket-time display snapshot of the matched unit's project name, or null.</param>
/// <param name="CrmBuyerUnitNumber">Ticket-time display snapshot of the matched unit's number, or null.</param>
/// <param name="ManualProjectName">The agent-entered Project name, when no CRM Buyer match was linked at creation.</param>
/// <param name="ManualUnitNumber">The agent-entered Unit Number, when no CRM Buyer match was linked at creation.</param>
/// <param name="CustomerVerificationSource">The external lookup source that verified the customer at creation ("Pact"/"Tasleeh"), or null for CRM Buyer tickets and plain manual entry.</param>
/// <param name="ExternalCustomerId">The source's own customer identifier (for PACT, its tenantID) — an external identifier only.</param>
/// <param name="ExternalUnitId">The source's own identifier for the selected unit (for PACT, its unitID) — an external identifier only.</param>
/// <param name="ExternalCustomerName">Ticket-time display snapshot of the verified external customer's name, or null when the source held none.</param>
/// <param name="ExternalCustomerEmail">Ticket-time display snapshot of the verified external customer's email, or null when the source held none.</param>
public sealed record TicketResponseDto(
    long TicketId,
    string TicketNumber,
    int OriginatingDepartmentId,
    int CurrentDepartmentId,
    int? UnitReferenceId,
    int? ContactReferenceId,
    int? CategoryId,
    byte? PriorityId,
    string TicketStatus,
    string VerificationStatus,
    string EscalationLevel,
    string SlaState,
    string RequestSummary,
    DateTime CreatedAtUtc,
    string RowVersion,
    int? CrmBuyerCustomerId = null,
    int? CrmBuyerLeadId = null,
    int? CrmBuyerUnitId = null,
    int? CrmBuyerProjectId = null,
    string? CrmBuyerCustomerName = null,
    string? CrmBuyerProjectName = null,
    string? CrmBuyerUnitNumber = null,
    string? ManualProjectName = null,
    string? ManualUnitNumber = null,
    string? CustomerVerificationSource = null,
    string? ExternalCustomerId = null,
    string? ExternalUnitId = null,
    string? ExternalCustomerName = null,
    string? ExternalCustomerEmail = null);

public enum TicketCreationOutcome
{
    Success,

    IntakeRecordNotFound,
    IntakeRecordAlreadyLinked,

    /// <summary>UnitReferenceId and ContactReferenceId were not both supplied or both omitted — a customer match is always a unit+contact pair, never one alone.</summary>
    UnitOrContactReferenceMismatch,

    /// <summary>UnitReferenceId did not resolve to a real, previously cached unit reference.</summary>
    UnitReferenceNotFound,

    /// <summary>ContactReferenceId did not resolve to a real, previously cached contact reference.</summary>
    ContactReferenceNotFound,

    /// <summary>CrmBuyerCustomerId/CrmBuyerLeadId/CrmBuyerUnitId/CrmBuyerProjectId were not all supplied or all omitted together — a real CRM Buyer match is always all four, never some.</summary>
    CrmBuyerReferenceMismatch,

    /// <summary>A ticket may carry a real CRM Buyer match (CrmBuyerUnitId) or a manually-entered Project/Unit Number, never both.</summary>
    CrmBuyerAndManualProjectUnitBothSupplied,

    /// <summary>A ticket may carry a real CRM Buyer match or an external-lookup verification (CustomerVerificationSource — PACT/Tasleeh), never both.</summary>
    CrmBuyerAndExternalVerificationBothSupplied,

    /// <summary>ExternalCustomerId/ExternalUnitId were supplied without a CustomerVerificationSource — external identifiers never travel without their source.</summary>
    ExternalVerificationSourceMissing,

    /// <summary>CategoryId was supplied but does not resolve to an active Ticket Category.</summary>
    CategoryNotFound,

    /// <summary>Neither a CategoryId nor a DepartmentId was supplied — a ticket needs one of the two to know where it belongs, and neither is ever invented.</summary>
    DepartmentOrCategoryRequired,

    /// <summary>DepartmentId was supplied for an unclassified ticket but does not resolve to an active Department.</summary>
    DepartmentNotFound,

    /// <summary>A PriorityId was supplied without a CategoryId. An unclassified ticket carries no priority at all — priority is a judgement made at classification, alongside the category, never before it.</summary>
    PriorityRequiresCategory,
    PriorityNotFound,

    /// <summary>Item 9 (senior review): the Category's routed Department is missing or deactivated — never silently route a ticket to a department nobody is staffing.</summary>
    DepartmentInactive,

    /// <summary>The IntakeRecord named a Department and the selected Category routes to a different one — the Category dropdown never offers this combination, so this only fires against a request built outside it.</summary>
    CategoryDepartmentMismatch,

    /// <summary>A same-department, same-day TicketNumber collision (real DB unique-index race) — nothing else was touched; retrying the whole request is always correct.</summary>
    TicketNumberCollision,

    /// <summary>The selected request type's workflow has no Published version to pin the ticket to — an administration gap (the workflow is still a Draft), never silently ignored.</summary>
    RequestTypeWorkflowNotPublished,

    /// <summary>Workflow/Automation phase 2: RequestTypeId did not resolve to an active request type.</summary>
    RequestTypeNotFound,

    /// <summary>Workflow/Automation phase 2: the request type belongs to a different department than the one the ticket routes to — request types are never offered across departments.</summary>
    RequestTypeDepartmentMismatch,

    /// <summary>Workflow/Automation phase 2: a Genesys interaction context was supplied without its conversation id — the one field a Genesys-sourced context cannot lack, because it is the traceability link.</summary>
    GenesysConversationIdRequired
}

public sealed record TicketCreationResult(TicketCreationOutcome Outcome, TicketResponseDto? Response = null)
{
    public static TicketCreationResult Success(TicketResponseDto response) => new(TicketCreationOutcome.Success, response);
    public static TicketCreationResult Failure(TicketCreationOutcome outcome) => new(outcome);
}
