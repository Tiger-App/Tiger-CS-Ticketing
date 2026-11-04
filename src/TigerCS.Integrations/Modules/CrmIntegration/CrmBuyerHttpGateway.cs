using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TigerCS.Application.Modules.CustomerVerification.CrmIntegration;
using TigerCS.Application.Modules.CustomerVerification.Dto;

namespace TigerCS.Integrations.Modules.CrmIntegration;

/// <summary>
/// Real HTTP-backed <see cref="ICrmBuyerLookupGateway"/> — calls the legacy
/// CRM MVC 4.7 application's <c>GET /TicketingSystem/GetBuyerByPhone</c>
/// endpoint, already implemented and manually verified CRM-side. Registered
/// as a typed <see cref="HttpClient"/> (<see cref="IntegrationsServiceCollectionExtensions"/>)
/// with its base address bound from <see cref="CrmGatewayOptions.BaseUrl"/>.
///
/// <para>
/// <b>Every failure mode maps to a <see cref="CrmBuyerLookupResult"/>
/// outcome — this gateway never throws for an expected CRM response.</b>
/// Timeouts, DNS/connection failures, an unconfigured base address, a
/// non-200/400/401 status, and a 200 body that doesn't parse as the
/// documented contract (or answers <c>success:false</c>) all collapse to
/// <see cref="CrmBuyerLookupOutcome.Unavailable"/> or
/// <see cref="CrmBuyerLookupOutcome.InvalidResponse"/> rather than an
/// unhandled exception — CRM lookup must never crash the caller.
/// </para>
///
/// <para>
/// <b>The X-SECRET-KEY value is never logged.</b> Every log line below names
/// the failure and the phone number only (masked — Security-Architecture.md
/// §11's discipline for PII in logs applies to a phone number exactly as it
/// does to an email address); the secret itself never appears in a format
/// string or an exception message this gateway constructs, and the default
/// <c>HttpClientFactory</c> logging handler does not log arbitrary request
/// header values.
/// </para>
/// </summary>
public sealed class CrmBuyerHttpGateway(
    HttpClient httpClient, IOptions<CrmGatewayOptions> options, ILogger<CrmBuyerHttpGateway> logger)
    : ICrmBuyerLookupGateway
{
    private const string SecretHeaderName = "X-SECRET-KEY";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true
    };

    public async Task<CrmBuyerLookupResult> GetBuyerByPhoneAsync(string phoneNumber, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(phoneNumber);

        var secretKey = options.Value.SecretKey;
        if (string.IsNullOrWhiteSpace(secretKey))
        {
            logger.LogError(
                "Crm:SecretKey is not configured — cannot call CRM GetBuyerByPhone for {MaskedPhoneNumber}. "
                + "See docs/DEV-SETUP.md for how to configure it via user-secrets/environment variable.",
                Mask(phoneNumber));
            return CrmBuyerLookupResult.Unavailable("Crm:SecretKey is not configured.", CrmLookupFailureKind.NotConfigured);
        }

        using var request = new HttpRequestMessage(
            HttpMethod.Get, $"TicketingSystem/GetBuyerByPhone?phoneNumber={Uri.EscapeDataString(phoneNumber)}");
        request.Headers.TryAddWithoutValidation(SecretHeaderName, secretKey);

        HttpResponseMessage response;
        try
        {
            response = await httpClient.SendAsync(request, cancellationToken);
        }
        catch (TaskCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            // HttpClient.Timeout elapsed — distinct from the caller's own
            // cancellationToken being cancelled, which is left to propagate.
            logger.LogWarning(ex, "CRM GetBuyerByPhone timed out for {MaskedPhoneNumber}.", Mask(phoneNumber));
            return CrmBuyerLookupResult.Unavailable("CRM request timed out.", CrmLookupFailureKind.TimedOut);
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException)
        {
            // InvalidOperationException covers an unconfigured Crm:BaseUrl
            // (typed HttpClient with no BaseAddress rejects a relative URI).
            logger.LogWarning(ex, "CRM GetBuyerByPhone could not be reached for {MaskedPhoneNumber}.", Mask(phoneNumber));
            return CrmBuyerLookupResult.Unavailable("CRM could not be reached.", CrmLookupFailureKind.Unreachable);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Anything else unexpected still comes back as a classified failure: an unhandled fault here would surface as a 500 to every
            // caller and, in the customer search, discard the PACT/Tasleeh results. Only the exception TYPE is logged - its message is not
            // ours to vouch for as free of customer data.
            logger.LogError(
                "CRM GetBuyerByPhone failed unexpectedly ({ExceptionType}) for {MaskedPhoneNumber}.", ex.GetType().Name, Mask(phoneNumber));
            return CrmBuyerLookupResult.Unavailable("CRM lookup failed unexpectedly.", CrmLookupFailureKind.UnexpectedError);
        }

        using (response)
        {
            switch (response.StatusCode)
            {
                case HttpStatusCode.OK:
                    return await ParseSuccessResponseAsync(response, phoneNumber, cancellationToken);
                case HttpStatusCode.Unauthorized:
                    logger.LogWarning("CRM GetBuyerByPhone returned 401 Unauthorized — check Crm:SecretKey.");
                    return CrmBuyerLookupResult.Unauthorized("CRM rejected the configured secret key.");
                case HttpStatusCode.NotFound:
                    return CrmBuyerLookupResult.NotFound();
                case HttpStatusCode.BadRequest:
                    logger.LogWarning(
                        "CRM GetBuyerByPhone returned 400 Bad Request for {MaskedPhoneNumber}.", Mask(phoneNumber));
                    return CrmBuyerLookupResult.InvalidResponse("CRM rejected the request (400).", CrmLookupFailureKind.RejectedRequest);
                default:
                    logger.LogWarning(
                        "CRM GetBuyerByPhone returned unexpected status {StatusCode} for {MaskedPhoneNumber}.",
                        (int)response.StatusCode, Mask(phoneNumber));
                    return CrmBuyerLookupResult.Unavailable($"CRM returned unexpected status {(int)response.StatusCode}.", CrmLookupFailureKind.UnexpectedStatus);
            }
        }
    }

    private async Task<CrmBuyerLookupResult> ParseSuccessResponseAsync(
        HttpResponseMessage response, string phoneNumber, CancellationToken cancellationToken)
    {
        CrmBuyerLookupHttpResponse? payload;
        try
        {
            payload = await response.Content.ReadFromJsonAsync<CrmBuyerLookupHttpResponse>(JsonOptions, cancellationToken);
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException)
        {
            // NotSupportedException covers a non-JSON content type (e.g. an
            // HTML error page from the legacy MVC app instead of a JSON body).
            logger.LogWarning(ex, "CRM GetBuyerByPhone returned a malformed response for {MaskedPhoneNumber}.", Mask(phoneNumber));
            return CrmBuyerLookupResult.InvalidResponse("CRM returned a malformed response body.");
        }

        if (payload is null || !payload.Success)
        {
            return CrmBuyerLookupResult.InvalidResponse(payload?.Message);
        }

        if (!payload.Found || payload.Buyers is not { Count: > 0 })
        {
            return CrmBuyerLookupResult.NotFound(payload.Message);
        }

        // A buyer without a customer node is unusable; a missing units list means "no units", not a crash.
        var buyers = payload.Buyers.Where(b => b?.Customer is not null).Select(MapBuyer).ToList();
        if (buyers.Count == 0)
        {
            return CrmBuyerLookupResult.NotFound(payload.Message);
        }
        return CrmBuyerLookupResult.Success(buyers, payload.Message);
    }

    private static CrmBuyerMatchDto MapBuyer(CrmBuyerHttpDto buyer) => new(
        new CrmCustomerDto(
            buyer.Customer.CustomerId,
            buyer.Customer.FullNameEnglish,
            buyer.Customer.FullNameArabic,
            buyer.Customer.MobileNumber,
            buyer.Customer.Email),
        (buyer.Units ?? []).Select(unit => new CrmBuyerUnitDto(
            unit.LeadId,
            unit.LeadStatus,
            unit.LeadStatusName,
            unit.UnitId,
            unit.UnitNumber,
            unit.UnitStatus,
            unit.UnitType,
            unit.FloorNumber,
            unit.ProjectId,
            unit.ProjectName,
            unit.ProjectArabicName,
            unit.CustomerType,
            unit.CustomerTypeName,
            unit.ProjectCode)).ToList());

    /// <summary>Security-Architecture.md §11's masking discipline, applied to a phone number for log lines — enough to correlate, never enough to identify.</summary>
    private static string Mask(string phoneNumber) =>
        phoneNumber.Length <= 4 ? "***" : $"***{phoneNumber[^4..]}";
}
