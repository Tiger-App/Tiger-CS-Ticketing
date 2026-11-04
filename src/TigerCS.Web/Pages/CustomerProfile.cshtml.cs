using TigerCS.Application.Modules.Ticketing.CustomerNames;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using TigerCS.Application.Modules.Collections.Dto;
using TigerCS.Application.Modules.CrmDocuments.Dto;
using TigerCS.Application.Modules.Ticketing.Dto;
using TigerCS.Domain.Modules.Ticketing;
using TigerCS.Web.Models;
using TigerCS.Web.Services;
using TigerCS.Web.Services.Api;
using TigerCS.Web.Services.Auth;

namespace TigerCS.Web.Pages;

/// <summary>
/// The Customer Profile (`/Customers/{customerKey}`): a CRM-style workspace
/// for one customer identity — header (name, phone, verification source,
/// primary unit), then Overview / Contact Info / Units / Tickets /
/// Interactions. Everything comes from what TigerCS itself persisted about
/// the customer (the Customers directory profile: tickets, units, phones,
/// interactions); for a CRM-verified customer the Contact Info and Units tabs
/// are enriched with the live CRM Buyer record through the existing
/// ticket-anchored customer-profile endpoint — the same read the New Ticket
/// wizard relies on, never a new CRM API.
/// </summary>
public sealed class CustomerProfileModel(
    CustomersApiClient customersApiClient,
    TicketsApiClient ticketsApiClient,
    TicketNameResolver nameResolver,
    CollectionsApiClient collectionsApiClient,
    CustomerPaymentPanelLoader paymentPanelLoader,
    CustomerDocumentsApiClient documentsApiClient,
    CustomerDocumentsPanelLoader documentsPanelLoader) : PageModel
{
    public const string PaymentTab = "payment";

    /// <summary>
    /// The Payment tab. Deferred (fetched when the tab is opened, via
    /// <see cref="OnGetPaymentPanelAsync"/>) unless the page was asked for
    /// with <c>?tab=payment</c> — the no-JavaScript path, the account
    /// selector and the return from Send Reminder — when it is rendered here.
    /// </summary>
    public CustomerPaymentPanel PaymentPanel { get; private set; } = new() { CustomerKey = string.Empty, State = PaymentPanelState.Deferred };

    public bool PaymentTabActive { get; private set; }

    [TempData]
    public string? PaymentNotice { get; set; }

    [TempData]
    public bool PaymentNoticeIsError { get; set; }

    /// <summary>
    /// The Documents tab. Deferred (fetched when the tab is opened, via <see cref="OnGetDocumentsPanelAsync"/>) unless the
    /// page was asked for with <c>?tab=documents</c> — the no-JavaScript path, the unit selector and the return from
    /// Send to customer — when it is rendered here.
    /// </summary>
    public CustomerDocumentsPanel DocumentsPanel { get; private set; } = CustomerDocumentsPanelLoader.Deferred(string.Empty, null);

    public bool DocumentsTabActive { get; private set; }

    [TempData]
    public string? DocumentsNotice { get; set; }

    [TempData]
    public bool DocumentsNoticeIsError { get; set; }

    public string CustomerKey { get; private set; } = string.Empty;
    public ApiOutcome Outcome { get; private set; }
    public CustomerDirectoryProfileDto? Profile { get; private set; }

    /// <summary>Live CRM Buyer details for a CRM-verified customer — null when the customer is not CRM-verified or the call failed (the page then says so, and still shows everything persisted).</summary>
    public CustomerProfileDto? CrmProfile { get; private set; }

    /// <summary>Where "Back to Customers" leads: the remembered directory list (search, filters, page), or the bare directory.</summary>
    public string CustomersHref { get; private set; } = CustomersContext.BasePath;

    /// <summary>The ticket the agent came from, when they arrived from Ticket Details — for a "Back to ticket" link.</summary>
    public long? FromTicketId { get; private set; }
    public string? FromTicketNumber { get; private set; }

    public TicketNameResolver NameResolver => nameResolver;
    public CurrentUser? Viewer { get; private set; }
    public bool CanCreateTicket { get; private set; }
    public bool ViewerCanReopen => TicketActions.CanReopen(Viewer?.Roles);

    public string DisplayName =>
        Profile is null ? "Customer"
        : CustomerNameResolver.FirstValid(Profile.DisplayName, CrmProfile?.FullNameEnglish, CrmProfile?.FullNameArabic) is { } name ? name
        : (Profile.IdentityKind == "Phone" ? "Unnamed caller" : $"{CustomersModel.SourceLabel(Profile.VerificationSource)} customer");

    /// <summary>
    /// The one number this customer is reached on: CRM's live record when
    /// there is one, else the most recently captured number. A CRM record
    /// that came back with a blank number counts as having none.
    /// </summary>
    public string? PrimaryPhone => FirstNonBlank(CrmProfile?.MobileNumber) ?? Profile?.PhoneNumbers.FirstOrDefault();

    /// <summary>
    /// The customer's OTHER numbers — genuinely different ones only.
    /// "+971509724162", "971509724162", "+971 50 972 4162" and
    /// "971-50-972-4162" are one number written four ways, so they are never
    /// listed as aliases of each other; a real second number still is. See
    /// <see cref="CustomerContact.OtherPhones"/>.
    /// </summary>
    public IReadOnlyList<string> OtherPhones => CustomerContact.OtherPhones(PrimaryPhone, Profile?.PhoneNumbers);

    /// <summary>The best real email: CRM's live record, else the verified external source's snapshot, else whatever a conversation captured. Never invented.</summary>
    public string? PrimaryEmail => FirstNonBlank(CrmProfile?.Email) ?? Profile?.Emails.FirstOrDefault();

    /// <summary>The customer's other emails, deduplicated against the primary case-insensitively.</summary>
    public IReadOnlyList<string> OtherEmails => CustomerContact.OtherEmails(PrimaryEmail, Profile?.Emails);

    private static string? FirstNonBlank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;

    /// <summary>The unit the customer's most recent ticket was raised for.</summary>
    public CustomerDirectoryUnitDto? PrimaryUnit => Profile?.Units.FirstOrDefault();

    /// <summary>The New Ticket wizard, with this customer's phone carried forward so the lookup step is pre-filled.</summary>
    public string NewTicketHref =>
        PrimaryPhone is { } phone ? $"/NewTicket?phoneNumber={Uri.EscapeDataString(phone)}" : "/NewTicket";

    public async Task<IActionResult> OnGetAsync(string customerKey, long? fromTicket, string? tab, string? account, int? unit, CancellationToken cancellationToken)
    {
        CustomerKey = customerKey;
        Viewer = CurrentUser.FromPrincipal(User);
        CanCreateTicket = TicketCreationPolicy.AppliesTo(Viewer);
        CustomersHref = CustomersContext.HrefFromCookieValue(Request.Cookies[CustomersContext.CookieName]);
        FromTicketId = fromTicket;

        await nameResolver.PrimeDepartmentsAsync(cancellationToken);

        var result = await customersApiClient.GetProfileAsync(customerKey, cancellationToken);
        Outcome = result.Outcome;
        if (!result.IsSuccess || result.Value is null)
        {
            return Outcome is ApiOutcome.NotFound or ApiOutcome.ValidationError ? NotFound() : Page();
        }

        Profile = result.Value;
        FromTicketNumber = FromTicketId is { } fromId ? Profile.Tickets.FirstOrDefault(t => t.TicketId == fromId)?.TicketNumber : null;

        if (Profile.IdentityKind == "Crm")
        {
            // Live CRM details, anchored on the customer's latest ticket (the
            // endpoint verifies the CRM record is this ticket's own customer).
            var crm = await ticketsApiClient.GetCustomerProfileAsync(Profile.LastTicketId, cancellationToken);
            CrmProfile = crm.IsSuccess ? crm.Value : null;
        }

        PaymentTabActive = string.Equals(tab, PaymentTab, StringComparison.OrdinalIgnoreCase);
        PaymentPanel = PaymentTabActive
            ? await paymentPanelLoader.LoadAsync(customerKey, CrmCustomerIdOf(Profile), account, PaymentNotice, PaymentNoticeIsError, cancellationToken)
            : new CustomerPaymentPanel { CustomerKey = customerKey, CrmCustomerId = CrmCustomerIdOf(Profile), State = CustomerPaymentPanel.HasPaymentSource(customerKey, CrmCustomerIdOf(Profile)) ? PaymentPanelState.Deferred : PaymentPanelState.NotCrmCustomer };

        DocumentsTabActive = string.Equals(tab, CustomerProfileTabs.Documents, StringComparison.OrdinalIgnoreCase);
        DocumentsPanel = DocumentsTabActive
            ? await documentsPanelLoader.LoadAsync(customerKey, unit, DocumentsNotice, DocumentsNoticeIsError, cancellationToken)
            : CustomerDocumentsPanelLoader.Deferred(customerKey, unit);

        return Page();
    }

    /// <summary>
    /// The Documents tab's content alone, fetched by site.js when the tab is opened and again when the unit changes.
    /// Nothing about the customer or unit is trusted from here: the Api checks both against CRM on every call.
    /// </summary>
    public async Task<IActionResult> OnGetDocumentsPanelAsync(string customerKey, int? unit, CancellationToken cancellationToken)
    {
        var panel = await documentsPanelLoader.LoadAsync(customerKey, unit, null, false, cancellationToken);
        return Partial("_CustomerDocumentsTab", panel);
    }

    /// <summary>
    /// Open / Download. The Api fetches the file from CRM with the server-side credential after re-checking that the customer is
    /// visible to this agent, the unit is the customer's and CRM lists the record; this only relays the bytes. A failure is a
    /// small plain-text page (the link opens in a new tab), never a blank file.
    /// </summary>
    public async Task<IActionResult> OnGetDocumentFileAsync(
        string customerKey, int? unit, string? type, string? record, bool download, CancellationToken cancellationToken)
    {
        if (unit is not { } leadId || string.IsNullOrWhiteSpace(type) || string.IsNullOrWhiteSpace(record))
        {
            return FileFailure(StatusCodes.Status400BadRequest, "The document link is incomplete.");
        }

        var result = await documentsApiClient.GetFileAsync(customerKey, leadId, type, record, download, cancellationToken);
        if (!result.IsSuccess || result.Value is not { } file)
        {
            return result.Outcome switch
            {
                ApiOutcome.Forbidden => FileFailure(StatusCodes.Status403Forbidden, "You don't have access to this document, or it is not one of this customer's documents."),
                ApiOutcome.Unauthorized => FileFailure(StatusCodes.Status401Unauthorized, "Your session is no longer valid. Sign in again."),
                ApiOutcome.NotFound => FileFailure(StatusCodes.Status404NotFound, "This document is no longer available."),
                ApiOutcome.ValidationError => FileFailure(StatusCodes.Status400BadRequest, "The document link is not valid."),
                ApiOutcome.BadGateway => FileFailure(StatusCodes.Status502BadGateway, "CRM refused to give the file. " + result.Detail),
                _ => FileFailure(StatusCodes.Status503ServiceUnavailable, "The document could not be retrieved right now. Try again shortly.")
            };
        }

        Response.Headers.CacheControl = "private, no-store";
        Response.Headers.XContentTypeOptions = "nosniff";
        var disposition = new System.Net.Http.Headers.ContentDispositionHeaderValue(download ? "attachment" : "inline") { FileNameStar = file.FileName };
        Response.Headers.ContentDisposition = disposition.ToString();
        return File(file.Bytes, file.ContentType);
    }

    private ContentResult FileFailure(int status, string message) =>
        new() { StatusCode = status, ContentType = "text/plain; charset=utf-8", Content = message };

    /// <summary>
    /// Send to customer. The Api re-checks ownership, takes the destination from CRM (nothing here names an address),
    /// and reports what the delivery service did. The form's own Idempotency-Key makes a double submit send once. The
    /// answer is relayed in words that match the evidence — accepted by the mail server is not delivered.
    /// </summary>
    public async Task<IActionResult> OnPostSendDocumentAsync(
        string customerKey, int? unit, string? type, string? record, string? documentName, string? idempotencyKey, CancellationToken cancellationToken)
    {
        var name = string.IsNullOrWhiteSpace(documentName) ? "The document" : documentName.Trim();
        var result = await documentsApiClient.SendAsync(
            customerKey, new CustomerDocumentSendRequestDto(unit, type, record),
            string.IsNullOrWhiteSpace(idempotencyKey) ? Guid.NewGuid().ToString("N") : idempotencyKey.Trim(), cancellationToken);

        (DocumentsNotice, DocumentsNoticeIsError) = CustomerDocumentsPanel.SendNotice(result, name);
        return RedirectToPage(null, null, new { customerKey, tab = CustomerProfileTabs.Documents, unit }, "documents");
    }

    /// <summary>
    /// The Payment tab's content alone, fetched by site.js when the tab is
    /// opened. The CRM customer id is always taken from the profile the
    /// viewer is allowed to see — never from the request.
    /// </summary>
    public async Task<IActionResult> OnGetPaymentPanelAsync(string customerKey, string? account, CancellationToken cancellationToken)
    {
        var result = await customersApiClient.GetProfileAsync(customerKey, cancellationToken);
        if (!result.IsSuccess || result.Value is null)
        {
            return result.Outcome is ApiOutcome.NotFound or ApiOutcome.ValidationError ? NotFound() : StatusCode(StatusCodes.Status502BadGateway);
        }

        var panel = await paymentPanelLoader.LoadAsync(customerKey, CrmCustomerIdOf(result.Value), account, null, false, cancellationToken);
        return Partial("_CustomerPaymentTab", panel);
    }

    /// <summary>
    /// Send Reminder. Queues the selected candidate on the chosen channels;
    /// the Api re-reads the balance, enforces the reminder permission, refuses
    /// a changed or expired candidate (409) and a duplicate in the cycle. The
    /// form's own Idempotency-Key makes a double submit queue once.
    /// </summary>
    public async Task<IActionResult> OnPostSendReminderAsync(
        string customerKey, string? accountId, string? candidateId, string[]? channels, string? language, string? idempotencyKey,
        CancellationToken cancellationToken)
    {
        var profile = await customersApiClient.GetProfileAsync(customerKey, cancellationToken);
        if (!profile.IsSuccess || profile.Value is null || CrmCustomerIdOf(profile.Value) is null)
        {
            return NotFound();
        }

        var result = await collectionsApiClient.QueueReminderAsync(
            new QueueCollectionsReminderRequestDto(candidateId, channels ?? [], language),
            string.IsNullOrWhiteSpace(idempotencyKey) ? Guid.NewGuid().ToString("N") : idempotencyKey,
            cancellationToken);

        (PaymentNotice, PaymentNoticeIsError) = result.IsSuccess
            ? ($"Reminder {result.Value!.ReminderId} queued for {CustomerPaymentPanel.Money(result.Value.ReminderAmount, result.Value.Currency)} on "
               + $"{string.Join(", ", result.Value.Channels.Select(c => CustomerPaymentPanel.Label(c.Channel)))}. Queued does not mean delivered.", false)
            : (CustomerPaymentPanel.ApiOutcomeMessage(result.Outcome, result.Detail), true);

        return RedirectToPage(null, null, new { customerKey, tab = PaymentTab, account = accountId }, "payment");
    }

    /// <summary>The Tiger CRM customer id, only for a customer identified in CRM.</summary>
    public static long? CrmCustomerIdOf(CustomerDirectoryProfileDto profile) =>
        profile.IdentityKind == "Crm" && profile.CrmBuyerCustomerId is { } id ? id : null;

    public static string SourceLabel(string verificationSource) => CustomersModel.SourceLabel(verificationSource);

    public static string SourceCssKey(string verificationSource) => CustomersModel.SourceCssKey(verificationSource);

    /// <summary>True while a ticket is in a non-terminal status.</summary>
    public static bool IsActive(string ticketStatus) => ticketStatus is "Open" or "InProgress" or "PendingCustomer" or "PendingThirdParty";
}
