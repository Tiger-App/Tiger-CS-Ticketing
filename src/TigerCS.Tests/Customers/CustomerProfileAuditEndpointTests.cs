using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using TigerCS.Application.Modules.IdentityAndAccess.Dto;
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
/// The Customer Audit History through the real Api host: every actual change to a customer's profile is audited field by field, only actual
/// changes are recorded, retries are recorded once, history survives a phone-number change, a customer edit never touches a ticket (a closed
/// ticket stays closed with its owner, SLA and closure data), and viewing history is a permission of its own.
/// </summary>
public class CustomerProfileAuditEndpointTests
{
    private const int CustomerId = 9301;
    private const string Key = "crm:9301";
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    // ------------------------------------------------------------------------------------------------------------- helpers

    private static async Task<(HttpClient Client, Guid EmployeeId)> SignInAsync(TigerCsApiFactory factory, string role)
    {
        var (username, password, employeeId) = await factory.SeedEmployeeAsync(role);
        var client = factory.CreateClient();
        var login = await (await client.PostAsJsonAsync("/api/auth/login", new LoginRequestDto(username, password))).Content.ReadFromJsonAsync<LoginResponseDto>();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", login!.AccessToken);
        return (client, employeeId);
    }

    private static async Task<long> SeedCustomerAsync(TigerCsApiFactory factory, int crmCustomerId, string? project, string? unit, string name = "Mohamed Ismaik")
    {
        await factory.SeedPrioritiesAsync();
        var departmentId = await factory.CreateDepartmentAsync("Customers " + Guid.NewGuid(), Guid.NewGuid().ToString("N")[..8]);
        var categoryId = await factory.CreateCategoryAsync("General", departmentId);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TigerCsDbContext>();
        var ticket = Ticket.CreateVerifiedFromCrmBuyer(
            $"TCK-{Guid.NewGuid():N}"[..24], departmentId, crmCustomerId, 1, 1, 1, name, project, unit, categoryId,
            (byte)PriorityLevel.High, "request", DateTime.UtcNow);
        db.Tickets.Add(ticket);
        await db.SaveChangesAsync();
        return ticket.TicketId;
    }

    /// <summary>A phone-only customer: an unverified ticket whose intake carries the number, exactly as the directory derives it.</summary>
    private static async Task<string> SeedPhoneCustomerAsync(TigerCsApiFactory factory, string phone)
    {
        await factory.SeedPrioritiesAsync();
        var departmentId = await factory.CreateDepartmentAsync("Phone " + Guid.NewGuid(), Guid.NewGuid().ToString("N")[..8]);
        var categoryId = await factory.CreateCategoryAsync("General", departmentId);
        var (_, _, employeeId) = await factory.SeedEmployeeAsync(Roles.CsAgent);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TigerCsDbContext>();
        var ticket = Ticket.CreateUnverified($"TCK-{Guid.NewGuid():N}"[..24], departmentId, categoryId, (byte)PriorityLevel.Low, "call", DateTime.UtcNow);
        db.Tickets.Add(ticket);
        await db.SaveChangesAsync();
        var intake = new IntakeRecord(1, phone, departmentId, false, null, null, employeeId, DateTime.UtcNow.AddMinutes(-5));
        intake.LinkToTicket(ticket.TicketId, CrmVerificationStatus.Unverified, hasSelectedUnit: false);
        db.IntakeRecords.Add(intake);
        await db.SaveChangesAsync();
        return CustomerIdentity.Phone(phone).Key;
    }

    private static Task<HttpResponseMessage> PutAsync(HttpClient client, UpdateCustomerProfileRequestDto request, string key = Key) =>
        client.PutAsJsonAsync($"/api/customers/profile/{Uri.EscapeDataString(key)}", request);

