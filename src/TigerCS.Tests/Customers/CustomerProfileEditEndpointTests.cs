using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using TigerCS.Application.Modules.IdentityAndAccess.Dto;
using TigerCS.Application.Modules.Ticketing.Abstractions;
using TigerCS.Application.Modules.Ticketing.Dto;
using TigerCS.Domain.Modules.IdentityAndAccess;
using TigerCS.Domain.Modules.IdentityAndAccess.Permissions;
using TigerCS.Domain.Modules.SlaAndEscalation;
using TigerCS.Domain.Modules.Ticketing;
using TigerCS.Infrastructure.Persistence;
using TigerCS.Tests.IdentityAndAccess.Integration;
using P = TigerCS.Domain.Modules.IdentityAndAccess.Permissions.Permissions;

namespace TigerCS.Tests.Customers;

/// <summary>
/// Editing a customer's phone, project and unit from the Customers directory, through the real Api host: who may, what is validated, what is
/// stored (and what is not), what the list / profile / search / Dashboard counts then show, and the audit trail.
/// </summary>
public class CustomerProfileEditEndpointTests
{
    private const int CustomerId = 9101;
    private const string Key = "crm:9101";
    private const string AuditAction = "UpdateCustomerProfile";

    private static async Task<(HttpClient Client, Guid EmployeeId)> SignInAsync(TigerCsApiFactory factory, string role)
    {
        var (username, password, employeeId) = await factory.SeedEmployeeAsync(role);
        var client = factory.CreateClient();
        var login = await (await client.PostAsJsonAsync("/api/auth/login", new LoginRequestDto(username, password)))
            .Content.ReadFromJsonAsync<LoginResponseDto>();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", login!.AccessToken);
        return (client, employeeId);
    }

    /// <summary>A CRM-verified customer with one ticket, written straight to the database: the directory derives the customer from it.</summary>
    private static async Task<long> SeedCustomerAsync(TigerCsApiFactory factory, int crmCustomerId, string? project, string? unit)
    {
        await factory.SeedPrioritiesAsync();
        var departmentId = await factory.CreateDepartmentAsync("Customers " + Guid.NewGuid(), Guid.NewGuid().ToString("N")[..8]);
        var categoryId = await factory.CreateCategoryAsync("General", departmentId);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TigerCsDbContext>();
        var ticket = Ticket.CreateVerifiedFromCrmBuyer(
            $"TCK-{Guid.NewGuid():N}"[..24], departmentId, crmCustomerId, 1, 1, 1, "Test Customer", project, unit, categoryId,
            (byte)PriorityLevel.High, "request", DateTime.UtcNow);
        db.Tickets.Add(ticket);
        await db.SaveChangesAsync();
        return ticket.TicketId;
    }

    private static async Task SeedStandardCustomersAsync(TigerCsApiFactory factory)
    {
        await SeedCustomerAsync(factory, CustomerId, "Tiger Bay Towers", "1105");
        await SeedCustomerAsync(factory, 9102, "Zebra Towers", "ZT-9");   // a second project, so the catalogue can judge a unit against it
    }

    private static Task<HttpResponseMessage> EditAsync(HttpClient client, string? phone, string? project, string? unit, string key = Key) =>
        client.PutAsJsonAsync($"/api/customers/profile/{Uri.EscapeDataString(key)}", new UpdateCustomerProfileRequestDto(phone, project, unit));

    private static async Task<CustomerProfileEditedDto> EditOkAsync(HttpClient client, string? phone, string? project, string? unit)
    {
        var response = await EditAsync(client, phone, project, unit);
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.OK, $"{(int)response.StatusCode}: {body}");
        return JsonSerializer.Deserialize<CustomerProfileEditedDto>(body, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
    }

