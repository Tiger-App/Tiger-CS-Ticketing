using System.Globalization;
using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using TigerCS.Api.OpenApi;
using TigerCS.Application.Modules.Collections;
using TigerCS.Application.Modules.Collections.Dto;
using TigerCS.Application.Modules.Collections.Services;
using TigerCS.Application.Modules.Ticketing.Services;
using TigerCS.Infrastructure.Modules.IdentityAndAccess.Authorization;
using TigerCS.Infrastructure.Modules.IdentityAndAccess.Services;
using TigerCS.Domain.Modules.IdentityAndAccess.Permissions;

namespace TigerCS.Api.Controllers;

/// <summary>
/// Collections for Genesys: <c>/api/genesys/collections</c>. Genesys reaches it
/// the same way it reaches the rest of <c>api/genesys</c> — through
/// TigerGroupWeb, signed in as a TigerCS integration service account. The
/// account must also be listed in <c>Collections:Authorization:IntegrationEmployeeIds</c>
/// for outcomes and VoiceBot queueing; the Genesys scope alone grants no
/// financial operation.
/// </summary>
[Route("api/genesys/collections")]
[Tags(OpenApiTags.Collections)]
[AllowServiceIdentity]
public sealed class GenesysCollectionsController(
    CollectionsAccountQueryAppService queries,
    CollectionsPaymentSummaryAppService paymentSummaries,
    CollectionsReminderAppService reminders,
    CollectionsReminderOutcomeAppService outcomes) : CollectionsControllerBase(queries, paymentSummaries, reminders, outcomes)
{
    protected override CollectionsReadSurface Surface => CollectionsReadSurface.Genesys;
}

