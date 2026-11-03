using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using TigerCS.Application.Modules.IdentityAndAccess.Dto;
using TigerCS.Application.Modules.Ticketing.Dto;
using TigerCS.Domain.Modules.Ticketing;
using TigerCS.Tests.IdentityAndAccess.Integration;

namespace TigerCS.Tests.Ticketing.Integration;

/// <summary>
/// End-to-end over the REAL PACT HTTP integration path: the real Api host
/// with <c>Pact:Provider=Http</c>, so customer lookup runs through the real
/// <c>PactCustomerHttpGateway</c> typed HttpClient making genuine HTTP calls
/// (X-API-KEY header and all) — against a local stub PACT server speaking
/// PACT's real <c>GET v1/contracts/{mobile}</c> wire shape (the flat
/// <c>data</c> array). The API key and base URL come from this fixture's
/// in-memory configuration, never from a committed appsettings file.
///
/// <para>
/// This is the deepest PACT E2E that can run inside the test suite: every
/// layer is real except the PACT server itself (which lives on Tiger's
/// internal network and is unreachable from CI). The sequence exercised is
/// the real agent flow: intake → department-scoped customer lookup →
/// agent selects one PACT unit (never auto-selected — the selection is a
/// deliberate pick from the returned list) → ticket creation carrying that
/// choice through the Manual Project/Unit fields (PACT has no local
/// UnitReference/ContactReference cache, so a PACT selection persists as the
/// agent-entered project/unit snapshot — the same manual path Ticket
/// Details already renders) → ticket detail read-back. Plus the two
/// never-block guarantees: an unknown mobile (empty <c>data</c> array →
/// NotFound) and a PACT server error (→ Failed) both still allow manual
/// ticket creation.
/// </para>
/// </summary>
public sealed class PactHttpLookupEndToEndTests : IAsyncLifetime
{
    private const string KnownMobile = "+971509990002";
    private const string UnknownMobile = "+971500000404";
    private const string ServerErrorMobile = "+971500000500";

    // Generated per run — never a committed value, mirroring how a real
    // deployment supplies PactApi:ApiKey via user-secrets/environment.
    private readonly string _apiKey = $"e2e-{Guid.NewGuid():N}";

    private readonly HttpListener _pactStub = new();
    private readonly List<(string PathAndQuery, string? ApiKey)> _stubRequests = [];
    private TigerCsApiFactory _factory = null!;
    private Task? _stubLoop;

    /// <summary>
    /// PACT's real response shape for <see cref="KnownMobile"/>: one tenant
    /// (7001), three contract rows, same tenantID — two CURRENT contracts
    /// (ending a year and two years from now, computed per run so the
    /// fixture never silently expires) and one contract that ended in 2020.
    /// The lookup retains all three contracts and flags the expired one;
    /// New Ticket filters it when offering selectable units.
    /// </summary>
    private static readonly string KnownMobileContractsJson = $$"""
        {
          "data": [
            {
              "tenantID": 7001, "companyID": 3,
              "projectCode": "104", "projectName": "Tiger Marina Residences",
              "unitID": 700, "unitCode": "104-2304", "unitType": "Residential", "unitNumber": "2304", "unitStatus": "Occupied",
              "contractID": 88001, "contractStartDate": "2025-01-01T00:00:00", "contractEndDate": "{{PactDate(DateTime.UtcNow.AddYears(1))}}",
              "contractNetAmount": 52000.50, "contractDiscountAmount": 0, "contractServicesNetAmount": 1200.00, "contractServicesDiscountAmount": 0,
              "customerMobile": "+971509990002", "customerName": "Fatima Noor", "customerEmail": "fatima@example.test",
              "customerBuyerType": 2, "grossArea": 120.5, "netArea": 98.2
            },
            {
              "tenantID": 7001, "companyID": 3,
              "projectCode": "105", "projectName": "Tiger Bay Towers",
              "unitID": 701, "unitCode": "105-1105", "unitType": "Commercial", "unitNumber": "1105", "unitStatus": "Occupied",
              "contractID": 88002, "contractStartDate": "2024-06-01T00:00:00", "contractEndDate": "{{PactDate(DateTime.UtcNow.AddYears(2))}}",
              "contractNetAmount": 91000.00, "contractDiscountAmount": 500.00, "contractServicesNetAmount": 2100.00, "contractServicesDiscountAmount": 0,
              "customerMobile": "+971509990002", "customerName": "Fatima Noor", "customerEmail": "fatima@example.test",
              "customerBuyerType": 2, "grossArea": 210.0, "netArea": 180.0
            },
            {
              "tenantID": 7001, "companyID": 3,
              "projectCode": "106", "projectName": "Tiger Heights",
              "unitID": 702, "unitCode": "106-0801", "unitType": "Residential", "unitNumber": "0801", "unitStatus": "Vacant",
              "contractID": 88003, "contractStartDate": "2019-01-01 00:00:00", "contractEndDate": "2020-12-31 00:00:00",
              "contractNetAmount": 40000.00, "contractDiscountAmount": 0, "contractServicesNetAmount": 900.00, "contractServicesDiscountAmount": 0,
              "customerMobile": "+971509990002", "customerName": "Fatima Noor", "customerEmail": "fatima@example.test",
              "customerBuyerType": 2, "grossArea": 95.0, "netArea": 80.0
            }
          ]
        }
        """;

