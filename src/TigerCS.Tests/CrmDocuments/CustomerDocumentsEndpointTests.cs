using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using TigerCS.Application.Modules.CrmDocuments.Abstractions;
using TigerCS.Application.Modules.CrmDocuments.Services;
using TigerCS.Application.Modules.CustomerVerification.CrmIntegration;
using TigerCS.Application.Modules.CustomerVerification.Dto;
using TigerCS.Application.Modules.IdentityAndAccess.Dto;
using TigerCS.Application.Modules.Notifications.Abstractions;
using TigerCS.Application.Modules.Ticketing.Dto;
using TigerCS.Domain.Modules.IdentityAndAccess;
using TigerCS.Domain.Modules.CustomerVerification;
using TigerCS.Domain.Modules.SlaAndEscalation;
using TigerCS.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using TigerCS.Integrations.Modules.CrmIntegration;
using TigerCS.Integrations.Modules.EmailIntegration;
using TigerCS.Tests.CustomerVerification.Fakes;
using TigerCS.Tests.IdentityAndAccess.Integration;

namespace TigerCS.Tests.CrmDocuments;

/// <summary>
/// The Customer Details Documents tab through the real API host: the customer-documents routes, the real CRM gateways
/// (<c>GetBuyerByPhone</c>, <c>GetCustomerDocuments</c>, credentialed file fetch) over an in-process stub of Tiger CRM that
/// records every request, and the recording mail sender, so what the agent is told can be compared with what was
/// actually requested of CRM and actually mailed.
///
/// <para>
/// CRM here has customer 9001 with two units (lead 12345 / 12346). Each unit has its own documents with distinct bytes, so
/// "the right file for the right unit" is a byte comparison, not a status code. Another customer's document (5999) exists
/// in the stub so a reachable-by-guess id would be seen.
/// </para>
/// </summary>
public sealed class CustomerDocumentsEndpointTests : IDisposable
{
    private const string CrmBase = "https://crm.uat.example.test:8014/";
    private const string SecretKey = "uat-like-secret";
    private const string Phone = "+971501234567";
    private const string BuyerEmail = "ahmed.ali@example.test";
    private const string Key = "crm:9001";

    private sealed class StubCrm
    {
        public List<(HttpMethod Method, string Url, string? Secret, string? Body)> Requests { get; } = [];
        public string? Email { get; set; } = BuyerEmail;
        public HttpStatusCode DocumentsStatus { get; set; } = HttpStatusCode.OK;
        public HashSet<string> FailingTypes { get; } = [];
        public bool BuyerLookupFails { get; set; }

        /// <summary>(leadId, CRM type name) → attachments (id, name).</summary>
        public Dictionary<(int Lead, string Type), (string Id, string Name)[]> Documents { get; } = new()
        {
            [(12345, "ReservationForm")] = [("4001", "Reservation Form 1205.pdf")],
            [(12345, "TigerContract")] = [("5001", "SPA 1205.pdf"), ("5002", "Addendum 1205.pdf")],
            [(12346, "ReservationForm")] = [("4002", "Reservation Form 1403.pdf")],
            [(12346, "TigerContract")] = [("5003", "SPA 1403.pdf")],
            [(12346, "RegistrationReceipt")] = [("6001", "Receipt 1403.pdf")]
        };

        public IEnumerable<(HttpMethod Method, string Url, string? Secret, string? Body)> DocumentCalls =>
            Requests.Where(r => r.Url.Contains("GetCustomerDocuments", StringComparison.Ordinal));

        public IEnumerable<(HttpMethod Method, string Url, string? Secret, string? Body)> FileCalls =>
            Requests.Where(r => r.Url.Contains("GetCustomerDocumentFile", StringComparison.Ordinal));

        public static byte[] FileBytes(string id) => Encoding.ASCII.GetBytes($"%PDF-1.7\nrecord-{id}\n%%EOF\n");

