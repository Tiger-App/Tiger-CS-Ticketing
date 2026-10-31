using TigerCS.Application.Abstractions;
using TigerCS.Application.Modules.Administration.Dto;
using TigerCS.Application.Modules.IdentityAndAccess.Abstractions;
using TigerCS.Application.Modules.WorkflowConfiguration.Abstractions;
using TigerCS.Domain.Modules.IdentityAndAccess;
using TigerCS.Domain.Modules.SlaAndEscalation;
using TigerCS.Domain.Modules.WorkflowConfiguration;

namespace TigerCS.Application.Modules.Administration.Services;

/// <summary>
/// Request Type administration over the existing phase-1..3 configuration
/// tables: the request type itself, its assignment rule, its approval
/// requirements (controlled approval types only) and its per-priority SLA
/// rows (existing confirmed values only — no new SLA rule is invented).
/// A request type referenced by tickets is never deleted and keeps its
/// department; Active/Inactive is the retirement path.
/// </summary>
public sealed class AdminRequestTypeAppService(
    IRequestTypeRepository requestTypeRepository,
    IDepartmentRepository departmentRepository,
    IWorkflowRepository workflowRepository,
    IWorkflowTemplateRepository workflowTemplateRepository,
    IRequestTypeAssignmentRuleRepository assignmentRuleRepository,
    IRequestTypeApprovalRequirementRepository approvalRequirementRepository,
    IRequestTypeSlaPolicyRepository slaPolicyRepository,
    IEmployeeRepository employeeRepository,
    IUserDepartmentAssignmentRepository userDepartmentAssignmentRepository,
    IWorkflowConfigurationUnitOfWork unitOfWork,
    IAuditEntryWriter auditWriter)
{
    public async Task<IReadOnlyList<AdminRequestTypeSummaryDto>> ListAsync(
        int? departmentId, bool includeInactive, CancellationToken cancellationToken = default)
    {
        var requestTypes = await requestTypeRepository.ListAsync(departmentId, includeInactive, cancellationToken);
        var departments = (await departmentRepository.ListAsync(activeOnly: false, cancellationToken))
            .ToDictionary(d => d.DepartmentId, d => d.Name);
        var workflows = (await workflowRepository.ListAsync(includeInactive: true, cancellationToken))
            .ToDictionary(w => w.WorkflowId);

        var result = new List<AdminRequestTypeSummaryDto>(requestTypes.Count);
        foreach (var requestType in requestTypes)
        {
            var published = await workflowTemplateRepository.GetPublishedAsync(requestType.WorkflowId, cancellationToken);
            var rule = await assignmentRuleRepository.GetByRequestTypeIdAsync(requestType.RequestTypeId, cancellationToken);
            var ticketCount = await requestTypeRepository.CountTicketsAsync(requestType.RequestTypeId, cancellationToken);
            result.Add(new AdminRequestTypeSummaryDto(
                requestType.RequestTypeId,
                requestType.Name,
                requestType.DepartmentId,
                departments.GetValueOrDefault(requestType.DepartmentId, "Unknown department"),
                requestType.IsActive,
                requestType.WorkflowId,
                workflows.TryGetValue(requestType.WorkflowId, out var workflow) ? workflow.Name : "Unknown workflow",
                published?.VersionNumber,
                await DescribeAssignmentAsync(rule, departments.GetValueOrDefault(requestType.DepartmentId, "Department"), cancellationToken),
                requestType.DefaultPriorityId,
                ticketCount));
        }

        return result;
    }

    /// <summary>The New Ticket picker: active request types of one department, flagged when their workflow has nothing published yet.</summary>
    public async Task<IReadOnlyList<RequestTypeOptionDto>> ListOptionsAsync(int? departmentId, CancellationToken cancellationToken = default)
    {
        var requestTypes = await requestTypeRepository.ListAsync(departmentId, includeInactive: false, cancellationToken);
        var options = new List<RequestTypeOptionDto>(requestTypes.Count);
        foreach (var requestType in requestTypes)
        {
            var published = await workflowTemplateRepository.GetPublishedAsync(requestType.WorkflowId, cancellationToken);
            options.Add(new RequestTypeOptionDto(
                requestType.RequestTypeId, requestType.Name, requestType.DepartmentId, requestType.DefaultPriorityId, published is not null));
        }

        return options.OrderBy(o => o.Name, StringComparer.Ordinal).ToList();
    }

    public async Task<AdminRequestTypeDetailDto?> GetAsync(int requestTypeId, CancellationToken cancellationToken = default)
    {
        var requestType = await requestTypeRepository.GetByIdAsync(requestTypeId, cancellationToken);
        if (requestType is null)
        {
            return null;
        }

        var department = await departmentRepository.GetByIdAsync(requestType.DepartmentId, cancellationToken);
        var workflow = await workflowRepository.GetByIdAsync(requestType.WorkflowId, cancellationToken);
        var published = await workflowTemplateRepository.GetPublishedAsync(requestType.WorkflowId, cancellationToken);
        var draft = await workflowTemplateRepository.GetDraftAsync(requestType.WorkflowId, cancellationToken);
        var rule = await assignmentRuleRepository.GetByRequestTypeIdAsync(requestTypeId, cancellationToken);
        var requirements = await approvalRequirementRepository.ListByRequestTypeIdAsync(requestTypeId, cancellationToken);
        var slaPolicies = await slaPolicyRepository.ListByRequestTypeAsync(requestTypeId, cancellationToken);
        var ticketCount = await requestTypeRepository.CountTicketsAsync(requestTypeId, cancellationToken);

        var requirementDtos = new List<ApprovalRequirementDto>();
        foreach (var requirement in requirements)
        {
            var targetDepartment = requirement.TargetDepartmentId is { } targetDepartmentId
                ? await departmentRepository.GetByIdAsync(targetDepartmentId, cancellationToken)
                : null;
            var targetEmployee = requirement.TargetEmployeeId is { } targetEmployeeId
                ? await employeeRepository.GetByIdAsync(targetEmployeeId, cancellationToken)
                : null;
            requirementDtos.Add(new ApprovalRequirementDto(
                requirement.ApprovalType, ApprovalTypeLabels.Label(requirement.ApprovalType), requirement.TargetKind,
                requirement.TargetDepartmentId, targetDepartment?.Name, requirement.TargetRoleName,
                requirement.TargetEmployeeId, targetEmployee?.DisplayName,
                requirement.BlocksWorkUntilApproved, requirement.IsActive));
        }

        return new AdminRequestTypeDetailDto(
            requestType.RequestTypeId,
            requestType.Name,
            requestType.DepartmentId,
            department?.Name ?? "Unknown department",
            requestType.IsActive,
            requestType.DefaultPriorityId,
            requestType.AllowAgentPriorityChange,
            requestType.AllowPendingCustomer,
            requestType.AllowPendingInternal,
            requestType.AllowReopen,
            requestType.RequiredFieldsJson,
            ticketCount,
            new WorkflowLinkDto(
                requestType.WorkflowId,
                workflow?.Name ?? "Unknown workflow",
                workflow?.IsActive ?? false,
                published?.WorkflowTemplateId,
                published?.VersionNumber,
                draft?.WorkflowTemplateId,
                draft?.VersionNumber,
                published?.Steps.Where(s => s.ApprovalType is not null).Select(s => s.ApprovalType!.Value).Distinct().ToList() ?? []),
            await ToRuleDtoAsync(rule, cancellationToken),
            requirementDtos,
            slaPolicies.Select(ToSlaDto).ToList());
    }

    public async Task<AdminResult<AdminRequestTypeDetailDto>> CreateAsync(
        Guid actorEmployeeId, SaveRequestTypeRequestDto request, CancellationToken cancellationToken = default)
    {
        var errors = await ValidateAsync(request, existing: null, cancellationToken);
        if (errors.Count > 0)
        {
            return AdminResult<AdminRequestTypeDetailDto>.Invalid(errors);
        }

        var requestType = new RequestType(
            request.DepartmentId, request.Name.Trim(), request.WorkflowId, request.DefaultPriorityId,
            request.AllowAgentPriorityChange, request.AllowPendingCustomer, request.AllowPendingInternal, request.AllowReopen,
            request.RequiredFieldsJson);
        await requestTypeRepository.AddAsync(requestType, cancellationToken);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        await auditWriter.WriteAsync(
            actorEmployeeId, "AdminCreateRequestType", "RequestType", requestType.RequestTypeId.ToString(),
            beforeValue: null, afterValue: Describe(requestType), Guid.NewGuid(), cancellationToken);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        return AdminResult<AdminRequestTypeDetailDto>.Success((await GetAsync(requestType.RequestTypeId, cancellationToken))!);
    }

    public async Task<AdminResult<AdminRequestTypeDetailDto>> UpdateAsync(
        Guid actorEmployeeId, int requestTypeId, SaveRequestTypeRequestDto request, CancellationToken cancellationToken = default)
    {
        var requestType = await requestTypeRepository.GetByIdAsync(requestTypeId, cancellationToken);
        if (requestType is null)
        {
            return AdminResult<AdminRequestTypeDetailDto>.NotFound();
        }

        var errors = await ValidateAsync(request, requestType, cancellationToken);
        if (errors.Count > 0)
        {
            return AdminResult<AdminRequestTypeDetailDto>.Invalid(errors);
        }

        if (request.DepartmentId != requestType.DepartmentId)
        {
            var ticketCount = await requestTypeRepository.CountTicketsAsync(requestTypeId, cancellationToken);
            if (ticketCount > 0)
            {
                return AdminResult<AdminRequestTypeDetailDto>.Conflict(
                    $"This request type already governs {ticketCount} ticket(s); its department cannot change. Deactivate it and create a new request type in the other department instead.");
            }

            requestType.ChangeDepartment(request.DepartmentId);
        }

        var before = Describe(requestType);
        requestType.Update(
            request.Name, request.WorkflowId, request.DefaultPriorityId,
            request.AllowAgentPriorityChange, request.AllowPendingCustomer, request.AllowPendingInternal, request.AllowReopen,
            request.RequiredFieldsJson);

        await auditWriter.WriteAsync(
            actorEmployeeId, "AdminUpdateRequestType", "RequestType", requestTypeId.ToString(),
            before, Describe(requestType), Guid.NewGuid(), cancellationToken);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        return AdminResult<AdminRequestTypeDetailDto>.Success((await GetAsync(requestTypeId, cancellationToken))!);
    }

    public async Task<AdminResult<AdminRequestTypeDetailDto>> SetActivationAsync(
        Guid actorEmployeeId, int requestTypeId, SetActiveRequestDto request, CancellationToken cancellationToken = default)
    {
        var requestType = await requestTypeRepository.GetByIdAsync(requestTypeId, cancellationToken);
        if (requestType is null)
        {
            return AdminResult<AdminRequestTypeDetailDto>.NotFound();
        }

        if (request.IsActive && await workflowTemplateRepository.GetPublishedAsync(requestType.WorkflowId, cancellationToken) is null)
        {
            return AdminResult<AdminRequestTypeDetailDto>.Conflict(
                "This request type's workflow has no published version. Publish the workflow before activating the request type.");
        }

        var before = requestType.IsActive;
        if (request.IsActive)
        {
            requestType.Activate();
        }
        else
        {
            requestType.Deactivate();
        }

        await auditWriter.WriteAsync(
            actorEmployeeId, request.IsActive ? "AdminActivateRequestType" : "AdminDeactivateRequestType", "RequestType", requestTypeId.ToString(),
            $"IsActive={before}", $"IsActive={requestType.IsActive};Reason={request.Reason}", Guid.NewGuid(), cancellationToken);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        return AdminResult<AdminRequestTypeDetailDto>.Success((await GetAsync(requestTypeId, cancellationToken))!);
    }

    public async Task<AdminResult<AdminRequestTypeDetailDto>> SaveAssignmentRuleAsync(
        Guid actorEmployeeId, int requestTypeId, SaveAssignmentRuleRequestDto request, CancellationToken cancellationToken = default)
    {
        var requestType = await requestTypeRepository.GetByIdAsync(requestTypeId, cancellationToken);
        if (requestType is null)
        {
            return AdminResult<AdminRequestTypeDetailDto>.NotFound();
        }

        if (!Enum.IsDefined(request.Mode))
        {
            return AdminResult<AdminRequestTypeDetailDto>.Invalid("The assignment mode is not one of the supported modes.");
        }

        var errors = new List<string>();
        if (request.Mode is not (AssignmentMode.DepartmentQueue or AssignmentMode.RoundRobin))
        {
            if (request.PrimaryEmployeeId is not { } primaryId || primaryId == Guid.Empty)
            {
                errors.Add("A primary assignee is required for this assignment mode.");
            }
            else
            {
                errors.AddRange(await ValidateMemberAsync(primaryId, requestType.DepartmentId, "primary assignee", cancellationToken));
            }
        }

        if (request.Mode == AssignmentMode.Team)
        {
            var members = (request.MemberEmployeeIds ?? []).Where(m => m != request.PrimaryEmployeeId).Distinct().ToList();
            if (members.Count == 0)
            {
                errors.Add("A team rule needs at least one member besides the primary assignee.");
            }

            foreach (var member in members)
            {
                errors.AddRange(await ValidateMemberAsync(member, requestType.DepartmentId, "team member", cancellationToken));
            }
        }

        if (errors.Count > 0)
        {
            return AdminResult<AdminRequestTypeDetailDto>.Invalid(errors);
        }

        var existing = await assignmentRuleRepository.GetByRequestTypeIdAsync(requestTypeId, cancellationToken);
        var before = existing is null ? null : DescribeRule(existing);
        if (existing is not null)
        {
            assignmentRuleRepository.Remove(existing);
            await unitOfWork.SaveChangesAsync(cancellationToken);
        }

        RequestTypeAssignmentRule rule;
        try
        {
            rule = request.Mode switch
            {
                AssignmentMode.DepartmentQueue => RequestTypeAssignmentRule.ForDepartmentQueue(requestTypeId, request.IsActive),
                AssignmentMode.RoundRobin => RequestTypeAssignmentRule.ForRoundRobin(requestTypeId, request.IsActive),
                AssignmentMode.SpecificEmployee => RequestTypeAssignmentRule.ForSpecificEmployee(requestTypeId, request.PrimaryEmployeeId!.Value, request.IsActive),
                _ => RequestTypeAssignmentRule.ForTeam(
                    requestTypeId, request.PrimaryEmployeeId!.Value, request.MemberEmployeeIds ?? [], request.TeamName, request.IsActive)
            };
        }
        catch (ArgumentException ex)
        {
            return AdminResult<AdminRequestTypeDetailDto>.Invalid(ex.Message);
        }

        await assignmentRuleRepository.AddAsync(rule, cancellationToken);
        await auditWriter.WriteAsync(
            actorEmployeeId, "AdminSaveAssignmentRule", "RequestType", requestTypeId.ToString(),
            before, DescribeRule(rule), Guid.NewGuid(), cancellationToken);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        return AdminResult<AdminRequestTypeDetailDto>.Success((await GetAsync(requestTypeId, cancellationToken))!);
    }

    public async Task<AdminResult<AdminRequestTypeDetailDto>> SaveApprovalRequirementAsync(
        Guid actorEmployeeId, int requestTypeId, ApprovalType approvalType, SaveApprovalRequirementRequestDto request,
        CancellationToken cancellationToken = default)
    {
        var requestType = await requestTypeRepository.GetByIdAsync(requestTypeId, cancellationToken);
        if (requestType is null)
        {
            return AdminResult<AdminRequestTypeDetailDto>.NotFound();
        }

        if (!Enum.IsDefined(approvalType))
        {
            return AdminResult<AdminRequestTypeDetailDto>.Invalid("The approval type is not one of the supported approval types.");
        }

        if (!Enum.IsDefined(request.TargetKind))
        {
            return AdminResult<AdminRequestTypeDetailDto>.Invalid("The approver target kind is not one of the supported kinds.");
        }

        var errors = new List<string>();
        switch (request.TargetKind)
        {
            case ApprovalTargetKind.Department:
                if (request.TargetDepartmentId is not { } targetDepartmentId)
                {
                    errors.Add("A deciding department is required.");
                }
                else if (await departmentRepository.GetByIdAsync(targetDepartmentId, cancellationToken) is null)
                {
                    errors.Add("The deciding department does not exist.");
                }

                if (request.TargetRoleName is not null && !Roles.All.Contains(request.TargetRoleName))
                {
                    errors.Add($"'{request.TargetRoleName}' is not one of the fixed roles.");
                }

                break;
            case ApprovalTargetKind.Role:
                if (string.IsNullOrWhiteSpace(request.TargetRoleName))
                {
                    errors.Add("A deciding role is required.");
                }
                else if (!Roles.All.Contains(request.TargetRoleName))
                {
                    errors.Add($"'{request.TargetRoleName}' is not one of the fixed roles.");
                }

                break;
            case ApprovalTargetKind.Employee:
                if (request.TargetEmployeeId is not { } targetEmployeeId || targetEmployeeId == Guid.Empty)
                {
                    errors.Add("A deciding employee is required.");
                }
                else if (await employeeRepository.GetByIdAsync(targetEmployeeId, cancellationToken) is null)
                {
                    errors.Add("The deciding employee does not exist.");
                }

                break;
        }

        if (errors.Count > 0)
        {
            return AdminResult<AdminRequestTypeDetailDto>.Invalid(errors);
        }

        var existing = await approvalRequirementRepository.GetAsync(requestTypeId, approvalType, cancellationToken);
        string? before = null;
        try
        {
            if (existing is null)
            {
                var created = request.TargetKind switch
                {
                    ApprovalTargetKind.Department => RequestTypeApprovalRequirement.ForDepartment(
                        requestTypeId, approvalType, request.TargetDepartmentId!.Value, request.TargetRoleName, request.BlocksWorkUntilApproved, request.IsActive),
                    ApprovalTargetKind.Role => RequestTypeApprovalRequirement.ForRole(
                        requestTypeId, approvalType, request.TargetRoleName!, request.BlocksWorkUntilApproved, request.IsActive),
                    _ => RequestTypeApprovalRequirement.ForEmployee(
                        requestTypeId, approvalType, request.TargetEmployeeId!.Value, request.BlocksWorkUntilApproved, request.IsActive)
                };
                await approvalRequirementRepository.AddAsync(created, cancellationToken);
                existing = created;
            }
            else
            {
                before = DescribeRequirement(existing);
                existing.Update(
                    request.TargetKind, request.TargetDepartmentId, request.TargetRoleName, request.TargetEmployeeId,
                    request.BlocksWorkUntilApproved, request.IsActive);
            }
        }
        catch (ArgumentException ex)
        {
            return AdminResult<AdminRequestTypeDetailDto>.Invalid(ex.Message);
        }

        await auditWriter.WriteAsync(
            actorEmployeeId, "AdminSaveApprovalRequirement", "RequestType", requestTypeId.ToString(),
            before, DescribeRequirement(existing), Guid.NewGuid(), cancellationToken);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        return AdminResult<AdminRequestTypeDetailDto>.Success((await GetAsync(requestTypeId, cancellationToken))!);
    }

    public async Task<AdminResult<AdminRequestTypeDetailDto>> SaveSlaPolicyAsync(
        Guid actorEmployeeId, int requestTypeId, byte priorityId, SaveSlaPolicyRequestDto request, CancellationToken cancellationToken = default)
    {
        var requestType = await requestTypeRepository.GetByIdAsync(requestTypeId, cancellationToken);
        if (requestType is null)
        {
            return AdminResult<AdminRequestTypeDetailDto>.NotFound();
        }

        if (!Enum.IsDefined(typeof(PriorityLevel), priorityId))
        {
            return AdminResult<AdminRequestTypeDetailDto>.Invalid("The priority is not one of the fixed priorities.");
        }

        var existing = await slaPolicyRepository.GetAsync(requestTypeId, priorityId, cancellationToken);
        string? before = null;
        try
        {
            if (existing is null)
            {
                existing = new RequestTypeSlaPolicy(
                    requestTypeId, priorityId, request.Trigger, request.Unit,
                    request.FirstResponseTargetValue, request.FirstResponseMaximumValue,
                    request.ResolutionTargetValue, request.ResolutionMaximumValue,
                    request.IsImmediate, request.ClockBasis, request.PausesOnPendingCustomer,
                    // Retired with TicketStatus.PendingThirdParty: a NEW policy
                    // is created "not decided", never configured for a pause
                    // window no ticket can open any more.
                    pausesOnPendingInternal: null,
                    request.WarningThresholdPercent, request.IsActive);
                await slaPolicyRepository.AddAsync(existing, cancellationToken);
            }
            else
            {
                before = DescribeSla(existing);
                existing.Update(
                    request.Trigger, request.Unit,
                    request.FirstResponseTargetValue, request.FirstResponseMaximumValue,
                    request.ResolutionTargetValue, request.ResolutionMaximumValue,
                    request.IsImmediate, request.ClockBasis, request.PausesOnPendingCustomer,
                    // ...and an EXISTING policy keeps whatever it already
                    // stores, rather than taking it from the request. The
                    // column and its historical values are preserved
                    // (no migration), the field stays on the request DTO so the
                    // wire contract is unchanged, and the retired setting is
                    // simply frozen: the Administration UI no longer offers it,
                    // and its blank save form would otherwise clear every
                    // stored value the moment anyone touched an unrelated SLA
                    // number.
                    pausesOnPendingInternal: existing.PausesOnPendingInternal,
                    request.WarningThresholdPercent, request.IsActive);
            }
        }
        catch (ArgumentException ex)
        {
            return AdminResult<AdminRequestTypeDetailDto>.Invalid(ex.Message);
        }

        await auditWriter.WriteAsync(
            actorEmployeeId, "AdminSaveRequestTypeSla", "RequestType", requestTypeId.ToString(),
            before, DescribeSla(existing), Guid.NewGuid(), cancellationToken);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        return AdminResult<AdminRequestTypeDetailDto>.Success((await GetAsync(requestTypeId, cancellationToken))!);
    }

    private async Task<List<string>> ValidateAsync(SaveRequestTypeRequestDto request, RequestType? existing, CancellationToken cancellationToken)
    {
        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(request.Name))
        {
            errors.Add("Name is required.");
        }
        else if (await requestTypeRepository.NameExistsAsync(request.DepartmentId, request.Name.Trim(), existing?.RequestTypeId, cancellationToken))
        {
            errors.Add($"A request type named '{request.Name.Trim()}' already exists in this department.");
        }

        var department = await departmentRepository.GetByIdAsync(request.DepartmentId, cancellationToken);
        if (department is null)
        {
            errors.Add("The selected department does not exist.");
        }
        else if (!department.IsActive && (existing is null || existing.DepartmentId != request.DepartmentId))
        {
            errors.Add($"Department '{department.Name}' is inactive and cannot receive new request types.");
        }

        var workflow = await workflowRepository.GetByIdAsync(request.WorkflowId, cancellationToken);
        if (workflow is null)
        {
            errors.Add("The selected workflow does not exist.");
        }
        else
        {
            if (!workflow.IsActive && (existing is null || existing.WorkflowId != request.WorkflowId))
            {
                errors.Add($"Workflow '{workflow.Name}' is inactive and cannot be selected.");
            }

            if (await workflowTemplateRepository.GetPublishedAsync(workflow.WorkflowId, cancellationToken) is null)
            {
                errors.Add($"Workflow '{workflow.Name}' has no published version yet. Publish it before assigning it to a request type.");
            }
        }

        if (!Enum.IsDefined(typeof(PriorityLevel), request.DefaultPriorityId))
        {
            errors.Add("The default priority is not one of the fixed priorities.");
        }

        return errors;
    }

    private async Task<List<string>> ValidateMemberAsync(Guid employeeId, int departmentId, string role, CancellationToken cancellationToken)
    {
        var employee = await employeeRepository.GetByIdAsync(employeeId, cancellationToken);
        if (employee is null)
        {
            return [$"The {role} does not exist."];
        }

        var errors = new List<string>();
        if (!employee.IsActive)
        {
            errors.Add($"{employee.DisplayName} is deactivated and cannot be the {role}.");
        }

        if (!await userDepartmentAssignmentRepository.ExistsAsync(employeeId, departmentId, cancellationToken))
        {
            errors.Add($"{employee.DisplayName} is not a member of this request type's department.");
        }

        return errors;
    }

    private async Task<AssignmentRuleDto?> ToRuleDtoAsync(RequestTypeAssignmentRule? rule, CancellationToken cancellationToken)
    {
        if (rule is null)
        {
            return null;
        }

        var primary = rule.PrimaryEmployeeId is { } primaryId
            ? await employeeRepository.GetByIdAsync(primaryId, cancellationToken)
            : null;

        var members = new List<NamedEmployeeDto>();
        foreach (var member in rule.Members)
        {
            var employee = await employeeRepository.GetByIdAsync(member.EmployeeId, cancellationToken);
            members.Add(new NamedEmployeeDto(member.EmployeeId, employee?.DisplayName ?? "Unknown user", employee?.IsActive ?? false));
        }

        return new AssignmentRuleDto(rule.Mode, rule.PrimaryEmployeeId, primary?.DisplayName, rule.TeamName, members, rule.IsActive);
    }

    private async Task<string> DescribeAssignmentAsync(RequestTypeAssignmentRule? rule, string departmentName, CancellationToken cancellationToken)
    {
        if (rule is { IsActive: true, Mode: AssignmentMode.RoundRobin })
        {
            return $"{departmentName} (Round Robin)";
        }

        if (rule is null || !rule.IsActive || rule.Mode == AssignmentMode.DepartmentQueue || rule.PrimaryEmployeeId is not { } primaryId)
        {
            return $"{departmentName} Queue";
        }

        var primary = await employeeRepository.GetByIdAsync(primaryId, cancellationToken);
        var primaryName = primary?.DisplayName ?? "Unknown user";
        return rule.Mode == AssignmentMode.Team
            ? $"{rule.TeamName ?? "Team"} (primary: {primaryName})"
            : primaryName;
    }

    private static SlaPolicyDto ToSlaDto(RequestTypeSlaPolicy policy) => new(
        policy.PriorityId, policy.Trigger, policy.Unit,
        policy.FirstResponseTargetValue, policy.FirstResponseMaximumValue,
        policy.ResolutionTargetValue, policy.ResolutionMaximumValue,
        policy.IsImmediate, policy.ClockBasis, policy.PausesOnPendingCustomer, policy.PausesOnPendingInternal,
        policy.WarningThresholdPercent, policy.IsActive);

    private static string Describe(RequestType r) =>
        $"Name={r.Name};DepartmentId={r.DepartmentId};WorkflowId={r.WorkflowId};DefaultPriorityId={r.DefaultPriorityId};"
        + $"AllowAgentPriorityChange={r.AllowAgentPriorityChange};AllowPendingCustomer={r.AllowPendingCustomer};"
        + $"AllowPendingInternal={r.AllowPendingInternal};AllowReopen={r.AllowReopen};IsActive={r.IsActive}";

    private static string DescribeRule(RequestTypeAssignmentRule r) =>
        $"Mode={r.Mode};PrimaryEmployeeId={r.PrimaryEmployeeId};TeamName={r.TeamName};Members={string.Join("|", r.Members.Select(m => m.EmployeeId))};IsActive={r.IsActive}";

    private static string DescribeRequirement(RequestTypeApprovalRequirement r) =>
        $"ApprovalType={r.ApprovalType};TargetKind={r.TargetKind};TargetDepartmentId={r.TargetDepartmentId};TargetRoleName={r.TargetRoleName};TargetEmployeeId={r.TargetEmployeeId};BlocksWork={r.BlocksWorkUntilApproved};IsActive={r.IsActive}";

    private static string DescribeSla(RequestTypeSlaPolicy p) =>
        $"PriorityId={p.PriorityId};Trigger={p.Trigger};Unit={p.Unit};FR={p.FirstResponseTargetValue}-{p.FirstResponseMaximumValue};Res={p.ResolutionTargetValue}-{p.ResolutionMaximumValue};Immediate={p.IsImmediate};ClockBasis={p.ClockBasis};PauseCustomer={p.PausesOnPendingCustomer};PauseInternal={p.PausesOnPendingInternal};Warn={p.WarningThresholdPercent};IsActive={p.IsActive}";
}
