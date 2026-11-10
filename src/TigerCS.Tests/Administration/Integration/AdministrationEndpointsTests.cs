using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using TigerCS.Application.Modules.Administration.Dto;
using TigerCS.Application.Modules.IdentityAndAccess.Dto;
using TigerCS.Application.Modules.Ticketing.Dto;
using TigerCS.Domain.Modules.IdentityAndAccess;
using TigerCS.Domain.Modules.SlaAndEscalation;
using TigerCS.Domain.Modules.Ticketing;
using TigerCS.Domain.Modules.WorkflowConfiguration;
using TigerCS.Infrastructure.Persistence;
using TigerCS.Tests.IdentityAndAccess.Integration;

namespace TigerCS.Tests.Administration.Integration;

/// <summary>
/// End-to-end administration through the real Api host: users, departments,
/// request types, the Workflow Designer's versioning lifecycle, and ticket
/// pinning to the version that was active at creation.
/// </summary>
public class AdministrationEndpointsTests : IClassFixture<TigerCsApiFactory>
{
    private readonly TigerCsApiFactory _factory;

    public AdministrationEndpointsTests(TigerCsApiFactory factory) => _factory = factory;

    private async Task<(HttpClient Client, Guid EmployeeId)> CreateClientAsync(string role)
    {
        var (username, password, employeeId) = await _factory.SeedEmployeeAsync(role);
        var client = _factory.CreateClient();
        var loginResponse = await client.PostAsJsonAsync("/api/auth/login", new LoginRequestDto(username, password));
        loginResponse.EnsureSuccessStatusCode();
        var login = await loginResponse.Content.ReadFromJsonAsync<LoginResponseDto>();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", login!.AccessToken);
        return (client, employeeId);
    }

