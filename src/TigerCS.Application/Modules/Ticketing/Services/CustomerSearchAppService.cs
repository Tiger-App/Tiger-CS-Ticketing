using Microsoft.Extensions.Logging;
using TigerCS.Application.Modules.CustomerVerification.CrmIntegration;
using TigerCS.Application.Modules.CustomerVerification.Services;
using TigerCS.Application.Modules.Ticketing.Dto;

namespace TigerCS.Application.Modules.Ticketing.Services;

/// <summary>
/// The Dashboard/Customer Workspace's standalone customer search: one phone
/// number, searched across the sources the system already integrates,
/// before any intake record or ticket exists. Pure composition — the CRM
/// identity comes from the existing real CRM Buyer Lookup
/// (<see cref="CrmBuyerLookupAppService"/>, the same service the New Ticket
/// wizard calls), and the PACT/Tasleeh legs are
/// <see cref="CustomerLookupAppService.SearchExternalSourcesByPhoneAsync"/> —
/// no gateway, mapping, or verification rule is duplicated here, so the
/// workspace can never disagree with the wizard about who a customer is.
///
/// <para>
/// Read-only and side-effect free: no intake record is created, nothing is
/// persisted, and — matching every existing lookup — no outcome here ever
/// gates ticket creation. A source that fails reports Failed alongside the
/// others' results, never hiding them.
/// </para>
/// </summary>
public sealed class CustomerSearchAppService(
    CrmBuyerLookupAppService crmBuyerLookupAppService,
    CustomerLookupAppService customerLookupAppService,
    ILogger<CustomerSearchAppService>? logger = null)
{
    /// <summary><see cref="CustomerSearchResultDto.CrmFailureKind"/>: CRM could not be used (not configured, unreachable, or answering with an error status).</summary>
    public const string FailureKindUnavailable = "Unavailable";

    /// <summary><see cref="CustomerSearchResultDto.CrmFailureKind"/>: CRM did not answer before the request timed out.</summary>
    public const string FailureKindTimeout = "Timeout";

    /// <summary><see cref="CustomerSearchResultDto.CrmFailureKind"/>: rejected credentials, an unusable response, or any other fault.</summary>
    public const string FailureKindError = "Error";

    public async Task<CustomerSearchResultDto> SearchByPhoneAsync(
        string phoneNumber, CancellationToken cancellationToken = default)
    {
        var crmTask = LookUpCrmAsync(phoneNumber, cancellationToken);
        var externalTask = customerLookupAppService.SearchExternalSourcesByPhoneAsync(phoneNumber, cancellationToken);

        await Task.WhenAll(crmTask, externalTask);
        var crmResult = await crmTask;
        var externalResults = await externalTask;

        var crmStatus = crmResult.Outcome switch
        {
            CrmBuyerLookupOutcome.Success => "Found",
            CrmBuyerLookupOutcome.NotFound => "NotFound",
            CrmBuyerLookupOutcome.AmbiguousCustomerMatch => "AmbiguousMatch",
            _ => "Failed"
        };

        return new CustomerSearchResultDto(
            phoneNumber,
            crmStatus,
            crmResult.Outcome == CrmBuyerLookupOutcome.Success ? crmResult.Buyers ?? [] : [],
            externalResults,
            FailureKindOf(crmResult));
    }

    /// <summary>
    /// The CRM leg, isolated: an unexpected fault in it is reported as a failed CRM lookup (kind <c>Error</c>) next to whatever PACT/Tasleeh
    /// returned, instead of aborting the whole search - and, above all, instead of reading as "no customer". Only the exception type is
    /// logged; the phone number and any message that might echo it are not.
    /// </summary>
    private async Task<CrmBuyerLookupResult> LookUpCrmAsync(string phoneNumber, CancellationToken cancellationToken)
    {
        try
        {
            return await crmBuyerLookupAppService.GetBuyerByPhoneAsync(phoneNumber, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger?.LogError("Customer search: the CRM lookup failed unexpectedly ({ExceptionType}).", ex.GetType().Name);
            return CrmBuyerLookupResult.Unavailable("CRM lookup failed unexpectedly.", CrmBuyerLookupFailureReason.Error);
        }
    }

    /// <summary>
    /// Splits a failed CRM lookup into Unavailable / Timeout / Error (null for Found, NotFound and AmbiguousMatch, which are answers, not
    /// failures). <c>CrmStatus</c> stays "Failed" for all of them, exactly as before.
    /// </summary>
    internal static string? FailureKindOf(CrmBuyerLookupResult crmResult) => crmResult.Outcome switch
    {
        CrmBuyerLookupOutcome.Success or CrmBuyerLookupOutcome.NotFound or CrmBuyerLookupOutcome.AmbiguousCustomerMatch => null,
        CrmBuyerLookupOutcome.Unavailable => crmResult.FailureReason switch
        {
            CrmBuyerLookupFailureReason.Timeout => FailureKindTimeout,
            CrmBuyerLookupFailureReason.Error => FailureKindError,
            _ => FailureKindUnavailable
        },
        _ => FailureKindError
    };
}
