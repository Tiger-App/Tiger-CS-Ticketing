using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using TigerCS.Api.Controllers;
using TigerCS.Application.Modules.IdentityAndAccess.Dto;
using TigerCS.Application.Modules.Ticketing.Dto;
using TigerCS.Domain.Modules.IdentityAndAccess;
using TigerCS.Domain.Modules.Ticketing;
using TigerCS.Infrastructure.Persistence;
using TigerCS.Tests.IdentityAndAccess.Integration;

namespace TigerCS.Tests.GenesysIntegration.Integration;

/// <summary>
/// Over real HTTP: the live transcript on <c>PATCH /api/genesys/tickets/{id}</c> (authentication, idempotency, reconciliation with the final
/// transcript, no handoff) and the Tickets "Unclassified" filter with Classify from Ticket Details' endpoint (department permissions, no
/// handoff created by being unclassified).
/// </summary>
public sealed class UnclassifiedAndLiveTranscriptEndpointTests : IDisposable
{
    private readonly TigerCsApiFactory factory = new();

    public void Dispose() => factory.Dispose();

    private async Task<HttpClient> ClientAsync(string role, int? departmentId = null) =>
        (await EmployeeClientAsync(role, departmentId)).Client;

    private async Task<(HttpClient Client, Guid EmployeeId)> EmployeeClientAsync(string role, int? departmentId = null)
    {
        var (username, password, employeeId) = await factory.SeedEmployeeAsync(role);
        if (departmentId is { } department)
        {
            await factory.AssignPrimaryDepartmentAsync(employeeId, department);
        }

        var client = factory.CreateClient();
        var login = await (await client.PostAsJsonAsync("/api/auth/login", new LoginRequestDto(username, password)))
            .Content.ReadFromJsonAsync<LoginResponseDto>();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", login!.AccessToken);
        return (client, employeeId);
    }

