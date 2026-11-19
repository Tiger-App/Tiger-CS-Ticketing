using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using TigerCS.Application.Modules.IdentityAndAccess.Dto;
using TigerCS.Application.Modules.Ticketing.Dto;
using TigerCS.Domain.Modules.IdentityAndAccess;
using TigerCS.Domain.Modules.SlaAndEscalation;
using TigerCS.Domain.Modules.Ticketing;
using TigerCS.Infrastructure.Persistence;
using TigerCS.Tests.IdentityAndAccess.Integration;

namespace TigerCS.Tests.Customers;

/// <summary>
/// The production defect: the Customer Profile showed "Mohamed Ismaik" while Ticket Details said "Unnamed caller" for the same phone-based
/// customer. Both pages must give a customer the same name, e-mail and phone whichever of the customer's numbers, tickets or conversations
/// the name was captured on - and a ticket's own historical snapshot must never be rewritten to get there.
/// </summary>
public class CustomerNameConsistencyEndpointTests
{
    private const string IntakePhone = "+971509998877";
    private const string OtherNumber = "+971521112233";
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    private static async Task<HttpClient> SignInAsync(TigerCsApiFactory factory, string role)
    {
        var (username, password, _) = await factory.SeedEmployeeAsync(role);
        var client = factory.CreateClient();
        var login = await (await client.PostAsJsonAsync("/api/auth/login", new LoginRequestDto(username, password))).Content.ReadFromJsonAsync<LoginResponseDto>();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", login!.AccessToken);
        return client;
    }

    /// <summary>A phone-only customer with <paramref name="tickets"/> unverified tickets (oldest first); returns their ids.</summary>
    private static async Task<long[]> SeedPhoneCustomerAsync(TigerCsApiFactory factory, int tickets, Action<TigerCsDbContext, long[]>? decorate = null)
    {
        await factory.SeedPrioritiesAsync();
        var departmentId = await factory.CreateDepartmentAsync("Phone " + Guid.NewGuid(), Guid.NewGuid().ToString("N")[..8]);
        var categoryId = await factory.CreateCategoryAsync("General", departmentId);
        var (_, _, agentId) = await factory.SeedEmployeeAsync(Roles.CsAgent);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TigerCsDbContext>();
        var ids = new List<long>();
        for (var i = 0; i < tickets; i++)
        {
            var created = DateTime.UtcNow.AddHours(-(tickets - i));
            var ticket = Ticket.CreateUnverified($"TCK-{Guid.NewGuid():N}"[..24], departmentId, categoryId, (byte)PriorityLevel.Low, $"call {i}", created);
            db.Tickets.Add(ticket);
            await db.SaveChangesAsync();
            var intake = new IntakeRecord(1, IntakePhone, departmentId, false, null, null, agentId, created.AddMinutes(-5));
            intake.LinkToTicket(ticket.TicketId, CrmVerificationStatus.Unverified, hasSelectedUnit: false);
            db.IntakeRecords.Add(intake);
            await db.SaveChangesAsync();
            ids.Add(ticket.TicketId);
        }

        decorate?.Invoke(db, [.. ids]);
        await db.SaveChangesAsync();
        return [.. ids];
    }

    private static string Key => CustomerIdentity.Phone(IntakePhone).Key;

