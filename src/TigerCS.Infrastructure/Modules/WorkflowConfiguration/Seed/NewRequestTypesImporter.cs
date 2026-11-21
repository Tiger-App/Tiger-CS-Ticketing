using Microsoft.EntityFrameworkCore;
using TigerCS.Domain.Modules.ClassificationAndRouting;
using TigerCS.Domain.Modules.IdentityAndAccess;
using TigerCS.Domain.Modules.SlaAndEscalation;
using TigerCS.Domain.Modules.WorkflowConfiguration;
using TigerCS.Infrastructure.Persistence;
using static TigerCS.Infrastructure.Modules.WorkflowConfiguration.Seed.NewRequestTypesBusinessReview;

namespace TigerCS.Infrastructure.Modules.WorkflowConfiguration.Seed;

/// <summary>What the importer did for one workbook row.</summary>
public enum NewRequestTypeImportOutcome : byte
{
    /// <summary>Created active, with its workflow version 1 Published.</summary>
    Created = 1,

    /// <summary>This import already created it (its workflow carries the Request Code). Left exactly as it is — administration edits made during UAT are never overwritten.</summary>
    AlreadyImported = 2,

    /// <summary>A request type of the same name already exists in the owning department. It is reused as-is (never modified), so no duplicate is created.</summary>
    ExistingRequestTypeReused = 3,

    /// <summary>A workflow or workflow version already uses the Request Code as its code but is not this import's request type. Never modified.</summary>
    SkippedCodeInUse = 4
}

/// <param name="RequestCode">The workbook's Request Code.</param>
/// <param name="Outcome">What happened.</param>
/// <param name="RequestTypeId">The request type created, previously imported or reused, when there is one.</param>
/// <param name="SlaConfigured">True when a <see cref="RequestTypeSlaPolicy"/> row exists for it from this import.</param>
/// <param name="SimilarExistingRequestType">An existing request type in the owning department with a similar (not identical) name, or null.</param>
/// <param name="ApprovalRequirementsAdded">The existing approvals this run configured on the request type (its workflow waits for them and none was configured yet).</param>
public sealed record NewRequestTypeImportRowResult(
    string RequestCode,
    NewRequestTypeImportOutcome Outcome,
    int? RequestTypeId,
    bool SlaConfigured,
    string? SimilarExistingRequestType,
    IReadOnlyList<ApprovalType>? ApprovalRequirementsAdded = null);

/// <param name="Rows">One result per workbook row, in workbook order.</param>
/// <param name="CreatedDepartments">The owning departments this run created because they were missing.</param>
/// <param name="ApprovalDepartmentMissing">True when a row's workflow waits for Accounting Approval but no Accounting department exists — the approval is then NOT configured (the department is never created here).</param>
public sealed record NewRequestTypeImportResult(
    IReadOnlyList<NewRequestTypeImportRowResult> Rows,
    IReadOnlyList<string> CreatedDepartments,
    bool ApprovalDepartmentMissing = false)
{
    public int Count(NewRequestTypeImportOutcome outcome) => Rows.Count(r => r.Outcome == outcome);
}