    /// <summary>A date in the ISO-8601 form PACT's contracts endpoint emits.</summary>
    private static string PactDate(DateTime value) => value.ToString("yyyy-MM-dd'T'HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture);

    public Task InitializeAsync()
    {
        // Loopback-only stub on an OS-assigned free port — no fixed port to
        // collide with a parallel test run.
        var port = GetFreeTcpPort();
        var baseUrl = $"http://127.0.0.1:{port}/";
        _pactStub.Prefixes.Add(baseUrl);
        _pactStub.Start();
        _stubLoop = Task.Run(ServeAsync);

        _factory = new TigerCsApiFactory
        {
            ExtraConfiguration = new Dictionary<string, string?>
            {
                ["Pact:Provider"] = "Http",
                ["PactApi:BaseUrl"] = baseUrl,
                ["PactApi:ApiKey"] = _apiKey
            }
        };
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        await _factory.DisposeAsync();
        _pactStub.Stop();
        _pactStub.Close();
        if (_stubLoop is not null)
        {
            await _stubLoop;
        }
    }

    [Fact]
    public async Task PactHttpLookup_KnownMobile_FullFlow_LookupSelectUnitCreateTicket_PersistsAndReadsBack()
    {
        var client = await CreateAuthenticatedClientAsync();
        await _factory.SeedPrioritiesAsync();
        var departmentId = await _factory.CreateDepartmentAsync("Leasing " + Guid.NewGuid(), Guid.NewGuid().ToString("N")[..8]);
        var categoryId = await _factory.CreateCategoryAsync("Tenancy Inquiry", departmentId);
        // PACT enabled for the selected department — the data-driven scoping
        // under test, not a hard-coded source list.
        await _factory.SeedDepartmentCustomerLookupSourceAsync(departmentId, CustomerLookupSource.Pact);

        // Intake with the known PACT mobile, scoped to the PACT department.
        var intakeResponse = await client.PostAsJsonAsync(
            "/api/intake-records", new CreateIntakeRecordRequestDto("Phone", KnownMobile, departmentId, false, null, null));
        Assert.Equal(HttpStatusCode.Created, intakeResponse.StatusCode);
        var intake = await intakeResponse.Content.ReadFromJsonAsync<IntakeRecordResponseDto>();
        // The persisted intake keeps the number exactly as entered — the
        // '+'-stripping is PACT-request-side only, never the stored value.
        Assert.Equal(KnownMobile, intake!.PhoneNumber);

        // Customer lookup → the real PactCustomerHttpGateway calls the stub
        // PACT server over real HTTP.
        var lookupResponse = await client.GetAsync($"/api/intake-records/{intake!.IntakeRecordId}/customer-lookup");
        Assert.Equal(HttpStatusCode.OK, lookupResponse.StatusCode);
        var lookup = await lookupResponse.Content.ReadFromJsonAsync<CustomerLookupResultDto>();

        // Only the department's configured source was searched, and it found
        // the customer.
        var pactSource = Assert.Single(lookup!.Sources);
        Assert.Equal("Pact", pactSource.Source);
        Assert.Equal("Found", pactSource.Status);

        // tenantID, name, project, unit numbers, unitID-based external ids,
        // Raw identity and every contract are retained, including the
        // expired contract as historical evidence. No unit is auto-selected.
        var customer = Assert.Single(pactSource.Customers);
        Assert.Equal("7001", customer.ExternalCustomerId);
        Assert.Equal("Fatima Noor", customer.DisplayName);
        Assert.Equal(KnownMobile, customer.PhoneNumber);
        Assert.Equal("fatima@example.test", customer.Email);
        Assert.Equal("2", customer.CustomerType);
        Assert.Equal(3, customer.Units.Count);
        var marinaUnit = Assert.Single(customer.Units, u => u.ExternalUnitId == "700");
        Assert.Equal("2304", marinaUnit.UnitNumber);
        Assert.Equal("Tiger Marina Residences", marinaUnit.PropertyName);
        Assert.Equal("Residential", marinaUnit.UnitType);
        var bayUnit = Assert.Single(customer.Units, u => u.ExternalUnitId == "701");
        Assert.Equal("1105", bayUnit.UnitNumber);
        Assert.Equal("Tiger Bay Towers", bayUnit.PropertyName);
        Assert.True(Assert.Single(customer.Units, u => u.ExternalUnitId == "702").IsContractExpired);
        // No local reference ids for PACT — display enrichment, never
        // linkable by id (so also never auto-linked to the ticket).
        Assert.All(customer.Units, u =>
        {
            Assert.Null(u.UnitReferenceId);
            Assert.Null(u.ContactReferenceId);
        });

        // The stub really was called over HTTP with the configured key and
        // the URL-encoded mobile in the path — and customerBuyerType came on
        // the contracts response, so the customer-type fallback endpoint was
        // never called.
        var stubRequest = Assert.Single(_stubRequests, r => r.PathAndQuery.StartsWith("/v1/contracts/", StringComparison.Ordinal));
        Assert.Equal(_apiKey, stubRequest.ApiKey);
        // PACT received the normalized number: '+' stripped, digits intact.
        Assert.Contains(KnownMobile.Replace("+", ""), stubRequest.PathAndQuery);
        Assert.DoesNotContain("+", stubRequest.PathAndQuery);
        Assert.DoesNotContain("%2B", stubRequest.PathAndQuery, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(_stubRequests, r => r.PathAndQuery.EndsWith("/customer-type", StringComparison.Ordinal));

        // The agent deliberately selects the SECOND unit (proving no
        // first-result bias anywhere) and continues ticket creation. The
        // selection persists two ways: the generic external verification
        // identity (source + PACT's own tenantID/unitID — external
        // identifiers only) and the Manual Project/Unit snapshot the Ticket
        // Details page renders for a unit with no local reference.
        var selectedUnit = bayUnit;
        var ticketResponse = await client.PostAsJsonAsync(
            "/api/tickets",
            new CreateTicketRequestDto(
                intake.IntakeRecordId, UnitReferenceId: null, ContactReferenceId: null, categoryId, PriorityId: 3,
                RequestSummary: $"AC fault reported by PACT tenant {customer.DisplayName} (PACT tenant 7001, unit {selectedUnit.UnitNumber})",
                ManualProjectName: selectedUnit.PropertyName,
                ManualUnitNumber: selectedUnit.UnitNumber,
                CustomerVerificationSource: "Pact",
                ExternalCustomerId: customer.ExternalCustomerId,
                ExternalUnitId: selectedUnit.ExternalUnitId));
        Assert.Equal(HttpStatusCode.Created, ticketResponse.StatusCode);
        var ticket = await ticketResponse.Content.ReadFromJsonAsync<TicketResponseDto>();

        // The selected PACT unit persisted onto the ticket — snapshot AND
        // external verification identity — and the intake is linked.
        Assert.Equal("Tiger Bay Towers", ticket!.ManualProjectName);
        Assert.Equal("1105", ticket.ManualUnitNumber);
        Assert.Equal("Pact", ticket.CustomerVerificationSource);
        Assert.Equal("7001", ticket.ExternalCustomerId);
        Assert.Equal("701", ticket.ExternalUnitId);
        Assert.Equal("Unverified", ticket.VerificationStatus);
        Assert.Null(ticket.UnitReferenceId);
        Assert.Null(ticket.ContactReferenceId);

        // Stored on the ticket row itself, not just echoed in the response.
        var storedTicket = await _factory.GetTicketAsync(ticket.TicketId);
        Assert.NotNull(storedTicket);
        Assert.Equal("Pact", storedTicket!.CustomerVerificationSource);
        Assert.Equal("7001", storedTicket.ExternalCustomerId);
        Assert.Equal("701", storedTicket.ExternalUnitId);

        // Ticket detail (what the Ticket Details page renders) reads the
        // selection back — enough to display "Verified via PACT" with the
        // tenant/unit ids, distinct from a plain manual entry.
        // The agent takes the ticket (assigned to them), the only way a CS Agent can open it.
        await _factory.AssignOwnerAsync(ticket.TicketId, TigerCsApiFactory.UserIdOf(client));
        var detailResponse = await client.GetAsync($"/api/tickets/{ticket.TicketId}");
        Assert.Equal(HttpStatusCode.OK, detailResponse.StatusCode);
        var detail = await detailResponse.Content.ReadFromJsonAsync<TicketDetailDto>();
        Assert.Equal("Tiger Bay Towers", detail!.ManualProjectName);
        Assert.Equal("1105", detail.ManualUnitNumber);
        Assert.Equal("Pact", detail.CustomerVerificationSource);
        Assert.Equal("7001", detail.ExternalCustomerId);
        Assert.Equal("701", detail.ExternalUnitId);
        Assert.Contains("unit 1105", detail.RequestSummary);
    }

    [Fact]
    public async Task PactHttpLookup_UnknownMobile_EmptyDataArray_NotFound_ManualTicketCreationStillWorks()
    {
        var client = await CreateAuthenticatedClientAsync();
        await _factory.SeedPrioritiesAsync();
        var departmentId = await _factory.CreateDepartmentAsync("Leasing " + Guid.NewGuid(), Guid.NewGuid().ToString("N")[..8]);
        var categoryId = await _factory.CreateCategoryAsync("Tenancy Inquiry", departmentId);
        await _factory.SeedDepartmentCustomerLookupSourceAsync(departmentId, CustomerLookupSource.Pact);

        var intakeResponse = await client.PostAsJsonAsync(
            "/api/intake-records", new CreateIntakeRecordRequestDto("Phone", UnknownMobile, departmentId, false, null, null));
        var intake = await intakeResponse.Content.ReadFromJsonAsync<IntakeRecordResponseDto>();

        var lookupResponse = await client.GetAsync($"/api/intake-records/{intake!.IntakeRecordId}/customer-lookup");
        var lookup = await lookupResponse.Content.ReadFromJsonAsync<CustomerLookupResultDto>();
        var pactSource = Assert.Single(lookup!.Sources);
        Assert.Equal("Pact", pactSource.Source);
        Assert.Equal("NotFound", pactSource.Status);

        // No match never blocks New Ticket — manual customer/unit entry.
        var ticketResponse = await client.PostAsJsonAsync(
            "/api/tickets",
            new CreateTicketRequestDto(
                intake.IntakeRecordId, null, null, categoryId, PriorityId: 3,
                RequestSummary: "Caller not on file in PACT — manual entry",
                ManualProjectName: "Tiger Marina Residences", ManualUnitNumber: "0000"));
        Assert.Equal(HttpStatusCode.Created, ticketResponse.StatusCode);
        var ticket = await ticketResponse.Content.ReadFromJsonAsync<TicketResponseDto>();
        Assert.Equal("Unverified", ticket!.VerificationStatus);
        Assert.Equal("0000", ticket.ManualUnitNumber);
        // Manual entry carries no external verification identity.
        Assert.Null(ticket.CustomerVerificationSource);
        Assert.Null(ticket.ExternalCustomerId);
        Assert.Null(ticket.ExternalUnitId);
    }

    [Fact]
    public async Task PactHttpLookup_PactServerError_Failed_TicketCreationStillWorks()
    {
        var client = await CreateAuthenticatedClientAsync();
        await _factory.SeedPrioritiesAsync();
        var departmentId = await _factory.CreateDepartmentAsync("Leasing " + Guid.NewGuid(), Guid.NewGuid().ToString("N")[..8]);
        var categoryId = await _factory.CreateCategoryAsync("Tenancy Inquiry", departmentId);
        await _factory.SeedDepartmentCustomerLookupSourceAsync(departmentId, CustomerLookupSource.Pact);

        var intakeResponse = await client.PostAsJsonAsync(
            "/api/intake-records", new CreateIntakeRecordRequestDto("Phone", ServerErrorMobile, departmentId, false, null, null));
        var intake = await intakeResponse.Content.ReadFromJsonAsync<IntakeRecordResponseDto>();

        var lookupResponse = await client.GetAsync($"/api/intake-records/{intake!.IntakeRecordId}/customer-lookup");
        Assert.Equal(HttpStatusCode.OK, lookupResponse.StatusCode);
        var pactSource = Assert.Single((await lookupResponse.Content.ReadFromJsonAsync<CustomerLookupResultDto>())!.Sources);
        Assert.Equal("Pact", pactSource.Source);
        Assert.Equal("Failed", pactSource.Status);

        // A PACT outage never blocks New Ticket either.
        var ticketResponse = await client.PostAsJsonAsync(
            "/api/tickets",
            new CreateTicketRequestDto(
                intake.IntakeRecordId, null, null, categoryId, PriorityId: 2,
                RequestSummary: "PACT unavailable during lookup — proceeding manually"));
        Assert.Equal(HttpStatusCode.Created, ticketResponse.StatusCode);
    }

    private async Task<HttpClient> CreateAuthenticatedClientAsync()
    {
        var (username, password, _) = await _factory.SeedEmployeeAsync("CS Agent");
        var client = _factory.CreateClient();

        var loginResponse = await client.PostAsJsonAsync("/api/auth/login", new LoginRequestDto(username, password));
        loginResponse.EnsureSuccessStatusCode();
        var login = await loginResponse.Content.ReadFromJsonAsync<LoginResponseDto>();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", login!.AccessToken);

        return client;
    }

    /// <summary>The stub PACT server: validates X-API-KEY and answers <c>GET v1/contracts/{mobile}</c> in PACT's real wire shape.</summary>
    private async Task ServeAsync()
    {
        while (_pactStub.IsListening)
        {
            HttpListenerContext context;
            try
            {
                context = await _pactStub.GetContextAsync();
            }
            catch (Exception ex) when (ex is HttpListenerException or ObjectDisposedException)
            {
                return; // listener stopped — test teardown
            }

            var pathAndQuery = context.Request.Url!.PathAndQuery;
            var presentedKey = context.Request.Headers["X-API-KEY"];
            lock (_stubRequests)
            {
                _stubRequests.Add((pathAndQuery, presentedKey));
            }

            var (statusCode, body) = Respond(pathAndQuery, presentedKey);
            context.Response.StatusCode = statusCode;
            if (body is not null)
            {
                context.Response.ContentType = "application/json";
                var bytes = Encoding.UTF8.GetBytes(body);
                await context.Response.OutputStream.WriteAsync(bytes);
            }

            context.Response.Close();
        }
    }

    private (int StatusCode, string? Body) Respond(string pathAndQuery, string? presentedKey)
    {
        if (presentedKey != _apiKey)
        {
            return (401, null);
        }

        // The real PACT stores numbers WITHOUT the '+' prefix — a number
        // sent as "+9715..." does not match an existing customer. The stub
        // mirrors that: only the normalized digits match, so this whole test
        // fails if the gateway ever sends the '+' again.
        if (pathAndQuery.Contains("%2B", StringComparison.OrdinalIgnoreCase) || pathAndQuery.Contains('+'))
        {
            return (200, """{ "data": [] }""");
        }

        if (pathAndQuery.Contains(ServerErrorMobile.Replace("+", ""), StringComparison.Ordinal))
        {
            return (500, null);
        }

        if (pathAndQuery.Contains(KnownMobile.Replace("+", ""), StringComparison.Ordinal))
        {
            return (200, KnownMobileContractsJson);
        }

        return (200, """{ "data": [] }""");
    }

    private static int GetFreeTcpPort()
    {
        var listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }
}