    private static async Task<CustomerHistoryDto> TicketDetailsCustomerAsync(HttpClient client, long ticketId)
    {
        var response = await client.GetAsync($"/api/tickets/{ticketId}/customer-history");
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.OK, $"{(int)response.StatusCode}: {body}");
        return JsonSerializer.Deserialize<CustomerHistoryDto>(body, Web)!;
    }

    private static async Task<CustomerDirectoryProfileDto> ProfileAsync(HttpClient client)
    {
        var response = await client.GetAsync($"/api/customers/profile/{Uri.EscapeDataString(Key)}");
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<CustomerDirectoryProfileDto>(Web))!;
    }

    private static async Task<CustomerDirectoryRowDto> DirectoryRowAsync(HttpClient client)
    {
        var list = await (await client.GetAsync("/api/customers?pageSize=100")).Content.ReadFromJsonAsync<CustomerDirectoryListResultDto>(Web);
        return Assert.Single(list!.Items, r => r.CustomerKey == Key);
    }

    private static async Task<HttpResponseMessage> SaveContactAsync(HttpClient client, string phone, string name, string? email = null) =>
        await client.PutAsJsonAsync("/api/customers/contact", new SaveCustomerContactRequestDto(phone, name, email, Key));

    /// <summary>Asserts the three surfaces that previously disagreed all say the same thing.</summary>
    private static async Task AssertEveryPageAgreesAsync(HttpClient client, long ticketId, string expectedName)
    {
        var profile = await ProfileAsync(client);
        var row = await DirectoryRowAsync(client);
        var ticketPage = await TicketDetailsCustomerAsync(client, ticketId);

        Assert.Equal(expectedName, profile.DisplayName);                   // Customer Profile
        Assert.Equal(expectedName, row.DisplayName);                       // Customer Directory
        Assert.Equal(expectedName, ticketPage.ManualCustomerName);         // Ticket Details (and Related Tickets): what the page shows first
    }

    [Fact]
    public async Task ANameSavedUnderAnotherNumberTheCustomerCalledFrom_IsShownByTicketDetailsToo_NotUnnamedCaller()
    {
        // The production shape: the customer rings from IntakePhone (the number on the ticket's intake) but the name was saved against a
        // different number they were also seen on. Ticket Details looked up only the intake number, so it said "Unnamed caller".
        using var factory = new TigerCsApiFactory();
        var ids = await SeedPhoneCustomerAsync(factory, 2, (db, tickets) =>
            db.TicketInteractions.Add(TicketInteraction.CreateLocal(tickets[^1], 1, OtherNumber, DateTime.UtcNow, isOriginatingInteraction: true)));
        var manager = await SignInAsync(factory, Roles.SystemAdministrator);
        Assert.Equal(HttpStatusCode.OK, (await SaveContactAsync(manager, OtherNumber, "Mohamed Ismaik", "m.ismaik@example.com")).StatusCode);

        await AssertEveryPageAgreesAsync(manager, ids[^1], "Mohamed Ismaik");
        await AssertEveryPageAgreesAsync(manager, ids[0], "Mohamed Ismaik");   // an older ticket of the same customer says the same
        Assert.Equal("m.ismaik@example.com", (await TicketDetailsCustomerAsync(manager, ids[0])).ManualCustomerEmail);
        Assert.Equal("m.ismaik@example.com", (await ProfileAsync(manager)).Details!.Email);
    }

    [Fact]
    public async Task ANameCapturedOnAnotherTicketsConversation_IsShownOnEveryTicketOfThatCustomer()
    {
        using var factory = new TigerCsApiFactory();
        var ids = await SeedPhoneCustomerAsync(factory, 2, (db, tickets) =>
            db.TicketInteractions.Add(TicketInteraction.CreateFromGenesys(
                tickets[0], 1, IntakePhone, "conv-1", null, null, null, null, null, DateTime.UtcNow, "Inbound", DateTime.UtcNow,
                isOriginatingInteraction: true, customerName: "Layla Hassan")));
        var manager = await SignInAsync(factory, Roles.SystemAdministrator);

        // The newer ticket has no name of its own; the customer's profile does, from the older conversation.
        await AssertEveryPageAgreesAsync(manager, ids[^1], "Layla Hassan");
    }

    [Fact]
    public async Task ANameEnteredOnTheProfile_IsTheNameEverywhere_AndNoTicketSnapshotIsRewritten()
    {
        using var factory = new TigerCsApiFactory();
        var ids = await SeedPhoneCustomerAsync(factory, 1, (db, tickets) =>
            db.TicketInteractions.Add(TicketInteraction.CreateFromGenesys(
                tickets[0], 1, IntakePhone, "conv-2", null, null, null, null, null, DateTime.UtcNow, "Inbound", DateTime.UtcNow,
                isOriginatingInteraction: true, customerName: "Original Caller Name")));
        var manager = await SignInAsync(factory, Roles.SystemAdministrator);
        await AssertEveryPageAgreesAsync(manager, ids[0], "Original Caller Name");

        var edit = await manager.PutAsJsonAsync(
            $"/api/customers/profile/{Uri.EscapeDataString(Key)}", new UpdateCustomerProfileRequestDto(null, null, null, FullName: "Mohamed Ismaik"));
        Assert.Equal(HttpStatusCode.OK, edit.StatusCode);

        await AssertEveryPageAgreesAsync(manager, ids[0], "Mohamed Ismaik");
        // The conversation's own record - the historical snapshot - is exactly as it was captured.
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TigerCsDbContext>();
        Assert.Equal("Original Caller Name", (await db.TicketInteractions.SingleAsync(i => i.TicketId == ids[0])).CustomerName);
    }

    [Fact]
    public async Task AfterThePhoneIsEdited_ANameSavedForTheNewNumberIsStillFoundEverywhere()
    {
        // The latent defect: the profile saved a name under the EDITED number, which neither page then looked up.
        using var factory = new TigerCsApiFactory();
        var ids = await SeedPhoneCustomerAsync(factory, 1);
        var manager = await SignInAsync(factory, Roles.SystemAdministrator);
        Assert.Equal(HttpStatusCode.OK, (await manager.PutAsJsonAsync(
            $"/api/customers/profile/{Uri.EscapeDataString(Key)}", new UpdateCustomerProfileRequestDto("+971555556666", null, null))).StatusCode);

        Assert.Equal(HttpStatusCode.OK, (await SaveContactAsync(manager, "+971555556666", "Khalid Noor")).StatusCode);

        await AssertEveryPageAgreesAsync(manager, ids[0], "Khalid Noor");
        Assert.Equal("+971555556666", (await TicketDetailsCustomerAsync(manager, ids[0])).ResolvedCustomerPhone);   // the page shows the corrected number too
    }

    [Fact]
    public async Task TheTicketQueueAndGlobalSearch_FindATicketByTheCustomersCurrentName()
    {
        using var factory = new TigerCsApiFactory();
        var ids = await SeedPhoneCustomerAsync(factory, 1);
        var manager = await SignInAsync(factory, Roles.SystemAdministrator);

        async Task<int> FindAsync(string term) =>
            (await (await manager.GetAsync($"/api/tickets?search={Uri.EscapeDataString(term)}&page=1&pageSize=25")).Content.ReadFromJsonAsync<TicketListResultDto>(Web))!.Items.Count;
        Assert.Equal(0, await FindAsync("Ismaik"));

        Assert.Equal(HttpStatusCode.OK, (await SaveContactAsync(manager, IntakePhone, "Mohamed Ismaik", "ismaik.mohamed@example.com")).StatusCode);
        Assert.Equal(1, await FindAsync("Ismaik"));                              // by name
        Assert.Equal(1, await FindAsync("ismaik.mohamed@example.com"));          // by e-mail
        Assert.Equal(0, await FindAsync("Somebody Else"));

        // A name entered on the profile is found the same way.
        await manager.PutAsJsonAsync($"/api/customers/profile/{Uri.EscapeDataString(Key)}", new UpdateCustomerProfileRequestDto(null, null, null, FullName: "Zainab Qureshi"));
        Assert.Equal(1, await FindAsync("Qureshi"));
        var directory = await (await manager.GetAsync("/api/customers?search=Qureshi&pageSize=25")).Content.ReadFromJsonAsync<CustomerDirectoryListResultDto>(Web);
        Assert.Single(directory!.Items);
        Assert.Single(ids);
    }

    [Theory]
    [InlineData("0501234567", "+971501234567")]
    [InlineData("041234567", "+97141234567")]
    [InlineData("0097141234567", "+97141234567")]
    public async Task ACustomerIsFoundByAUaeNumberWhicheverWayItIsWritten(string searched, string stored)
    {
        using var factory = new TigerCsApiFactory();
        await factory.SeedPrioritiesAsync();
        var departmentId = await factory.CreateDepartmentAsync("Phone " + Guid.NewGuid(), Guid.NewGuid().ToString("N")[..8]);
        var categoryId = await factory.CreateCategoryAsync("General", departmentId);
        var (_, _, agentId) = await factory.SeedEmployeeAsync(Roles.CsAgent);
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TigerCsDbContext>();
            var ticket = Ticket.CreateUnverified($"TCK-{Guid.NewGuid():N}"[..24], departmentId, categoryId, (byte)PriorityLevel.Low, "call", DateTime.UtcNow);
            db.Tickets.Add(ticket);
            await db.SaveChangesAsync();
            var intake = new IntakeRecord(1, stored, departmentId, false, null, null, agentId, DateTime.UtcNow.AddMinutes(-5));
            intake.LinkToTicket(ticket.TicketId, CrmVerificationStatus.Unverified, hasSelectedUnit: false);
            db.IntakeRecords.Add(intake);
            await db.SaveChangesAsync();
        }

        var manager = await SignInAsync(factory, Roles.SystemAdministrator);
        var customers = await (await manager.GetAsync($"/api/customers?search={Uri.EscapeDataString(searched)}&pageSize=25")).Content.ReadFromJsonAsync<CustomerDirectoryListResultDto>(Web);
        var tickets = await (await manager.GetAsync($"/api/tickets?search={Uri.EscapeDataString(searched)}&page=1&pageSize=25")).Content.ReadFromJsonAsync<TicketListResultDto>(Web);

        Assert.Single(customers!.Items);
        Assert.Single(tickets!.Items);
    }
}
