using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using TigerCS.Api.OpenApi;
using TigerCS.Application.Modules.GenesysIntegration.Dto;
using TigerCS.Application.Modules.GenesysIntegration.Services;
using TigerCS.Infrastructure.Modules.IdentityAndAccess.Authorization;
using TigerCS.Domain.Modules.IdentityAndAccess.Permissions;

namespace TigerCS.Api.Controllers;

/// <summary>
/// The inbound Genesys boundary: Genesys tells TigerCS that an inquiry
/// reached an agent, and later that the conversation ended.
///
/// <para>
/// <b>Genesys never calls TigerCS directly.</b> Genesys Cloud authenticates
/// to TigerGroupWeb with OAuth 2.0 client credentials
/// (<c>POST https://tigergroup.ae/api/genesys/oauth/token</c>, scope
/// <c>ticketing.genesys</c>), and TigerGroupWeb's <c>TicketingGenesysService</c>
/// forwards the same three routes here unchanged, authenticated as an
/// ordinary TigerCS service account — the same JWT bearer authentication
/// every other client of this API uses (<c>POST /api/auth/login</c>), scoped
/// to the same policy as manual ticket creation because that is precisely
/// what these endpoints do. The service account's credentials live only in
/// TigerGroupWeb's configuration; Genesys never holds them.
/// </para>
///
/// <para>
/// <b>The one ingestion path.</b> Phone, website chat, WhatsApp and social
/// media all post the same normalized inquiry to
/// <see cref="Ingest"/>; there is no per-channel endpoint and no per-channel
/// ticket-creation code. Genesys never reaches Tiger CRM, PACT or Tasleeh
/// directly — customer lookup happens inside TigerCS, through the services
/// the New Ticket wizard already uses.
/// </para>
/// </summary>
[ApiController]
[Route("api/genesys")]
[Authorize(Policy = PolicyNames.CustomerVerification)]
[Tags(OpenApiTags.Genesys)]
[NoPermissionRequired("Genesys integration surface; reached by the integration service account, which cannot hold permission overrides.")]
public class GenesysController(
    GenesysInquiryIngestionAppService ingestionAppService,
    GenesysTicketUpdateAppService ticketUpdateAppService,
    GenesysCustomerLookupAppService customerLookupAppService,
    GenesysCustomerUnitDetailsAppService unitDetailsAppService,
    GenesysAgentContextAppService agentContextAppService,
    GenesysScreenPopAppService screenPopAppService) : ControllerBase
{
    /// <summary>Stable, machine-readable error codes for the agent identity mapping — carried as the <c>code</c> member of the standard ProblemDetails body.</summary>
    public static class ErrorCodes
    {
        public const string AgentNotMapped = "GENESYS_AGENT_NOT_MAPPED";
        public const string AgentInactive = "GENESYS_AGENT_INACTIVE";
        public const string ScreenPopNotConfigured = "GENESYS_SCREEN_POP_NOT_CONFIGURED";
        public const string CustomerNotVerified = "CUSTOMER_NOT_VERIFIED";
        public const string UnitNotEligible = "UNIT_NOT_ELIGIBLE";
        public const string CustomerAmbiguous = "CUSTOMER_AMBIGUOUS";
        public const string CrmUnavailable = "CRM_UNAVAILABLE";
    }

    /// <summary>Create — or reuse — the one ticket for a Genesys conversation, on any channel.</summary>
    /// <remarks>
    /// <b>Idempotent on <c>conversationId</c> + <c>serviceRequestId</c>.</b> The
    /// first accepted event for that pair creates a ticket; every retry or
    /// duplicate returns that same ticket with outcome <c>AlreadyIngested</c>
    /// and creates nothing. A unique index on the pair makes this hold under
    /// concurrent delivery, not merely under sequential retries. A conversation
    /// can carry several distinct service requests (documents, then apartment
    /// leasing): send a different <c>serviceRequestId</c> for each and each gets
    /// its own ticket with its own lifecycle. <b>Without a
    /// <c>serviceRequestId</c> the conversation is one ticket</b>, exactly as
    /// before — a second request under the same conversation id returns the first ticket.
    ///
    /// <para>
    /// <b>This endpoint means exactly one thing:</b> create or reuse the
    /// ticket for this conversation. It is not an event receiver for call
    /// progress, and there is no event field. The voice flow posts it from
    /// the Genesys inbound call flow, right after the caller lookup and
    /// before the call is queued, with <c>Call.Ani</c> as the customer phone
    /// (a "tel:" address is normalized here). Digital channels (Live Chat /
    /// Web Messaging, WhatsApp, social) post it when the conversation starts.
    /// Queue changes, agent connects, transfers, handoffs and the end all
    /// arrive on <c>PATCH</c> against the same ticket — never a second
    /// ticket.
    /// </para>
    ///
    /// <para>
    /// The department comes from the customer's explicit website-chat choice
    /// (Leasing / Customer Service / Maintenance) when there is one, and
    /// otherwise from the configured Genesys queue → department mapping.
    /// Category and Request Type are never inferred from the subject or the queue: the ticket is
    /// created <b>Unclassified</b> (no category at all — never a placeholder
    /// one), on the configured default priority (<c>Genesys:DefaultTicketPriority</c>,
    /// "Normal"), and its SLA clock starts at creation like any other ticket.
    /// When the bot supplies a valid <c>requestType</c> it is applied
    /// immediately, together with the Category paired with it (active, same department, same name; none is created or guessed) — the request type's department becomes responsible (the
    /// originating department is never rewritten), the configured automatic
    /// assignment runs (no eligible employee leaves it in the department
    /// queue) and its SLA policy replaces the Normal-priority targets
    /// without restarting the clock. With no request type supplied the ticket
    /// stays Unclassified for the existing manual classification workflow (no human handoff is recorded);
    /// the bot can send it later on <c>PATCH</c>. A repeated delivery of the
    /// same <c>conversationId</c> changes nothing.
    /// </para>
    /// </remarks>
    /// <param name="request">The normalized inquiry.</param>
    /// <response code="200">The conversation was already ingested — the same ticket is returned, and nothing was created.</response>
    /// <response code="201">A ticket was created for this conversation.</response>
    /// <response code="400">The channel was not recognized, or conversationId was blank.</response>
    /// <response code="422">The inquiry could not be turned into a ticket — no department could be resolved, a request type was supplied but is invalid (nothing is written), or ticket creation itself was refused.</response>
    /// <response code="503">The Genesys integration is switched off (<c>Genesys:Enabled</c> is false).</response>
    [HttpPost("tickets")]
    [ProducesResponseType<GenesysInquiryAcceptedResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<GenesysInquiryAcceptedResponse>(StatusCodes.Status201Created)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status422UnprocessableEntity)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> Ingest([FromBody] GenesysInquiryRequest request, CancellationToken cancellationToken)
    {
        var employeeId = GetEmployeeId();
        if (employeeId is null)
        {
            return Unauthorized();
        }

        if (!GenesysContractMapper.TryMap(request, out var inquiry, out var mappingError))
        {
            ModelState.AddModelError(nameof(request.Channel), mappingError!);
            return ValidationProblem(ModelState);
        }

        var result = await ingestionAppService.IngestAsync(employeeId.Value, inquiry, cancellationToken);

        return result.Outcome switch
        {
            GenesysIngestionOutcome.TicketCreated => Created(
                $"/api/tickets/{result.Ticket!.TicketId}",
                new GenesysInquiryAcceptedResponse(
                    nameof(GenesysIngestionOutcome.TicketCreated), inquiry.ConversationId,
                    result.Ticket.TicketId, result.Ticket.TicketNumber,
                    result.Classification?.Status, result.Classification?.RequestTypeId,
                    result.Ticket.CurrentDepartmentId, result.Classification?.AssignedEmployeeId,
                    result.Classification?.Detail, inquiry.ServiceRequestId)),

            // The retry answer: 200 rather than 201, same body, no new ticket.
            GenesysIngestionOutcome.AlreadyIngested when result.Ticket is not null => Ok(
                new GenesysInquiryAcceptedResponse(
                    nameof(GenesysIngestionOutcome.AlreadyIngested), inquiry.ConversationId,
                    result.Ticket.TicketId, result.Ticket.TicketNumber,
                    ServiceRequestId: inquiry.ServiceRequestId)),

            GenesysIngestionOutcome.IntegrationDisabled => Problem(
                type: "https://tigercs.internal/problems/genesys-integration-disabled",
                title: "The Genesys integration is disabled",
                detail: "Genesys:Enabled is false — no Genesys inquiry is processed while the integration is switched off. Normal ticket creation is unaffected.",
                statusCode: StatusCodes.Status503ServiceUnavailable),

            GenesysIngestionOutcome.ConversationIdRequired => Problem(
                type: "https://tigercs.internal/problems/genesys-conversation-id-required",
                title: "Genesys conversation id required",
                detail: "conversationId is what makes this call idempotent — without it the inquiry cannot be accepted.",
                statusCode: StatusCodes.Status400BadRequest),

            GenesysIngestionOutcome.DepartmentNotResolved => Problem(
                type: "https://tigercs.internal/problems/genesys-department-not-resolved",
                title: "No department could be resolved for this inquiry",
                detail: result.Detail
                    ?? "No department could be resolved from departmentId, departmentCode or the queue mapping.",
                statusCode: StatusCodes.Status422UnprocessableEntity),

            GenesysIngestionOutcome.RequestTypeInvalid => Problem(
                type: "https://tigercs.internal/problems/genesys-request-type-invalid",
                title: "The supplied request type is not valid",
                detail: (result.Detail ?? "The request type does not exist, is inactive, is ambiguous or cannot route.")
                    + " No ticket was created. Omit requestType to create the ticket for human classification, or send a valid one.",
                statusCode: StatusCodes.Status422UnprocessableEntity),

            GenesysIngestionOutcome.DefaultClassificationNotConfigured => Problem(
                type: "https://tigercs.internal/problems/default-classification-not-configured",
                title: "The department's General classification is not configured",
                detail: (result.Detail ?? "The department has no usable General category / request type.")
                    + " No ticket was created and nothing was moved. Run EnsureDefaultGeneralClassification.sql; redelivering the same inquiry then succeeds.",
                statusCode: StatusCodes.Status409Conflict),

            GenesysIngestionOutcome.ChannelNotConfigured => Problem(
                type: "https://tigercs.internal/problems/genesys-channel-not-configured",
                title: "The inquiry's channel is not configured",
                detail: result.Detail ?? "No active channel is configured for this Genesys channel.",
                statusCode: StatusCodes.Status422UnprocessableEntity),

            GenesysIngestionOutcome.TicketCreationFailed => Problem(
                type: "https://tigercs.internal/problems/genesys-ticket-creation-failed",
                title: "The ticket could not be created for this inquiry",
                detail: $"Ticket creation was refused: {result.TicketCreationOutcome}.",
                statusCode: StatusCodes.Status422UnprocessableEntity),

            _ => Problem(statusCode: StatusCodes.Status500InternalServerError)
        };
    }

    /// <summary>Update the Genesys-owned side of a ticket: agent context, conversation end and transcript, and human-handoff state.</summary>
    /// <remarks>
    /// The one update contract. Every part of the body is optional and
    /// independently idempotent — send only what changed, and resend freely.
    ///
    /// <para>
    /// <b>Genesys owns the conversation; TigerCS owns the ticket.</b> This
    /// accepts conversation facts, plus the one business fact the bot may
    /// report: the <c>requestType</c> it identified. TigerCS validates it and
    /// applies that request type's department routing, automatic assignment
    /// and SLA policy itself (the SLA clock is never restarted). There is
    /// deliberately no field for category, priority, status, owner,
    /// department, resolution or closure: those move through their own
    /// TigerCS operations, with their own authorization and SLA consequences.
    /// </para>
    ///
    /// <para>
    /// <b>Nothing here closes the ticket.</b> Ending a conversation finalizes
    /// the interaction and stores the transcript; the ticket carries on under
    /// the existing workflow. The response echoes <c>ticketStatus</c> on
    /// every call to make that visible.
    /// </para>
    ///
    /// <para>
    /// <c>conversationId</c> must resolve to an interaction on the ticket in
    /// the route — that cross-check is what stops a transcript being applied
    /// to the wrong ticket. A malformed transcript is refused before anything
    /// is written, so a bad update never leaves a half-stored conversation.
    /// </para>
    /// </remarks>
    /// <param name="ticketId">The ticket, as returned by <c>POST /api/genesys/tickets</c>.</param>
    /// <param name="request">The conversation facts that changed.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <response code="200">Everything supplied was applied, or was already in that state.</response>
    /// <response code="400">conversationId was blank, a transcript message had an unrecognized sender or empty body, the handoff mode or trigger is not recognized, a handoff stand-down named no reason, or customerConfirmation was sent without confirmedResolved: true.</response>
    /// <response code="404">No such ticket, or no interaction exists for this conversation.</response>
    /// <response code="409">The conversation belongs to a different ticket than the one in the route, or the ticket already has a different request type, is closed (a request type cannot be classified onto it and human follow-up cannot be queued on it — open a new ticket), or changed while being classified (retry).</response>
    /// <response code="422">A handoff assignment was supplied but no outstanding human work exists to apply it to, or the supplied requestType does not exist, is inactive, is ambiguous by name, or cannot route.</response>
    /// <response code="503">The Genesys integration is switched off.</response>
    [HttpPatch("tickets/{ticketId:long}")]
    [ProducesResponseType<GenesysTicketUpdateResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status422UnprocessableEntity)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> UpdateTicket(
        long ticketId, [FromBody] GenesysTicketUpdateRequest request, CancellationToken cancellationToken)
    {
        var employeeId = GetEmployeeId();
        if (employeeId is null)
        {
            return Unauthorized();
        }

        var result = await ticketUpdateAppService.UpdateAsync(
            employeeId.Value, ticketId, GenesysContractMapper.Map(request), cancellationToken);

        return result.Outcome switch
        {
            GenesysTicketUpdateOutcome.Applied => Ok(
                new GenesysTicketUpdateResponse(
                    nameof(GenesysTicketUpdateOutcome.Applied), request.ConversationId,
                    result.TicketId!.Value, result.TicketNumber!, result.TicketStatus!,
                    result.ConversationEnded, result.TranscriptMessageCount,
                    result.HandoffStatus, result.TicketAgentHandoffId,
                    result.AwaitingCustomerReply, result.InactivityDeadlineUtc, result.AwaitingCustomerReplyNote,
                    result.Classification?.Status, result.Classification?.RequestTypeId,
                    result.Classification?.DepartmentId, result.Classification?.AssignedEmployeeId,
                    result.ClosedByAi, result.ClosedByAiNote)),

            GenesysTicketUpdateOutcome.IntegrationDisabled => Problem(
                type: "https://tigercs.internal/problems/genesys-integration-disabled",
                title: "The Genesys integration is disabled",
                detail: "Genesys:Enabled is false — no Genesys event is processed while the integration is switched off.",
                statusCode: StatusCodes.Status503ServiceUnavailable),

            GenesysTicketUpdateOutcome.ConversationIdRequired => Problem(
                type: "https://tigercs.internal/problems/genesys-conversation-id-required",
                title: "Genesys conversation id required",
                detail: "conversationId is what ties an update to its interaction — without it the update cannot be accepted.",
                statusCode: StatusCodes.Status400BadRequest),

            GenesysTicketUpdateOutcome.InvalidTranscript => Problem(
                type: "https://tigercs.internal/problems/genesys-invalid-transcript",
                title: "The transcript could not be stored",
                detail: result.Detail
                    ?? "A transcript message had an unrecognized sender or an empty body. Nothing was stored — resend the whole update.",
                statusCode: StatusCodes.Status400BadRequest),

            GenesysTicketUpdateOutcome.InvalidHandoffMode => Problem(
                type: "https://tigercs.internal/problems/genesys-invalid-handoff-mode",
                title: "Unrecognized follow-up mode",
                detail: result.Detail,
                statusCode: StatusCodes.Status400BadRequest),

            GenesysTicketUpdateOutcome.InvalidHandoffTrigger => Problem(
                type: "https://tigercs.internal/problems/genesys-invalid-handoff-trigger",
                title: "Unrecognized handoff trigger",
                detail: result.Detail
                    ?? "handoff.trigger must be CustomerRequestedHuman, AiConnectionLost, AiEscalated, RoutingDecision or AgentTransfer — or omitted.",
                statusCode: StatusCodes.Status400BadRequest),

            GenesysTicketUpdateOutcome.HandoffReasonRequired => Problem(
                type: "https://tigercs.internal/problems/genesys-handoff-reason-required",
                title: "A reason is required to stand human work down",
                detail: result.Detail
                    ?? "handoff.required = false stands the outstanding human work down, and needs handoff.reason to say why.",
                statusCode: StatusCodes.Status400BadRequest),

            GenesysTicketUpdateOutcome.InvalidCustomerConfirmation => Problem(
                type: "https://tigercs.internal/problems/genesys-invalid-customer-confirmation",
                title: "The customer confirmation is not a confirmation",
                detail: result.Detail,
                statusCode: StatusCodes.Status400BadRequest),

            GenesysTicketUpdateOutcome.InvalidRequestType => Problem(
                type: "https://tigercs.internal/problems/genesys-invalid-request-type",
                title: "The request type cannot be used",
                detail: result.Detail,
                statusCode: StatusCodes.Status422UnprocessableEntity),

            GenesysTicketUpdateOutcome.DefaultClassificationNotConfigured => Problem(
                type: "https://tigercs.internal/problems/default-classification-not-configured",
                title: "The department's General classification is not configured",
                detail: (result.Detail ?? "The department has no usable General category / request type.")
                    + " The ticket was not handed to a person without a valid classification. Run EnsureDefaultGeneralClassification.sql and resend.",
                statusCode: StatusCodes.Status409Conflict),

            GenesysTicketUpdateOutcome.RequestTypeConflict => Problem(
                type: "https://tigercs.internal/problems/genesys-request-type-conflict",
                title: "The ticket already has a different request type",
                detail: result.Detail,
                statusCode: StatusCodes.Status409Conflict),

            GenesysTicketUpdateOutcome.TicketClosed => Problem(
                type: "https://tigercs.internal/problems/ticket-closed",
                title: "The ticket is closed",
                detail: result.Detail ?? "A request type cannot be classified onto a closed ticket.",
                statusCode: StatusCodes.Status409Conflict),

            GenesysTicketUpdateOutcome.ConcurrencyConflict => Problem(
                type: "https://tigercs.internal/problems/concurrency-conflict",
                title: "The ticket or conversation changed while the request was applied",
                detail: result.Detail,
                statusCode: StatusCodes.Status409Conflict),

            GenesysTicketUpdateOutcome.TicketNotFound => Problem(
                type: "https://tigercs.internal/problems/ticket-not-found",
                title: "No such ticket",
                statusCode: StatusCodes.Status404NotFound),

            GenesysTicketUpdateOutcome.ConversationNotFound => Problem(
                type: "https://tigercs.internal/problems/genesys-conversation-not-found",
                title: "No interaction exists for this conversation",
                detail: result.Detail ?? "This conversation never produced a ticket.",
                statusCode: StatusCodes.Status404NotFound),

            GenesysTicketUpdateOutcome.ConversationTicketMismatch => Problem(
                type: "https://tigercs.internal/problems/genesys-conversation-ticket-mismatch",
                title: "This conversation belongs to a different ticket",
                detail: result.Detail,
                statusCode: StatusCodes.Status409Conflict),

            GenesysTicketUpdateOutcome.NoOpenHandoff => Problem(
                type: "https://tigercs.internal/problems/genesys-no-open-handoff",
                title: "No outstanding human work for this conversation",
                detail: result.Detail,
                statusCode: StatusCodes.Status422UnprocessableEntity),

            _ => Problem(statusCode: StatusCodes.Status500InternalServerError)
        };
    }

    /// <summary>Look a caller up by phone number — customer, units, their existing tickets, and a flat screen pop.</summary>
    /// <remarks>
    /// Called from the Genesys inbound call flow with <c>Call.Ani</c>, before
    /// the ticket is created, so the agent's screen pop already names the
    /// caller. Any Genesys ANI format is accepted ("tel:+971…", "+971…",
    /// "971…"); it is searched as "+971…". A withheld caller id is a normal
    /// "nobody found".
    ///
    /// <para>
    /// <b>Context, not a yes/no.</b> Where a customer is found this returns
    /// the CRM Buyer with every eligible unit, the PACT/Tasleeh matches, and
    /// the tickets that already arrived from this number — with the open ones
    /// first, so an agent can see "this caller already has an NOC request
    /// running" before they speak.
    /// </para>
    ///
    /// <para>
    /// <b>Never a gate.</b> <c>found: false</c> is a normal answer and
    /// <c>200 OK</c>, not a 404 — and neither it nor a lookup source being
    /// down stops the Create Ticket call that follows.
    /// </para>
    ///
    /// <para>
    /// This reuses the same CRM Buyer Lookup and PACT/Tasleeh services the
    /// New Ticket wizard uses. There is no second CRM integration, and
    /// Genesys never reaches those systems directly. Read-only: nothing is
    /// created or persisted.
    /// </para>
    /// </remarks>
    /// <param name="phoneNumber">Required. The caller's number.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <response code="200">What each source found — possibly nothing, which is a normal answer.</response>
    /// <response code="400">phoneNumber was missing or blank.</response>
    /// <response code="503">The Genesys integration is switched off.</response>
    [HttpGet("customers/lookup")]
    [ProducesResponseType<GenesysCustomerLookupResultDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> LookUpCustomer(
        [FromQuery] string phoneNumber, CancellationToken cancellationToken)
    {
        if (GetEmployeeId() is null)
        {
            return Unauthorized();
        }

        if (string.IsNullOrWhiteSpace(phoneNumber))
        {
            ModelState.AddModelError(nameof(phoneNumber), "phoneNumber is required.");
            return ValidationProblem(ModelState);
        }

        var result = await customerLookupAppService.LookUpAsync(phoneNumber, cancellationToken);

        return result is null
            ? Problem(
                type: "https://tigercs.internal/problems/genesys-integration-disabled",
                title: "The Genesys integration is disabled",
                detail: "Genesys:Enabled is false — no Genesys request is processed while the integration is switched off.",
                statusCode: StatusCodes.Status503ServiceUnavailable)
            : Ok(result);
    }

    /// <summary>The selected unit's and project's customer-facing details for a verified customer — or, with no unit selected, the customer's eligible units.</summary>
    /// <remarks>
    /// <b>Read-only; a POST only so the customer's number stays out of URLs and access logs.</b>
    /// Authorization is decided server-side against CRM on every call: the
    /// verified <c>phoneNumber</c> must resolve to the customer named by
    /// <c>customerReference</c>, and <c>unitId</c> must be one of that
    /// customer's own units. A unit id alone grants nothing, and a unit that
    /// belongs to someone else is refused exactly like one that does not exist.
    ///
    /// <para>
    /// Without <c>unitId</c> the answer is <c>mode: UnitSelectionRequired</c>
    /// with <c>eligibleUnits</c> — the bot asks which one. With it, the answer
    /// is <c>mode: UnitDetails</c>. Values CRM does not record are <c>null</c>;
    /// unit-level and project-level handover dates are separate fields, and
    /// <c>handoverDateSource</c> says which pair applies. The project's
    /// construction completion (percentage and expected/actual completion dates)
    /// is separate from handover. The customer's own sale — actual sold price and
    /// registration cost — is private: it is returned only with a valid
    /// <c>verificationSessionId</c> (see <c>financialDetailsStatus</c>). No internal notes or
    /// other customer data is ever returned.
    /// </para>
    /// </remarks>
    /// <param name="request">The verified customer, the verified number, and optionally the selected unit.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <response code="200">Eligible units (no unitId), or the selected unit's and project's details.</response>
    /// <response code="400">customerReference or phoneNumber missing/malformed, or unitId not positive.</response>
    /// <response code="403">The customer is not the one CRM resolves for the verified number (<c>CUSTOMER_NOT_VERIFIED</c>), or the unit is not one of their units (<c>UNIT_NOT_ELIGIBLE</c>).</response>
    /// <response code="409">CRM holds more than one customer for the number (<c>CUSTOMER_AMBIGUOUS</c>); nothing is disclosed.</response>
    /// <response code="502">Tiger CRM could not be reached or answered unusably (<c>CRM_UNAVAILABLE</c>).</response>
    /// <response code="503">The Genesys integration is switched off.</response>
    [HttpPost("customers/unit-details")]
    [ProducesResponseType<GenesysCustomerUnitDetailsResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status502BadGateway)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> GetCustomerUnitDetails(
        [FromBody] GenesysCustomerUnitDetailsRequest request, CancellationToken cancellationToken)
    {
        if (GetEmployeeId() is not { } callerEmployeeId)
        {
            return Unauthorized();
        }

        var result = await unitDetailsAppService.GetAsync(
            request.CustomerReference, request.PhoneNumber, request.UnitId,
            callerEmployeeId, request.VerificationSessionId, cancellationToken);

        return result.Outcome switch
        {
            GenesysUnitDetailsOutcome.UnitDetails or GenesysUnitDetailsOutcome.UnitSelectionRequired => Ok(result.Response),

            GenesysUnitDetailsOutcome.IntegrationDisabled => Problem(
                type: "https://tigercs.internal/problems/genesys-integration-disabled",
                title: "The Genesys integration is disabled",
                detail: "Genesys:Enabled is false — no Genesys request is processed while the integration is switched off.",
                statusCode: StatusCodes.Status503ServiceUnavailable),

            GenesysUnitDetailsOutcome.CustomerReferenceInvalid => FieldProblem(
                nameof(request.CustomerReference), "customerReference is required and must be a CRM customer (crm:{id} or the plain id)."),
            GenesysUnitDetailsOutcome.PhoneNumberInvalid => FieldProblem(
                nameof(request.PhoneNumber), "phoneNumber is required and must contain a number."),
            GenesysUnitDetailsOutcome.UnitIdInvalid => FieldProblem(
                nameof(request.UnitId), "unitId must be a positive CRM unit id."),

            GenesysUnitDetailsOutcome.CustomerNotVerified => CodedProblem(
                ErrorCodes.CustomerNotVerified,
                type: "https://tigercs.internal/problems/genesys-customer-not-verified",
                title: "The customer could not be verified",
                detail: "The customer reference does not match a customer CRM holds for the verified number.",
                statusCode: StatusCodes.Status403Forbidden),

            GenesysUnitDetailsOutcome.UnitNotEligible => CodedProblem(
                ErrorCodes.UnitNotEligible,
                type: "https://tigercs.internal/problems/genesys-unit-not-eligible",
                title: "The unit is not available to this customer",
                detail: "The requested unit is not one of this customer's units.",
                statusCode: StatusCodes.Status403Forbidden),

            GenesysUnitDetailsOutcome.AmbiguousCustomer => CodedProblem(
                ErrorCodes.CustomerAmbiguous,
                type: "https://tigercs.internal/problems/genesys-customer-ambiguous",
                title: "More than one customer matches the verified number",
                detail: "CRM holds more than one customer for this number. Nothing was disclosed; hand over to an agent.",
                statusCode: StatusCodes.Status409Conflict),

            _ => CodedProblem(
                ErrorCodes.CrmUnavailable,
                type: "https://tigercs.internal/problems/crm-unavailable",
                title: "CRM is currently unavailable",
                detail: "Tiger CRM could not be reached or answered unusably.",
                statusCode: StatusCodes.Status502BadGateway)
        };
    }

    private IActionResult FieldProblem(string field, string message)
    {
        ModelState.AddModelError(field, message);
        return ValidationProblem(ModelState);
    }

    /// <summary>Identify the Genesys agent working in Ticketing, and record them as the handler of the conversation's interaction.</summary>
    /// <remarks>
    /// The <b>agent action</b> endpoint: when an agent opens or works with
    /// Ticketing from Genesys, this resolves exactly which Ticketing user that
    /// agent is — by the immutable Genesys User ID, never by name or email —
    /// and answers with that user's id, roles and departments.
    ///
    /// <para>
    /// <b>Mapping is administrative, never automatic.</b> A Genesys User ID
    /// that no Ticketing user carries is refused with <c>403</c> and the code
    /// <c>GENESYS_AGENT_NOT_MAPPED</c>; no user is ever created here. Set the
    /// mapping on the Ticketing user (<c>AspNetUsers.GenesysUserId</c>) first.
    /// </para>
    ///
    /// <para>
    /// <b>The handler is resolved on the server.</b> The request carries no
    /// Ticketing user id, and none would be honoured: the interaction's
    /// <c>HandledByUserId</c> is whatever the Genesys User ID maps to. When
    /// <c>conversationId</c> is supplied it must resolve to an ingested
    /// conversation; the first agent resolved on an interaction is the one
    /// recorded, and a later agent does not overwrite it.
    /// </para>
    ///
    /// <para>
    /// <b>Records who handled the interaction and nothing else.</b> The
    /// ticket's assignee, department, queue, status and workflow are not
    /// changed by this call.
    /// </para>
    /// </remarks>
    /// <param name="request">The agent's Genesys identity, and the conversation being worked.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <response code="200">The agent resolved to a Ticketing user; when a conversation was supplied, its interaction now records that user as the handler.</response>
    /// <response code="400">genesysUserId was missing or blank.</response>
    /// <response code="403">The Genesys agent is not mapped to a Ticketing user (<c>GENESYS_AGENT_NOT_MAPPED</c>), or the mapped user is deactivated (<c>GENESYS_AGENT_INACTIVE</c>).</response>
    /// <response code="404">A conversation id was supplied but no interaction exists for it.</response>
    /// <response code="503">The Genesys integration is switched off.</response>
    [HttpPost("agent-context")]
    [ProducesResponseType<GenesysAgentContextResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> ResolveAgentContext(
        [FromBody] GenesysAgentContextRequest request, CancellationToken cancellationToken)
    {
        var employeeId = GetEmployeeId();
        if (employeeId is null)
        {
            return Unauthorized();
        }

        // The ordinary validation answer, before any lookup: an agent action
        // without the agent's id is a malformed request, not a mapping gap.
        if (string.IsNullOrWhiteSpace(request.GenesysUserId))
        {
            ModelState.AddModelError(nameof(request.GenesysUserId), "genesysUserId is required.");
            return ValidationProblem(ModelState);
        }

        var result = await agentContextAppService.ResolveAsync(
            employeeId.Value, GenesysContractMapper.Map(request), cancellationToken);

        return result.Outcome switch
        {
            GenesysAgentContextOutcome.Resolved => Ok(
                new GenesysAgentContextResponse(
                    nameof(GenesysAgentContextOutcome.Resolved),
                    result.Agent!.GenesysUserId,
                    result.Agent.UserId,
                    result.Agent.UserName,
                    result.Agent.DisplayName,
                    result.Roles ?? [],
                    result.DepartmentIds ?? [],
                    string.IsNullOrWhiteSpace(request.ConversationId) ? null : request.ConversationId.Trim(),
                    result.TicketId,
                    result.TicketNumber,
                    result.TicketInteractionId,
                    result.HandledByUserId)),

            GenesysAgentContextOutcome.IntegrationDisabled => Problem(
                type: "https://tigercs.internal/problems/genesys-integration-disabled",
                title: "The Genesys integration is disabled",
                detail: "Genesys:Enabled is false — no Genesys request is processed while the integration is switched off.",
                statusCode: StatusCodes.Status503ServiceUnavailable),

            GenesysAgentContextOutcome.AgentIdRequired => ValidationProblem(
                new ValidationProblemDetails(new Dictionary<string, string[]>
                {
                    [nameof(request.GenesysUserId)] = [result.Detail ?? "genesysUserId is required."]
                })),

            GenesysAgentContextOutcome.AgentNotMapped => CodedProblem(
                ErrorCodes.AgentNotMapped,
                type: "https://tigercs.internal/problems/genesys-agent-not-mapped",
                title: "Genesys agent is not mapped to a Ticketing user",
                detail: "Genesys agent is not mapped to a Ticketing user.",
                statusCode: StatusCodes.Status403Forbidden),

            GenesysAgentContextOutcome.AgentInactive => CodedProblem(
                ErrorCodes.AgentInactive,
                type: "https://tigercs.internal/problems/genesys-agent-inactive",
                title: "The Ticketing user mapped to this Genesys agent is deactivated",
                detail: result.Detail ?? "The Ticketing user mapped to this Genesys agent is deactivated.",
                statusCode: StatusCodes.Status403Forbidden),

            GenesysAgentContextOutcome.ConversationNotFound => Problem(
                type: "https://tigercs.internal/problems/genesys-conversation-not-found",
                title: "No interaction exists for this conversation",
                detail: result.Detail ?? "This conversation never produced a ticket.",
                statusCode: StatusCodes.Status404NotFound),

            _ => Problem(statusCode: StatusCodes.Status500InternalServerError)
        };
    }

    /// <summary>Issue a Secure Screen Pop launch URL that opens TigerCS Web, signed in as the Genesys agent's mapped TigerCS user.</summary>
    /// <remarks>
    /// Genesys opens the returned <c>launchUrl</c> in any browser or WebView —
    /// no TigerCS login cookie is needed beforehand. TigerCS Web redeems the
    /// token, creates an ordinary authenticated session for the mapped user,
    /// and redirects to <c>targetPath</c>.
    ///
    /// <para>
    /// <b>The token:</b> 256 bits of cryptographic randomness, valid for one
    /// hour and exactly one use. Only its SHA-256 hash is stored, and it
    /// carries no username, password or user data.
    /// </para>
    ///
    /// <para>
    /// <b>Authentication only.</b> The session is the one a password login
    /// would give the mapped user; the target page is authorized under the
    /// normal department-visibility rules, and a ticket the user may not see
    /// stays refused. The token grants no access of its own.
    /// </para>
    ///
    /// <para>
    /// <b>Landing priority:</b> the ticket <c>conversationId</c> produced
    /// (whose interaction then records this agent as handler) → <c>ticketId</c>
    /// → Customer Lookup for <c>customerPhone</c> → the Tickets list. A value
    /// that resolves to nothing falls through to the next.
    /// </para>
    /// </remarks>
    /// <param name="request">The agent's Genesys User ID and, optionally, what to open.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <response code="200">A launch URL, valid once for one hour.</response>
    /// <response code="400">genesysUserId was missing or blank.</response>
    /// <response code="403">The Genesys agent is not mapped (<c>GENESYS_AGENT_NOT_MAPPED</c>) or the mapped user is deactivated (<c>GENESYS_AGENT_INACTIVE</c>).</response>
    /// <response code="503">The Genesys integration is switched off, or <c>Genesys:ScreenPopWebBaseUrl</c> is not configured (<c>GENESYS_SCREEN_POP_NOT_CONFIGURED</c>).</response>
    [HttpPost("screen-pop")]
    [ProducesResponseType<GenesysScreenPopResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> IssueScreenPop(
        [FromBody] GenesysScreenPopRequest request, CancellationToken cancellationToken)
    {
        var employeeId = GetEmployeeId();
        if (employeeId is null)
        {
            return Unauthorized();
        }

        if (string.IsNullOrWhiteSpace(request.GenesysUserId))
        {
            ModelState.AddModelError(nameof(request.GenesysUserId), "genesysUserId is required.");
            return ValidationProblem(ModelState);
        }

        var result = await screenPopAppService.IssueAsync(employeeId.Value, GenesysContractMapper.Map(request), cancellationToken);

        return result.Outcome switch
        {
            GenesysScreenPopIssueOutcome.Issued => Ok(new GenesysScreenPopResponse(
                result.LaunchUrl!,
                result.ExpiresAtUtc!.Value,
                (int)Domain.Modules.GenesysIntegration.GenesysScreenPopLaunch.Lifetime.TotalSeconds,
                result.TargetPath!,
                result.TicketId)),

            GenesysScreenPopIssueOutcome.IntegrationDisabled => Problem(
                type: "https://tigercs.internal/problems/genesys-integration-disabled",
                title: "The Genesys integration is disabled",
                detail: "Genesys:Enabled is false — no Genesys request is processed while the integration is switched off.",
                statusCode: StatusCodes.Status503ServiceUnavailable),

            GenesysScreenPopIssueOutcome.NotConfigured => CodedProblem(
                ErrorCodes.ScreenPopNotConfigured,
                type: "https://tigercs.internal/problems/genesys-screen-pop-not-configured",
                title: "Screen Pop is not configured",
                detail: result.Detail ?? "Genesys:ScreenPopWebBaseUrl is not configured.",
                statusCode: StatusCodes.Status503ServiceUnavailable),

            GenesysScreenPopIssueOutcome.AgentIdRequired => ValidationProblem(
                new ValidationProblemDetails(new Dictionary<string, string[]>
                {
                    [nameof(request.GenesysUserId)] = [result.Detail ?? "genesysUserId is required."]
                })),

            GenesysScreenPopIssueOutcome.AgentInactive => CodedProblem(
                ErrorCodes.AgentInactive,
                type: "https://tigercs.internal/problems/genesys-agent-inactive",
                title: "The Ticketing user mapped to this Genesys agent is deactivated",
                detail: result.Detail ?? "The Ticketing user mapped to this Genesys agent is deactivated.",
                statusCode: StatusCodes.Status403Forbidden),

            _ => CodedProblem(
                ErrorCodes.AgentNotMapped,
                type: "https://tigercs.internal/problems/genesys-agent-not-mapped",
                title: "Genesys agent is not mapped to a Ticketing user",
                detail: "Genesys agent is not mapped to a Ticketing user.",
                statusCode: StatusCodes.Status403Forbidden)
        };
    }

    /// <summary>
    /// The standard ProblemDetails body every other refusal on this API uses,
    /// plus a stable machine-readable <c>code</c> member — so an integrator
    /// can branch on the code while the human-facing title/detail stay free
    /// to change. Not a second error format.
    /// </summary>
    private ObjectResult CodedProblem(string code, string type, string title, string detail, int statusCode)
    {
        var result = Problem(type: type, title: title, detail: detail, statusCode: statusCode);
        if (result.Value is ProblemDetails problem)
        {
            problem.Extensions["code"] = code;
        }

        return result;
    }

    private Guid? GetEmployeeId()
    {
        var idValue = User.FindFirstValue(ClaimTypes.NameIdentifier);
        return idValue is not null && Guid.TryParse(idValue, out var employeeId) ? employeeId : null;
    }
}