        public HttpMessageHandler Handler => new StubHttpMessageHandler(async (request, _) =>
        {
            var body = request.Content is null ? null : await request.Content.ReadAsStringAsync();
            Requests.Add((request.Method, request.RequestUri!.ToString(),
                request.Headers.TryGetValues("X-SECRET-KEY", out var v) ? v.Single() : null, body));
            var path = request.RequestUri!.AbsolutePath;

            if (path.EndsWith("/TicketingSystem/GetBuyerByPhone", StringComparison.Ordinal))
            {
                return BuyerLookupFails
                    ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
                    : Json(HttpStatusCode.OK, Buyer(Email));
            }

            if (path.EndsWith("/TicketingSystem/GetCustomerDocuments", StringComparison.Ordinal))
            {
                using var doc = JsonDocument.Parse(body!);
                var customerId = doc.RootElement.GetProperty("CustomerID").GetInt32();
                var leadId = doc.RootElement.GetProperty("LeadID").GetInt32();
                var type = doc.RootElement.GetProperty("DocumentType").GetString()!;
                if (FailingTypes.Contains(type))
                {
                    return new HttpResponseMessage(HttpStatusCode.InternalServerError);
                }

                if (DocumentsStatus != HttpStatusCode.OK)
                {
                    return new HttpResponseMessage(DocumentsStatus);
                }

                var rows = Documents.GetValueOrDefault((leadId, type)) ?? [];
                return Json(HttpStatusCode.OK, JsonSerializer.Serialize(new
                {
                    customerId, leadId, count = rows.Length, selectionRequired = rows.Length > 1,
                    attachments = rows.Select(r => new { attachmentId = r.Id, fileUrl = $"/Uploads/{r.Id}.pdf", name = r.Name }).ToArray()
                }));
            }

            // The authenticated file route: CRM re-checks the attachment belongs to that customer, lead and type; otherwise 404.
            using var fileRequest = JsonDocument.Parse(body!);
            var fileLead = fileRequest.RootElement.GetProperty("LeadID").GetInt32();
            var fileType = fileRequest.RootElement.GetProperty("DocumentType").GetString()!;
            var id = fileRequest.RootElement.GetProperty("AttachmentId").GetRawText();
            if (!(Documents.GetValueOrDefault((fileLead, fileType)) ?? []).Any(r => r.Id.ToString() == id))
            {
                return new HttpResponseMessage(HttpStatusCode.NotFound);
            }

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(FileBytes(id)) { Headers = { ContentType = new MediaTypeHeaderValue("application/pdf") } }
            };
        });
    }

    private static HttpResponseMessage Json(HttpStatusCode status, string json) =>
        new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private static string Buyer(string? email) => JsonSerializer.Serialize(new
    {
        success = true, found = true, message = "Buyer found successfully.",
        buyers = new[]
        {
            new
            {
                customer = new { customerId = 9001, fullNameEnglish = "Ahmed Ali", fullNameArabic = (string?)null, mobileNumber = Phone, email },
                units = new[]
                {
                    new { leadId = 12345, leadStatus = 8, leadStatusName = "Sold", unitId = 1101, unitNumber = "1205", unitStatus = 3, unitType = 2, floorNumber = 12, projectId = 79, projectName = "Tiger Sky Tower", projectArabicName = (string?)null, customerType = 1, customerTypeName = "Buyer" },
                    new { leadId = 12346, leadStatus = 8, leadStatusName = "Sold", unitId = 1102, unitNumber = "1403", unitStatus = 3, unitType = 2, floorNumber = 14, projectId = 79, projectName = "Tiger Sky Tower", projectArabicName = (string?)null, customerType = 1, customerTypeName = "Buyer" }
                }
            }
        }
    });

    private sealed class FailingEmailSender(bool transient) : IEmailSender
    {
        public int Attempts { get; private set; }

        public Task<EmailSendResult> SendAsync(EmailMessage message, CancellationToken cancellationToken = default)
        {
            Attempts++;
            return Task.FromResult(transient ? EmailSendResult.Transient("smtp 451") : EmailSendResult.Permanent("smtp 550"));
        }
    }

    /// <summary>
    /// The test host's mail sender is the in-memory recorder, which truthfully reports it is not real mail. Tests that exercise
    /// the send path stand in for a configured SMTP provider by wrapping the real email channel and claiming it is real; the
    /// message still lands in the recorder so the attachment can be read back.
    /// </summary>
    private sealed class ClaimsRealMailChannel(IDocumentDeliveryChannelSender inner) : IDocumentDeliveryChannelSender
    {
        public DocumentDeliveryChannel Channel => inner.Channel;
        public bool SendsRealMail => true;
        public Task<DocumentDeliveryResult> SendAsync(DocumentDeliveryMessage message, CancellationToken cancellationToken = default) =>
            inner.SendAsync(message, cancellationToken);
    }

    private readonly StubCrm _crm = new();
    private readonly TigerCsApiFactory _factory;

    public CustomerDocumentsEndpointTests() => _factory = CreateFactory(enabled: true, configure: null);

    private TigerCsApiFactory CreateFactory(bool enabled, Action<IServiceCollection>? configure, bool claimsRealMail = true, string[]? sendRoles = null)
    {
        var factory = BuildFactory(enabled, configure, claimsRealMail);
        // Unset keeps the shipped default (CS Agent, CS Supervisor, CS Manager); a configured list replaces it.
        for (var i = 0; sendRoles is not null && i < sendRoles.Length; i++)
        {
            factory.ExtraConfiguration[$"CrmDocuments:AgentSendRoles:{i}"] = sendRoles[i];
        }

        return factory;
    }

    private TigerCsApiFactory BuildFactory(bool enabled, Action<IServiceCollection>? configure, bool claimsRealMail) => new()
    {
        ExtraConfiguration =
        {
            ["CrmDocuments:Enabled"] = enabled ? "true" : "false",
            ["CrmDocuments:OtpCodePepper"] = "test-only-pepper",
            ["Crm:BaseUrl"] = CrmBase,
            ["Crm:SecretKey"] = SecretKey
        },
        ExtraServices = services =>
        {
            if (claimsRealMail)
            {
                services.RemoveAll<IDocumentDeliveryChannelSender>();
                services.AddScoped<IDocumentDeliveryChannelSender>(sp =>
                    new ClaimsRealMailChannel(ActivatorUtilities.CreateInstance<EmailDocumentDeliveryChannelSender>(sp)));
            }

            services.RemoveAll<ICrmBuyerLookupGateway>();
            services.AddHttpClient<ICrmBuyerLookupGateway, CrmBuyerHttpGateway>(c => c.BaseAddress = new Uri(CrmBase))
                .ConfigurePrimaryHttpMessageHandler(() => _crm.Handler);
            services.AddHttpClient<CrmDocumentHttpGateway>(c => c.BaseAddress = new Uri(CrmBase))
                .ConfigurePrimaryHttpMessageHandler(() => _crm.Handler);
            services.RemoveAll<TigerCS.Application.Modules.CrmDocuments.Abstractions.ICrmDocumentGateway>();
            services.AddScoped<TigerCS.Application.Modules.CrmDocuments.Abstractions.ICrmDocumentGateway>(
                sp => sp.GetRequiredService<CrmDocumentHttpGateway>());
            configure?.Invoke(services);
        }
    };

    public void Dispose() => _factory.Dispose();

    private RecordingEmailSender Mailbox => _factory.Services.GetRequiredService<RecordingEmailSender>();

    private async Task<HttpClient> ClientAsync(string role = Roles.SystemAdministrator, TigerCsApiFactory? factory = null)
    {
        factory ??= _factory;
        var (username, password, _) = await factory.SeedEmployeeAsync(role);
        var client = factory.CreateClient();
        var login = await client.PostAsJsonAsync("/api/auth/login", new LoginRequestDto(username, password));
        login.EnsureSuccessStatusCode();
        var body = await login.Content.ReadFromJsonAsync<LoginResponseDto>();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", body!.AccessToken);
        return client;
    }

    /// <summary>A ticket for the CRM customer 9001 (phone <see cref="Phone"/>) — the Customers directory then has <c>crm:9001</c>.</summary>
    /// <param name="assignedTo">A CS Agent's client to assign the ticket to - a CS Agent sees a customer only through a ticket currently assigned to them (or one a department employee holds for them); handling the call grants nothing.</param>
    private async Task<int> CreateCrmCustomerTicketAsync(HttpClient client, TigerCsApiFactory? factory = null, HttpClient? assignedTo = null)
    {
        factory ??= _factory;
        await factory.SeedPrioritiesAsync();
        var departmentId = await factory.CreateDepartmentAsync("Docs " + Guid.NewGuid(), Guid.NewGuid().ToString("N")[..8]);
        var categoryId = await factory.CreateCategoryAsync("Documents", departmentId);

        var intake = await (await client.PostAsJsonAsync(
            "/api/intake-records", new CreateIntakeRecordRequestDto("Phone", Phone, departmentId, false, null, null)))
            .Content.ReadFromJsonAsync<IntakeRecordResponseDto>();
        var buyers = await (await client.GetAsync($"/api/crm/buyers?phoneNumber={Uri.EscapeDataString(Phone)}"))
            .Content.ReadFromJsonAsync<List<CrmBuyerMatchDto>>();
        var buyer = Assert.Single(buyers!);
        var unit = buyer.Units[0];
        var created = await client.PostAsJsonAsync(
            "/api/tickets",
            new CreateTicketRequestDto(
                intake!.IntakeRecordId, null, null, categoryId, (byte)PriorityLevel.High, "Need my documents",
                CrmBuyerCustomerId: buyer.Customer.CustomerId, CrmBuyerLeadId: unit.LeadId, CrmBuyerUnitId: unit.UnitId,
                CrmBuyerProjectId: unit.ProjectId, CrmBuyerCustomerName: buyer.Customer.FullNameEnglish,
                CrmBuyerProjectName: unit.ProjectName, CrmBuyerUnitNumber: unit.UnitNumber));
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        if (assignedTo is not null)
        {
            var ticket = await created.Content.ReadFromJsonAsync<TicketResponseDto>();
            await factory.AssignOwnerAsync(ticket!.TicketId, TigerCsApiFactory.UserIdOf(assignedTo));
        }

        return departmentId;
    }

    private async Task<HttpClient> ReadyClientAsync()
    {
        var client = await ClientAsync();
        await CreateCrmCustomerTicketAsync(client);
        _crm.Requests.Clear();
        return client;
    }

    private static async Task<JsonElement> JsonAsync(HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.Clone();

    private static string[] Ids(JsonElement group) =>
        group.GetProperty("documents").EnumerateArray().Select(d => d.GetProperty("recordId").GetString()!).ToArray();

    private static JsonElement Group(JsonElement list, string type) =>
        list.GetProperty("groups").EnumerateArray().Single(g => g.GetProperty("documentType").GetString() == type);

    private static HttpRequestMessage Send(string customerKey, object body, string? idempotencyKey)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, $"/api/customers/{customerKey}/documents/send") { Content = JsonContent.Create(body) };
        if (idempotencyKey is not null)
        {
            request.Headers.Add("Idempotency-Key", idempotencyKey);
        }

        return request;
    }

    private static string NewKey() => "agent-" + Guid.NewGuid().ToString("N");

    // =====================================================================
    //  Retrieval: right documents, right unit, CRM contract
    // =====================================================================

    [Fact]
    public async Task SeveralUnits_WithNoneSelected_ListUnitsOnly_AndAskCrmForNoDocuments()
    {
        var client = await ReadyClientAsync();

        var response = await client.GetAsync($"/api/customers/{Key}/documents");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var list = await JsonAsync(response);
        Assert.Equal("UnitSelectionRequired", list.GetProperty("status").GetString());
        Assert.Equal([12345, 12346], list.GetProperty("units").EnumerateArray().Select(u => u.GetProperty("leadId").GetInt32()).ToArray());
        Assert.Empty(list.GetProperty("groups").EnumerateArray());
        Assert.Empty(_crm.DocumentCalls); // nothing was guessed
    }

    [Fact]
    public async Task SelectedUnit_ShowsExactlyThatUnitsDocuments_ThroughCrmTypes4_5_6_ForThatCustomerAndLead()
    {
        var client = await ReadyClientAsync();

        var list = await JsonAsync(await client.GetAsync($"/api/customers/{Key}/documents?crmLeadId=12345"));

        Assert.Equal("Ready", list.GetProperty("status").GetString());
        Assert.Equal(12345, list.GetProperty("selectedLeadId").GetInt32());
        Assert.Equal(["ReservationForm", "Contract", "RegistrationReceipt"], list.GetProperty("groups").EnumerateArray().Select(g => g.GetProperty("documentType").GetString()!).ToArray());
        Assert.Equal([4, 5, 6], list.GetProperty("groups").EnumerateArray().Select(g => g.GetProperty("crmAttachmentType").GetInt32()).ToArray());
        Assert.Equal(["4001"], Ids(Group(list, "ReservationForm")));
        Assert.Equal(["5001", "5002"], Ids(Group(list, "Contract")));
        Assert.Empty(Ids(Group(list, "RegistrationReceipt")));
        Assert.Equal("Ok", Group(list, "RegistrationReceipt").GetProperty("status").GetString()); // empty, not an error

        // The request to CRM named this customer, this lead and the CRM vocabulary for 4 / 5 / 6.
        var sent = _crm.DocumentCalls.Select(c => JsonDocument.Parse(c.Body!).RootElement).ToList();
        Assert.Equal(3, sent.Count);
        Assert.All(sent, s => Assert.Equal(9001, s.GetProperty("CustomerID").GetInt32()));
        Assert.All(sent, s => Assert.Equal(12345, s.GetProperty("LeadID").GetInt32()));
        Assert.Equivalent(new[] { "ReservationForm", "TigerContract", "RegistrationReceipt" }, sent.Select(s => s.GetProperty("DocumentType").GetString()!).ToArray());
        Assert.All(_crm.DocumentCalls, c => Assert.Equal(SecretKey, c.Secret));
    }

    [Fact]
    public async Task SwitchingUnits_ReturnsOnlyTheNewUnitsDocuments_NothingFromThePreviousUnit()
    {
        var client = await ReadyClientAsync();

        var first = await JsonAsync(await client.GetAsync($"/api/customers/{Key}/documents?crmLeadId=12345"));
        var second = await JsonAsync(await client.GetAsync($"/api/customers/{Key}/documents?crmLeadId=12346"));

        Assert.Equal(["5001", "5002"], Ids(Group(first, "Contract")));
        Assert.Equal(12346, second.GetProperty("selectedLeadId").GetInt32());
        Assert.Equal(["4002"], Ids(Group(second, "ReservationForm")));
        Assert.Equal(["5003"], Ids(Group(second, "Contract")));
        Assert.Equal(["6001"], Ids(Group(second, "RegistrationReceipt")));
        var secondJson = second.GetProperty("groups").GetRawText(); // the selector legitimately still lists both units
        foreach (var previous in new[] { "4001", "5001", "5002", "1205" })
        {
            Assert.DoesNotContain(previous, secondJson);
        }
    }

    [Fact]
    public async Task ACrmStorageReference_NeverReachesTheBrowser()
    {
        var client = await ReadyClientAsync();

        var json = await (await client.GetAsync($"/api/customers/{Key}/documents?crmLeadId=12345")).Content.ReadAsStringAsync();

        Assert.DoesNotContain("/Uploads/", json);
        Assert.DoesNotContain("fileUrl", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(SecretKey, json);
        Assert.DoesNotContain(BuyerEmail, json); // only the masked form
    }

    [Fact]
    public async Task AUnitThatIsNotTheCustomers_IsRefused_AndCrmIsNeverAskedForDocuments()
    {
        var client = await ReadyClientAsync();

        var response = await client.GetAsync($"/api/customers/{Key}/documents?crmLeadId=22222");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("RECORD_OWNERSHIP_MISMATCH", (await JsonAsync(response)).GetProperty("code").GetString());
        Assert.Empty(_crm.DocumentCalls);
    }

    [Fact]
    public async Task OneDocumentTypeFailingAtCrm_ShowsItsOwnError_AndTheOthersStillList()
    {
        var client = await ReadyClientAsync();
        _crm.FailingTypes.Add("TigerContract");

        var list = await JsonAsync(await client.GetAsync($"/api/customers/{Key}/documents?crmLeadId=12346"));

        Assert.Equal("Ready", list.GetProperty("status").GetString());
        Assert.Equal("Unavailable", Group(list, "Contract").GetProperty("status").GetString());
        Assert.Empty(Ids(Group(list, "Contract")));
        Assert.Equal(["4002"], Ids(Group(list, "ReservationForm")));
        Assert.Equal(["6001"], Ids(Group(list, "RegistrationReceipt")));
    }

    [Fact]
    public async Task CrmBuyerLookupDown_IsA503_NotAnEmptyList()
    {
        var client = await ReadyClientAsync();
        _crm.BuyerLookupFails = true;

        var response = await client.GetAsync($"/api/customers/{Key}/documents?crmLeadId=12345");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Empty(_crm.DocumentCalls);
    }

    [Fact]
    public async Task ACustomerKeyThatIsNotAKnownVisibleCustomer_HasNoDocuments_AndCrmIsNotAsked()
    {
        var client = await ClientAsync();
        await CreateCrmCustomerTicketAsync(client);
        _crm.Requests.Clear();

        var response = await client.GetAsync("/api/customers/phone:%2B971500000777/documents");

        // No such directory customer is visible → the same answer as for a customer the agent may not see.
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Empty(_crm.Requests);
    }

    // =====================================================================
    //  Open / Download: the actual file, through the authorized flow
    // =====================================================================

    [Fact]
    public async Task OpenAndDownload_ReturnTheExactFileOfTheListedRecord_FetchedWithTheServerSideCredential()
    {
        var client = await ReadyClientAsync();

        var open = await client.GetAsync($"/api/customers/{Key}/documents/12345/Contract/5002/file");
        var download = await client.GetAsync($"/api/customers/{Key}/documents/12345/Contract/5002/file?download=true");

        Assert.Equal(HttpStatusCode.OK, open.StatusCode);
        Assert.Equal(StubCrm.FileBytes("5002"), await open.Content.ReadAsByteArrayAsync());
        Assert.Equal("application/pdf", open.Content.Headers.ContentType?.MediaType);
        Assert.Equal("inline", open.Content.Headers.ContentDisposition?.DispositionType);
        Assert.Equal("attachment", download.Content.Headers.ContentDisposition?.DispositionType);
        Assert.Equal("Addendum 1205.pdf", download.Content.Headers.ContentDisposition?.FileNameStar ?? download.Content.Headers.ContentDisposition?.FileName?.Trim('"'));
        Assert.Equal("nosniff", open.Headers.GetValues("X-Content-Type-Options").Single());
        Assert.Contains("no-store", open.Headers.CacheControl?.ToString());
        Assert.Equal(StubCrm.FileBytes("5002"), await download.Content.ReadAsByteArrayAsync());

        var fetched = _crm.FileCalls.First();
        Assert.Equal("https://crm.uat.example.test:8014/TicketingSystem/GetCustomerDocumentFile", fetched.Url);
        Assert.Equal(HttpMethod.Post, fetched.Method);
        Assert.Equal(SecretKey, fetched.Secret);
        Assert.Contains("\"AttachmentId\":5002", fetched.Body);
    }

    [Fact]
    public async Task AnArabicFileName_SurvivesIntoTheDownloadHeader()
    {
        var client = await ReadyClientAsync();
        _crm.Documents[(12345, "TigerContract")] = [("5001", "عقد البيع 1205.pdf")];

        var download = await client.GetAsync($"/api/customers/{Key}/documents/12345/Contract/5001/file?download=true");

        Assert.Equal(HttpStatusCode.OK, download.StatusCode);
        Assert.Equal("عقد البيع 1205.pdf", download.Content.Headers.ContentDisposition?.FileNameStar);
        Assert.Equal(StubCrm.FileBytes("5001"), await download.Content.ReadAsByteArrayAsync());
    }

    [Fact]
    public async Task ARecordOfTheOtherUnit_CannotBeOpenedThroughThisUnit()
    {
        var client = await ReadyClientAsync();

        // 5003 is lead 12346's contract; asking for it under lead 12345 must not fetch it.
        var response = await client.GetAsync($"/api/customers/{Key}/documents/12345/Contract/5003/file");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Empty(_crm.FileCalls);
    }

    [Theory]
    [InlineData("5999")] // another customer's document id
    [InlineData("nope")]
    public async Task AGuessedRecordId_IsRefusedBeforeAnythingIsFetched(string recordId)
    {
        var client = await ReadyClientAsync();

        var response = await client.GetAsync($"/api/customers/{Key}/documents/12345/Contract/{recordId}/file");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Empty(_crm.FileCalls);
    }

    [Fact]
    public async Task OpeningAUnitThatIsNotTheCustomers_IsRefused_AndNothingIsFetched()
    {
        var client = await ReadyClientAsync();

        var response = await client.GetAsync($"/api/customers/{Key}/documents/22222/Contract/5999/file");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Empty(_crm.FileCalls);
        Assert.Empty(_crm.DocumentCalls);
    }

    [Theory]
    [InlineData("UnitLayout")]
    [InlineData("Banana")]
    public async Task OnlyTheThreeTabTypesCanBeRequested(string type)
    {
        var client = await ReadyClientAsync();

        var response = await client.GetAsync($"/api/customers/{Key}/documents/12345/{type}/5001/file");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(_crm.Requests);
    }

    [Fact]
    public async Task ACustomerTheAgentMayNotSee_YieldsNothing_NoListNoFileNoSend_AndCrmIsNeverCalled()
    {
        // The ticket lives in a department the Department Employee does not belong to.
        var admin = await ClientAsync();
        await CreateCrmCustomerTicketAsync(admin);
        var outsider = await ClientAsync(Roles.DepartmentEmployee);
        _crm.Requests.Clear();

        var list = await outsider.GetAsync($"/api/customers/{Key}/documents?crmLeadId=12345");
        var file = await outsider.GetAsync($"/api/customers/{Key}/documents/12345/Contract/5001/file");
        var send = await outsider.SendAsync(Send(Key, new { crmLeadId = 12345, documentType = "Contract", recordId = "5001" }, NewKey()));

        Assert.Equal(HttpStatusCode.NotFound, list.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, file.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, send.StatusCode);
        Assert.Empty(_crm.Requests);
        Assert.Empty(Mailbox.Recorded);
    }

    [Fact]
    public async Task SystemAdministrator_IsAuthorizedOnEveryDocumentsRoute_ThroughTheOverride()
    {
        var admin = await ReadyClientAsync();

        Assert.Equal(HttpStatusCode.OK, (await admin.GetAsync($"/api/customers/{Key}/documents?crmLeadId=12345")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await admin.GetAsync($"/api/customers/{Key}/documents/12345/Contract/5001/file")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await admin.SendAsync(Send(Key, new { crmLeadId = 12345, documentType = "Contract", recordId = "5001" }, NewKey()))).StatusCode);
    }

    [Fact]
    public async Task Unauthenticated_IsRefused()
    {
        var anonymous = _factory.CreateClient();

        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync($"/api/customers/{Key}/documents")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync($"/api/customers/{Key}/documents/12345/Contract/5001/file")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.SendAsync(Send(Key, new { }, NewKey()))).StatusCode);
    }

    // =====================================================================
    //  Send to customer: the existing delivery service, reported as it is
    // =====================================================================

    [Fact]
    public async Task Send_MailsTheActualFileAsAnAttachmentToCrmsAddress_AndReportsAcceptedNotDelivered()
    {
        var client = await ReadyClientAsync();

        var response = await client.SendAsync(Send(Key, new { crmLeadId = 12345, documentType = "Contract", recordId = "5002" }, NewKey()));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var result = await JsonAsync(response);
        Assert.Equal("Sent", result.GetProperty("status").GetString());
        Assert.False(result.GetProperty("duplicate").GetBoolean());
        Assert.Equal("5002", result.GetProperty("recordId").GetString());
        Assert.Equal("a***@e***.test", result.GetProperty("maskedDestination").GetString());
        // The contract of the response: no address, no document, and no claim of delivery.
        var raw = result.GetRawText();
        Assert.DoesNotContain(BuyerEmail, raw);
        Assert.DoesNotContain("delivered", raw, StringComparison.OrdinalIgnoreCase);

        var mail = Assert.Single(Mailbox.Recorded);
        Assert.Equal(BuyerEmail, mail.ToAddress);
        Assert.Equal(["Addendum 1205.pdf"], mail.AttachmentFileNames);
        var fetched = Assert.Single(_crm.FileCalls);
        Assert.EndsWith("/TicketingSystem/GetCustomerDocumentFile", fetched.Url);
        Assert.Equal(SecretKey, fetched.Secret);
    }

    [Fact]
    public async Task Send_ARetryOfTheSameClickWithTheSameKey_AndAnotherClickWithinTheWindow_SendNothingMore()
    {
        var client = await ReadyClientAsync();
        var key = NewKey();
        var body = new { crmLeadId = 12345, documentType = "Contract", recordId = "5001" };

        var first = await JsonAsync(await client.SendAsync(Send(Key, body, key)));
        var replay = await JsonAsync(await client.SendAsync(Send(Key, body, key)));
        var again = await JsonAsync(await client.SendAsync(Send(Key, body, NewKey())));

        Assert.False(first.GetProperty("duplicate").GetBoolean());
        Assert.True(replay.GetProperty("duplicate").GetBoolean());
        Assert.True(again.GetProperty("duplicate").GetBoolean());
        Assert.Single(Mailbox.Recorded);
    }

    [Fact]
    public async Task Send_RequiresAnIdempotencyKey()
    {
        var client = await ReadyClientAsync();

        var response = await client.SendAsync(Send(Key, new { crmLeadId = 12345, documentType = "Contract", recordId = "5001" }, null));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(Mailbox.Recorded);
    }

    [Theory]
    [InlineData(22222, "Contract", "5999")]   // a unit that is not the customer's
    [InlineData(12345, "Contract", "5003")]   // the other unit's document
    [InlineData(12345, "Contract", "5999")]   // another customer's document id
    public async Task Send_OfARecordCrmDoesNotListForThisCustomerAndUnit_IsRefused_NothingFetchedNothingMailed(int lead, string type, string record)
    {
        var client = await ReadyClientAsync();

        var response = await client.SendAsync(Send(Key, new { crmLeadId = lead, documentType = type, recordId = record }, NewKey()));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("RECORD_OWNERSHIP_MISMATCH", (await JsonAsync(response)).GetProperty("code").GetString());
        Assert.Empty(_crm.FileCalls);
        Assert.Empty(Mailbox.Recorded);
    }

    [Fact]
    public async Task Send_ForARoleWithoutTheSendGrant_IsRefused_AndNothingIsMailed()
    {
        var admin = await ClientAsync();
        await CreateCrmCustomerTicketAsync(admin);
        var reader = await ClientAsync(Roles.ReportingUser);
        _crm.Requests.Clear();

        var response = await reader.SendAsync(Send(Key, new { crmLeadId = 12345, documentType = "Contract", recordId = "5001" }, NewKey()));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Empty(Mailbox.Recorded);
        Assert.Empty(_crm.FileCalls);
    }

    [Fact]
    public async Task Send_WhenCrmHoldsNoEmail_Is422_AndTheListSaysWhySendingIsUnavailable()
    {
        var client = await ReadyClientAsync();
        _crm.Email = null;

        var list = await JsonAsync(await client.GetAsync($"/api/customers/{Key}/documents?crmLeadId=12345"));
        var response = await client.SendAsync(Send(Key, new { crmLeadId = 12345, documentType = "Contract", recordId = "5001" }, NewKey()));

        var send = list.GetProperty("send");
        Assert.False(send.GetProperty("available").GetBoolean());
        Assert.Contains("no valid email", send.GetProperty("unavailableReason").GetString());
        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("DELIVERY_DESTINATION_UNAVAILABLE", (await JsonAsync(response)).GetProperty("code").GetString());
        Assert.Empty(Mailbox.Recorded);
    }

    [Fact]
    public async Task TheListSaysSendingIsAvailable_WithTheMaskedDestination_WhenEverythingNeededIsInPlace()
    {
        var client = await ReadyClientAsync();

        var send = (await JsonAsync(await client.GetAsync($"/api/customers/{Key}/documents?crmLeadId=12345"))).GetProperty("send");

        Assert.True(send.GetProperty("available").GetBoolean());
        Assert.Equal("a***@e***.test", send.GetProperty("maskedDestination").GetString());
    }

    [Fact]
    public async Task Send_WhenTheMailServerRefuses_IsReportedAsAFailure_NeverAsSent()
    {
        var sender = new FailingEmailSender(transient: true);
        using var factory = CreateFactory(enabled: true, services =>
        {
            services.RemoveAll<IEmailSender>();
            services.AddSingleton<IEmailSender>(sender);
        });
        var client = await ClientAsync(factory: factory);
        await CreateCrmCustomerTicketAsync(client, factory);
        var key = NewKey();
        var body = new { crmLeadId = 12345, documentType = "Contract", recordId = "5001" };

        var failed = await client.SendAsync(Send(Key, body, key));

        Assert.Equal(HttpStatusCode.BadGateway, failed.StatusCode);
        var problem = await JsonAsync(failed);
        Assert.Equal("DELIVERY_FAILED", problem.GetProperty("code").GetString());
        Assert.True(problem.GetProperty("retryable").GetBoolean());
        Assert.Equal(1, sender.Attempts);
    }

    [Fact]
    public async Task WhenDocumentCopiesAreSwitchedOff_TheTabStillListsAndOpensFiles_ButSendingIsRefusedWithTheReason()
    {
        using var factory = CreateFactory(enabled: false, configure: null);
        var client = await ClientAsync(factory: factory);
        await CreateCrmCustomerTicketAsync(client, factory);

        var list = await JsonAsync(await client.GetAsync($"/api/customers/{Key}/documents?crmLeadId=12345"));
        var file = await client.GetAsync($"/api/customers/{Key}/documents/12345/Contract/5001/file");
        var send = await client.SendAsync(Send(Key, new { crmLeadId = 12345, documentType = "Contract", recordId = "5001" }, NewKey()));

        Assert.Equal("Ready", list.GetProperty("status").GetString());
        Assert.False(list.GetProperty("send").GetProperty("available").GetBoolean());
        Assert.Contains("CrmDocuments:Enabled", list.GetProperty("send").GetProperty("unavailableReason").GetString());
        Assert.Equal(HttpStatusCode.OK, file.StatusCode);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, send.StatusCode);
        Assert.Equal("DOCUMENT_COPY_DISABLED", (await JsonAsync(send)).GetProperty("code").GetString());
        Assert.Empty(factory.Services.GetRequiredService<RecordingEmailSender>().Recorded);
    }

    // =====================================================================
    //  Explicit send permission
    // =====================================================================

    [Fact]
    public async Task ARoleThatMaySeeTheCustomer_ButHasNoSendGrant_CanListAndOpen_ButNotSend()
    {
        // A CS Agent sees a customer whose ticket is assigned to them; with the grant narrowed to CS Manager it can read but not send.
        using var factory = CreateFactory(enabled: true, configure: null, sendRoles: [Roles.CsManager]);
        var admin = await ClientAsync(factory: factory);
        var agent = await ClientAsync(Roles.CsAgent, factory);
        await CreateCrmCustomerTicketAsync(admin, factory, assignedTo: agent);
        _crm.Requests.Clear();

        var list = await JsonAsync(await agent.GetAsync($"/api/customers/{Key}/documents?crmLeadId=12345"));
        var open = await agent.GetAsync($"/api/customers/{Key}/documents/12345/Contract/5001/file");
        _crm.Requests.Clear();
        var send = await agent.SendAsync(Send(Key, new { crmLeadId = 12345, documentType = "Contract", recordId = "5001" }, NewKey()));

        Assert.Equal("Ready", list.GetProperty("status").GetString());
        Assert.False(list.GetProperty("send").GetProperty("available").GetBoolean());
        Assert.Contains("has not been granted permission", list.GetProperty("send").GetProperty("unavailableReason").GetString());
        Assert.Equal(HttpStatusCode.OK, open.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, send.StatusCode);
        Assert.Equal("DOCUMENT_SEND_NOT_PERMITTED", (await JsonAsync(send)).GetProperty("code").GetString());
        Assert.Empty(_crm.Requests); // refused before the customer was even resolved
        Assert.Empty(factory.Services.GetRequiredService<RecordingEmailSender>().Recorded);
        Assert.Empty(await DeliveryRowsAsync(factory));

        // ...and the role that holds the grant can.
        var manager = await ClientAsync(Roles.CsManager, factory);
        var ok = await manager.SendAsync(Send(Key, new { crmLeadId = 12345, documentType = "Contract", recordId = "5001" }, NewKey()));
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
    }

    [Theory]
    [InlineData(Roles.CsAgent)]
    [InlineData(Roles.CsSupervisor)]
    [InlineData(Roles.CsManager)]
    public async Task TheShippedDefaultGrant_CoversCsAgentSupervisorAndManager(string role)
    {
        var admin = await ClientAsync();
        var client = await ClientAsync(role);
        await CreateCrmCustomerTicketAsync(admin, assignedTo: client);

        var response = await client.SendAsync(Send(Key, new { crmLeadId = 12345, documentType = "Contract", recordId = "5001" }, NewKey()));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task ADepartmentEmployeeWhoCanSeeTheCustomer_IsNotGrantedSending()
    {
        var admin = await ClientAsync();
        var (username, password, employeeId) = await _factory.SeedEmployeeAsync(Roles.DepartmentEmployee);
        var client = _factory.CreateClient();
        var login = await (await client.PostAsJsonAsync("/api/auth/login", new LoginRequestDto(username, password))).Content.ReadFromJsonAsync<LoginResponseDto>();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", login!.AccessToken);
        // The customer is visible through a ticket ASSIGNED to the employee (an employee's visibility is assignment-only), not via the send grant.
        var departmentId = await CreateCrmCustomerTicketAsync(admin, assignedTo: client);
        await _factory.AssignDepartmentAsync(employeeId, departmentId);

        var list = await JsonAsync(await client.GetAsync($"/api/customers/{Key}/documents?crmLeadId=12345"));
        var send = await client.SendAsync(Send(Key, new { crmLeadId = 12345, documentType = "Contract", recordId = "5001" }, NewKey()));

        Assert.Equal("Ready", list.GetProperty("status").GetString()); // can see it...
        Assert.False(list.GetProperty("send").GetProperty("available").GetBoolean());
        Assert.Equal(HttpStatusCode.Forbidden, send.StatusCode); // ...cannot send it
        Assert.Equal("DOCUMENT_SEND_NOT_PERMITTED", (await JsonAsync(send)).GetProperty("code").GetString());
    }

    [Fact]
    public async Task TheDestinationIsAlwaysCrmsAddress_EvenIfTheRequestTriesToSupplyOne()
    {
        var client = await ReadyClientAsync();

        var response = await client.SendAsync(Send(Key, new { crmLeadId = 12345, documentType = "Contract", recordId = "5001", email = "attacker@evil.test", to = "attacker@evil.test", destination = "attacker@evil.test" }, NewKey()));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var mail = Assert.Single(Mailbox.Recorded);
        Assert.Equal(BuyerEmail, mail.ToAddress);
        Assert.DoesNotContain("evil.test", mail.ToAddress);
    }

    // =====================================================================
    //  Persistence: agent-origin rows are explicit; Genesys rows keep real sessions
    // =====================================================================

    private static async Task<List<CrmDocumentDeliveryRequest>> DeliveryRowsAsync(TigerCsApiFactory factory)
    {
        using var scope = factory.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<TigerCsDbContext>().CrmDocumentDeliveryRequests.AsNoTracking().ToListAsync();
    }

    [Fact]
    public async Task AnAgentSend_IsStoredWithAnExplicitOrigin_ItsCrmScope_AndNoVerificationSession()
    {
        var client = await ReadyClientAsync();

        await client.SendAsync(Send(Key, new { crmLeadId = 12345, documentType = "Contract", recordId = "5002" }, NewKey()));

        var row = Assert.Single(await DeliveryRowsAsync(_factory));
        Assert.Equal(DocumentDeliveryOrigin.AgentDesk, row.Origin);
        Assert.Null(row.VerificationSessionId); // nothing invented in the session column
        Assert.Equal(9001, row.CrmCustomerId);
        Assert.Equal(12345, row.CrmLeadId);
        Assert.Equal(CrmDocumentType.Contract, row.DocumentType);
        Assert.Equal("5002", row.CrmRecordId);
        Assert.Equal(DocumentDeliveryStatus.Sent, row.Status);
        Assert.NotNull(row.SentAtUtc);
        Assert.Equal("a***@e***.test", row.MaskedDestination);
        Assert.DoesNotContain(BuyerEmail, row.MaskedDestination); // never the full address
    }

    [Fact]
    public async Task TwoAgentsSendingTheSameDocumentUnderDifferentKeys_EmailItOnce_AndTheSecondIsToldSo()
    {
        var first = await ReadyClientAsync();
        var second = await ClientAsync();
        var body = new { crmLeadId = 12345, documentType = "Contract", recordId = "5001" };

        var a = await JsonAsync(await first.SendAsync(Send(Key, body, NewKey())));
        var b = await JsonAsync(await second.SendAsync(Send(Key, body, NewKey())));

        Assert.False(a.GetProperty("duplicate").GetBoolean());
        Assert.True(b.GetProperty("duplicate").GetBoolean());
        Assert.Single(Mailbox.Recorded);
        Assert.Single(await DeliveryRowsAsync(_factory)); // the suppressed one wrote no second row
    }

    [Fact]
    public async Task TheSameDocumentOfAnotherUnit_IsNotSuppressedByAnEarlierUnitsSend()
    {
        var client = await ReadyClientAsync();
        _crm.Documents[(12346, "TigerContract")] = [("5001", "Same id, other unit.pdf")]; // a record id reused under another lead

        await client.SendAsync(Send(Key, new { crmLeadId = 12345, documentType = "Contract", recordId = "5001" }, NewKey()));
        var other = await JsonAsync(await client.SendAsync(Send(Key, new { crmLeadId = 12346, documentType = "Contract", recordId = "5001" }, NewKey())));

        Assert.False(other.GetProperty("duplicate").GetBoolean());
        Assert.Equal(2, Mailbox.Recorded.Count);
        var leads = (await DeliveryRowsAsync(_factory)).Select(r => r.CrmLeadId!.Value).Order().ToList();
        Assert.Equal(new[] { 12345, 12346 }, leads);
    }

    [Fact]
    public async Task AKeyReusedForADifferentUnit_IsAnIdempotencyConflict()
    {
        var client = await ReadyClientAsync();
        var key = NewKey();
        await client.SendAsync(Send(Key, new { crmLeadId = 12345, documentType = "Contract", recordId = "5001" }, key));

        var reused = await client.SendAsync(Send(Key, new { crmLeadId = 12346, documentType = "Contract", recordId = "5003" }, key));

        Assert.Equal(HttpStatusCode.Conflict, reused.StatusCode);
        Assert.Single(Mailbox.Recorded);
    }

    // =====================================================================
    //  The Recording mail provider is never presented as real delivery
    // =====================================================================

    [Fact]
    public async Task WithTheRecordingMailProvider_SendingIsUnavailable_Refused_AndNothingIsFetchedClaimedOrRecorded()
    {
        using var factory = CreateFactory(enabled: true, configure: null, claimsRealMail: false);
        var client = await ClientAsync(factory: factory);
        await CreateCrmCustomerTicketAsync(client, factory);

        var list = await JsonAsync(await client.GetAsync($"/api/customers/{Key}/documents?crmLeadId=12345"));
        _crm.Requests.Clear();
        var send = await client.SendAsync(Send(Key, new { crmLeadId = 12345, documentType = "Contract", recordId = "5001" }, NewKey()));

        var sendInfo = list.GetProperty("send");
        Assert.False(sendInfo.GetProperty("available").GetBoolean());
        Assert.Contains("only records messages in memory", sendInfo.GetProperty("unavailableReason").GetString());
        Assert.Equal(HttpStatusCode.ServiceUnavailable, send.StatusCode);
        var problem = await JsonAsync(send);
        Assert.Equal("EMAIL_TRANSPORT_NOT_REAL", problem.GetProperty("code").GetString());
        Assert.DoesNotContain("accepted", problem.GetRawText(), StringComparison.OrdinalIgnoreCase);
        Assert.Empty(_crm.FileCalls);
        Assert.Empty(factory.Services.GetRequiredService<RecordingEmailSender>().Recorded);
        Assert.Empty(await DeliveryRowsAsync(factory));
        // Viewing is unaffected.
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync($"/api/customers/{Key}/documents/12345/Contract/5001/file")).StatusCode);
    }
}