/// <summary>
/// Imports <see cref="NewRequestTypesBusinessReview"/> — the Customer
/// Service UAT baseline. <c>ImportNewRequestTypes_UAT.sql</c> is the
/// equivalent for an existing UAT database and follows exactly the same rules.
///
/// <list type="bullet">
///   <item><description><b>Available.</b> Every created request type is active with a Published workflow, so it can be used in UAT straight away; administrators refine or deactivate it from the Administration screens.</description></item>
///   <item><description><b>No duplicates.</b> An existing request type of the same name in the owning department is reused as-is.</description></item>
///   <item><description><b>Idempotent.</b> A row is recognised as already imported by its Request Code (the code of the workflow its request type points at); a second run creates nothing.</description></item>
///   <item><description><b>Additive only.</b> Never updates or deletes an existing row.</description></item>
///   <item><description><b>Basic.</b> The workbook's "Conditional" approvals are not configured (no rule, and no existing approval type matches those approvers), and no SLA row where the workbook gives no number ("Based on severity") — the standard behaviour applies until one is added from Administration.</description></item>
///   <item><description><b>Real approvals only.</b> The one approval step in the translations — Accounting's part of a NOC — is the EXISTING Accounting Approval, decided by the Accounting department. It is added to the request type (created or reused) only when that request type has no Accounting Approval requirement at all, active or not; nothing existing is changed. Supporting teams' steps are manual and informational.</description></item>
///   <item><description><b>Departments.</b> Owning departments are resolved by code, then exact name; Facilities Management and Leasing Customer Services are created when missing.</description></item>
///   <item><description><b>Transactional</b> on a relational provider: all rows or none.</description></item>
/// </list>
///
/// <para>
/// <b>First Response is not stored.</b> A <see cref="RequestTypeSlaPolicy"/>
/// row has one <see cref="SlaDurationUnit"/> for both of its deadlines; the
/// workbook gives First Response in business hours and Resolution in
/// business days. Resolution is stored as given (Days, business-hours clock).
/// </para>
/// </summary>
public static class NewRequestTypesImporter
{
    public static async Task<NewRequestTypeImportResult> ImportAsync(
        TigerCsDbContext dbContext, DateTime utcNow, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(dbContext);

        // Joins a transaction the caller already opened (the Production seeder wraps every importer in one) instead of
        // starting a second one, which EF Core refuses; only a transaction started here is committed here.
        await using var transaction = dbContext.Database.IsRelational() && dbContext.Database.CurrentTransaction is null
            ? await dbContext.Database.BeginTransactionAsync(cancellationToken)
            : null;

        var departments = await LoadDepartmentsAsync(dbContext, cancellationToken);
        var createdDepartments = new List<string>();
        foreach (var reference in CreatableOwningDepartments)
        {
            if (Find(departments, reference) is null)
            {
                dbContext.Departments.Add(new Department(reference.Name, reference.Code));
                createdDepartments.Add(reference.Name);
            }
        }

        if (createdDepartments.Count > 0)
        {
            await dbContext.SaveChangesAsync(cancellationToken);
            departments = await LoadDepartmentsAsync(dbContext, cancellationToken);
        }

        // A DEACTIVATED Accounting department is treated as absent: the import never wires a new approval to a retired department
        // (and never reactivates it). The affected approval is then simply not configured, like the missing-department case.
        var accountingDepartmentId = departments
            .FirstOrDefault(d => string.Equals(d.Code, WorkflowReferenceData.AccountingCode, StringComparison.OrdinalIgnoreCase))
            ?.DepartmentId;
        if (accountingDepartmentId is { } candidateAccountingId
            && !await dbContext.Departments.AsNoTracking().AnyAsync(d => d.DepartmentId == candidateAccountingId && d.IsActive, cancellationToken))
        {
            accountingDepartmentId = null;
        }
        var approvalDepartmentMissing = false;

        var results = new List<NewRequestTypeImportRowResult>();
        foreach (var row in Rows())
        {
            var departmentId = Find(departments, row.OwningDepartment)?.DepartmentId
                ?? throw new InvalidOperationException($"{row.RequestCode}: owning department '{row.OwningDepartment.Name}' ({row.OwningDepartment.Code}) does not exist.");
            var result = await ImportRowAsync(dbContext, row, departmentId, utcNow, cancellationToken);

            if (result.RequestTypeId is { } requestTypeId && row.Approvals.Count > 0)
            {
                if (accountingDepartmentId is null)
                {
                    approvalDepartmentMissing = true;
                }
                else
                {
                    result = result with
                    {
                        ApprovalRequirementsAdded = await AddMissingApprovalsAsync(
                            dbContext, row, requestTypeId, accountingDepartmentId.Value, cancellationToken)
                    };
                }
            }

            results.Add(result);
        }

        // Intake presents Department as Request Category and its children as
        // Request Types. Each configured child needs its own exact routing row;
        // reusing an arbitrary category would misclassify the ticket history.
        var routingCategories = await dbContext.Categories.ToListAsync(cancellationToken);
        var activeTypes = await dbContext.RequestTypes.Where(r => r.IsActive).ToListAsync(cancellationToken);
        foreach (var requestType in activeTypes)
        {
            if (routingCategories.Any(c => c.DepartmentId == requestType.DepartmentId
                    && string.Equals(c.Name.Trim(), requestType.Name.Trim(), StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }
            var category = new Category(requestType.Name, requestType.DepartmentId);
            dbContext.Categories.Add(category);
            routingCategories.Add(category);
        }
        await dbContext.SaveChangesAsync(cancellationToken);

        if (transaction is not null)
        {
            await transaction.CommitAsync(cancellationToken);
        }

        return new NewRequestTypeImportResult(results, createdDepartments, approvalDepartmentMissing);
    }

    /// <summary>
    /// Configures the existing approvals a row's workflow waits for — today
    /// only Accounting Approval, targeted at the Accounting department exactly
    /// as the reference seed targets Send Receipts'. A requirement that
    /// already exists for the pair, active or deactivated, is left alone (the
    /// pair is unique, and reactivating one would overturn an operator).
    /// </summary>
    private static async Task<IReadOnlyList<ApprovalType>> AddMissingApprovalsAsync(
        TigerCsDbContext dbContext, Row row, int requestTypeId, int accountingDepartmentId, CancellationToken cancellationToken)
    {
        var added = new List<ApprovalType>();
        foreach (var approvalType in row.Approvals)
        {
            if (approvalType != ApprovalType.AccountingApproval)
            {
                throw new InvalidOperationException($"{row.RequestCode}: only Accounting Approval is configured by this import, not {approvalType}.");
            }

            if (await dbContext.RequestTypeApprovalRequirements.AnyAsync(
                    r => r.RequestTypeId == requestTypeId && r.ApprovalType == approvalType, cancellationToken))
            {
                continue;
            }

            dbContext.RequestTypeApprovalRequirements.Add(
                RequestTypeApprovalRequirement.ForDepartment(requestTypeId, approvalType, accountingDepartmentId));
            added.Add(approvalType);
        }

        if (added.Count > 0)
        {
            await dbContext.SaveChangesAsync(cancellationToken);
        }

        return added;
    }

    private sealed record DepartmentKey(int DepartmentId, string Code, string Name);

    private static async Task<List<DepartmentKey>> LoadDepartmentsAsync(TigerCsDbContext dbContext, CancellationToken cancellationToken) =>
        await dbContext.Departments.AsNoTracking()
            .Select(d => new DepartmentKey(d.DepartmentId, d.Code, d.Name))
            .ToListAsync(cancellationToken);

    /// <summary>By code first (unique), then by exact name (unique).</summary>
    private static DepartmentKey? Find(IReadOnlyList<DepartmentKey> departments, DepartmentRef reference) =>
        departments.FirstOrDefault(d => string.Equals(d.Code, reference.Code, StringComparison.OrdinalIgnoreCase))
        ?? departments.FirstOrDefault(d => string.Equals(d.Name, reference.Name, StringComparison.OrdinalIgnoreCase));

    private static async Task<NewRequestTypeImportRowResult> ImportRowAsync(
        TigerCsDbContext dbContext, Row row, int departmentId, DateTime utcNow, CancellationToken cancellationToken)
    {
        string? similar = null;
        if (row.SimilarExistingRequestTypeName is { } similarName
            && await dbContext.RequestTypes.AnyAsync(r => r.DepartmentId == departmentId && r.Name == similarName, cancellationToken))
        {
            similar = similarName;
        }

        NewRequestTypeImportRowResult Result(NewRequestTypeImportOutcome outcome, int? requestTypeId = null, bool sla = false) =>
            new(row.RequestCode, outcome, requestTypeId, sla, similar);

        var workflow = await dbContext.Workflows.AsNoTracking()
            .FirstOrDefaultAsync(w => w.Code == row.WorkflowCode, cancellationToken);
        if (workflow is not null)
        {
            var imported = await dbContext.RequestTypes.AsNoTracking()
                .FirstOrDefaultAsync(r => r.WorkflowId == workflow.WorkflowId && r.DepartmentId == departmentId, cancellationToken);
            if (imported is null)
            {
                return Result(NewRequestTypeImportOutcome.SkippedCodeInUse);
            }

            var hasSla = await dbContext.RequestTypeSlaPolicies.AnyAsync(p => p.RequestTypeId == imported.RequestTypeId, cancellationToken);
            return Result(NewRequestTypeImportOutcome.AlreadyImported, imported.RequestTypeId, hasSla);
        }

        if (await dbContext.WorkflowTemplates.AnyAsync(t => t.Code == row.WorkflowCode, cancellationToken))
        {
            return Result(NewRequestTypeImportOutcome.SkippedCodeInUse);
        }

        var existing = await dbContext.RequestTypes.AsNoTracking()
            .FirstOrDefaultAsync(r => r.DepartmentId == departmentId && r.Name == row.Name, cancellationToken);
        if (existing is not null)
        {
            return Result(NewRequestTypeImportOutcome.ExistingRequestTypeReused, existing.RequestTypeId);
        }

        workflow = new Workflow(row.WorkflowCode, row.WorkflowName, row.WorkflowDescription, utcNow);
        dbContext.Workflows.Add(workflow);
        await dbContext.SaveChangesAsync(cancellationToken);

        // Version 1's code is the workflow code — the same convention the
        // Workflow Designer uses (AdminWorkflowAppService.VersionCode).
        var version = new WorkflowTemplate(
            workflow.WorkflowId, versionNumber: 1, row.WorkflowCode, row.WorkflowName, row.WorkflowDescription,
            allowsPendingCustomer: false, allowsPendingInternal: false, requiresApproval: false,
            utcNow, createdByEmployeeId: null);
        byte sequence = 1;
        foreach (var step in row.Steps)
        {
            version.AddStep(sequence++, step.Name, step.Kind, step.IsOptional, step.ApprovalType);
        }

        // The validated publish path, never PublishAsSeededBaseline: a flow
        // that the Workflow Designer would reject must not go live.
        version.Publish(utcNow, publishedByEmployeeId: null);
        dbContext.WorkflowTemplates.Add(version);

        var requestType = new RequestType(
            departmentId,
            row.Name,
            workflow.WorkflowId,
            (byte)row.Priority,
            allowAgentPriorityChange: false,
            allowPendingCustomer: false,
            allowPendingInternal: false,
            allowReopen: row.AllowReopenFlag,
            requiredFieldsJson: row.RequiredFieldsJson,
            isActive: true);
        dbContext.RequestTypes.Add(requestType);
        await dbContext.SaveChangesAsync(cancellationToken);

        var slaConfigured = false;
        if (row.ResolutionBusinessDays is { } days)
        {
            dbContext.RequestTypeSlaPolicies.Add(new RequestTypeSlaPolicy(
                requestType.RequestTypeId,
                (byte)row.Priority,
                SlaTriggerType.TicketCreated,
                SlaDurationUnit.Days,
                firstResponseTargetValue: null,
                firstResponseMaximumValue: null,
                resolutionTargetValue: days,
                resolutionMaximumValue: null,
                clockBasis: SlaClockBasis.BusinessHours));
            await dbContext.SaveChangesAsync(cancellationToken);
            slaConfigured = true;
        }

        return Result(NewRequestTypeImportOutcome.Created, requestType.RequestTypeId, slaConfigured);
    }
}