    private static async Task<CustomerProfileEditedDto> EditOkAsync(HttpClient client, UpdateCustomerProfileRequestDto request, string key = Key)
    {
        var response = await PutAsync(client, request, key);
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.OK, $"{(int)response.StatusCode}: {body}");
        return JsonSerializer.Deserialize<CustomerProfileEditedDto>(body, Web)!;
    }

    private static UpdateCustomerProfileRequestDto Details(
        string? name = null, string? email = null, string? landline = null, string? nationality = null, string? country = null,
        string? emirate = null, string? location = null, string? language = null, IReadOnlyList<string>? additional = null,
        string? reason = null, long? ticket = null, Guid? correlation = null, string? source = null) =>
        new(null, null, null, name, email, landline, additional, nationality, country, emirate, location, language, reason, ticket, correlation, source);

    private static async Task<CustomerDirectoryProfileDto> ProfileAsync(HttpClient client, string key = Key)
    {
        var response = await client.GetAsync($"/api/customers/profile/{Uri.EscapeDataString(key)}");
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<CustomerDirectoryProfileDto>(Web))!;
    }

    private static async Task<CustomerChangeHistoryDto> HistoryAsync(HttpClient client, string key = Key, int page = 1, int pageSize = 100)
    {
        var response = await client.GetAsync($"/api/customers/profile/{Uri.EscapeDataString(key)}/history?page={page}&pageSize={pageSize}");
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.OK, $"{(int)response.StatusCode}: {body}");
        return JsonSerializer.Deserialize<CustomerChangeHistoryDto>(body, Web)!;
    }

    private static async Task<List<T>> InDbAsync<T>(TigerCsApiFactory factory, Func<TigerCsDbContext, IQueryable<T>> query) where T : class
    {
        using var scope = factory.Services.CreateScope();
        return await query(scope.ServiceProvider.GetRequiredService<TigerCsDbContext>()).AsNoTracking().ToListAsync();
    }

    // -------------------------------------------------------------------------------------------- one row per changed field

    [Fact]
    public async Task EveryChangedField_IsRecordedOnItsOwnRow_WithWhoWhenWhereFromWhyAndOneSharedCorrelationId()
    {
        using var factory = new TigerCsApiFactory();
        var ticketId = await SeedCustomerAsync(factory, CustomerId, "Tiger Bay Towers", "1105");
        var (manager, managerId) = await SignInAsync(factory, Roles.CsManager);
        var started = DateTime.UtcNow.AddMinutes(-1);

        var result = await EditOkAsync(manager, Details(
            name: "Mohamed Ismaik Al Mansoori", email: "m.ismaik@example.com", landline: "04 123 4567", nationality: "Jordanian",
            country: "United Arab Emirates", emirate: "Dubai", location: "Business Bay", language: "Arabic",
            additional: ["+971 55 987 6543", "00971 50 111 2222"], reason: "Customer called to correct their record", ticket: ticketId, source: "UI"));
        Assert.True(result.Changed);

        var rows = await InDbAsync(factory, db => db.CustomerProfileChanges.OrderBy(c => c.CustomerProfileChangeId));
        Assert.Equal(
            ["AdditionalPhone", "AdditionalPhone", "Country", "Email", "Emirate", "FullName", "LandlineNumber", "Location", "Nationality", "PreferredLanguage"],
            rows.Select(r => r.FieldName).Order());

        // Old and new, per field: what the directory showed before and what it shows now.
        var fullName = rows.Single(r => r.FieldName == "FullName");
        Assert.Equal("Mohamed Ismaik", fullName.OldValue);
        Assert.Equal("Mohamed Ismaik Al Mansoori", fullName.NewValue);
        Assert.Null(rows.Single(r => r.FieldName == "Email").OldValue);
        Assert.Equal("m.ismaik@example.com", rows.Single(r => r.FieldName == "Email").NewValue);
        Assert.Equal("+97141234567", rows.Single(r => r.FieldName == "LandlineNumber").NewValue);   // a local landline, stored internationally
        Assert.Equal(["+971559876543", "+971501112222"], rows.Where(r => r.FieldName == "AdditionalPhone").Select(r => r.NewValue).OrderByDescending(v => v));

        // Who, when, where from, why, which ticket and which operation - on every row.
        Assert.All(rows, r =>
        {
            Assert.Equal(managerId, r.ModifiedByEmployeeId);
            Assert.Equal("Test Employee", r.ModifiedByName);
            Assert.InRange(r.ModifiedAtUtc, started, DateTime.UtcNow.AddMinutes(1));   // UTC
            Assert.Equal("UI", r.Source);
            Assert.Equal("Customer called to correct their record", r.ChangeReason);
            Assert.Equal(ticketId, r.RelatedTicketId);
            Assert.Equal(Key, r.CustomerKey);
        });
        Assert.Single(rows.Select(r => r.CorrelationId).Distinct());
        Assert.Single(rows.Select(r => r.CustomerProfileId).Distinct());
        Assert.Equal(rows.Count, rows.Select(r => r.ChangeKey).Distinct().Count());

        // Saved with the audit rows, in one operation: the profile shows what was saved.
        var profile = await ProfileAsync(manager);
        Assert.Equal("Mohamed Ismaik Al Mansoori", profile.DisplayName);
        Assert.Equal("m.ismaik@example.com", profile.Details!.Email);
        Assert.Equal("+97141234567", profile.Details.LandlineNumber);
        Assert.Equal("Dubai", profile.Details.Emirate);
        Assert.Equal(2, profile.Details.AdditionalPhones.Count);
    }

    [Fact]
    public async Task OnlyActualChangesAreRecorded_AnUnchangedValueWritesNothing()
    {
        using var factory = new TigerCsApiFactory();
        await SeedCustomerAsync(factory, CustomerId, "Tiger Bay Towers", "1105");
        var (manager, _) = await SignInAsync(factory, Roles.CsManager);
        await EditOkAsync(manager, Details(name: "Aisha Rahman", email: "aisha@example.com", nationality: "Emirati"));
        var before = (await InDbAsync(factory, db => db.CustomerProfileChanges)).Count;
        var auditBefore = (await InDbAsync(factory, db => db.AuditEntries)).Count;

        // The same values again - differently written - changes nothing.
        var again = await EditOkAsync(manager, Details(name: "  Aisha Rahman ", email: "aisha@example.com", nationality: "Emirati"));
        Assert.False(again.Changed);
        Assert.Equal(before, (await InDbAsync(factory, db => db.CustomerProfileChanges)).Count);
        Assert.Equal(auditBefore, (await InDbAsync(factory, db => db.AuditEntries)).Count);

        // Changing one field records exactly that field.
        await EditOkAsync(manager, Details(nationality: "Jordanian"));
        var latest = (await InDbAsync(factory, db => db.CustomerProfileChanges.OrderByDescending(c => c.CustomerProfileChangeId).Take(5))).ToList();
        var added = latest.Take(1).Single();
        Assert.Equal("Nationality", added.FieldName);
        Assert.Equal("Emirati", added.OldValue);
        Assert.Equal("Jordanian", added.NewValue);
        Assert.Equal(before + 1, (await InDbAsync(factory, db => db.CustomerProfileChanges)).Count);
    }

    [Fact]
    public async Task ARequestThatOmitsAField_NeverClearsIt_AndAnEmptyStringDoes()
    {
        using var factory = new TigerCsApiFactory();
        await SeedCustomerAsync(factory, CustomerId, "Tiger Bay Towers", "1105");
        var (manager, _) = await SignInAsync(factory, Roles.CsManager);
        await EditOkAsync(manager, Details(name: "Aisha Rahman", email: "aisha@example.com", language: "Arabic"));

        // A client that only knows the original three fields cannot wipe the rest by leaving them out.
        await EditOkAsync(manager, new UpdateCustomerProfileRequestDto("+971501234567", null, null));
        var profile = await ProfileAsync(manager);
        Assert.Equal("Aisha Rahman", profile.DisplayName);
        Assert.Equal("aisha@example.com", profile.Details!.Email);
        Assert.Equal("Arabic", profile.Details.PreferredLanguage);

        // An empty string is an explicit clear - and it is audited as one.
        await EditOkAsync(manager, Details(language: ""));
        Assert.Null((await ProfileAsync(manager)).Details!.PreferredLanguage);
        var cleared = (await InDbAsync(factory, db => db.CustomerProfileChanges.Where(c => c.FieldName == "PreferredLanguage").OrderBy(c => c.CustomerProfileChangeId))).Last();
        Assert.Equal("Arabic", cleared.OldValue);
        Assert.Null(cleared.NewValue);
    }

    [Fact]
    public async Task ARetryWithTheSameCorrelationId_IsRecordedOnce()
    {
        using var factory = new TigerCsApiFactory();
        await SeedCustomerAsync(factory, CustomerId, "Tiger Bay Towers", "1105");
        var (manager, _) = await SignInAsync(factory, Roles.CsManager);
        var correlation = Guid.NewGuid();

        var first = await EditOkAsync(manager, Details(name: "Aisha Rahman", email: "aisha@example.com", correlation: correlation));
        var retry = await EditOkAsync(manager, Details(name: "Aisha Rahman", email: "aisha@example.com", correlation: correlation));
        var replayedLater = await EditOkAsync(manager, Details(name: "Someone Else Entirely", correlation: correlation));   // same operation id: already done

        Assert.True(first.Changed);
        Assert.False(retry.Changed);
        Assert.False(replayedLater.Changed);
        var rows = await InDbAsync(factory, db => db.CustomerProfileChanges);
        Assert.Equal(2, rows.Count);   // name and e-mail, once
        Assert.All(rows, r => Assert.Equal(correlation, r.CorrelationId));
        Assert.Equal("Aisha Rahman", (await ProfileAsync(manager)).DisplayName);   // the replay with different content did not apply
        Assert.Single(await InDbAsync(factory, db => db.AuditEntries.Where(a => a.Action == "UpdateCustomerProfile")));
    }

    // -------------------------------------------------------------------------------------------------- validation

    [Theory]
    [InlineData("FullName", "UNKNOWN")]
    [InlineData("Email", "not-an-email")]
    [InlineData("Email", "a@b")]
    [InlineData("Landline", "0501234567")]          // a UAE mobile does not belong in the landline field
    [InlineData("Landline", "not a number")]
    [InlineData("Emirate", "Narnia")]
    [InlineData("Additional", "12")]
    public async Task InvalidValues_AreRefused_AndNothingIsSaved(string field, string value)
    {
        using var factory = new TigerCsApiFactory();
        await SeedCustomerAsync(factory, CustomerId, "Tiger Bay Towers", "1105");
        var (manager, _) = await SignInAsync(factory, Roles.CsManager);

        var request = field switch
        {
            "FullName" => Details(name: value),
            "Email" => Details(email: value),
            "Landline" => Details(landline: value),
            "Emirate" => Details(emirate: value),
            _ => Details(additional: [value]),
        };
        var response = await PutAsync(manager, request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(await InDbAsync(factory, db => db.CustomerProfileChanges));
        Assert.Empty(await InDbAsync(factory, db => db.CustomerProfileOverrides));
    }

    [Fact]
    public async Task AnEmirateIsOnlyEverTheOneStated_NeverDerivedFromAPhoneNumber()
    {
        using var factory = new TigerCsApiFactory();
        await SeedCustomerAsync(factory, CustomerId, "Tiger Bay Towers", "1105");
        var (manager, _) = await SignInAsync(factory, Roles.CsManager);

        // A Dubai landline (04) as the landline, and no emirate stated: no emirate is inferred.
        await EditOkAsync(manager, Details(landline: "04 123 4567"));
        var profile = await ProfileAsync(manager);
        Assert.Null(profile.Details!.Emirate);
        Assert.DoesNotContain(await InDbAsync(factory, db => db.CustomerProfileChanges), c => c.FieldName == "Emirate");
    }

    // -------------------------------------------------------------------------------- tickets are never touched by an edit

    [Fact]
    public async Task EditingACustomer_NeverTouchesAClosedTicket_ItsOwnerSlaStatusAndClosureDataAreUnchanged_AndItsSnapshotIsPreserved()
    {
        using var factory = new TigerCsApiFactory();
        var ticketId = await SeedCustomerAsync(factory, CustomerId, "Tiger Bay Towers", "1105");
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TigerCsDbContext>();
            var ticket = await db.Tickets.SingleAsync(t => t.TicketId == ticketId);
            // A closed ticket with an owner and a stopped SLA, written directly (the workflow that would get it there needs a whole
            // assignment history that has nothing to do with what is being proved here).
            var entry = db.Entry(ticket);
            entry.Property(nameof(Ticket.TicketStatus)).CurrentValue = TicketStatus.Closed;
            entry.Property(nameof(Ticket.CurrentOwnerEmployeeId)).CurrentValue = Guid.NewGuid();
            entry.Property(nameof(Ticket.SlaState)).CurrentValue = SlaState.Met;
            await db.SaveChangesAsync();
        }

        var snapshotBefore = await TicketStateAsync(factory, ticketId);
        Assert.Equal("Closed", snapshotBefore["TicketStatus"]?.ToString());
        var (manager, _) = await SignInAsync(factory, Roles.CsManager);

        await EditOkAsync(manager, new UpdateCustomerProfileRequestDto(
            "+971501234567", "Zebra Towers", "ZT-9", "A Brand New Name", "new@example.com", "04 123 4567", ["+971559876543"],
            "Jordanian", "United Arab Emirates", "Dubai", "Marina", "Arabic", "correction", ticketId, null, "UI"));

        // Every stored column of the ticket - status, owner, SLA state and dates, closure data, row version - is exactly as it was,
        // including the name captured when the ticket was raised: history is preserved, not overwritten.
        var snapshotAfter = await TicketStateAsync(factory, ticketId);
        Assert.Equal(snapshotBefore.Count, snapshotAfter.Count);
        foreach (var (column, value) in snapshotBefore)
        {
            Assert.Equal(value?.ToString(), snapshotAfter[column]?.ToString());
        }

        Assert.Equal("Mohamed Ismaik", snapshotAfter["CrmBuyerCustomerName"]?.ToString());
        Assert.Equal("Closed", snapshotAfter["TicketStatus"]?.ToString());
        // ...and the ticket is still reachable and is still the same customer's ticket.
        Assert.Equal("A Brand New Name", (await ProfileAsync(manager)).DisplayName);
        Assert.Contains((await ProfileAsync(manager)).Tickets, t => t.TicketId == ticketId && t.TicketStatus == "Closed");
    }

    private static async Task<Dictionary<string, object?>> TicketStateAsync(TigerCsApiFactory factory, long ticketId)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TigerCsDbContext>();
        var ticket = await db.Tickets.AsNoTracking().SingleAsync(t => t.TicketId == ticketId);
        var entry = db.Entry(ticket);
        return entry.Properties.ToDictionary(
            p => p.Metadata.Name,
            p => p.CurrentValue is byte[] bytes ? (object)Convert.ToHexString(bytes) : p.CurrentValue);
    }

    // ------------------------------------------------------------------------------ identity survives a phone-number change

    [Fact]
    public async Task HistoryIsPreserved_WhenThePrimaryPhoneChanges_EvenForAPhoneOnlyCustomer()
    {
        using var factory = new TigerCsApiFactory();
        var key = await SeedPhoneCustomerAsync(factory, "+971501234567");
        var (manager, _) = await SignInAsync(factory, Roles.CsManager);

        await EditOkAsync(manager, new UpdateCustomerProfileRequestDto(null, null, null, FullName: "Khalid Noor"), key);
        var profileBefore = await ProfileAsync(manager, key);
        Assert.Equal(key, profileBefore.CustomerKey);
        var stableId = profileBefore.CustomerProfileId;
        Assert.NotNull(stableId);

        // The number they are shown with changes - twice.
        await EditOkAsync(manager, new UpdateCustomerProfileRequestDto("+971559998888", null, null), key);
        await EditOkAsync(manager, new UpdateCustomerProfileRequestDto("+442079460958", null, null), key);

        // Same customer, same stable identifier, and the whole history - including the changes before the phone changed - is still there.
        var profileAfter = await ProfileAsync(manager, key);
        Assert.Equal(key, profileAfter.CustomerKey);
        Assert.Equal(stableId, profileAfter.CustomerProfileId);
        Assert.Equal("Khalid Noor", profileAfter.DisplayName);
        var history = await HistoryAsync(manager, key);
        Assert.Equal(["FullName", "MobileNumber", "MobileNumber"], history.Items.OrderBy(i => i.ChangeId).Select(i => i.Field));
        var phones = history.Items.Where(i => i.Field == "MobileNumber").OrderBy(i => i.ChangeId).ToList();
        Assert.Equal(("+971501234567", "+971559998888"), (phones[0].OldValue, phones[0].NewValue));
        Assert.Equal(("+971559998888", "+442079460958"), (phones[1].OldValue, phones[1].NewValue));
        Assert.Single((await InDbAsync(factory, db => db.CustomerProfileChanges)).Select(c => c.CustomerProfileId).Distinct());
    }

    // ------------------------------------------------------------------------------------------------- permissions

    [Fact]
    public async Task History_IsRestrictedToHoldersOfThePermission_AndIsReadOnly()
    {
        using var factory = new TigerCsApiFactory();
        await SeedCustomerAsync(factory, CustomerId, "Tiger Bay Towers", "1105");
        var (manager, _) = await SignInAsync(factory, Roles.CsManager);
        await EditOkAsync(manager, Details(name: "Aisha Rahman"));

        // Roles that may VIEW customers but not their change history.
        foreach (var role in new[] { Roles.CsAgent, Roles.CsSupervisor, Roles.DepartmentEmployee, Roles.GeneralManager, Roles.ChairmanCeo, Roles.ReportingUser })
        {
            var (client, _) = await SignInAsync(factory, role);
            var response = await client.GetAsync($"/api/customers/profile/{Key}/history");
            Assert.True(response.StatusCode == HttpStatusCode.Forbidden, $"{role}: {(int)response.StatusCode}");
        }

        // Administrator and CS Manager hold it; an explicit grant opens it, an explicit denial always refuses.
        var (admin, _) = await SignInAsync(factory, Roles.SystemAdministrator);
        Assert.Single((await HistoryAsync(admin)).Items);
        Assert.Single((await HistoryAsync(manager)).Items);
        var (granted, grantedId) = await SignInAsync(factory, Roles.CsSupervisor);
        await factory.SetPermissionOverrideAsync(grantedId, P.CustomersViewHistory, PermissionEffect.Grant);
        Assert.Single((await HistoryAsync(granted)).Items);
        await factory.SetPermissionOverrideAsync(grantedId, P.CustomersViewHistory, PermissionEffect.Deny);
        Assert.Equal(HttpStatusCode.Forbidden, (await granted.GetAsync($"/api/customers/profile/{Key}/history")).StatusCode);
        await factory.SetPermissionOverrideAsync(grantedId, P.CustomersViewHistory, PermissionEffect.Deny);
        var (deniedManager, deniedManagerId) = await SignInAsync(factory, Roles.CsManager);
        await factory.SetPermissionOverrideAsync(deniedManagerId, P.CustomersViewHistory, PermissionEffect.Deny);
        Assert.Equal(HttpStatusCode.Forbidden, (await deniedManager.GetAsync($"/api/customers/profile/{Key}/history")).StatusCode);

        // Ordinary users cannot edit or delete a history entry: there is no such endpoint, for anyone - the administrator included.
        var entryId = (await HistoryAsync(admin)).Items.Single().ChangeId;
        foreach (var client in new[] { admin, manager })
        {
            foreach (var url in new[] { $"/api/customers/profile/{Key}/history/{entryId}", $"/api/customers/profile/{Key}/history" })
            {
                Assert.True((await client.DeleteAsync(url)).StatusCode is HttpStatusCode.NotFound or HttpStatusCode.MethodNotAllowed, url);
                Assert.True((await client.PutAsJsonAsync(url, new { newValue = "forged" })).StatusCode is HttpStatusCode.NotFound or HttpStatusCode.MethodNotAllowed, url);
                Assert.True((await client.PatchAsJsonAsync(url, new { newValue = "forged" })).StatusCode is HttpStatusCode.NotFound or HttpStatusCode.MethodNotAllowed, url);
            }
        }

        Assert.Equal("Aisha Rahman", (await InDbAsync(factory, db => db.CustomerProfileChanges)).Single().NewValue);
    }

    [Fact]
    public async Task History_NeverReachesACustomerTheCallerCannotOpen_AndAnUnknownCustomerIsNotFound()
    {
        using var factory = new TigerCsApiFactory();
        await SeedCustomerAsync(factory, CustomerId, "Tiger Bay Towers", "1105");
        var (manager, _) = await SignInAsync(factory, Roles.CsManager);

        Assert.Equal(HttpStatusCode.NotFound, (await manager.GetAsync("/api/customers/profile/crm:424242/history")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await manager.GetAsync("/api/customers/profile/nonsense/history")).StatusCode);
        // A customer who was never edited has an empty history, not an error.
        Assert.Empty((await HistoryAsync(manager)).Items);
    }

    [Fact]
    public async Task EditingProperties_NeedsTheEditPermission()
    {
        using var factory = new TigerCsApiFactory();
        await SeedCustomerAsync(factory, CustomerId, "Tiger Bay Towers", "1105");
        var (agent, _) = await SignInAsync(factory, Roles.CsAgent);

        var response = await agent.PostAsJsonAsync($"/api/customers/profile/{Key}/properties", new CustomerPropertyRequestDto("Zebra Towers", "ZT-9", "Owner"));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Empty(await InDbAsync(factory, db => db.CustomerPropertyAssociations));
        Assert.Empty(await InDbAsync(factory, db => db.CustomerProfileChanges));
    }

    // ------------------------------------------------------------------------------------------------- properties

    [Fact]
    public async Task Properties_AreAddedEditedAndRemoved_AndEveryChangeIsAudited()
    {
        using var factory = new TigerCsApiFactory();
        await SeedCustomerAsync(factory, CustomerId, "Tiger Bay Towers", "1105");
        await SeedCustomerAsync(factory, 9302, "Zebra Towers", "ZT-9");
        await SeedCustomerAsync(factory, 9303, "Marina Heights", "MH-3");
        var (manager, managerId) = await SignInAsync(factory, Roles.CsManager);
        var url = $"/api/customers/profile/{Key}/properties";

        // Multiple properties for one customer, each with its own relationship.
        var owner = await manager.PostAsJsonAsync(url, new CustomerPropertyRequestDto("Zebra Towers", "zt-9", "Owner", Reason: "Customer showed a title deed"));
        Assert.Equal(HttpStatusCode.OK, owner.StatusCode);
        var tenant = await manager.PostAsJsonAsync(url, new CustomerPropertyRequestDto("Marina Heights", "MH-3", "tenant"));
        Assert.Equal(HttpStatusCode.OK, tenant.StatusCode);
        var both = (await tenant.Content.ReadFromJsonAsync<CustomerPropertyResult>(Web))!.Properties!;
        Assert.Equal(2, both.Count);
        Assert.All(both, p => Assert.False(p.IsVerified));   // manually added: never presented as verified ownership
        Assert.Equal("ZT-9", both.Single(p => p.ProjectName == "Zebra Towers").UnitNumber);   // the catalogue's spelling

        var profile = await ProfileAsync(manager);
        Assert.Equal(2, profile.ManualProperties!.Count);
        Assert.Contains(profile.Units, u => u.Source == "Crm" && u.UnitNumber == "1105");   // the verified unit is untouched and separate

        // Edit one: the relationship changes.
        var ownerId = profile.ManualProperties.Single(p => p.ProjectName == "Zebra Towers").AssociationId;
        var edited = await manager.PutAsJsonAsync($"{url}/{ownerId}", new CustomerPropertyRequestDto(null, null, "Representative"));
        Assert.Equal(HttpStatusCode.OK, edited.StatusCode);
        Assert.Equal("Representative", (await ProfileAsync(manager)).ManualProperties!.Single(p => p.AssociationId == ownerId).Relationship);

        // Re-saving the same values changes nothing and records nothing.
        var changesBefore = (await InDbAsync(factory, db => db.CustomerProfileChanges)).Count;
        var same = await manager.PutAsJsonAsync($"{url}/{ownerId}", new CustomerPropertyRequestDto("Zebra Towers", "ZT-9", "Representative"));
        Assert.False((await same.Content.ReadFromJsonAsync<CustomerPropertyResult>(Web))!.Changed);
        Assert.Equal(changesBefore, (await InDbAsync(factory, db => db.CustomerProfileChanges)).Count);

        // Remove one.
        var removed = await manager.DeleteAsync($"{url}/{ownerId}?reason=Entered%20in%20error");
        Assert.Equal(HttpStatusCode.OK, removed.StatusCode);
        Assert.Single((await ProfileAsync(manager)).ManualProperties!);

        // Every add, edit and removal is its own audited change, in order.
        var rows = (await InDbAsync(factory, db => db.CustomerProfileChanges.Where(c => c.FieldName == "Property").OrderBy(c => c.CustomerProfileChangeId))).ToList();
        Assert.Equal(4, rows.Count);
        Assert.Null(rows[0].OldValue);
        Assert.Equal("Zebra Towers / ZT-9 (Owner)", rows[0].NewValue);
        Assert.Equal("Customer showed a title deed", rows[0].ChangeReason);
        Assert.Equal("Marina Heights / MH-3 (Tenant)", rows[1].NewValue);
        Assert.Equal("Zebra Towers / ZT-9 (Owner)", rows[2].OldValue);
        Assert.Equal("Zebra Towers / ZT-9 (Representative)", rows[2].NewValue);
        Assert.Equal("Zebra Towers / ZT-9 (Representative)", rows[3].OldValue);
        Assert.Null(rows[3].NewValue);
        Assert.Equal("Entered in error", rows[3].ChangeReason);
        Assert.All(rows, r => Assert.Equal(managerId, r.ModifiedByEmployeeId));
    }

    [Fact]
    public async Task Properties_RefuseDuplicates_AndInvalidInput_AndAnotherCustomersAssociation()
    {
        using var factory = new TigerCsApiFactory();
        await SeedCustomerAsync(factory, CustomerId, "Tiger Bay Towers", "1105");
        await SeedCustomerAsync(factory, 9302, "Zebra Towers", "ZT-9");
        var (manager, _) = await SignInAsync(factory, Roles.CsManager);
        var url = $"/api/customers/profile/{Key}/properties";
        Assert.Equal(HttpStatusCode.OK, (await manager.PostAsJsonAsync(url, new CustomerPropertyRequestDto("Zebra Towers", "ZT-9", "Owner"))).StatusCode);

        // The same property twice - however it is written - is refused.
        Assert.Equal(HttpStatusCode.Conflict, (await manager.PostAsJsonAsync(url, new CustomerPropertyRequestDto("ZEBRA TOWERS", "zt-9", "Tenant"))).StatusCode);
        // So is a unit the customer already has as a VERIFIED unit: nothing is duplicated, and nothing is relabelled as manual.
        Assert.Equal(HttpStatusCode.Conflict, (await manager.PostAsJsonAsync(url, new CustomerPropertyRequestDto("Tiger Bay Towers", "1105", "Owner"))).StatusCode);

        // Invalid input.
        foreach (var bad in new[]
                 {
                     new CustomerPropertyRequestDto("Marina Heights", "MH-3", "Landlord"),    // not a relationship
                     new CustomerPropertyRequestDto("Marina Heights", "MH-3", null),          // relationship is required
                     new CustomerPropertyRequestDto("Marina Heights", null, "Owner"),         // unit is required
                     new CustomerPropertyRequestDto(null, "MH-3", "Owner"),                   // project is required
                     new CustomerPropertyRequestDto("Tiger Bay Towers", "ZT-9", "Owner"),     // ZT-9 belongs to Zebra Towers
                 })
        {
            Assert.Equal(HttpStatusCode.BadRequest, (await manager.PostAsJsonAsync(url, bad)).StatusCode);
        }

        Assert.Single(await InDbAsync(factory, db => db.CustomerPropertyAssociations));

        // Another customer's association - or a verified unit's id - is never found through this customer.
        var other = await manager.PostAsJsonAsync("/api/customers/profile/crm:9302/properties", new CustomerPropertyRequestDto("Marina Heights", "MH-3", "Owner"));
        Assert.Equal(HttpStatusCode.OK, other.StatusCode);
        var otherId = (await InDbAsync(factory, db => db.CustomerPropertyAssociations)).Single(a => a.CustomerKey == "crm:9302").CustomerPropertyAssociationId;
        Assert.Equal(HttpStatusCode.NotFound, (await manager.DeleteAsync($"{url}/{otherId}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await manager.PutAsJsonAsync($"{url}/{otherId}", new CustomerPropertyRequestDto("A", "B", "Owner"))).StatusCode);
        Assert.Equal(2, (await InDbAsync(factory, db => db.CustomerPropertyAssociations)).Count);
    }

    [Fact]
    public async Task APropertyRetriedWithTheSameCorrelationId_IsAddedOnce()
    {
        using var factory = new TigerCsApiFactory();
        await SeedCustomerAsync(factory, CustomerId, "Tiger Bay Towers", "1105");
        await SeedCustomerAsync(factory, 9302, "Zebra Towers", "ZT-9");
        var (manager, _) = await SignInAsync(factory, Roles.CsManager);
        var url = $"/api/customers/profile/{Key}/properties";
        var correlation = Guid.NewGuid();
        var request = new CustomerPropertyRequestDto("Zebra Towers", "ZT-9", "Owner", CorrelationId: correlation);

        Assert.Equal(HttpStatusCode.OK, (await manager.PostAsJsonAsync(url, request)).StatusCode);
        var retry = await manager.PostAsJsonAsync(url, request);

        Assert.Equal(HttpStatusCode.OK, retry.StatusCode);   // answered as done, not as a duplicate error
        Assert.False((await retry.Content.ReadFromJsonAsync<CustomerPropertyResult>(Web))!.Changed);
        Assert.Single(await InDbAsync(factory, db => db.CustomerPropertyAssociations));
        Assert.Single(await InDbAsync(factory, db => db.CustomerProfileChanges.Where(c => c.FieldName == "Property")));
    }

    // --------------------------------------------------------------------------------------------- the model itself

    [Fact]
    public void AChangeRowRefusesNoChange_AndTheChangeKeyIsStable()
    {
        var profile = Guid.NewGuid();
        var correlation = Guid.NewGuid();
        var employee = Guid.NewGuid();
        Assert.Throws<ArgumentException>(() => new CustomerProfileChange(profile, Key, "FullName", "A", "A", employee, "X", DateTime.UtcNow, "UI", null, null, correlation));

        var one = new CustomerProfileChange(profile, Key, "FullName", "A", "B", employee, "X", DateTime.UtcNow, "UI", null, null, correlation);
        var two = new CustomerProfileChange(profile, Key, "FullName", "A", "B", employee, "Y", DateTime.UtcNow.AddMinutes(5), "API", "why", 7, correlation);
        Assert.Equal(one.ChangeKey, two.ChangeKey);   // what identifies the change is the operation, the customer, the field and the values
        Assert.Equal(CustomerProfileChange.ChangeKeyLength, one.ChangeKey.Length);
        Assert.NotEqual(one.ChangeKey, new CustomerProfileChange(profile, Key, "FullName", "A", "C", employee, "X", DateTime.UtcNow, "UI", null, null, correlation).ChangeKey);
        Assert.Equal("API", new CustomerProfileChange(profile, Key, "Email", null, "a@b.co", employee, "X", DateTime.UtcNow, "invented", null, null, correlation).Source);   // a caller cannot invent a source
    }
}
