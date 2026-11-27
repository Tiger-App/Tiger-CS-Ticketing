using System.Net;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using TigerCS.Application.Modules.QueueingIntegration;
using TigerCS.Application.Modules.QueueingIntegration.Dto;
using TigerCS.Application.Modules.Ticketing.Dto;
using TigerCS.Application.Modules.Ticketing.Services;
using TigerCS.Domain.Modules.IdentityAndAccess;
using TigerCS.Domain.Modules.QueueingIntegration;
using TigerCS.Domain.Modules.Ticketing;
using TigerCS.Infrastructure.Persistence;
using static TigerCS.Tests.QueueingIntegration.QueueingFixture;

namespace TigerCS.Tests.QueueingIntegration;

/// <summary>
/// The Queueing mirror's "service name" and "no per-service mapping" rules: the Queueing System's own service name is preserved and shown (never routed by), a company mapping is
/// mandatory while a ticket-type mapping is only an optional override of the department's General request type, and adding the display-only name leaves every stored EventId
/// and payload hash exactly as it was.
/// </summary>
public sealed class QueueingTicketTypeNameTests(QueueingFixture f) : IClassFixture<QueueingFixture>
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    // ------------------------------------------------------------------ idempotency: compatibility with every event stored before the field existed

    /// <summary>
    /// The expected JSON and SHA-256 below were produced by the code as it stood BEFORE TicketTypeName existed (the previous ToCanonicalJson / HashOf, run over these very
    /// requests). A name-less request must still serialise to exactly those bytes, so a stored PayloadHash keeps matching a redelivery of the same event.
    /// </summary>
    [Theory]
    [InlineData("""{"EventId":"evt-1","EventType":"TicketCreated","ExternalTicketId":"qt-1","EventSequence":1,"QueueNumber":"A-017","CompanyId":"company-a","TicketTypeId":"type-complaint","CustomerName":"Aisha Khan","CustomerPhone":"+971500000123","CustomerEmail":"aisha@example.test","OccurredAtUtc":"2026-11-01T10:00:00Z"}""", """{"eventId":"evt-1","eventType":"TicketCreated","externalTicketId":"qt-1","eventSequence":1,"queueNumber":"A-017","companyId":"company-a","ticketTypeId":"type-complaint","customerName":"Aisha Khan","customerPhone":"\u002B971500000123","customerEmail":"aisha@example.test","occurredAtUtc":"2026-11-01T10:00:00Z"}""", "b0a7d078f9eb168304d8bea3e20b3b65c252c1604e77ded78715b278219c0edf")]
    [InlineData("""{"EventId":" evt-2 ","EventType":"TicketCreated","ExternalTicketId":" qt-2 ","EventSequence":1,"QueueNumber":"B-001","CompanyId":"4","TicketTypeId":"57","CustomerPhone":"0500000123","OccurredAtUtc":"2026-11-01T14:00:00+04:00"}""", """{"eventId":"evt-2","eventType":"TicketCreated","externalTicketId":"qt-2","eventSequence":1,"queueNumber":"B-001","companyId":"4","ticketTypeId":"57","customerPhone":"0500000123","occurredAtUtc":"2026-11-01T10:00:00Z"}""", "4125077cbac35764b0d8da2625a90a5bf85b1c841d333c4b8a03ef925a633846")]
    [InlineData("""{"EventId":"evt-3","EventType":"TicketAssigned","ExternalTicketId":"qt-1","EventSequence":2,"AssignedUserId":"u-9","PerformedByUserId":"u-8","CounterId":"c-3","OccurredAtUtc":"2026-11-01T10:05:00.123Z"}""", """{"eventId":"evt-3","eventType":"TicketAssigned","externalTicketId":"qt-1","eventSequence":2,"assignedUserId":"u-9","performedByUserId":"u-8","counterId":"c-3","occurredAtUtc":"2026-11-01T10:05:00.123Z"}""", "494c826b9fdd25715929af02b24186b930be7530b2292ec16b857ebadd870d10")]
    [InlineData("""{"EventId":"evt-4","EventType":"TicketCommentAdded","ExternalTicketId":"qt-1","EventSequence":3,"Comment":"He said \"hello\"\nسلام & <b>x</b> ü","OccurredAtUtc":"2026-11-01T10:06:00.1234567Z"}""", """{"eventId":"evt-4","eventType":"TicketCommentAdded","externalTicketId":"qt-1","eventSequence":3,"comment":"He said \u0022hello\u0022\n\u0633\u0644\u0627\u0645 \u0026 \u003Cb\u003Ex\u003C/b\u003E \u00FC","occurredAtUtc":"2026-11-01T10:06:00.1234567Z"}""", "65abf4f23f9bd7664f9935764fa9b0e3e5381de5ee9ec2db11336ee9850767ad")]
    [InlineData("""{"EventId":"evt-5","EventType":"TicketClosed","ExternalTicketId":"qt-1","EventSequence":4,"Reason":"Served; ok=1","OccurredAtUtc":"2026-11-01T10:20:00"}""", """{"eventId":"evt-5","eventType":"TicketClosed","externalTicketId":"qt-1","eventSequence":4,"reason":"Served; ok=1","occurredAtUtc":"2026-11-01T10:20:00Z"}""", "a6c3131be5caace6f3cc4f734558a731a12593f411a80a511ca208d5ac4ba5a3")]
    [InlineData("""{"eventid":"evt-6","eventtype":"TicketCancelled","externalticketid":"qt-3","eventsequence":2,"reason":null,"occurredatutc":"2026-11-01T10:20:00Z"}""", """{"eventId":"evt-6","eventType":"TicketCancelled","externalTicketId":"qt-3","eventSequence":2,"occurredAtUtc":"2026-11-01T10:20:00Z"}""", "52c399740d7fb8dec2fc65c86f4bf7ff244ae653a526f899d5d264f108153d6d")]
    public void ARequestWithoutAName_HasExactlyTheCanonicalJsonAndHashItHadBeforeTheFieldExisted(string wireBody, string legacyCanonicalJson, string legacySha256)
    {
        var request = JsonSerializer.Deserialize<QueueingEventRequest>(wireBody, Web)!;

        Assert.Equal(legacyCanonicalJson, request.ToCanonicalJson());     // what is stored in Payload
        Assert.Equal(legacyCanonicalJson, request.ToIdentityJson());      // what the hash is taken over
        Assert.Equal(legacySha256, QueueingEvent.HashOf(request.ToIdentityJson()));
    }

    [Fact]
    public void TheNameIsLeftOutOfTheIdentity_ButEveryOtherFieldStillChangesIt()
    {
        var baseline = Created("qt-identity", eventId: "evt-identity");
        var named = baseline with { TicketTypeName = "Lease Renewal" };

        Assert.Equal(baseline.ToIdentityJson(), named.ToIdentityJson());
        Assert.NotEqual(baseline.ToCanonicalJson(), named.ToCanonicalJson());             // the stored payload does carry the name
        Assert.Contains("\"ticketTypeName\":\"Lease Renewal\"", named.ToCanonicalJson());
        Assert.DoesNotContain("ticketTypeName", baseline.ToCanonicalJson());

        var hash = QueueingEvent.HashOf(baseline.ToIdentityJson());
        foreach (var changed in new[]
                 {
                     baseline with { CompanyId = "company-z" },
                     baseline with { TicketTypeId = "type-z" },
                     baseline with { QueueNumber = "Z-999" },
                     baseline with { CustomerPhone = "+971500009999" },
                     baseline with { CustomerName = "Someone Else" },
                     baseline with { EventSequence = 2 },
                     baseline with { EventType = "TicketCancelled" },
                     baseline with { ExternalTicketId = "qt-other" },
                     baseline with { OccurredAtUtc = baseline.OccurredAtUtc!.Value.AddSeconds(1) },
                     baseline with { Comment = "x" },
                     baseline with { Reason = "x" },
                     baseline with { AssignedUserId = "u" }
                 })
        {
            Assert.NotEqual(hash, QueueingEvent.HashOf((changed with { TicketTypeName = "Lease Renewal" }).ToIdentityJson()));
        }
    }

    [Fact]
    public async Task AnEventStoredBeforeTheFieldExisted_IsStillADuplicate_NotAConflict_WhenRedeliveredWithOrWithoutAName()
    {
        // A row exactly as the previous code stored it: payload and hash both over the legacy canonical JSON (identityPayload not supplied).
        var id = NewTicketId();
        var original = Created(id, eventId: "evt-legacy-" + Guid.NewGuid().ToString("N"));
        var legacyRow = new QueueingEvent(original.EventId!, QueueingEventType.TicketCreated, id, 1, original.OccurredAtUtc!.Value, original.ToCanonicalJson(), DateTime.UtcNow);
        legacyRow.MarkProcessed(QueueingOutcomeCodes.TicketCreated, null, null, DateTime.UtcNow);
        await f.ReadAsync(async db => { db.QueueingEvents.Add(legacyRow); await db.SaveChangesAsync(); return 0; });

        var (plainHttp, plain) = await f.PostAsync(original);
        Assert.Equal(HttpStatusCode.OK, plainHttp.StatusCode);
        Assert.Equal(QueueingResultStatus.Duplicate, plain!.Status);

        var (namedHttp, named) = await f.PostAsync(original with { TicketTypeName = "A name the old sender never sent" });
        Assert.Equal(HttpStatusCode.OK, namedHttp.StatusCode);
        Assert.Equal(QueueingResultStatus.Duplicate, named!.Status);

        // ...while a genuinely different body under the same EventId is still caught.
        var (conflictHttp, conflict) = await f.PostAsync(original with { CompanyId = "another-company" });
        Assert.Equal(HttpStatusCode.Conflict, conflictHttp.StatusCode);
        Assert.Equal("EVENT_ID_REUSED", conflict!.Outcome);
        Assert.Equal(0, await f.CountTicketsAsync(id));                                   // none of the redeliveries created anything
    }

    [Fact]
    public async Task ChangingOnlyTheName_OfAStoredEvent_IsADuplicate_AndTheFirstNameStays()
    {
        var (company, department, general) = await SeedMappedCompanyWithGeneralAsync("Name Wins First");
        var id = NewTicketId();
        var original = Created(id, companyId: company, ticketTypeId: "svc-1", eventId: "evt-name-" + Guid.NewGuid().ToString("N")) with { TicketTypeName = "First Name" };

        Assert.Equal(HttpStatusCode.Created, (await f.PostAsync(original)).Http.StatusCode);
        var (http, body) = await f.PostAsync(original with { TicketTypeName = "Second Name" });

        Assert.Equal(HttpStatusCode.OK, http.StatusCode);
        Assert.Equal(QueueingResultStatus.Duplicate, body!.Status);
        Assert.Equal(1, await f.CountTicketsAsync(id));
        var link = (await f.FindLinkAsync((await f.FindTicketAsync(id))!.TicketId))!;
        Assert.Equal("First Name", link.ExternalTicketTypeName);
        Assert.Equal((department, general), ((await f.FindTicketAsync(id))!.CurrentDepartmentId, (await f.FindTicketAsync(id))!.RequestTypeId));
    }

    // ------------------------------------------------------------------ the service name is preserved and shown

    [Fact]
    public async Task AnUnmappedService_KeepsItsQueueingNameCompanyAndTypeId_AndIsClassifiedWithTheDepartmentsGeneral()
    {
        var (company, department, general) = await SeedMappedCompanyWithGeneralAsync("Mirror Dept");
        var id = NewTicketId();
        var request = Created(id, companyId: company, ticketTypeId: "svc-brand-new") with { TicketTypeName = "Lease Renewal" };

        var (http, body) = await f.PostAsync(request);

        Assert.Equal(HttpStatusCode.Created, http.StatusCode);
        var ticket = (await f.FindTicketAsync(id))!;
        Assert.Equal(TicketIntegrationSources.QueueingSystem, ticket.IntegrationSource);       // still distinguishable in reports
        Assert.Equal((department, general), (ticket.CurrentDepartmentId, ticket.RequestTypeId)); // internal classification
        Assert.Contains("Lease Renewal", ticket.RequestSummary);                                 // the summary names the service the customer queued for
        Assert.DoesNotContain("General", ticket.RequestSummary);

        var link = (await f.FindLinkAsync(ticket.TicketId))!;
        Assert.Equal((company, "svc-brand-new", "Lease Renewal"), (link.CompanyId, link.ExternalTicketTypeId, link.ExternalTicketTypeName));
        var audit = await f.Factory.GetAuditEntriesAsync(ticket.TicketId.ToString());
        Assert.Contains(audit, a => a.Action == "QueueingTicketCreated" && a.AfterValue!.Contains("TicketTypeName=Lease Renewal;"));
        Assert.Equal(body!.TicketId, ticket.TicketId);
    }

    [Fact]
    public async Task TicketDetails_ShowTheQueueingService_AndTheTimelineNamesIt()
    {
        var (company, _, _) = await SeedMappedCompanyWithGeneralAsync("Details Dept");
        var id = NewTicketId();
        await f.PostAsync(Created(id, companyId: company, ticketTypeId: "svc-9") with { TicketTypeName = "Tenancy Contract" });
        var ticket = (await f.FindTicketAsync(id))!;
        var manager = (await f.Factory.SeedEmployeeAsync(Roles.CsManager)).EmployeeId;

        using var scope = f.Factory.Services.CreateScope();
        var detail = await scope.ServiceProvider.GetRequiredService<TicketQueryAppService>().GetDetailAsync(manager, [Roles.CsManager], ticket.TicketId);

        Assert.Equal(TicketQueryOutcome.Success, detail.Outcome);
        Assert.Equal(TicketIntegrationSources.QueueingSystem, detail.Response!.IntegrationSource);
        Assert.Equal("Tenancy Contract", detail.Response.QueueingServiceName);
        Assert.Equal("svc-9", detail.Response.QueueingTicketTypeId);
        Assert.Equal(company, detail.Response.QueueingCompanyId);
        Assert.Contains("Tenancy Contract", detail.Response.RequestSummary);
        Assert.NotNull(detail.Response.RequestTypeId);                                           // the internal General classification is preserved
    }

    [Fact]
    public async Task WithoutAName_TheTicketTypeIdIsShownInstead_AndNothingFails()
    {
        var (company, _, _) = await SeedMappedCompanyWithGeneralAsync("No Name Dept");
        var id = NewTicketId();
        Assert.Equal(HttpStatusCode.Created, (await f.PostAsync(Created(id, companyId: company, ticketTypeId: "svc-77"))).Http.StatusCode);

        var ticket = (await f.FindTicketAsync(id))!;
        Assert.Contains("ticket type svc-77", ticket.RequestSummary);
        Assert.Null((await f.FindLinkAsync(ticket.TicketId))!.ExternalTicketTypeName);
    }

    [Fact]
    public async Task ANameOver200Characters_IsRejectedAsInvalid_AndNothingIsStored()
    {
        var request = Created(NewTicketId()) with { TicketTypeName = new string('x', 201) };
        var (http, body) = await f.PostAsync(request);

        Assert.Equal(HttpStatusCode.BadRequest, http.StatusCode);
        Assert.Contains("TicketTypeName", body!.Errors!.Keys);
        Assert.Null(await f.FindEventAsync(request.EventId!));
    }

    // ------------------------------------------------------------------ routing: company mapping mandatory, ticket-type mapping an optional override, no hard-coded ids

    [Fact]
    public async Task TwoCompanies_WithNoTicketTypeMappingsAtAll_RouteToTheirOwnDepartmentsGeneral_AndKeepTheirOwnServiceNames()
    {
        var (companyOne, departmentOne, generalOne) = await SeedMappedCompanyWithGeneralAsync("Company One Dept");
        var (companyTwo, departmentTwo, generalTwo) = await SeedMappedCompanyWithGeneralAsync("Company Two Dept");
        var first = NewTicketId();
        var second = NewTicketId();

        // the very same ticket type id in two companies means two different services
        Assert.Equal(HttpStatusCode.Created, (await f.PostAsync(Created(first, companyId: companyOne, ticketTypeId: "7") with { TicketTypeName = "Service A" })).Http.StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await f.PostAsync(Created(second, companyId: companyTwo, ticketTypeId: "7") with { TicketTypeName = "Service B" })).Http.StatusCode);

        var one = (await f.FindTicketAsync(first))!;
        var two = (await f.FindTicketAsync(second))!;
        Assert.Equal((departmentOne, generalOne), (one.CurrentDepartmentId, one.RequestTypeId));
        Assert.Equal((departmentTwo, generalTwo), (two.CurrentDepartmentId, two.RequestTypeId));
        Assert.Equal("Service A", (await f.FindLinkAsync(one.TicketId))!.ExternalTicketTypeName);
        Assert.Equal("Service B", (await f.FindLinkAsync(two.TicketId))!.ExternalTicketTypeName);
    }

    [Fact]
    public async Task AnExplicitTicketTypeMapping_StillOverridesGeneral_AndTheServiceNameIsStillKept()
    {
        var (company, _, general) = await SeedMappedCompanyWithGeneralAsync("Override Dept");
        var (overrideDepartment, overrideType) = await SeedRoutableTypeAsync("Override Target");
        await f.ReadAsync(async db => { db.QueueingTicketTypeMappings.Add(new QueueingTicketTypeMapping(company, "svc-special", overrideType, DateTime.UtcNow)); await db.SaveChangesAsync(); return 0; });

        var special = NewTicketId();
        var ordinary = NewTicketId();
        await f.PostAsync(Created(special, companyId: company, ticketTypeId: "svc-special") with { TicketTypeName = "Special Service" });
        await f.PostAsync(Created(ordinary, companyId: company, ticketTypeId: "svc-other") with { TicketTypeName = "Other Service" });

        var overridden = (await f.FindTicketAsync(special))!;
        Assert.Equal((overrideDepartment, overrideType), (overridden.CurrentDepartmentId, overridden.RequestTypeId));
        Assert.Equal("Special Service", (await f.FindLinkAsync(overridden.TicketId))!.ExternalTicketTypeName);
        Assert.Equal(general, (await f.FindTicketAsync(ordinary))!.RequestTypeId);
    }

    [Fact]
    public async Task AnExplicitMapping_WithoutACompanyMapping_IsHeld_UntilTheCompanyIsMapped()
    {
        var company = "company-explicit-only-" + Guid.NewGuid().ToString("N")[..6];
        var (department, requestType) = await SeedRoutableTypeAsync("Explicit Only");
        await f.ReadAsync(async db => { db.QueueingTicketTypeMappings.Add(new QueueingTicketTypeMapping(company, "t-1", requestType, DateTime.UtcNow)); await db.SaveChangesAsync(); return 0; });

        var id = NewTicketId();
        var (http, body) = await f.PostAsync(Created(id, companyId: company, ticketTypeId: "t-1"));

        Assert.Equal(HttpStatusCode.Accepted, http.StatusCode);                                  // held - never routed by an explicit mapping alone
        Assert.Equal(QueueingOutcomeCodes.CompanyNotMapped, body!.Outcome);
        Assert.Equal(0, await f.CountTicketsAsync(id));

        await f.ReadAsync(async db => { db.QueueingCompanyMappings.Add(new QueueingCompanyMapping(company, department, DateTime.UtcNow)); await db.SaveChangesAsync(); return 0; });
        await f.MakeRetriesDueAsync();
        await f.RetryAsync();

        Assert.Equal(1, await f.CountTicketsAsync(id));
        Assert.Equal(department, (await f.FindTicketAsync(id))!.CurrentDepartmentId);
    }

    [Fact]
    public async Task AnUnmappedCompany_IsHeld_WhateverTheTicketTypeAndNameSent_AndNoDefaultDepartmentIsUsed()
    {
        await SeedMappedCompanyWithGeneralAsync("Bystander Dept");                                // a department with a General exists - it must not be borrowed
        var id = NewTicketId();
        var (http, body) = await f.PostAsync(Created(id, companyId: "company-never-mapped-" + Guid.NewGuid().ToString("N")[..6], ticketTypeId: "svc-1") with { TicketTypeName = "Anything" });

        Assert.Equal(HttpStatusCode.Accepted, http.StatusCode);
        Assert.Equal(QueueingOutcomeCodes.CompanyNotMapped, body!.Outcome);
        Assert.Equal(0, await f.CountTicketsAsync(id));
    }

    [Fact]
    public async Task AMappedDepartmentWithNoGeneral_HoldsTheEvent_InsteadOfInventingARequestType()
    {
        var department = await f.Factory.CreateDepartmentAsync("No General " + Guid.NewGuid().ToString("N")[..6], "N" + Guid.NewGuid().ToString("N")[..8]);
        await f.Factory.CreateCategoryAsync("Something Else", department);
        await f.Factory.CreateRequestTypeAsync("Something Else", department);
        var company = "company-no-general-" + Guid.NewGuid().ToString("N")[..6];
        await f.ReadAsync(async db => { db.QueueingCompanyMappings.Add(new QueueingCompanyMapping(company, department, DateTime.UtcNow)); await db.SaveChangesAsync(); return 0; });

        var id = NewTicketId();
        var (http, body) = await f.PostAsync(Created(id, companyId: company, ticketTypeId: "svc-1") with { TicketTypeName = "Some Service" });

        Assert.Equal(HttpStatusCode.Accepted, http.StatusCode);
        Assert.Equal(QueueingOutcomeCodes.TicketTypeNotMapped, body!.Outcome);
        Assert.Contains("General", body.Detail);
        Assert.Equal(0, await f.CountTicketsAsync(id));
    }

    // ------------------------------------------------------------------ manual changes: classification is blocked on a queue ticket only

    [Fact]
    public async Task ManualReclassification_OfAQueueTicket_IsRefused_AndTheClassificationIsUntouched()
    {
        var (company, _, general) = await SeedMappedCompanyWithGeneralAsync("Reclassify Dept");
        var (otherDepartment, otherType) = await SeedRoutableTypeAsync("Reclassify Target");
        var id = NewTicketId();
        await f.PostAsync(Created(id, companyId: company, ticketTypeId: "svc-1"));
        var before = (await f.FindTicketAsync(id))!;
        var manager = (await f.Factory.SeedEmployeeAsync(Roles.CsManager)).EmployeeId;

        using var scope = f.Factory.Services.CreateScope();
        var classification = scope.ServiceProvider.GetRequiredService<TicketClassificationAppService>();
        var category = await f.ReadAsync(db => Task.FromResult(db.Categories.First(c => c.DepartmentId == otherDepartment).CategoryId));
        var result = await classification.ClassifyAsync(manager, [Roles.CsManager], before.TicketId, new ClassifyTicketRequestDto(category, before.PriorityId ?? 2, otherType, before.RowVersion));

        Assert.Equal(TicketMutationOutcome.QueueManagedTicket, result.Outcome);
        var after = (await f.FindTicketAsync(id))!;
        Assert.Equal((general, before.CategoryId, before.WorkflowTemplateId, before.PriorityId), (after.RequestTypeId, after.CategoryId, after.WorkflowTemplateId, after.PriorityId));
    }

    [Fact]
    public async Task TheClassificationGuard_NeverBlocksAnOrdinaryTicket_AndYieldsToTheAllowPolicy()
    {
        var ordinary = Ticket.CreateUnverified("TCK-GUARD-1", f.DepartmentId, 1, 2, "An ordinary TigerCS ticket", DateTime.UtcNow, null, null);
        Assert.False(QueueManagedTicketGuard.Blocks(ordinary, f.Options));                       // normal tickets: classification behaves exactly as before

        var (company, _, _) = await SeedMappedCompanyWithGeneralAsync("Guard Dept");
        var id = NewTicketId();
        await f.PostAsync(Created(id, companyId: company, ticketTypeId: "svc-1"));
        var queueTicket = (await f.FindTicketAsync(id))!;
        Assert.True(QueueManagedTicketGuard.Blocks(queueTicket, f.Options));
        f.Options.ManualChangePolicy = QueueingManualChangePolicy.Allow;
        try
        {
            Assert.False(QueueManagedTicketGuard.Blocks(queueTicket, f.Options));
        }
        finally
        {
            f.Options.ManualChangePolicy = QueueingManualChangePolicy.Block;
        }
    }

    // ------------------------------------------------------------------ helpers

    private async Task<(string CompanyId, int DepartmentId, int GeneralRequestTypeId)> SeedMappedCompanyWithGeneralAsync(string name)
    {
        var department = await f.Factory.CreateDepartmentAsync(name + " " + Guid.NewGuid().ToString("N")[..6], "M" + Guid.NewGuid().ToString("N")[..8]);
        await f.Factory.CreateCategoryAsync("General", department);
        var general = await f.Factory.CreateRequestTypeAsync("General", department);
        var company = "company-" + Guid.NewGuid().ToString("N")[..8];
        await f.ReadAsync(async db => { db.QueueingCompanyMappings.Add(new QueueingCompanyMapping(company, department, DateTime.UtcNow)); await db.SaveChangesAsync(); return 0; });
        return (company, department, general);
    }

    private async Task<(int DepartmentId, int RequestTypeId)> SeedRoutableTypeAsync(string name)
    {
        var department = await f.Factory.CreateDepartmentAsync(name + " " + Guid.NewGuid().ToString("N")[..6], "R" + Guid.NewGuid().ToString("N")[..8]);
        await f.Factory.CreateCategoryAsync(name, department);
        return (department, await f.Factory.CreateRequestTypeAsync(name, department));
    }
}