    private async Task<GenesysInquiryAcceptedResponse> CreateTicketAsync(HttpClient genesys, int departmentId, string conversationId, int? requestTypeId = null)
    {
        var response = await genesys.PostAsJsonAsync("/api/genesys/tickets", new GenesysInquiryRequest(
            conversationId, "LiveChat", CustomerPhone: "+971500000011", CustomerName: "Ahmed Ali", DepartmentId: departmentId,
            RequestType: requestTypeId is { } id ? new GenesysRequestTypePart(id) : null));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<GenesysInquiryAcceptedResponse>())!;
    }

    private static GenesysTranscriptMessageRequest Msg(string sender, string body, DateTime at, string? id) =>
        new(sender, at, body, SenderName: sender, ExternalMessageId: id);

    private async Task<List<TicketInteractionMessage>> StoredMessagesAsync(string conversationId)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TigerCsDbContext>();
        var interaction = await db.TicketInteractions.AsNoTracking().SingleAsync(i => i.GenesysConversationId == conversationId);
        return await db.TicketInteractionMessages.AsNoTracking()
            .Where(m => m.TicketInteractionId == interaction.TicketInteractionId).OrderBy(m => m.Sequence).ToListAsync();
    }

    // ---- live transcript ----

    [Fact]
    public async Task LiveTranscript_RequiresAnAuthenticatedServiceAccount()
    {
        await factory.SeedPrioritiesAsync();
        var departmentId = await factory.CreateDepartmentAsync("Live " + Guid.NewGuid(), Guid.NewGuid().ToString("N")[..8]);
        var genesys = await ClientAsync(Roles.CsAgent);
        var conversationId = Guid.NewGuid().ToString();
        var ticket = await CreateTicketAsync(genesys, departmentId, conversationId);
        var body = new GenesysTicketUpdateRequest(conversationId, Transcript: [Msg("Customer", "hi", DateTime.UtcNow, "m-1")]);

        var anonymous = await factory.CreateClient().PatchAsJsonAsync($"/api/genesys/tickets/{ticket.TicketId}", body);
        var departmentEmployee = await (await ClientAsync(Roles.DepartmentEmployee, departmentId)).PatchAsJsonAsync($"/api/genesys/tickets/{ticket.TicketId}", body);

        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, departmentEmployee.StatusCode);
        Assert.Empty(await StoredMessagesAsync(conversationId));
    }

    [Fact]
    public async Task LiveTranscript_IsIdempotentOverHttp_AndTheFinalTranscriptReconcilesWithoutDuplicates_NorAHandoff()
    {
        await factory.SeedPrioritiesAsync();
        var departmentId = await factory.CreateDepartmentAsync("Live " + Guid.NewGuid(), Guid.NewGuid().ToString("N")[..8]);
        var genesys = await ClientAsync(Roles.CsAgent);
        var conversationId = Guid.NewGuid().ToString();
        var ticket = await CreateTicketAsync(genesys, departmentId, conversationId);
        var t0 = DateTime.UtcNow.AddMinutes(-20);
        var url = $"/api/genesys/tickets/{ticket.TicketId}";

        GenesysTicketUpdateRequest Live(params GenesysTranscriptMessageRequest[] m) => new(conversationId, Transcript: m);
        var first = Live(Msg("Customer", "hi", t0, "m-1"), Msg("VirtualAgent", "hello", t0.AddMinutes(1), "m-2"));

        var one = await genesys.PatchAsJsonAsync(url, first);
        var retry = await genesys.PatchAsJsonAsync(url, first);
        Assert.Equal(HttpStatusCode.OK, one.StatusCode);
        Assert.Equal(HttpStatusCode.OK, retry.StatusCode);
        Assert.Equal(2, (await retry.Content.ReadFromJsonAsync<GenesysTicketUpdateResponse>())!.TranscriptMessageCount);

        var end = await genesys.PatchAsJsonAsync(url, new GenesysTicketUpdateRequest(
            conversationId,
            Ended: new GenesysConversationEndPart(t0.AddMinutes(5), "CustomerDisconnect",
            [
                Msg("Customer", "hi", t0, "m-1"), Msg("VirtualAgent", "hello", t0.AddMinutes(1), "m-2"), Msg("Customer", "thanks", t0.AddMinutes(4), "m-3")
            ])));
        var ended = (await end.Content.ReadFromJsonAsync<GenesysTicketUpdateResponse>())!;

        Assert.True(ended.ConversationEnded);
        Assert.Equal(3, ended.TranscriptMessageCount);
        Assert.Equal(["hi", "hello", "thanks"], (await StoredMessagesAsync(conversationId)).Select(m => m.Body));
        Assert.Null(ended.HandoffStatus);
        var stored = await factory.GetTicketAsync(ticket.TicketId);
        Assert.Null(stored!.FirstHumanResponseAtUtc);
        Assert.Equal(TicketStatus.Open, stored.TicketStatus);
    }

    [Fact]
    public async Task LiveTranscript_WithAnUnknownSender_Is400_AndWritesNothing()
    {
        await factory.SeedPrioritiesAsync();
        var departmentId = await factory.CreateDepartmentAsync("Live " + Guid.NewGuid(), Guid.NewGuid().ToString("N")[..8]);
        var genesys = await ClientAsync(Roles.CsAgent);
        var conversationId = Guid.NewGuid().ToString();
        var ticket = await CreateTicketAsync(genesys, departmentId, conversationId);

        var response = await genesys.PatchAsJsonAsync($"/api/genesys/tickets/{ticket.TicketId}", new GenesysTicketUpdateRequest(
            conversationId, Transcript: [Msg("Customer", "ok", DateTime.UtcNow, "m-1"), Msg("Robot", "?", DateTime.UtcNow, "m-2")]));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(await StoredMessagesAsync(conversationId));
    }

    // ---- Unclassified filter + classify ----

    [Fact]
    public async Task UnclassifiedFilter_ListsOnlyTicketsWithNoRequestType_WithinTheCallersDepartments_AndCreatesNoHandoff()
    {
        await factory.SeedPrioritiesAsync();
        var deptA = await factory.CreateDepartmentAsync("Unclassified A " + Guid.NewGuid(), Guid.NewGuid().ToString("N")[..8]);
        var deptB = await factory.CreateDepartmentAsync("Unclassified B " + Guid.NewGuid(), Guid.NewGuid().ToString("N")[..8]);
        var requestTypeA = await factory.CreateRequestTypeAsync("Handover " + Guid.NewGuid(), deptA);
        var genesys = await ClientAsync(Roles.CsAgent);

        var unclassifiedA = await CreateTicketAsync(genesys, deptA, Guid.NewGuid().ToString());
        var classifiedA = await CreateTicketAsync(genesys, deptA, Guid.NewGuid().ToString(), requestTypeA);
        var unclassifiedB = await CreateTicketAsync(genesys, deptB, Guid.NewGuid().ToString());

        // A department employee sees only tickets ASSIGNED to them (not the department's queue): assign them the unclassified and the classified one.
        var (memberOfA, memberOfAId) = await EmployeeClientAsync(Roles.DepartmentEmployee, deptA);
        await factory.AssignOwnerAsync(unclassifiedA.TicketId, memberOfAId);
        await factory.AssignOwnerAsync(classifiedA.TicketId, memberOfAId);
        var filtered = await memberOfA.GetFromJsonAsync<TicketListResultDto>("/api/tickets?unclassified=true&pageSize=100");
        var unfiltered = await memberOfA.GetFromJsonAsync<TicketListResultDto>("/api/tickets?pageSize=100");

        var ids = filtered!.Items.Select(t => t.TicketId).ToList();
        Assert.Contains(unclassifiedA.TicketId, ids);
        Assert.DoesNotContain(classifiedA.TicketId, ids);          // has a request type
        Assert.DoesNotContain(unclassifiedB.TicketId, ids);        // not assigned to them (another department): never visible
        Assert.Contains(classifiedA.TicketId, unfiltered!.Items.Select(t => t.TicketId));

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TigerCsDbContext>();
        Assert.Empty(await db.TicketAgentHandoffs.AsNoTracking().ToListAsync());      // being unclassified is not a request for a human
    }

    [Fact]
    public async Task AnAssignedDepartmentEmployee_ClassifiesFromTicketDetails_AndAnOutsiderCannot()
    {
        await factory.SeedPrioritiesAsync();
        var deptA = await factory.CreateDepartmentAsync("Classify A " + Guid.NewGuid(), Guid.NewGuid().ToString("N")[..8]);
        var deptB = await factory.CreateDepartmentAsync("Classify B " + Guid.NewGuid(), Guid.NewGuid().ToString("N")[..8]);
        var requestTypeId = await factory.CreateRequestTypeAsync("Handover " + Guid.NewGuid(), deptA);
        var categoryId = await factory.CreateCategoryAsync("Handover " + Guid.NewGuid(), deptA);
        var genesys = await ClientAsync(Roles.CsAgent);
        var ticket = await CreateTicketAsync(genesys, deptA, Guid.NewGuid().ToString());

        var outsider = await ClientAsync(Roles.DepartmentEmployee, deptB);
        var (member, memberId) = await EmployeeClientAsync(Roles.DepartmentEmployee, deptA);
        await factory.AssignOwnerAsync(ticket.TicketId, memberId);   // classification from Ticket Details needs the ticket to be theirs
        var detail = await member.GetFromJsonAsync<TicketDetailDto>($"/api/tickets/{ticket.TicketId}");
        var request = new ClassifyTicketRequestDto(categoryId, detail!.PriorityId!.Value, requestTypeId, Convert.FromBase64String(detail.RowVersion));

        var refused = await outsider.PostAsJsonAsync($"/api/tickets/{ticket.TicketId}/classification", request);
        Assert.NotEqual(HttpStatusCode.OK, refused.StatusCode);

        var accepted = await member.PostAsJsonAsync($"/api/tickets/{ticket.TicketId}/classification", request);
        Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
        var classified = (await accepted.Content.ReadFromJsonAsync<TicketDetailDto>())!;
        Assert.Equal(categoryId, classified.CategoryId);
        Assert.Equal(requestTypeId, classified.RequestTypeId);

        var stillListed = await member.GetFromJsonAsync<TicketListResultDto>("/api/tickets?unclassified=true&pageSize=100");
        Assert.DoesNotContain(ticket.TicketId, stillListed!.Items.Select(t => t.TicketId));

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TigerCsDbContext>();
        Assert.Empty(await db.TicketAgentHandoffs.AsNoTracking().Where(h => h.TicketId == ticket.TicketId).ToListAsync());
    }
}
