using System.Net.Http.Headers;
using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using TigerCS.Api.OpenApi;
using TigerCS.Application.Modules.CrmDocuments.Dto;
using TigerCS.Application.Modules.CrmDocuments.Services;
using TigerCS.Infrastructure.Modules.IdentityAndAccess.Authorization;
using TigerCS.Domain.Modules.IdentityAndAccess.Permissions;

namespace TigerCS.Api.Controllers;

/// <summary>
/// The Customer Details <b>Documents</b> tab: a customer's Reservation Form, Tiger Contract and Registration Receipt
/// for one unit, the authorized file behind each, and "Send to customer".
///
/// <para>
/// Same door as the Customer Profile: signed-in staff (<c>AuthenticatedStaff</c>), the customer must be visible to the
/// caller, and every unit and record is checked against Tiger CRM's own answer for that customer
/// (<see cref="CustomerDocumentsAppService"/>). The file route streams the bytes TigerCS fetched with the server-side CRM
/// credential — the browser never receives CRM's storage reference or credential. Sending reuses the delivery service
/// behind the Genesys <c>send-copy</c> route and reports its outcome unchanged (<see cref="CrmDocumentCopyResponses"/>).
/// </para>
/// </summary>
[ApiController]
[Route("api/customers/{customerKey}/documents")]
[Authorize(Policy = PolicyNames.AuthenticatedStaff)]
[Tags(OpenApiTags.CustomerDirectory)]
public sealed class CustomerDocumentsController(CustomerDocumentsAppService documents) : ControllerBase
{
    /// <summary>The customer's documents for one unit (<c>crmLeadId</c>), or the unit list when several units exist and none is chosen.</summary>
    /// <param name="customerKey">The directory key, <c>crm:{customerId}</c>. Only CRM customers have documents.</param>
    /// <param name="crmLeadId">The CRM lead of the selected unit. Must be one of the customer's units (403 otherwise).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    [HttpGet]
    [RequirePermission(Permissions.CustomersView)]
    [ProducesResponseType<CustomerDocumentsDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status502BadGateway)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> List(string customerKey, [FromQuery] int? crmLeadId, CancellationToken cancellationToken)
    {
        if (Caller() is not { } caller)
        {
            return Unauthorized();
        }

        var result = await documents.ListAsync(caller.EmployeeId, caller.Roles, customerKey, crmLeadId, cancellationToken);
        return result.IsSuccess ? Ok(result.Value) : Failure(result);
    }

    /// <summary>
    /// The file of one listed document. <c>download=true</c> asks the browser to save it; otherwise it is offered inline.
    /// The record must be one CRM lists right now for this customer, unit and type.
    /// </summary>
    /// <param name="customerKey">The directory key, <c>crm:{customerId}</c>.</param>
    /// <param name="crmLeadId">The unit's CRM lead id.</param>
    /// <param name="documentType"><c>ReservationForm</c>, <c>Contract</c> or <c>RegistrationReceipt</c>.</param>
    /// <param name="recordId">CRM's attachment id from the listing.</param>
    /// <param name="download">True to send as an attachment (save), false to open inline.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    [HttpGet("{crmLeadId:int}/{documentType}/{recordId}/file")]
    [RequirePermission(Permissions.CustomersView)]
    [Produces("application/octet-stream")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status502BadGateway)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> GetFile(
        string customerKey, int crmLeadId, string documentType, string recordId, [FromQuery] bool download, CancellationToken cancellationToken)
    {
        if (Caller() is not { } caller)
        {
            return Unauthorized();
        }

        var result = await documents.GetFileAsync(caller.EmployeeId, caller.Roles, customerKey, crmLeadId, documentType, recordId, cancellationToken);
        if (!result.IsSuccess)
        {
            return Failure(result);
        }

        var content = result.Value!;
        Response.Headers.CacheControl = "private, no-store";
        Response.Headers.XContentTypeOptions = "nosniff";
        var disposition = new ContentDispositionHeaderValue(download ? "attachment" : "inline") { FileNameStar = content.FileName };
        Response.Headers.ContentDisposition = disposition.ToString();
        return File(content.Bytes, content.ContentType);
    }

    /// <summary>
    /// Email one listed document to the customer, as an attachment, to the address CRM holds for them. Same delivery
    /// service and idempotency rules as <c>POST /api/genesys/documents/send-copy</c>; <c>Sent</c> means the mail
    /// server accepted the message — not that the customer received or opened it.
    /// </summary>
    /// <param name="customerKey">The directory key, <c>crm:{customerId}</c>.</param>
    /// <param name="request">The unit, document type and record to send.</param>
    /// <param name="idempotencyKey">Required. Reuse it on a retry of the same click; a new send needs a new key.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    [HttpPost("send")]
    [RequirePermission(Permissions.CustomersSend)]
    [ProducesResponseType<CrmDocumentCopyResult>(StatusCodes.Status200OK)]
    [ProducesResponseType<CrmDocumentCopyResult>(StatusCodes.Status202Accepted)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status422UnprocessableEntity)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status502BadGateway)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> Send(
        string customerKey, [FromBody] CustomerDocumentSendRequestDto request,
        [FromHeader(Name = "Idempotency-Key")] string? idempotencyKey, CancellationToken cancellationToken)
    {
        if (Caller() is not { } caller)
        {
            return Unauthorized();
        }

        var result = await documents.SendAsync(caller.EmployeeId, caller.Roles, customerKey, request, idempotencyKey, cancellationToken);
        return this.ToDocumentCopyResponse(result);
    }

    private IActionResult Failure<T>(CustomerDocumentsResult<T> result)
    {
        var status = result.Outcome switch
        {
            CustomerDocumentsOutcome.InvalidRequest => StatusCodes.Status400BadRequest,
            CustomerDocumentsOutcome.NotFound => StatusCodes.Status404NotFound,
            CustomerDocumentsOutcome.OwnershipMismatch => StatusCodes.Status403Forbidden,
            CustomerDocumentsOutcome.SourceRejected => StatusCodes.Status502BadGateway,
            _ => StatusCodes.Status503ServiceUnavailable
        };

        var problem = Problem(
            type: $"https://tigercs.internal/problems/{(result.Code ?? "customer-documents").ToLowerInvariant().Replace('_', '-')}",
            title: "Customer documents", detail: result.Message, statusCode: status);
        if (problem.Value is ProblemDetails details)
        {
            details.Extensions["code"] = result.Code;
        }

        return problem;
    }

    private (Guid EmployeeId, IReadOnlyCollection<string> Roles)? Caller()
    {
        var idValue = User.FindFirstValue(ClaimTypes.NameIdentifier);
        return idValue is not null && Guid.TryParse(idValue, out var employeeId)
            ? (employeeId, User.FindAll(ClaimTypes.Role).Select(c => c.Value).ToList())
            : null;
    }
}