/// <summary>
/// Collections for TigerCS Web's Payment tab: <c>/api/collections</c> — the
/// same service and the same server-side financial checks, under the existing
/// Web-to-API authentication. No Genesys credential ever reaches the browser.
/// </summary>
[Route("api/collections")]
[Tags(OpenApiTags.Collections)]
public sealed class CollectionsController(
    CollectionsAccountQueryAppService queries,
    CollectionsPaymentSummaryAppService paymentSummaries,
    CollectionsReminderAppService reminders,
    CollectionsReminderOutcomeAppService outcomes) : CollectionsControllerBase(queries, paymentSummaries, reminders, outcomes)
{
    /// <summary>
    /// Returns the payment and fine summary for a verified lookup customer before
    /// ticket creation, subject to the caller's financial read permissions.
    /// </summary>
    [HttpGet("customer-lookup/payment-summary")]
    [RequirePermission(Permissions.CollectionsView)]
    [Authorize(Policy = PolicyNames.CustomerVerification)]
    [ProducesResponseType<CollectionsPaymentSummaryResponseDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> GetLookupPaymentSummary(
        [FromQuery] string phoneNumber, [FromQuery] string customerKey,
        [FromServices] CustomerSearchAppService search, CancellationToken cancellationToken)
    {
        if (Caller() is not { } caller) return Unauthorized();
        return ToResponse(await PaymentSummaries.GetForLookupAsync(caller, phoneNumber, customerKey, search, cancellationToken));
    }

    protected override CollectionsReadSurface Surface => CollectionsReadSurface.Web;
}

/// <summary>
/// The six Collections routes (TigerCS_Collections_API_Specification.md).
///
/// <para>
/// Every route authorizes the actual caller against explicit Collections
/// grants — financial read, reminder send, outcome reporting — on top of the
/// authenticated-staff policy; ticket permissions are not involved. Figures
/// come from the authoritative financial source on every call: while it is
/// unavailable the financial routes answer <c>503 FinanceUnavailable</c>,
/// never an empty list or a zero.
/// </para>
///
/// <para>
/// Errors are the standard ProblemDetails body with the specification's
/// machine-readable <c>code</c> and a <c>message</c>, plus <c>traceId</c>.
/// </para>
/// </summary>
[ApiController]
[Authorize(Policy = PolicyNames.AuthenticatedStaff)]
public abstract class CollectionsControllerBase(
    CollectionsAccountQueryAppService queries,
    CollectionsPaymentSummaryAppService paymentSummaries,
    CollectionsReminderAppService reminders,
    CollectionsReminderOutcomeAppService outcomes) : ControllerBase
{
    /// <summary>
    /// EDSM's payment summary for a PACT-identified TigerCS customer: one entry per
    /// confirmed (companyID, tenantID) pair (docs/Collections/Genesys-Collections-API.md).
    /// The same service backs the Payment tab and Genesys.
    /// </summary>
    /// <param name="customerKey">The TigerCS customer key: <c>ext:Pact:{tenantID}</c>. CRM keys answer NotMapped.</param>
    /// <param name="includeTransactions">Also return payment-transactions types 1–3 per company (default true).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <response code="200">Mapped (with per-company status), or NotMapped with the reason.</response>
    /// <response code="400">InvalidRequest — malformed customer key.</response>
    /// <response code="403">Forbidden — no financial-read permission.</response>
    /// <response code="404">AccountNotFound — customer not known to TigerCS, or not visible.</response>
    /// <response code="503">FinanceUnavailable (PACT unreachable), or CollectionsDisabled.</response>
    [HttpGet("customers/by-key/{customerKey}/payment-summary")]
    [RequirePermission(Permissions.CollectionsView)]
    [ProducesResponseType<CollectionsPaymentSummaryResponseDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> GetPaymentSummary(
        string customerKey, [FromQuery] bool? includeTransactions, CancellationToken cancellationToken)
    {
        if (Caller() is not { } caller) return Unauthorized();
        return ToResponse(await paymentSummaries.GetAsync(caller, customerKey, includeTransactions ?? true, ReadDeadline(), cancellationToken));
    }

    /// <summary>
    /// Read-only payment history for one of the customer's confirmed EDSM companies:
    /// EDSM payment-transactions type Paid (1), Due (2) or Outstanding (3). Type All (4)
    /// is refused, because for rented companies EDSM writes to its databases on that path.
    /// </summary>
    /// <param name="customerKey">The TigerCS customer key: <c>ext:Pact:{tenantID}</c>.</param>
    /// <param name="companyId">Required. One of the companies the payment summary lists for this customer.</param>
    /// <param name="type">Required. <c>Paid</c>, <c>Due</c>, <c>Outstanding</c> (or 1, 2, 3).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <response code="200">The rows EDSM returned (possibly none).</response>
    /// <response code="400">InvalidRequest — missing companyId, or type missing, unknown or All (4).</response>
    /// <response code="403">Forbidden — no financial-read permission.</response>
    /// <response code="404">AccountNotFound — customer unknown/not visible, or the company is not among its PACT contracts.</response>
    /// <response code="422">CustomerNotMapped — no verified PACT mapping (e.g. a CRM customer).</response>
    /// <response code="503">FinanceUnavailable (PACT or EDSM failed), or CollectionsDisabled.</response>
    [HttpGet("customers/by-key/{customerKey}/payment-transactions")]
    [RequirePermission(Permissions.CollectionsView)]
    [ProducesResponseType<CollectionsPaymentTransactionsResponseDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status422UnprocessableEntity)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> GetPaymentTransactions(
        string customerKey, [FromQuery] int? companyId, [FromQuery] string? type, CancellationToken cancellationToken)
    {
        if (Caller() is not { } caller) return Unauthorized();
        return ToResponse(await paymentSummaries.GetTransactionsAsync(caller, customerKey, companyId, type, ReadDeadline(), cancellationToken));
    }

    /// <summary>Outstanding amounts for one or all of a customer's accounts.</summary>
    /// <param name="crmCustomerId">The Tiger CRM customer id.</param>
    /// <param name="accountId">One finance account. Must belong to the customer.</param>
    /// <param name="unitId">The account(s) for one CRM unit; several contracts on a unit are returned separately.</param>
    /// <param name="cursor">From a previous response's nextCursor.</param>
    /// <param name="pageSize">1–100, default 50.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <response code="200">The accounts, each in its own currency.</response>
    /// <response code="400">InvalidRequest.</response>
    /// <response code="403">Forbidden — no financial-read permission.</response>
    /// <response code="404">AccountNotFound — absent, or not this customer's.</response>
    /// <response code="503">FinanceUnavailable, or Collections disabled.</response>
    [HttpGet("customers/{crmCustomerId}/outstanding")]
    [RequirePermission(Permissions.CollectionsView)]
    [ProducesResponseType<CollectionsOutstandingResponseDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> GetOutstanding(
        string crmCustomerId, [FromQuery] string? accountId, [FromQuery] long? unitId, [FromQuery] string? cursor, [FromQuery] int? pageSize,
        CancellationToken cancellationToken)
    {
        if (Caller() is not { } caller) return Unauthorized();
        if (!TryCustomer(crmCustomerId, out var id)) return InvalidCustomer();
        return ToResponse(await queries.GetOutstandingAsync(caller, id, accountId, unitId, cursor, pageSize, cancellationToken));
    }

    /// <summary>The instalment schedule (<c>view=instalments</c>, default) or posted payment history (<c>view=history</c>) of one account.</summary>
    /// <param name="crmCustomerId">The Tiger CRM customer id.</param>
    /// <param name="accountId">Required when the customer has more than one account in scope.</param>
    /// <param name="unitId">Narrow to one CRM unit.</param>
    /// <param name="view">instalments or history.</param>
    /// <param name="fromDate">history only: earliest payment date (YYYY-MM-DD).</param>
    /// <param name="toDate">history only: latest payment date.</param>
    /// <param name="cursor">From a previous response's nextCursor.</param>
    /// <param name="pageSize">1–100, default 50.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <response code="200">One page of instalments or posted payments.</response>
    /// <response code="400">InvalidRequest — e.g. accountId needed, bad view or date range.</response>
    /// <response code="403">Forbidden.</response>
    /// <response code="404">AccountNotFound.</response>
    /// <response code="503">FinanceUnavailable, or Collections disabled.</response>
    [HttpGet("customers/{crmCustomerId}/payments")]
    [RequirePermission(Permissions.CollectionsView)]
    [ProducesResponseType<CollectionsInstalmentsResponseDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<CollectionsPaymentHistoryResponseDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> GetPayments(
        string crmCustomerId,
        [FromQuery] string? accountId,
        [FromQuery] long? unitId,
        [FromQuery] string? view,
        [FromQuery] DateOnly? fromDate,
        [FromQuery] DateOnly? toDate,
        [FromQuery] string? cursor,
        [FromQuery] int? pageSize,
        CancellationToken cancellationToken)
    {
        if (Caller() is not { } caller) return Unauthorized();
        if (!TryCustomer(crmCustomerId, out var id)) return InvalidCustomer();

        return (view ?? "instalments").Trim().ToLowerInvariant() switch
        {
            "instalments" => ToResponse(await queries.GetInstalmentsAsync(caller, id, accountId, unitId, cursor, pageSize, cancellationToken)),
            "history" => ToResponse(await queries.GetPaymentHistoryAsync(caller, id, accountId, unitId, fromDate, toDate, cursor, pageSize, cancellationToken)),
            _ => Error(StatusCodes.Status400BadRequest, "InvalidRequest", "view must be \"instalments\" or \"history\".")
        };
    }

    /// <summary>Accounts eligible for one reminder type today, with the quoted amount, its basis and qualifying instalments.</summary>
    /// <remarks>
    /// Windows (Dubai calendar): OverdueMonthly 1st–4th, CurrentMonth 15th,
    /// MonthEndFollowUp three days before month end. Settled, stale and
    /// inconsistent accounts are never listed, and contact details are never
    /// exposed. Queue a candidate with <c>POST reminders</c> before
    /// <c>expiresAtUtc</c>.
    /// </remarks>
    /// <param name="reminderType">Required: OverdueMonthly, CurrentMonth or MonthEndFollowUp.</param>
    /// <param name="businessDate">Optional; must be today in Asia/Dubai.</param>
    /// <param name="crmCustomerId">Extension: only this customer's accounts.</param>
    /// <param name="accountId">Extension: only this account (with crmCustomerId).</param>
    /// <param name="cursor">From a previous response's nextCursor.</param>
    /// <param name="pageSize">1–100, default 50.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <response code="200">One page of candidates (empty when the window is closed or nobody is due).</response>
    /// <response code="400">InvalidRequest.</response>
    /// <response code="403">Forbidden — no reminder permission.</response>
    /// <response code="503">FinanceUnavailable, or Collections disabled.</response>
    [HttpGet("reminders/candidates")]
    [RequirePermission(Permissions.CollectionsView)]
    [ProducesResponseType<CollectionsReminderCandidatesResponseDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> ListCandidates(
        [FromQuery] string? reminderType,
        [FromQuery] DateOnly? businessDate,
        [FromQuery] long? crmCustomerId,
        [FromQuery] string? accountId,
        [FromQuery] string? cursor,
        [FromQuery] int? pageSize,
        CancellationToken cancellationToken)
    {
        if (Caller() is not { } caller) return Unauthorized();
        return ToResponse(await reminders.ListCandidatesAsync(caller, reminderType, businessDate, crmCustomerId, accountId, cursor, pageSize, cancellationToken));
    }

    /// <summary>Queue one reminder from a candidate, on one or more channels.</summary>
    /// <remarks>
    /// The balance and eligibility are re-read before accepting (and again
    /// before each TigerCS delivery). An expired or changed candidate is
    /// <c>409 CandidateChanged</c> with a <c>replacementCandidate</c>. Duplicate
    /// prevention is per account + type + cycle + channel; an Idempotency-Key
    /// replay returns the original job (200), the same key with a different
    /// body is <c>409 IdempotencyConflict</c>. Retries apply to failed channels
    /// only. VoiceBot is queued only by the Genesys integration account.
    /// </remarks>
    /// <param name="request">The candidate, channels and language.</param>
    /// <param name="idempotencyKey">Recommended. Replays return the original job.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <response code="200">Replay of an earlier request with the same Idempotency-Key.</response>
    /// <response code="202">Queued. Queued does not mean delivered.</response>
    /// <response code="400">InvalidRequest.</response>
    /// <response code="403">Forbidden.</response>
    /// <response code="409">CandidateChanged or IdempotencyConflict.</response>
    /// <response code="422">NoEligibleContact or ChannelNotEnabled.</response>
    /// <response code="503">FinanceUnavailable (including stale data), or Collections disabled.</response>
    [HttpPost("reminders")]
    [RequirePermission(Permissions.CollectionsSend)]
    [ProducesResponseType<CollectionsReminderJobDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<CollectionsReminderJobDto>(StatusCodes.Status202Accepted)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status422UnprocessableEntity)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> QueueReminder(
        [FromBody] QueueCollectionsReminderRequestDto request,
        [FromHeader(Name = "Idempotency-Key")] string? idempotencyKey,
        CancellationToken cancellationToken)
    {
        if (Caller() is not { } caller) return Unauthorized();
        return ToResponse(await reminders.QueueAsync(caller, request, idempotencyKey, cancellationToken));
    }

    /// <summary>Record a delivery event and/or the customer's response for one channel of a reminder.</summary>
    /// <remarks>
    /// Idempotent on <c>eventId</c> (and the Idempotency-Key header): a resend
    /// returns the original result; different content is
    /// <c>409 IdempotencyConflict</c>. A customer response in a conversation
    /// creates or reuses that conversation's ticket through the existing
    /// Genesys ingestion, routed to Collections by configuration, Unclassified,
    /// never resolved or closed. AlreadyPaid requests verification and posts
    /// nothing. When the ticket cannot be created yet the event is kept and the
    /// answer is 202 with <c>ticketResult: "Pending"</c>.
    /// </remarks>
    /// <param name="reminderId">"REM-123".</param>
    /// <param name="request">The event.</param>
    /// <param name="idempotencyKey">Optional.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <response code="200">Recorded (or replayed); ticketResult Created, Reused or NotRequired.</response>
    /// <response code="202">Recorded; ticketResult Pending — retried durably.</response>
    /// <response code="400">InvalidRequest.</response>
    /// <response code="403">Forbidden — only a configured integration account reports outcomes.</response>
    /// <response code="404">ReminderNotFound.</response>
    /// <response code="409">IdempotencyConflict, or ReminderSuppressed.</response>
    /// <response code="503">Collections disabled.</response>
    [HttpPost("reminders/{reminderId}/outcomes")]
    [RequirePermission(Permissions.CollectionsEdit)]
    [ProducesResponseType<RecordReminderOutcomeResponseDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<RecordReminderOutcomeResponseDto>(StatusCodes.Status202Accepted)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> RecordOutcome(
        string reminderId,
        [FromBody] RecordReminderOutcomeRequestDto request,
        [FromHeader(Name = "Idempotency-Key")] string? idempotencyKey,
        CancellationToken cancellationToken)
    {
        if (Caller() is not { } caller) return Unauthorized();
        return ToResponse(await outcomes.RecordAsync(caller, reminderId, request, idempotencyKey, cancellationToken));
    }

    /// <summary>A customer's reminder history: the amount quoted, per-channel status, responses and linked tickets. Read from TigerCS's own records, so it stays available while the financial source is down.</summary>
    /// <param name="crmCustomerId">The Tiger CRM customer id.</param>
    /// <param name="accountId">Narrow to one account.</param>
    /// <param name="cursor">From a previous response's nextCursor.</param>
    /// <param name="pageSize">1–100, default 50.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <response code="200">One page of reminders, newest first.</response>
    /// <response code="400">InvalidRequest.</response>
    /// <response code="403">Forbidden.</response>
    /// <response code="503">Collections disabled.</response>
    [HttpGet("customers/{crmCustomerId}/reminders")]
    [RequirePermission(Permissions.CollectionsView)]
    [ProducesResponseType<CollectionsReminderHistoryResponseDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> GetReminders(
        string crmCustomerId, [FromQuery] string? accountId, [FromQuery] string? cursor, [FromQuery] int? pageSize,
        CancellationToken cancellationToken)
    {
        if (Caller() is not { } caller) return Unauthorized();
        if (!TryCustomer(crmCustomerId, out var id)) return InvalidCustomer();
        return ToResponse(await queries.GetRemindersAsync(caller, id, accountId, cursor, pageSize, cancellationToken));
    }

    protected IActionResult ToResponse<T>(CollectionsResult<T> result) => result.Outcome switch
    {
        CollectionsOutcome.Success or CollectionsOutcome.Replayed => Ok(result.Value),
        CollectionsOutcome.Accepted => StatusCode(StatusCodes.Status202Accepted, result.Value),
        CollectionsOutcome.Disabled => Error(StatusCodes.Status503ServiceUnavailable, "CollectionsDisabled",
            "Collections is not enabled (Collections:Enabled is false)."),
        CollectionsOutcome.FinanceUnavailable => Error(StatusCodes.Status503ServiceUnavailable, "FinanceUnavailable",
            result.Detail ?? "Payment information is temporarily unavailable. Please try again."),
        CollectionsOutcome.Forbidden => Error(StatusCodes.Status403Forbidden, "Forbidden", result.Detail ?? "Not permitted."),
        CollectionsOutcome.InvalidRequest => Error(StatusCodes.Status400BadRequest, "InvalidRequest", result.Detail ?? "The request is not valid."),
        CollectionsOutcome.AccountNotFound => Error(StatusCodes.Status404NotFound, "AccountNotFound", result.Detail ?? "Account not found."),
        CollectionsOutcome.ReminderNotFound => Error(StatusCodes.Status404NotFound, "ReminderNotFound", result.Detail ?? "Reminder not found."),
        CollectionsOutcome.CandidateChanged => Error(StatusCodes.Status409Conflict, "CandidateChanged",
            result.Detail ?? "The candidate expired or changed.", result.Replacement),
        CollectionsOutcome.IdempotencyConflict => Error(StatusCodes.Status409Conflict, "IdempotencyConflict", result.Detail ?? "Idempotency conflict."),
        CollectionsOutcome.ReminderSuppressed => Error(StatusCodes.Status409Conflict, "ReminderSuppressed", result.Detail ?? "The reminder was suppressed."),
        CollectionsOutcome.NoEligibleContact => Error(StatusCodes.Status422UnprocessableEntity, "NoEligibleContact", result.Detail ?? "No approved destination."),
        CollectionsOutcome.ChannelNotEnabled => Error(StatusCodes.Status422UnprocessableEntity, "ChannelNotEnabled", result.Detail ?? "Channel not enabled."),
        CollectionsOutcome.NotMapped => Error(StatusCodes.Status422UnprocessableEntity, "CustomerNotMapped", result.Detail ?? "No verified PACT mapping for this customer."),
        _ => Problem(statusCode: StatusCodes.Status500InternalServerError)
    };

    /// <summary>The standard ProblemDetails body plus the specification's <c>code</c> and <c>message</c> — one error format, not two.</summary>
    private ObjectResult Error(int status, string code, string message, CollectionsReminderCandidateDto? replacement = null)
    {
        var result = Problem(
            type: $"https://tigercs.internal/problems/collections/{code}",
            title: code,
            detail: message,
            statusCode: status);
        if (result.Value is ProblemDetails problem)
        {
            problem.Extensions["code"] = code;
            problem.Extensions["message"] = message;
            if (replacement is not null)
            {
                problem.Extensions["replacementCandidate"] = replacement;
            }
        }

        return result;
    }

    private ObjectResult InvalidCustomer() =>
        Error(StatusCodes.Status400BadRequest, "InvalidRequest", "crmCustomerId must be a positive CRM customer id.");

    private static bool TryCustomer(string value, out long id) =>
        long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out id) && id > 0;

    /// <summary>Which prefix this controller serves; selects the read deadline's default.</summary>
    protected CollectionsPaymentSummaryAppService PaymentSummaries => paymentSummaries;
    protected abstract CollectionsReadSurface Surface { get; }

    /// <summary>
    /// The read's overall deadline: this surface's configured value, shortened (never lengthened)
    /// by <see cref="CollectionsEdsmOptions.DeadlineHeader"/> — TigerGroupWeb sends what it has
    /// left of the Genesys data action's time.
    /// </summary>
    private TimeSpan ReadDeadline()
    {
        int? requested = Request.Headers.TryGetValue(CollectionsEdsmOptions.DeadlineHeader, out var values)
            && int.TryParse(values.ToString(), NumberStyles.None, CultureInfo.InvariantCulture, out var seconds)
            ? seconds
            : null;
        return HttpContext.RequestServices.GetRequiredService<CollectionsEdsmOptions>().ReadDeadline(Surface, requested);
    }

    protected CollectionsCaller? Caller()
    {
        var idValue = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (idValue is null || !Guid.TryParse(idValue, out var employeeId))
        {
            return null;
        }

        var roles = User.FindAll(ClaimTypes.Role).Select(c => c.Value).ToArray();
        var departments = User.FindAll(TigerCsClaimTypes.DepartmentId)
            .Select(c => int.TryParse(c.Value, out var d) ? d : (int?)null)
            .OfType<int>()
            .ToArray();
        return new CollectionsCaller(employeeId, roles, departments);
    }
}
