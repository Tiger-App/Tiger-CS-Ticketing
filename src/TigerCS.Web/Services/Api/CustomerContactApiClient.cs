using Microsoft.Extensions.Logging;
using TigerCS.Application.Modules.Ticketing.Dto;

namespace TigerCS.Web.Services.Api;

/// <summary>
/// Calls TigerCS.Api's <c>api/customers/contact</c> — the name/e-mail an agent entered by hand, held by Tiger Ticketing only.
/// Never a CRM, PACT or Tasleeh call.
/// </summary>
public sealed class CustomerContactApiClient(HttpClient httpClient, ILogger<CustomerContactApiClient> logger) : ApiClientBase(httpClient, logger)
{
    public Task<ApiResult<CustomerContactDto>> FindAsync(string phoneNumber, CancellationToken cancellationToken) =>
        GetAsync<CustomerContactDto>($"api/customers/contact?phoneNumber={Uri.EscapeDataString(phoneNumber)}", cancellationToken);

    public Task<ApiResult<CustomerContactDto>> SaveAsync(SaveCustomerContactRequestDto request, CancellationToken cancellationToken) =>
        PutAsync<SaveCustomerContactRequestDto, CustomerContactDto>("api/customers/contact", request, cancellationToken);
}