    private static async Task<CustomerDirectoryListResultDto> ListAsync(HttpClient client, string query = "")
    {
        var response = await client.GetAsync($"/api/customers?pageSize=100{query}");
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<CustomerDirectoryListResultDto>())!;
    }

    private static async Task<CustomerDirectoryRowDto> RowAsync(HttpClient client, string key = Key) =>
        Assert.Single((await ListAsync(client)).Items, r => r.CustomerKey == key);

    private static async Task<List<T>> InDbAsync<T>(TigerCsApiFactory factory, Func<TigerCsDbContext, IQueryable<T>> query)
        where T : class
    {
        using var scope = factory.Services.CreateScope();
        return await query(scope.ServiceProvider.GetRequiredService<TigerCsDbContext>()).AsNoTracking().ToListAsync();
    }

    // ----------------------------------------------------------------------------------------------- permissions

    [Fact]
    public async Task Edit_IsRestrictedToAdministratorsCsManagersAndGrantedUsers_AndCanBeRevoked()
    {
        using var factory = new TigerCsApiFactory();
        await SeedStandardCustomersAsync(factory);

        // Everyone else is refused by default - including the roles that can read the whole directory.
        foreach (var role in new[]
                 {
                     Roles.CsAgent, Roles.CsSupervisor, Roles.DepartmentEmployee, Roles.DepartmentHead,
                     Roles.GeneralManager, Roles.ChairmanCeo, Roles.ReportingUser
                 })
        {
            var (client, _) = await SignInAsync(factory, role);
            Assert.True((await EditAsync(client, "+971501234567", null, null)).StatusCode == HttpStatusCode.Forbidden, role);
        }

        // System Administrator and CS Manager hold it through their roles.
        var (admin, _) = await SignInAsync(factory, Roles.SystemAdministrator);
        Assert.Equal(HttpStatusCode.OK, (await EditAsync(admin, "+971501234567", null, null)).StatusCode);
        var (manager, managerId) = await SignInAsync(factory, Roles.CsManager);
        Assert.Equal(HttpStatusCode.OK, (await EditAsync(manager, "+971501234568", null, null)).StatusCode);

        // An explicit grant opens it for anyone else who is allowed to do work...
        var (granted, grantedId) = await SignInAsync(factory, Roles.GeneralManager);
        Assert.Equal(HttpStatusCode.Forbidden, (await EditAsync(granted, "+971501234569", null, null)).StatusCode);
        await factory.SetPermissionOverrideAsync(grantedId, P.CustomersEdit, PermissionEffect.Grant);
        Assert.Equal(HttpStatusCode.OK, (await EditAsync(granted, "+971501234569", null, null)).StatusCode);

        // ...and revoking it closes it again, on the next request, with the same token.
        await factory.ClearPermissionOverrideAsync(grantedId, P.CustomersEdit);
        Assert.Equal(HttpStatusCode.Forbidden, (await EditAsync(granted, "+971501234560", null, null)).StatusCode);

        // An explicit denial beats the role.
        await factory.SetPermissionOverrideAsync(managerId, P.CustomersEdit, PermissionEffect.Deny);
        Assert.Equal(HttpStatusCode.Forbidden, (await EditAsync(manager, "+971501234561", null, null)).StatusCode);

        // Only the permitted edits were written: three of them, each audited; the refused ones left nothing behind.
        Assert.Equal(3, (await InDbAsync(factory, db => db.AuditEntries.Where(a => a.Action == AuditAction))).Count);
    }

    // ----------------------------------------------------------------------------------------------- a successful edit

    [Fact]
    public async Task Edit_ChangesWhatTheListProfileAndSearchShow_AndNothingElse()
    {
        using var factory = new TigerCsApiFactory();
        await SeedStandardCustomersAsync(factory);
        var (manager, _) = await SignInAsync(factory, Roles.CsManager);

        var listBefore = await ListAsync(manager);
        var rowBefore = Assert.Single(listBefore.Items, r => r.CustomerKey == Key);
        Assert.Equal("Tiger Bay Towers", rowBefore.ProjectName);
        Assert.Equal("1105", rowBefore.UnitNumber);
        Assert.Null(rowBefore.PhoneNumber);
        Assert.False(rowBefore.IsEdited);

        // The project is matched case-insensitively and takes the catalogue's spelling; the phone is normalized.
        var saved = await EditOkAsync(manager, "00971 50 123 4567", "zebra towers", "zt-9");
        Assert.True(saved.Changed);
        Assert.Equal("+971501234567", saved.PhoneNumber);
        Assert.Equal("Zebra Towers", saved.ProjectName);
        Assert.Equal("ZT-9", saved.UnitNumber);
        Assert.Equal(["PhoneNumber", "ProjectName", "UnitNumber"], saved.EditedFields);

        // The list shows the edit, marks it, and still lists every customer exactly once.
        var listAfter = await ListAsync(manager);
        Assert.Equal(listBefore.TotalCount, listAfter.TotalCount);
        var rowAfter = Assert.Single(listAfter.Items, r => r.CustomerKey == Key);
        Assert.Equal("+971501234567", rowAfter.PhoneNumber);
        Assert.Equal("Zebra Towers", rowAfter.ProjectName);
        Assert.Equal("ZT-9", rowAfter.UnitNumber);
        Assert.True(rowAfter.IsEdited);
        Assert.Equal(rowBefore.TotalTickets, rowAfter.TotalTickets);
        Assert.Equal(rowBefore.LastTicketNumber, rowAfter.LastTicketNumber);

        // The edited values are searchable; the old ones still find the customer through the ticket that carries them.
        Assert.Contains((await ListAsync(manager, "&search=%2B971501234567")).Items, r => r.CustomerKey == Key);
        Assert.Contains((await ListAsync(manager, "&search=501234567")).Items, r => r.CustomerKey == Key);
        Assert.Contains((await ListAsync(manager, "&search=Zebra")).Items, r => r.CustomerKey == Key);
        Assert.Contains((await ListAsync(manager, "&search=Tiger%20Bay")).Items, r => r.CustomerKey == Key);

        // The profile leads with the edit, keeps the tickets' own history, and says what the tickets said.
        var profile = (await (await manager.GetAsync($"/api/customers/profile/{Key}")).Content.ReadFromJsonAsync<CustomerDirectoryProfileDto>())!;
        Assert.Equal("+971501234567", profile.PhoneNumbers[0]);
        Assert.Equal("Edited", profile.Units[0].Source);
        Assert.Equal("Zebra Towers", profile.Units[0].ProjectName);
        Assert.Equal("Tiger Bay Towers", Assert.Single(profile.Tickets).ProjectName);
        Assert.Equal("Tiger Bay Towers", profile.Edit!.DerivedProjectName);
        Assert.Equal("Zebra Towers", profile.Edit.OverrideProjectName);

        // Local only: the ticket itself is untouched, and so is every other customer.
        var tickets = await InDbAsync(factory, db => db.Tickets.Where(t => t.CrmBuyerCustomerId == CustomerId));
        var ticket = Assert.Single(tickets);
        Assert.Equal("Tiger Bay Towers", ticket.CrmBuyerProjectName);
        Assert.Equal("1105", ticket.CrmBuyerUnitNumber);
        var other = await RowAsync(manager, "crm:9102");
        Assert.False(other.IsEdited);
        Assert.Equal("Zebra Towers", other.ProjectName);
    }

    [Fact]
    public async Task Edit_BlankValuesClearTheEdit_AndTheTicketsOwnValuesShowAgain()
    {
        using var factory = new TigerCsApiFactory();
        await SeedStandardCustomersAsync(factory);
        var (manager, _) = await SignInAsync(factory, Roles.CsManager);
        await EditOkAsync(manager, "+971501234567", "Zebra Towers", "ZT-9");
        Assert.Single(await InDbAsync(factory, db => db.CustomerProfileOverrides));

        var cleared = await EditOkAsync(manager, null, "", "   ");

        Assert.True(cleared.Changed);
        Assert.Empty(cleared.EditedFields);
        Assert.Equal("Tiger Bay Towers", cleared.ProjectName);
        Assert.Equal("1105", cleared.UnitNumber);
        Assert.Null(cleared.PhoneNumber);
        var row = await RowAsync(manager);
        Assert.False(row.IsEdited);
        Assert.Equal("Tiger Bay Towers", row.ProjectName);
        Assert.Empty(await InDbAsync(factory, db => db.CustomerProfileOverrides));   // the row is deleted, not left empty
    }

    [Fact]
    public async Task Edit_OfOneField_LeavesTheOthersAlone()
    {
        using var factory = new TigerCsApiFactory();
        await SeedStandardCustomersAsync(factory);
        var (manager, _) = await SignInAsync(factory, Roles.CsManager);

        var saved = await EditOkAsync(manager, "+442079460958", null, null);

        Assert.Equal(["PhoneNumber"], saved.EditedFields);
        Assert.Equal("+442079460958", saved.PhoneNumber);
        Assert.Equal("Tiger Bay Towers", saved.ProjectName);   // not edited: what the ticket says
        Assert.Equal("1105", saved.UnitNumber);
    }

    [Fact]
    public async Task Edit_OfAnUnknownOrInvalidCustomer_IsRefusedWithoutWritingAnything()
    {
        using var factory = new TigerCsApiFactory();
        await SeedStandardCustomersAsync(factory);
        var (manager, _) = await SignInAsync(factory, Roles.CsManager);

        Assert.Equal(HttpStatusCode.NotFound, (await EditAsync(manager, "+971501234567", null, null, "crm:987654321")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await EditAsync(manager, "+971501234567", null, null, "nonsense")).StatusCode);

        Assert.Empty(await InDbAsync(factory, db => db.CustomerProfileOverrides));
        Assert.Empty(await InDbAsync(factory, db => db.AuditEntries.Where(a => a.Action == AuditAction)));
    }

    // ----------------------------------------------------------------------------------------------- phone validation

    [Theory]
    [InlineData("0501234567")]
    [InlineData("+971 12345")]
    [InlineData("not a number")]
    [InlineData("+0 1234567890")]
    [InlineData("++971501234567")]
    [InlineData("+1234567")]
    public async Task Edit_RefusesAPhoneThatCannotBeNormalized_AndSavesNothing(string phone)
    {
        using var factory = new TigerCsApiFactory();
        await SeedStandardCustomersAsync(factory);
        var (manager, _) = await SignInAsync(factory, Roles.CsManager);

        var response = await EditAsync(manager, phone, null, null);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("PhoneNumber", await response.Content.ReadAsStringAsync(), StringComparison.OrdinalIgnoreCase);
        Assert.Empty(await InDbAsync(factory, db => db.CustomerProfileOverrides));
        Assert.Empty(await InDbAsync(factory, db => db.AuditEntries.Where(a => a.Action == AuditAction)));
    }

    [Theory]
    [InlineData("+971 50 123 4567", "+971501234567")]
    [InlineData("971501234567", "+971501234567")]
    [InlineData("0044 20 7946 0958", "+442079460958")]
    [InlineData("+1 (202) 555-0143", "+12025550143")]
    public async Task Edit_NormalizesAValidPhoneToItsInternationalForm(string typed, string expected)
    {
        using var factory = new TigerCsApiFactory();
        await SeedStandardCustomersAsync(factory);
        var (manager, _) = await SignInAsync(factory, Roles.CsManager);

        var saved = await EditOkAsync(manager, typed, null, null);

        Assert.Equal(expected, saved.PhoneNumber);
        Assert.Equal(expected, (await RowAsync(manager)).PhoneNumber);
        Assert.Equal(expected, Assert.Single(await InDbAsync(factory, db => db.CustomerProfileOverrides)).PhoneNumber);
    }

    // ----------------------------------------------------------------------------------------------- project / unit

    [Theory]
    [InlineData("Tiger Bay Towers", null, "UnitNumber", "together")]               // a project without a unit
    [InlineData(null, "1105", "ProjectName", "together")]                           // a unit without a project
    [InlineData("Tiger Bay Towers", "ZT-9", "UnitNumber", "belongs to project Zebra Towers")]   // a unit of a different project
    [InlineData("zebra towers", "1105", "UnitNumber", "belongs to project Tiger Bay Towers")]    // ...in either direction, whatever the case
    [InlineData("NOT_REGISTERED", "NOT_REGISTERED", "ProjectName", "real name")]    // a source's placeholder is not a value
    [InlineData("Tiger Bay Towers", "N/A", "UnitNumber", "real number")]
    public async Task Edit_RefusesAnInvalidProjectAndUnitCombination_AndSavesNothing(string? project, string? unit, string field, string message)
    {
        using var factory = new TigerCsApiFactory();
        await SeedStandardCustomersAsync(factory);
        var (manager, _) = await SignInAsync(factory, Roles.CsManager);

        var response = await EditAsync(manager, null, project, unit);
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains(field, body, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(message, body, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(await InDbAsync(factory, db => db.CustomerProfileOverrides));
        Assert.Empty(await InDbAsync(factory, db => db.AuditEntries.Where(a => a.Action == AuditAction)));
    }

    [Fact]
    public async Task Edit_RefusesATooLongProjectOrUnit()
    {
        using var factory = new TigerCsApiFactory();
        await SeedStandardCustomersAsync(factory);
        var (manager, _) = await SignInAsync(factory, Roles.CsManager);

        Assert.Equal(HttpStatusCode.BadRequest, (await EditAsync(manager, null, new string('p', 201), "1")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await EditAsync(manager, null, "Tiger Bay Towers", new string('1', 101))).StatusCode);
    }

    [Theory]
    [InlineData("Tiger Bay Towers", "1105")]       // a known project, one of its units
    [InlineData("Zebra Towers", "ZT-9")]
    [InlineData("Zebra Towers", "ZT-77")]          // a known project, a unit the catalogue has never seen: it cannot be judged, so it is accepted
    [InlineData("Brand New Project", "A-1")]       // a project the catalogue has not seen: nothing to judge against
    [InlineData("Brand New Project", "1105")]      // ...so even a unit recorded elsewhere cannot be judged against it
    public async Task Edit_AcceptsACombinationTheKnownProjectsDoNotContradict(string project, string unit)
    {
        using var factory = new TigerCsApiFactory();
        await SeedStandardCustomersAsync(factory);
        var (manager, _) = await SignInAsync(factory, Roles.CsManager);

        var saved = await EditOkAsync(manager, null, project, unit);

        Assert.Equal(project, saved.ProjectName);
        Assert.Equal(unit, saved.UnitNumber);
    }

    // ----------------------------------------------------------------------------------------------- friendly fallback

    [Fact]
    public async Task ASourcePlaceholder_IsNeverShownAsAProjectOrUnit()
    {
        using var factory = new TigerCsApiFactory();
        await SeedStandardCustomersAsync(factory);
        await SeedCustomerAsync(factory, 9103, "NOT_REGISTERED", "not registered");
        var (manager, _) = await SignInAsync(factory, Roles.CsManager);

        var row = await RowAsync(manager, "crm:9103");
        var profile = (await (await manager.GetAsync("/api/customers/profile/crm:9103")).Content.ReadFromJsonAsync<CustomerDirectoryProfileDto>())!;

        // Missing, not printed: the page says "Not provided".
        Assert.Null(row.ProjectName);
        Assert.Null(row.UnitNumber);
        Assert.Empty(profile.Units);
        Assert.Null(Assert.Single(profile.Tickets).ProjectName);
        Assert.Null(Assert.Single(profile.Tickets).UnitNumber);
    }

    // ----------------------------------------------------------------------------------------------- audit

    [Fact]
    public async Task Edit_IsAudited_WithTheCustomerTheOldAndNewValuesWhoChangedItAndWhen()
    {
        using var factory = new TigerCsApiFactory();
        await SeedStandardCustomersAsync(factory);
        var (manager, managerId) = await SignInAsync(factory, Roles.CsManager);
        var started = DateTime.UtcNow.AddMinutes(-1);

        await EditOkAsync(manager, "+971501234567", "Zebra Towers", "ZT-9");

        var entry = Assert.Single(await InDbAsync(factory, db => db.AuditEntries.Where(a => a.Action == AuditAction)));
        Assert.Equal(Key, entry.EntityId);                       // the customer
        Assert.Equal("Customer", entry.EntityType);
        Assert.Equal(managerId, entry.ActorEmployeeId);          // changed by
        Assert.InRange(entry.OccurredAtUtc, started, DateTime.UtcNow.AddMinutes(1));   // changed at

        var before = JsonSerializer.Deserialize<JsonElement>(entry.BeforeValue!);                // old value: what the directory showed
        Assert.Equal(JsonValueKind.Null, before.GetProperty("PhoneNumber").ValueKind);
        Assert.Equal("Tiger Bay Towers", before.GetProperty("ProjectName").GetString());
        Assert.Equal("1105", before.GetProperty("UnitNumber").GetString());
        var after = JsonSerializer.Deserialize<JsonElement>(entry.AfterValue!);                  // new value
        Assert.Equal("+971501234567", after.GetProperty("PhoneNumber").GetString());
        Assert.Equal("Zebra Towers", after.GetProperty("ProjectName").GetString());
        Assert.Equal("ZT-9", after.GetProperty("UnitNumber").GetString());

        // A second change records the first change's result as its old value.
        await EditOkAsync(manager, "+442079460958", "Zebra Towers", "ZT-9");
        var second = (await InDbAsync(factory, db => db.AuditEntries.Where(a => a.Action == AuditAction).OrderBy(a => a.AuditEntryId))).Last();
        Assert.Equal("+971501234567", JsonSerializer.Deserialize<JsonElement>(second.BeforeValue!).GetProperty("PhoneNumber").GetString());
        Assert.Equal("+442079460958", JsonSerializer.Deserialize<JsonElement>(second.AfterValue!).GetProperty("PhoneNumber").GetString());
    }

    [Fact]
    public async Task Edit_WithNothingToChange_SaysSo_AndWritesNoAuditEntry()
    {
        using var factory = new TigerCsApiFactory();
        await SeedStandardCustomersAsync(factory);
        var (manager, _) = await SignInAsync(factory, Roles.CsManager);
        await EditOkAsync(manager, "+971501234567", "Zebra Towers", "ZT-9");

        var again = await EditOkAsync(manager, "+971 50 123 4567", "ZEBRA TOWERS", "zt-9");   // the same edit, written differently

        Assert.False(again.Changed);
        Assert.Single(await InDbAsync(factory, db => db.AuditEntries.Where(a => a.Action == AuditAction)));
        Assert.Single(await InDbAsync(factory, db => db.CustomerProfileOverrides));
    }

    // ----------------------------------------------------------------------------------------------- the dashboard counts

    private static async Task<CustomerLocationCounts> CountsAsync(TigerCsApiFactory factory)
    {
        using var scope = factory.Services.CreateScope();
        var repository = scope.ServiceProvider.GetRequiredService<ICustomerDirectoryRepository>();
        return await repository.CountByLocationAsync(new CustomerDirectoryQuery(null, null, null, null, false, 1, 25));
    }

    [Fact]
    public async Task Edit_MovesTheCustomerBetweenTheDashboardLocations_WithoutChangingHowManyCustomersThereAre()
    {
        using var factory = new TigerCsApiFactory();
        await SeedStandardCustomersAsync(factory);
        var (manager, _) = await SignInAsync(factory, Roles.CsManager);

        var start = await CountsAsync(factory);
        Assert.Equal(2, start.Total);
        Assert.Equal(2, start.Unknown);                 // neither customer has a phone on any ticket

        await EditOkAsync(manager, "+971501234567", null, null);
        var within = await CountsAsync(factory);
        Assert.Equal(1, within.WithinUae);
        Assert.Equal(0, within.OutsideUae);
        Assert.Equal(1, within.Unknown);
        Assert.Equal(start.Total, within.Total);
        Assert.Contains((await ListAsync(manager, "&location=within-uae")).Items, r => r.CustomerKey == Key);
        Assert.DoesNotContain((await ListAsync(manager, "&location=outside-uae")).Items, r => r.CustomerKey == Key);

        await EditOkAsync(manager, "+442079460958", null, null);
        var outside = await CountsAsync(factory);
        Assert.Equal(0, outside.WithinUae);
        Assert.Equal(1, outside.OutsideUae);
        Assert.Equal(start.Total, outside.Total);
        Assert.Contains((await ListAsync(manager, "&location=outside-uae")).Items, r => r.CustomerKey == Key);
        Assert.DoesNotContain((await ListAsync(manager, "&location=within-uae")).Items, r => r.CustomerKey == Key);

        // Clearing the phone edit puts the customer back where their tickets place them.
        await EditOkAsync(manager, null, null, null);
        var cleared = await CountsAsync(factory);
        Assert.Equal(start, cleared);
    }

    private static async Task<CustomerLocationBreakdownDto> BreakdownAsync(HttpClient client)
    {
        var response = await client.GetAsync("/api/customers/unknown-breakdown");
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<CustomerLocationBreakdownDto>())!;
    }

    [Fact]
    public async Task Edit_KeepsTheUnknownList_ItsExplanations_AndTheBreakdown_InStepWithTheDashboardCard()
    {
        using var factory = new TigerCsApiFactory();
        await SeedStandardCustomersAsync(factory);
        var (manager, _) = await SignInAsync(factory, Roles.CsManager);

        // Both customers have no phone on any ticket: both Unknown, each explained, and the breakdown adds up to the card.
        var start = await CountsAsync(factory);
        Assert.Equal(2, start.Unknown);
        Assert.Equal(start.Unknown, (await BreakdownAsync(manager)).Total);
        var unknownBefore = await ListAsync(manager, "&location=unknown");
        Assert.Equal(start.Unknown, unknownBefore.TotalCount);
        Assert.All(unknownBefore.Items, r => Assert.NotNull(r.LocationReason));

        await EditOkAsync(manager, "+971501234567", null, null);

        // The edited customer is placed by their new number, so they leave the Unknown list and its explanations, and every figure still agrees.
        var counts = await CountsAsync(factory);
        Assert.Equal(1, counts.Unknown);
        Assert.Equal(counts.Unknown, (await BreakdownAsync(manager)).Total);
        var unknownAfter = await ListAsync(manager, "&location=unknown");
        Assert.Equal(counts.Unknown, unknownAfter.TotalCount);
        Assert.DoesNotContain(unknownAfter.Items, r => r.CustomerKey == Key);
        Assert.All(unknownAfter.Items, r => Assert.NotNull(r.LocationReason));
    }

    [Fact]
    public async Task EditingProjectAndUnitOnly_DoesNotMoveTheCustomerBetweenLocations()
    {
        using var factory = new TigerCsApiFactory();
        await SeedStandardCustomersAsync(factory);
        var (manager, _) = await SignInAsync(factory, Roles.CsManager);
        var start = await CountsAsync(factory);

        await EditOkAsync(manager, null, "Zebra Towers", "ZT-9");

        Assert.Equal(start, await CountsAsync(factory));
        Assert.Equal(start.Total, (await ListAsync(manager)).TotalCount);
    }
}