    private static async Task<T> ReadAsync<T>(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, $"{(int)response.StatusCode} {response.RequestMessage?.Method} {response.RequestMessage?.RequestUri}: {body}");
        return (await response.Content.ReadFromJsonAsync<T>())!;
    }

    private static string Unique(string prefix) => $"{prefix} {Guid.NewGuid():N}"[..Math.Min(40, prefix.Length + 33)];

    // ---------------------------------------------------------------- users

    [Fact]
    public async Task Users_CreateListGetEditRolesMembershipAndDeactivate()
    {
        var (admin, _) = await CreateClientAsync(Roles.SystemAdministrator);
        var departmentId = await _factory.CreateDepartmentAsync(Unique("Collections"), Guid.NewGuid().ToString("N")[..8]);
        var userName = $"u{Guid.NewGuid():N}"[..20];

        var created = await ReadAsync<AdminUserDto>(await admin.PostAsJsonAsync("/api/admin/users",
            new CreateUserRequestDto(userName, $"{userName}@example.test", "Created Via Admin", false, "Str0ng-Pass!", [Roles.DepartmentEmployee], departmentId)));
        Assert.Equal([Roles.DepartmentEmployee], created.Roles);
        Assert.True(Assert.Single(created.Departments).IsPrimary);

        // Identity's password policy is the one that decides.
        var weak = await admin.PostAsJsonAsync("/api/admin/users",
            new CreateUserRequestDto($"w{Guid.NewGuid():N}"[..20], null, "Weak", false, "short", [], null));
        Assert.Equal(HttpStatusCode.BadRequest, weak.StatusCode);

        var listed = await ReadAsync<AdminUserListDto>(await admin.GetAsync($"/api/admin/users?search={userName}"));
        Assert.Contains(listed.Items, u => u.EmployeeId == created.EmployeeId);

        var fetched = await ReadAsync<AdminUserDto>(await admin.GetAsync($"/api/admin/users/{created.EmployeeId}"));
        Assert.Equal("Created Via Admin", fetched.DisplayName);

        var edited = await ReadAsync<AdminUserDto>(await admin.PutAsJsonAsync($"/api/admin/users/{created.EmployeeId}/profile",
            new UpdateUserProfileRequestDto("Renamed Via Admin", "renamed@example.test", true)));
        Assert.Equal("Renamed Via Admin", edited.DisplayName);
        Assert.Equal("renamed@example.test", edited.Email);

        var roles = await ReadAsync<AdminUserDto>(await admin.PutAsJsonAsync($"/api/admin/users/{created.EmployeeId}/roles",
            new SetUserRolesRequestDto([Roles.DepartmentHead, Roles.ReportingUser])));
        Assert.Equal([Roles.DepartmentHead, Roles.ReportingUser], roles.Roles);

        var secondDepartment = await _factory.CreateDepartmentAsync(Unique("Registration"), Guid.NewGuid().ToString("N")[..8]);
        var membership = await ReadAsync<AdminUserDto>(await admin.PostAsJsonAsync($"/api/admin/users/{created.EmployeeId}/departments",
            new AddDepartmentMembershipRequestDto(secondDepartment, false)));
        Assert.Equal(2, membership.Departments.Count);

        var removePrimary = await admin.DeleteAsync($"/api/admin/users/{created.EmployeeId}/departments/{departmentId}");
        Assert.Equal(HttpStatusCode.Conflict, removePrimary.StatusCode);
        var removed = await ReadAsync<AdminUserDto>(await admin.DeleteAsync($"/api/admin/users/{created.EmployeeId}/departments/{secondDepartment}"));
        Assert.Single(removed.Departments);

        var deactivated = await ReadAsync<AdminUserDto>(await admin.PatchAsJsonAsync($"/api/admin/users/{created.EmployeeId}/activation",
            new SetActiveRequestDto(false, "left")));
        Assert.False(deactivated.IsActive);

        // Deactivation never deletes: the employee row and its account remain.
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TigerCsDbContext>();
        Assert.NotNull(await db.Employees.FindAsync(created.EmployeeId));
        Assert.NotNull(await db.Users.FindAsync(created.EmployeeId));
        Assert.Contains(await db.AuditEntries.Where(a => a.EntityId == created.EmployeeId.ToString()).ToListAsync(), a => a.Action == "AdminDeactivateUser");
    }

    [Fact]
    public async Task Users_TransferDepartments_AreSeparateFromMembership_ValidatedAuditedAndShownOnMe()
    {
        var (admin, _) = await CreateClientAsync(Roles.SystemAdministrator);
        var home = await _factory.CreateDepartmentAsync(Unique("Home"), Guid.NewGuid().ToString("N")[..8]);
        var registration = await _factory.CreateDepartmentAsync(Unique("Registration"), Guid.NewGuid().ToString("N")[..8]);
        var collections = await _factory.CreateDepartmentAsync(Unique("Collections"), Guid.NewGuid().ToString("N")[..8]);
        var userName = $"t{Guid.NewGuid():N}"[..20];
        var created = await ReadAsync<AdminUserDto>(await admin.PostAsJsonAsync("/api/admin/users",
            new CreateUserRequestDto(userName, $"{userName}@example.test", "Transfer Test", false, "Str0ng-Pass!", [Roles.CsSupervisor], home)));
        var url = $"/api/admin/users/{created.EmployeeId}/transfer-departments";

        Assert.Empty((await ReadAsync<UserTransferDepartmentsDto>(await admin.GetAsync(url))).DepartmentIds);          // no explicit list yet

        var saved = await ReadAsync<UserTransferDepartmentsDto>(await admin.PutAsJsonAsync(url,
            new SetUserTransferDepartmentsRequestDto([collections, registration, collections])));
        Assert.Equal(new[] { registration, collections }.Order(), saved.DepartmentIds);
        Assert.Equal(saved.DepartmentIds, (await ReadAsync<UserTransferDepartmentsDto>(await admin.GetAsync(url))).DepartmentIds);

        // Separate from membership: the user is still a member of their home department only, and nothing was granted by viewing it.
        var fetched = await ReadAsync<AdminUserDto>(await admin.GetAsync($"/api/admin/users/{created.EmployeeId}"));
        Assert.Equal([home], fetched.Departments.Select(d => d.DepartmentId));
        Assert.DoesNotContain(home, saved.DepartmentIds);

        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PutAsJsonAsync(url, new SetUserTransferDepartmentsRequestDto([987654]))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await admin.PutAsJsonAsync($"/api/admin/users/{Guid.NewGuid()}/transfer-departments", new SetUserTransferDepartmentsRequestDto([registration]))).StatusCode);

        // Only an administrator manages the list.
        var (manager, _) = await CreateClientAsync(Roles.CsManager);
        Assert.Equal(HttpStatusCode.Forbidden, (await manager.GetAsync(url)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await manager.PutAsJsonAsync(url, new SetUserTransferDepartmentsRequestDto([registration]))).StatusCode);

        // The user's own /me carries the list, so the Transfer form can offer exactly those departments.
        var user = _factory.CreateClient();
        var login = await ReadAsync<LoginResponseDto>(await user.PostAsJsonAsync("/api/auth/login", new LoginRequestDto(userName, "Str0ng-Pass!")));
        user.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", login.AccessToken);
        var me = await ReadAsync<CurrentUserResponseDto>(await user.GetAsync("/api/users/me"));
        Assert.Equal(saved.DepartmentIds, me.TransferDepartmentIds);

        Assert.Empty((await ReadAsync<UserTransferDepartmentsDto>(await admin.PutAsJsonAsync(url, new SetUserTransferDepartmentsRequestDto([])))).DepartmentIds);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TigerCsDbContext>();
        var audits = await db.AuditEntries.Where(a => a.EntityId == created.EmployeeId.ToString() && a.Action == "AdminSetTransferDepartments").ToListAsync();
        Assert.Equal(2, audits.Count);                                                                             // the save and the clear; the failed attempts wrote nothing
        Assert.Empty(await db.UserTransferDepartments.Where(t => t.EmployeeId == created.EmployeeId).ToListAsync());
    }

    // ------------------------------------------------------------- channels

    [Fact]
    public async Task Channels_ListAddEditActivationAndDuplicateCode_ThroughTheRealHost()
    {
        var (admin, _) = await CreateClientAsync(Roles.SystemAdministrator);
        var code = "CH" + Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();

        // The seeded catalogue (the approved production channels, fixed ids) is listed with status.
        var seeded = await ReadAsync<List<AdminChannelDto>>(await admin.GetAsync("/api/admin/channels"));
        Assert.Contains(seeded, c => c.ChannelId == WellKnownChannels.Phone && c.Code == "PHONE" && c.IsActive && c.RequiresPhone);
        Assert.Contains(seeded, c => c.ChannelId == WellKnownChannels.FaceToFaceKiosk && !c.RequiresPhone);

        var created = await ReadAsync<AdminChannelDto>(await admin.PostAsJsonAsync("/api/admin/channels",
            new SaveChannelRequestDto("Email " + code, code, RequiresPhone: false, IsGenesysEnabled: false, DisplayOrder: 50)));
        Assert.Equal(code, created.Code);
        Assert.True(created.IsActive);
        Assert.Equal(50, created.DisplayOrder);

        // Duplicate code (any case) is a 400 with a validation problem, on create and on edit.
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsJsonAsync("/api/admin/channels",
            new SaveChannelRequestDto("Another", code.ToLowerInvariant(), true, false, 0))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PutAsJsonAsync($"/api/admin/channels/{created.ChannelId}",
            new SaveChannelRequestDto("Email", "phone", false, false, 50))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsJsonAsync("/api/admin/channels",
            new SaveChannelRequestDto("", "", true, false, 0))).StatusCode);

        var edited = await ReadAsync<AdminChannelDto>(await admin.PutAsJsonAsync($"/api/admin/channels/{created.ChannelId}",
            new SaveChannelRequestDto("E-mail " + code, code, RequiresPhone: true, IsGenesysEnabled: true, DisplayOrder: 51)));
        Assert.Equal("E-mail " + code, edited.Name);
        Assert.True(edited.RequiresPhone);
        Assert.True(edited.IsGenesysEnabled);
        Assert.Equal(51, edited.DisplayOrder);

        var fetched = await ReadAsync<AdminChannelDto>(await admin.GetAsync($"/api/admin/channels/{created.ChannelId}"));
        Assert.Equal(edited, fetched);

        // Offered to Create Ticket while active, in display order …
        var offered = await ReadAsync<List<ChannelDto>>(await admin.GetAsync("/api/channels"));
        Assert.Contains(offered, c => c.ChannelId == created.ChannelId);
        Assert.Equal(offered.OrderBy(c => c.DisplayOrder).ThenBy(c => c.Name, StringComparer.Ordinal).Select(c => c.ChannelId), offered.Select(c => c.ChannelId));

        // … and not once deactivated, while still resolvable for history.
        var deactivated = await ReadAsync<AdminChannelDto>(await admin.PatchAsJsonAsync($"/api/admin/channels/{created.ChannelId}/activation",
            new SetActiveRequestDto(false, "unused")));
        Assert.False(deactivated.IsActive);
        Assert.DoesNotContain(await ReadAsync<List<ChannelDto>>(await admin.GetAsync("/api/channels")), c => c.ChannelId == created.ChannelId);
        Assert.Contains(await ReadAsync<List<ChannelDto>>(await admin.GetAsync("/api/channels?activeOnly=false")), c => c.ChannelId == created.ChannelId && !c.IsActive);
        Assert.Contains(await ReadAsync<List<AdminChannelDto>>(await admin.GetAsync("/api/admin/channels")), c => c.ChannelId == created.ChannelId && !c.IsActive);
        Assert.DoesNotContain(await ReadAsync<List<AdminChannelDto>>(await admin.GetAsync("/api/admin/channels?includeInactive=false")), c => c.ChannelId == created.ChannelId);

        var reactivated = await ReadAsync<AdminChannelDto>(await admin.PatchAsJsonAsync($"/api/admin/channels/{created.ChannelId}/activation",
            new SetActiveRequestDto(true)));
        Assert.True(reactivated.IsActive);

        // Never deleted: no DELETE endpoint, and the row is still there.
        Assert.Equal(HttpStatusCode.MethodNotAllowed, (await admin.DeleteAsync($"/api/admin/channels/{created.ChannelId}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await admin.GetAsync("/api/admin/channels/250")).StatusCode);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TigerCsDbContext>();
        Assert.NotNull(await db.Channels.FindAsync(created.ChannelId));
        var audit = await db.AuditEntries.Where(a => a.EntityType == "Channel" && a.EntityId == created.ChannelId.ToString()).ToListAsync();
        Assert.Contains(audit, a => a.Action == "AdminCreateChannel");
        Assert.Contains(audit, a => a.Action == "AdminUpdateChannel");
        Assert.Contains(audit, a => a.Action == "AdminDeactivateChannel");
        Assert.Contains(audit, a => a.Action == "AdminActivateChannel");
    }

    // ---------------------------------------------------------- departments

    [Fact]
    public async Task Department_coverage_is_reported_for_active_departments_only()
    {
        var (admin, _) = await CreateClientAsync(Roles.SystemAdministrator);
        var code = Guid.NewGuid().ToString("N")[..6].ToUpperInvariant();
        var created = await ReadAsync<AdminDepartmentDetailDto>(await admin.PostAsJsonAsync("/api/admin/departments", new SaveDepartmentRequestDto(Unique("Coverage"), code)));

        var empty = (await ReadAsync<List<TigerCS.Application.Modules.Administration.Services.DepartmentCoverageLine>>(await admin.GetAsync("/api/admin/departments/coverage")))
            .Single(c => c.DepartmentId == created.DepartmentId);
        Assert.Equal((0, TigerCS.Application.Modules.Administration.Services.DepartmentStaffingStatus.Unstaffed), (empty.ActiveEmployeeCount, empty.Status));

        var (_, _, memberId) = await _factory.SeedEmployeeAsync(Roles.DepartmentEmployee);
        await admin.PostAsJsonAsync($"/api/admin/departments/{created.DepartmentId}/members", new AddDepartmentMemberRequestDto(memberId, false));
        var staffed = (await ReadAsync<List<TigerCS.Application.Modules.Administration.Services.DepartmentCoverageLine>>(await admin.GetAsync("/api/admin/departments/coverage")))
            .Single(c => c.DepartmentId == created.DepartmentId);
        Assert.Equal((1, 1, TigerCS.Application.Modules.Administration.Services.DepartmentStaffingStatus.Staffed),
            (staffed.ActiveEmployeeCount, staffed.AssignableEmployeeCount, staffed.Status));

        await admin.PatchAsJsonAsync($"/api/admin/departments/{created.DepartmentId}/activation", new SetActiveRequestDto(false, null));
        Assert.DoesNotContain(
            await ReadAsync<List<TigerCS.Application.Modules.Administration.Services.DepartmentCoverageLine>>(await admin.GetAsync("/api/admin/departments/coverage")),
            c => c.DepartmentId == created.DepartmentId);
    }

    [Fact]
    public async Task Departments_CreateListGetEditMembersAndDeactivate()
    {
        var (admin, _) = await CreateClientAsync(Roles.SystemAdministrator);
        var name = Unique("Handover");
        var code = Guid.NewGuid().ToString("N")[..6].ToUpperInvariant();

        var created = await ReadAsync<AdminDepartmentDetailDto>(await admin.PostAsJsonAsync("/api/admin/departments", new SaveDepartmentRequestDto(name, code)));
        Assert.Equal(code, created.Code);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsJsonAsync("/api/admin/departments", new SaveDepartmentRequestDto(name, "ZZ"))).StatusCode);

        var listed = await ReadAsync<List<AdminDepartmentDto>>(await admin.GetAsync("/api/admin/departments"));
        Assert.Contains(listed, d => d.DepartmentId == created.DepartmentId);

        var renamed = await ReadAsync<AdminDepartmentDetailDto>(await admin.PutAsJsonAsync($"/api/admin/departments/{created.DepartmentId}",
            new SaveDepartmentRequestDto(name + " Desk", code)));
        Assert.Equal(name + " Desk", renamed.Name);

        var (_, _, memberId) = await _factory.SeedEmployeeAsync(Roles.DepartmentEmployee);
        var withMember = await ReadAsync<AdminDepartmentDetailDto>(await admin.PostAsJsonAsync($"/api/admin/departments/{created.DepartmentId}/members",
            new AddDepartmentMemberRequestDto(memberId, true)));
        Assert.Contains(withMember.Members, m => m.EmployeeId == memberId && m.IsPrimary);
        Assert.Contains((await ReadAsync<PagedResultDto<DepartmentUserDto>>(await admin.GetAsync($"/api/departments/{created.DepartmentId}/users"))).Items,
            m => m.EmployeeId == memberId);

        var fetched = await ReadAsync<AdminDepartmentDetailDto>(await admin.GetAsync($"/api/admin/departments/{created.DepartmentId}"));
        Assert.Single(fetched.Members);

        var withoutMember = await ReadAsync<AdminDepartmentDetailDto>(await admin.DeleteAsync($"/api/admin/departments/{created.DepartmentId}/members/{memberId}"));
        Assert.Empty(withoutMember.Members);

        var deactivated = await ReadAsync<AdminDepartmentDetailDto>(await admin.PatchAsJsonAsync($"/api/admin/departments/{created.DepartmentId}/activation",
            new SetActiveRequestDto(false)));
        Assert.False(deactivated.IsActive);

        // Excluded from new-work pickers, still resolvable by name for history.
        var active = await ReadAsync<List<DepartmentDto>>(await admin.GetAsync("/api/departments"));
        Assert.DoesNotContain(active, d => d.DepartmentId == created.DepartmentId);
        var all = await ReadAsync<List<DepartmentDto>>(await admin.GetAsync("/api/departments?activeOnly=false"));
        Assert.Equal(name + " Desk", all.Single(d => d.DepartmentId == created.DepartmentId).Name);

        // No DELETE endpoint exists for a department, and the model restricts deletion.
        Assert.Equal(HttpStatusCode.MethodNotAllowed, (await admin.DeleteAsync($"/api/admin/departments/{created.DepartmentId}")).StatusCode);
    }

    // -------------------------------------------------------- request types

    [Fact]
    public async Task RequestTypes_CreateListGetEditConfigureAndDeactivate()
    {
        var (admin, _) = await CreateClientAsync(Roles.SystemAdministrator);
        await _factory.SeedPrioritiesAsync();
        var collections = await _factory.CreateDepartmentAsync(Unique("Collections"), Guid.NewGuid().ToString("N")[..8]);
        var accounting = await _factory.CreateDepartmentAsync(Unique("Accounting"), Guid.NewGuid().ToString("N")[..8]);
        var workflow = await CreatePublishedWorkflowAsync(admin, Unique("Send Receipts Workflow"));

        var created = await ReadAsync<AdminRequestTypeDetailDto>(await admin.PostAsJsonAsync("/api/admin/request-types",
            new SaveRequestTypeRequestDto(collections, "Send Receipts", workflow.WorkflowId, (byte)PriorityLevel.Medium, false, false, true, true)));
        Assert.Equal(1, created.Workflow.ActiveVersionNumber);

        var listed = await ReadAsync<List<AdminRequestTypeSummaryDto>>(await admin.GetAsync($"/api/admin/request-types?departmentId={collections}"));
        Assert.Contains(listed, r => r.RequestTypeId == created.RequestTypeId);
        Assert.DoesNotContain(await ReadAsync<List<AdminRequestTypeSummaryDto>>(await admin.GetAsync($"/api/admin/request-types?departmentId={accounting}")),
            r => r.RequestTypeId == created.RequestTypeId);

        var fetched = await ReadAsync<AdminRequestTypeDetailDto>(await admin.GetAsync($"/api/admin/request-types/{created.RequestTypeId}"));
        Assert.Equal("Send Receipts", fetched.Name);

        var edited = await ReadAsync<AdminRequestTypeDetailDto>(await admin.PutAsJsonAsync($"/api/admin/request-types/{created.RequestTypeId}",
            new SaveRequestTypeRequestDto(collections, "Send Receipts", workflow.WorkflowId, (byte)PriorityLevel.Medium, true, false, true, true)));
        Assert.True(edited.AllowAgentPriorityChange);

        var (_, _, memberId) = await _factory.SeedEmployeeAsync(Roles.DepartmentEmployee);
        await _factory.AssignPrimaryDepartmentAsync(memberId, collections);
        var rule = await ReadAsync<AdminRequestTypeDetailDto>(await admin.PutAsJsonAsync($"/api/admin/request-types/{created.RequestTypeId}/assignment-rule",
            new SaveAssignmentRuleRequestDto(AssignmentMode.SpecificEmployee, memberId, null, null)));
        Assert.Equal(AssignmentMode.SpecificEmployee, rule.AssignmentRule!.Mode);

        var approval = await ReadAsync<AdminRequestTypeDetailDto>(await admin.PutAsJsonAsync(
            $"/api/admin/request-types/{created.RequestTypeId}/approval-requirements/AccountingApproval",
            new SaveApprovalRequirementRequestDto(ApprovalTargetKind.Department, accounting, null, null)));
        Assert.Equal(ApprovalType.AccountingApproval, Assert.Single(approval.ApprovalRequirements).ApprovalType);

        // ReopenApproval is configurable here — this is the runtime
        // configuration mechanism, and it accepts every defined type. Being
        // absent from the Workflow Designer's step dropdown does not make a
        // type unconfigurable; the two answer different questions.
        var reopenApproval = await ReadAsync<AdminRequestTypeDetailDto>(await admin.PutAsJsonAsync(
            $"/api/admin/request-types/{created.RequestTypeId}/approval-requirements/ReopenApproval",
            new SaveApprovalRequirementRequestDto(ApprovalTargetKind.Role, null, Roles.CsManager, null)));
        var reopenRequirement = Assert.Single(
            reopenApproval.ApprovalRequirements, r => r.ApprovalType == ApprovalType.ReopenApproval);
        Assert.Equal(ApprovalTargetKind.Role, reopenRequirement.TargetKind);
        Assert.Equal(Roles.CsManager, reopenRequirement.TargetRoleName);
        Assert.Equal("Reopen Approval", reopenRequirement.ApprovalTypeLabel);
        Assert.Equal(2, reopenApproval.ApprovalRequirements.Count);

        var sla = await ReadAsync<AdminRequestTypeDetailDto>(await admin.PutAsJsonAsync(
            $"/api/admin/request-types/{created.RequestTypeId}/sla-policies/{(byte)PriorityLevel.Medium}",
            new SaveSlaPolicyRequestDto(SlaTriggerType.ApprovalReceived, SlaDurationUnit.Days, null, null, 1, null, false, null, null, null, null)));
        Assert.Equal(SlaTriggerType.ApprovalReceived, Assert.Single(sla.SlaPolicies).Trigger);

        // The operational directory is department-filtered and active-only.
        var options = await ReadAsync<List<RequestTypeOptionDto>>(await admin.GetAsync($"/api/request-types?departmentId={collections}"));
        Assert.Contains(options, o => o.RequestTypeId == created.RequestTypeId && o.HasPublishedWorkflow);

        var deactivated = await ReadAsync<AdminRequestTypeDetailDto>(await admin.PatchAsJsonAsync($"/api/admin/request-types/{created.RequestTypeId}/activation",
            new SetActiveRequestDto(false)));
        Assert.False(deactivated.IsActive);
        Assert.DoesNotContain(await ReadAsync<List<RequestTypeOptionDto>>(await admin.GetAsync($"/api/request-types?departmentId={collections}")),
            o => o.RequestTypeId == created.RequestTypeId);
        Assert.Equal(HttpStatusCode.MethodNotAllowed, (await admin.DeleteAsync($"/api/admin/request-types/{created.RequestTypeId}")).StatusCode);
    }

    [Fact]
    public async Task RequestTypeDirectory_FiltersByDepartment_ForAnyStaff()
    {
        var (admin, _) = await CreateClientAsync(Roles.SystemAdministrator);
        var (agent, _) = await CreateClientAsync(Roles.CsAgent);
        var a = await _factory.CreateDepartmentAsync(Unique("Dept A"), Guid.NewGuid().ToString("N")[..8]);
        var b = await _factory.CreateDepartmentAsync(Unique("Dept B"), Guid.NewGuid().ToString("N")[..8]);
        var inA = await _factory.CreateRequestTypeAsync("Only In A", a);
        var inB = await _factory.CreateRequestTypeAsync("Only In B", b);

        var forA = await ReadAsync<List<RequestTypeOptionDto>>(await agent.GetAsync($"/api/request-types?departmentId={a}"));

        Assert.Contains(forA, o => o.RequestTypeId == inA);
        Assert.DoesNotContain(forA, o => o.RequestTypeId == inB);
        Assert.Contains(await ReadAsync<List<RequestTypeOptionDto>>(await admin.GetAsync("/api/request-types")), o => o.RequestTypeId == inB);
    }

    // ------------------------------------------------- workflows + pinning

    private static async Task<AdminWorkflowDetailDto> CreatePublishedWorkflowAsync(HttpClient admin, string name)
    {
        var created = await ReadAsync<AdminWorkflowDetailDto>(await admin.PostAsJsonAsync("/api/admin/workflows", new CreateWorkflowRequestDto(name, null)));
        var draft = created.Versions.Single(v => v.Status == WorkflowVersionStatus.Draft);
        await ReadAsync<WorkflowVersionDetailDto>(await admin.PostAsJsonAsync($"/api/admin/workflows/versions/{draft.WorkflowTemplateId}/publish", new { }));
        return await ReadAsync<AdminWorkflowDetailDto>(await admin.GetAsync($"/api/admin/workflows/{created.WorkflowId}"));
    }

    private async Task<TicketResponseDto> CreateTicketAsync(HttpClient client, int departmentId, int categoryId, int? requestTypeId)
    {
        var intake = await ReadAsync<IntakeRecordResponseDto>(await client.PostAsJsonAsync(
            "/api/intake-records", new CreateIntakeRecordRequestDto("Phone", "+971509990002", departmentId, false, null, null)));
        var response = await client.PostAsJsonAsync("/api/tickets",
            new CreateTicketRequestDto(intake.IntakeRecordId, null, null, categoryId, (byte)PriorityLevel.Medium, "Send my receipts", RequestTypeId: requestTypeId));
        return await ReadAsync<TicketResponseDto>(response);
    }

    [Fact]
    public async Task Workflows_DesignPublishVersionAndPinTickets()
    {
        var (admin, adminId) = await CreateClientAsync(Roles.SystemAdministrator);
        await _factory.SeedPrioritiesAsync();
        var collections = await _factory.CreateDepartmentAsync(Unique("Collections"), Guid.NewGuid().ToString("N")[..8]);
        var categoryId = await _factory.CreateCategoryAsync("Receipts", collections);

        // Catalog offers only controlled step and approval types.
        var catalog = await ReadAsync<WorkflowDesignerCatalogDto>(await admin.GetAsync("/api/admin/workflows/catalog"));
        Assert.Contains(catalog.StepKinds, k => k.Kind == WorkflowStepKind.WaitingForApproval && k.RequiresApprovalType);

        // The designer offers exactly the workflow-step-eligible types —
        // derived from ApprovalTypeRules rather than a fixed count, so adding
        // a type does not make this assertion stale.
        Assert.Equal(
            ApprovalTypeRules.WorkflowStepEligible.Order(),
            catalog.ApprovalTypes.Select(a => a.ApprovalType).Order());

        // Concretely: the two forward-flow approvals are offered, and the
        // post-closure request type is not.
        Assert.Contains(catalog.ApprovalTypes, a => a.ApprovalType == ApprovalType.AccountingApproval);
        Assert.Contains(catalog.ApprovalTypes, a => a.ApprovalType == ApprovalType.CustomerServiceApproval);
        Assert.DoesNotContain(catalog.ApprovalTypes, a => a.ApprovalType == ApprovalType.ReopenApproval);

        // Create → Draft V1 skeleton.
        var created = await ReadAsync<AdminWorkflowDetailDto>(await admin.PostAsJsonAsync("/api/admin/workflows",
            new CreateWorkflowRequestDto(Unique("Send Receipts Workflow"), "Collections receipts")));
        var v1 = created.Versions.Single();
        Assert.Equal(WorkflowVersionStatus.Draft, v1.Status);
        Assert.Contains(await ReadAsync<List<AdminWorkflowSummaryDto>>(await admin.GetAsync("/api/admin/workflows")), w => w.WorkflowId == created.WorkflowId);

        var renamed = await ReadAsync<AdminWorkflowDetailDto>(await admin.PutAsJsonAsync($"/api/admin/workflows/{created.WorkflowId}",
            new UpdateWorkflowRequestDto(created.Name, "Collections receipts (approved by Accounting)")));
        Assert.Equal("Collections receipts (approved by Accounting)", renamed.Description);

        // Design the Draft: settings, add an approval step, move it, branch it.
        var settings = await ReadAsync<WorkflowVersionDetailDto>(await admin.PutAsJsonAsync($"/api/admin/workflows/versions/{v1.WorkflowTemplateId}/settings",
            new UpdateVersionSettingsRequestDto("Send Receipts V1", null, false, true, false)));
        Assert.True(settings.AllowsPendingInternal);

        var withApproval = await ReadAsync<WorkflowVersionDetailDto>(await admin.PostAsJsonAsync($"/api/admin/workflows/versions/{v1.WorkflowTemplateId}/steps",
            new SaveStepRequestDto("Accounting Approval", WorkflowStepKind.WaitingForApproval, false, ApprovalType.AccountingApproval)));
        var approvalStep = withApproval.Steps.Single(s => s.Kind == WorkflowStepKind.WaitingForApproval);

        WorkflowVersionDetailDto current = withApproval;
        for (var i = 0; i < 3; i++)
        {
            current = await ReadAsync<WorkflowVersionDetailDto>(await admin.PostAsJsonAsync(
                $"/api/admin/workflows/versions/{v1.WorkflowTemplateId}/steps/{approvalStep.WorkflowTemplateStepId}/move",
                new MoveStepRequestDto(StepMoveDirection.Up)));
        }

        Assert.Equal(3, current.Steps.Single(s => s.WorkflowTemplateStepId == approvalStep.WorkflowTemplateStepId).Sequence);

        var queue = current.Steps.Single(s => s.Kind == WorkflowStepKind.Assigned);
        var branched = await ReadAsync<WorkflowVersionDetailDto>(await admin.PutAsJsonAsync(
            $"/api/admin/workflows/versions/{v1.WorkflowTemplateId}/steps/{approvalStep.WorkflowTemplateStepId}/transitions",
            new SetTransitionRequestDto(WorkflowStepOutcome.Rejected, queue.WorkflowTemplateStepId)));
        Assert.Equal(queue.Sequence, Assert.Single(branched.Steps.Single(s => s.WorkflowTemplateStepId == approvalStep.WorkflowTemplateStepId).Transitions).TargetSequence);

        var work = branched.Steps.Single(s => s.Kind == WorkflowStepKind.InProgress);
        var renamedStep = await ReadAsync<WorkflowVersionDetailDto>(await admin.PutAsJsonAsync(
            $"/api/admin/workflows/versions/{v1.WorkflowTemplateId}/steps/{work.WorkflowTemplateStepId}",
            new SaveStepRequestDto("Send Receipts", WorkflowStepKind.InProgress, false, null)));
        Assert.Contains(renamedStep.Steps, s => s.Name == "Send Receipts");

        // An invalid draft cannot publish: remove the Close step and try.
        var close = renamedStep.Steps.Single(s => s.Kind == WorkflowStepKind.Closed);
        var withoutClose = await ReadAsync<WorkflowVersionDetailDto>(await admin.DeleteAsync($"/api/admin/workflows/versions/{v1.WorkflowTemplateId}/steps/{close.WorkflowTemplateStepId}"));
        Assert.False(withoutClose.CanPublish);
        var refused = await admin.PostAsJsonAsync($"/api/admin/workflows/versions/{v1.WorkflowTemplateId}/publish", new { });
        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        Assert.Contains("no Close step", await refused.Content.ReadAsStringAsync());

        await ReadAsync<WorkflowVersionDetailDto>(await admin.PostAsJsonAsync($"/api/admin/workflows/versions/{v1.WorkflowTemplateId}/steps",
            new SaveStepRequestDto("Close", WorkflowStepKind.Closed, false, null)));

        // Publish V1 → read-only, Active.
        var publishedV1 = await ReadAsync<WorkflowVersionDetailDto>(await admin.PostAsJsonAsync($"/api/admin/workflows/versions/{v1.WorkflowTemplateId}/publish", new { }));
        Assert.Equal(WorkflowVersionStatus.Published, publishedV1.Status);
        Assert.Equal(adminId, (await ReadAsync<AdminWorkflowDetailDto>(await admin.GetAsync($"/api/admin/workflows/{created.WorkflowId}")))
            .Versions.Single(v => v.VersionNumber == 1).PublishedByName is null ? Guid.Empty : adminId);
        Assert.Equal(HttpStatusCode.Conflict, (await admin.PostAsJsonAsync($"/api/admin/workflows/versions/{v1.WorkflowTemplateId}/steps",
            new SaveStepRequestDto("Nope", WorkflowStepKind.InProgress, false, null))).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await admin.DeleteAsync($"/api/admin/workflows/versions/{v1.WorkflowTemplateId}")).StatusCode);

        // A request type on the workflow; a ticket created now pins V1.
        var requestType = await ReadAsync<AdminRequestTypeDetailDto>(await admin.PostAsJsonAsync("/api/admin/request-types",
            new SaveRequestTypeRequestDto(collections, "Send Receipts", created.WorkflowId, (byte)PriorityLevel.Medium, false, false, true, true)));
        Assert.Equal(v1.WorkflowTemplateId, requestType.Workflow.ActiveVersionId);

        var ticketOnV1 = await CreateTicketAsync(admin, collections, categoryId, requestType.RequestTypeId);
        var detailV1 = await ReadAsync<TicketDetailDto>(await admin.GetAsync($"/api/tickets/{ticketOnV1.TicketId}"));
        Assert.Equal(v1.WorkflowTemplateId, detailV1.WorkflowTemplateId);
        Assert.Equal(1, detailV1.WorkflowVersionNumber);
        Assert.Equal("Send Receipts", detailV1.RequestTypeName);

        // Create New Version → Draft V2 copied from V1; publish V2.
        var v2 = await ReadAsync<WorkflowVersionDetailDto>(await admin.PostAsJsonAsync($"/api/admin/workflows/{created.WorkflowId}/versions", new { }));
        Assert.Equal(2, v2.VersionNumber);
        Assert.Equal(publishedV1.Steps.Select(s => (s.Sequence, s.Name, s.Kind)), v2.Steps.Select(s => (s.Sequence, s.Name, s.Kind)));
        Assert.Equal(HttpStatusCode.Conflict, (await admin.PostAsJsonAsync($"/api/admin/workflows/{created.WorkflowId}/versions", new { })).StatusCode);

        var pendingStep = await ReadAsync<WorkflowVersionDetailDto>(await admin.PostAsJsonAsync($"/api/admin/workflows/versions/{v2.WorkflowTemplateId}/steps",
            new SaveStepRequestDto("Pending Customer", WorkflowStepKind.PendingCustomer, true, null)));
        var pending = pendingStep.Steps.Single(s => s.Kind == WorkflowStepKind.PendingCustomer);
        await ReadAsync<WorkflowVersionDetailDto>(await admin.PostAsJsonAsync(
            $"/api/admin/workflows/versions/{v2.WorkflowTemplateId}/steps/{pending.WorkflowTemplateStepId}/move", new MoveStepRequestDto(StepMoveDirection.Up)));
        var publishedV2 = await ReadAsync<WorkflowVersionDetailDto>(await admin.PostAsJsonAsync($"/api/admin/workflows/versions/{v2.WorkflowTemplateId}/publish", new { }));
        Assert.Equal(WorkflowVersionStatus.Published, publishedV2.Status);
        Assert.True(publishedV2.AllowsPendingCustomer, "a Pending Customer step implies the capability");

        // V1 is Historical and untouched; the V1 ticket is still on V1.
        var v1After = await ReadAsync<WorkflowVersionDetailDto>(await admin.GetAsync($"/api/admin/workflows/versions/{v1.WorkflowTemplateId}"));
        Assert.Equal(WorkflowVersionStatus.Historical, v1After.Status);
        Assert.Equal(publishedV1.Steps.Count, v1After.Steps.Count);
        Assert.Equal(1, v1After.TicketCount);
        Assert.Equal(v1.WorkflowTemplateId, (await ReadAsync<TicketDetailDto>(await admin.GetAsync($"/api/tickets/{ticketOnV1.TicketId}"))).WorkflowTemplateId);

        // A ticket created after publication pins V2.
        var ticketOnV2 = await CreateTicketAsync(admin, collections, categoryId, requestType.RequestTypeId);
        var detailV2 = await ReadAsync<TicketDetailDto>(await admin.GetAsync($"/api/tickets/{ticketOnV2.TicketId}"));
        Assert.Equal(v2.WorkflowTemplateId, detailV2.WorkflowTemplateId);
        Assert.Equal(2, detailV2.WorkflowVersionNumber);

        var overview = await ReadAsync<AdminWorkflowDetailDto>(await admin.GetAsync($"/api/admin/workflows/{created.WorkflowId}"));
        Assert.Equal(1, overview.Versions.Single(v => v.VersionNumber == 1).TicketCount);
        Assert.Equal(1, overview.Versions.Single(v => v.VersionNumber == 2).TicketCount);
        Assert.Contains(overview.RequestTypes, r => r.RequestTypeId == requestType.RequestTypeId);

        // Referenced versions are never deletable; a fresh unreferenced draft is.
        Assert.Equal(HttpStatusCode.Conflict, (await admin.DeleteAsync($"/api/admin/workflows/versions/{v2.WorkflowTemplateId}")).StatusCode);
        var v3 = await ReadAsync<WorkflowVersionDetailDto>(await admin.PostAsJsonAsync($"/api/admin/workflows/{created.WorkflowId}/versions", new { }));
        Assert.Equal(HttpStatusCode.NoContent, (await admin.DeleteAsync($"/api/admin/workflows/versions/{v3.WorkflowTemplateId}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await admin.GetAsync($"/api/admin/workflows/versions/{v3.WorkflowTemplateId}")).StatusCode);

        // Deactivating a workflow still used by an active request type is refused.
        Assert.Equal(HttpStatusCode.Conflict, (await admin.PatchAsJsonAsync($"/api/admin/workflows/{created.WorkflowId}/activation", new SetActiveRequestDto(false))).StatusCode);

        // A ticket without a request type pins nothing, exactly as before.
        var plain = await CreateTicketAsync(admin, collections, categoryId, null);
        var plainDetail = await ReadAsync<TicketDetailDto>(await admin.GetAsync($"/api/tickets/{plain.TicketId}"));
        Assert.Null(plainDetail.WorkflowTemplateId);
        Assert.Null(plainDetail.RequestTypeId);
    }

    [Fact]
    public async Task Ticket_cannot_be_created_on_a_request_type_whose_workflow_is_unpublished()
    {
        var (admin, _) = await CreateClientAsync(Roles.SystemAdministrator);
        await _factory.SeedPrioritiesAsync();
        var department = await _factory.CreateDepartmentAsync(Unique("Registration"), Guid.NewGuid().ToString("N")[..8]);
        var categoryId = await _factory.CreateCategoryAsync("Registration", department);
        var requestTypeId = await _factory.CreateRequestTypeAsync("Register Unit", department);

        // Publish V2 as a draft-only situation cannot happen through the API
        // (a Published version always exists once published), so simulate a
        // legacy/config gap directly: the workflow's only version is a Draft.
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TigerCsDbContext>();
            var requestType = await db.RequestTypes.SingleAsync(r => r.RequestTypeId == requestTypeId);
            var published = await db.WorkflowTemplates.SingleAsync(t => t.WorkflowId == requestType.WorkflowId && t.Status == WorkflowVersionStatus.Published);
            db.Entry(published).Property(nameof(WorkflowTemplate.Status)).CurrentValue = WorkflowVersionStatus.Draft;
            await db.SaveChangesAsync();
        }

        var intake = await ReadAsync<IntakeRecordResponseDto>(await admin.PostAsJsonAsync(
            "/api/intake-records", new CreateIntakeRecordRequestDto("Phone", "+971509990003", department, false, null, null)));
        var response = await admin.PostAsJsonAsync("/api/tickets",
            new CreateTicketRequestDto(intake.IntakeRecordId, null, null, categoryId, (byte)PriorityLevel.Medium, "x", RequestTypeId: requestTypeId));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Contains("not published", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Legacy_ticket_with_request_type_but_no_pinned_version_resolves_capabilities_from_the_active_version()
    {
        var (admin, _) = await CreateClientAsync(Roles.SystemAdministrator);
        await _factory.SeedPrioritiesAsync();
        var department = await _factory.CreateDepartmentAsync(Unique("Legacy Dept"), Guid.NewGuid().ToString("N")[..8]);
        var categoryId = await _factory.CreateCategoryAsync("Legacy", department);
        var requestTypeId = await _factory.CreateRequestTypeAsync("Legacy RT", department);

        var ticket = await CreateTicketAsync(admin, department, categoryId, requestTypeId);

        // Simulate a pre-versioning ticket: request type set, no pinned version.
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TigerCsDbContext>();
            var stored = await db.Tickets.SingleAsync(t => t.TicketId == ticket.TicketId);
            db.Entry(stored).Property(nameof(Domain.Modules.Ticketing.Ticket.WorkflowTemplateId)).CurrentValue = null;
            await db.SaveChangesAsync();
        }

        var detail = await ReadAsync<TicketDetailDto>(await admin.GetAsync($"/api/tickets/{ticket.TicketId}"));
        Assert.Null(detail.WorkflowTemplateId);
        Assert.Equal("Legacy RT", detail.RequestTypeName);

        // Pending Customer is allowed by the active version + request type, so
        // the lifecycle still resolves capabilities for the legacy ticket.
        var (_, _, ownerId) = await _factory.SeedEmployeeAsync(Roles.DepartmentEmployee);
        await _factory.AssignPrimaryDepartmentAsync(ownerId, department);
        var assigned = await ReadAsync<TicketDetailDto>(await admin.PostAsJsonAsync($"/api/tickets/{ticket.TicketId}/assignment",
            new AssignTicketRequestDto(ownerId, Convert.FromBase64String(detail.RowVersion))));
        var inProgress = await ReadAsync<TicketDetailDto>(await admin.PostAsJsonAsync($"/api/tickets/{ticket.TicketId}/status",
            new ChangeStatusRequestDto("InProgress", Convert.FromBase64String(assigned.RowVersion))));
        var pending = await admin.PostAsJsonAsync($"/api/tickets/{ticket.TicketId}/status",
            new ChangeStatusRequestDto("PendingCustomer", Convert.FromBase64String(inProgress.RowVersion), "Waiting for documents"));
        Assert.Equal(HttpStatusCode.OK, pending.StatusCode);
    }

    /// <summary>
    /// Genesys routing configuration through the real host: the Queue →
    /// Department mapping, which is the whole of it — a queue says which
    /// department an inquiry belongs to and nothing about what the customer
    /// wants, so there is no category configuration to administer.
    ///
    /// <para>
    /// Nothing here is seeded — the real Genesys queue ids are not known to
    /// this repository and are never invented, so an administrator creates
    /// every mapping. The listing therefore starts empty for a queue id this
    /// test invents for itself.
    /// </para>
    /// </summary>
    [Fact]
    public async Task GenesysRouting_QueueMappings_ThroughTheRealHost()
    {
        var (admin, _) = await CreateClientAsync(Roles.SystemAdministrator);
        var queueId = "queue-" + Guid.NewGuid().ToString("N")[..10];
        var departmentId = await _factory.CreateDepartmentAsync("Genesys Leasing " + Guid.NewGuid(), Guid.NewGuid().ToString("N")[..8]);
        var otherDepartmentId = await _factory.CreateDepartmentAsync("Genesys Maintenance " + Guid.NewGuid(), Guid.NewGuid().ToString("N")[..8]);
        var categoryId = await _factory.CreateCategoryAsync("General Inquiry", departmentId);

        // No mapping exists for a queue nobody configured.
        var before = await ReadAsync<List<AdminGenesysQueueMappingDto>>(await admin.GetAsync("/api/admin/genesys/queue-mappings"));
        Assert.DoesNotContain(before, m => m.QueueId == queueId);

        // Create.
        var created = await admin.PostAsJsonAsync(
            "/api/admin/genesys/queue-mappings",
            new SaveGenesysQueueMappingRequestDto(queueId, "Leasing queue", departmentId));
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var mapping = await created.Content.ReadFromJsonAsync<AdminGenesysQueueMappingDto>();
        Assert.Equal(queueId, mapping!.QueueId);
        Assert.Equal(departmentId, mapping.DepartmentId);
        Assert.True(mapping.IsActive);

        // The same queue cannot be mapped twice — re-pointing is an edit.
        var duplicate = await admin.PostAsJsonAsync(
            "/api/admin/genesys/queue-mappings",
            new SaveGenesysQueueMappingRequestDto(queueId, "Duplicate", otherDepartmentId));
        Assert.Equal(HttpStatusCode.BadRequest, duplicate.StatusCode);
        Assert.Contains("already mapped", await duplicate.Content.ReadAsStringAsync());

        // Re-point it at another department, and deactivate it.
        var updated = await admin.PutAsJsonAsync(
            $"/api/admin/genesys/queue-mappings/{mapping.GenesysQueueMappingId}",
            new SaveGenesysQueueMappingRequestDto(queueId, "Maintenance queue", otherDepartmentId, IsActive: false));
        Assert.Equal(HttpStatusCode.OK, updated.StatusCode);
        var afterUpdate = await updated.Content.ReadFromJsonAsync<AdminGenesysQueueMappingDto>();
        Assert.Equal(otherDepartmentId, afterUpdate!.DepartmentId);
        Assert.False(afterUpdate.IsActive);
        // The queue id is the identity and never changes.
        Assert.Equal(queueId, afterUpdate.QueueId);
    }

    [Fact]
    public async Task Sla_Configuration_IsReadableByTheAdministrator()
    {
        var (admin, _) = await CreateClientAsync(Roles.SystemAdministrator);

        var configuration = await ReadAsync<TigerCS.Application.Modules.SlaAndEscalation.Dto.SlaConfigurationDto>(
            await admin.GetAsync("/api/admin/sla/configuration"));

        // The four seeded per-priority policies, Critical first, and the rules the calculation applies.
        Assert.Equal([1, 2, 3, 4], configuration.Policies.Select(p => (int)p.PriorityId));
        Assert.Equal("24/7", configuration.Policies[0].ClockBasis);
        Assert.Contains("classification", configuration.ClockStartRule, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Accept & Start", configuration.FirstResponseRule, StringComparison.Ordinal);
        Assert.Contains("Pending Customer", configuration.PauseRule, StringComparison.Ordinal);
        Assert.DoesNotContain("not implemented", configuration.PauseRule, StringComparison.Ordinal);
    }
}
