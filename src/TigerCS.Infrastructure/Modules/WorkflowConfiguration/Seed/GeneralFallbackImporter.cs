using Microsoft.EntityFrameworkCore;
using TigerCS.Domain.Modules.ClassificationAndRouting;
using TigerCS.Domain.Modules.SlaAndEscalation;
using TigerCS.Domain.Modules.WorkflowConfiguration;
using TigerCS.Infrastructure.Persistence;

namespace TigerCS.Infrastructure.Modules.WorkflowConfiguration.Seed;

/// <summary>What <see cref="GeneralFallbackImporter"/> did.</summary>
public enum GeneralFallbackImportOutcome : byte
{
    /// <summary>The category, request type and a Published workflow were all missing or incomplete and were created.</summary>
    Created = 1,

    /// <summary>Customer Services / General already exists with a Published workflow. Nothing was written.</summary>
    AlreadyConfigured = 2,

    /// <summary>The Customer Services department (code CS) does not exist. It is never created here; nothing was written.</summary>
    DepartmentMissing = 3,

    /// <summary>More than one General category or request type exists in Customer Services. Nothing was written - a person must decide which one is canonical.</summary>
    DuplicatesNeedReview = 4,

    /// <summary>A General request type exists but its workflow has no Published version. Nothing was changed; publish it from Administration.</summary>
    ExistingWorkflowNotPublished = 5,

    /// <summary>A workflow or version already uses the code but is not the General request type's. Never modified.</summary>
    SkippedCodeInUse = 6
}

/// <param name="Outcome">What happened.</param>
/// <param name="CategoryId">The General category (created or existing), when there is exactly one.</param>
/// <param name="RequestTypeId">The General request type (created or existing), when there is exactly one.</param>
/// <param name="CategoryCreated">True when this run created the category.</param>
/// <param name="RequestTypeCreated">True when this run created the request type (and its workflow).</param>
public sealed record GeneralFallbackImportResult(
    GeneralFallbackImportOutcome Outcome, int? CategoryId, int? RequestTypeId, bool CategoryCreated, bool RequestTypeCreated);

/// <summary>
/// Makes sure the approved default classification exists: department <b>Customer Services</b> (code <c>CS</c>), category <b>General</b> and request
/// type <b>General</b> with a Published workflow - additively and idempotently, with the same construction as
/// <see cref="LeasingInquiryRequestTypeImporter"/>.
///
/// <list type="bullet">
///   <item><description><b>Reuse, never duplicate.</b> An existing General category or request type in Customer Services is used as it is; a
///   second run creates nothing. Duplicates already present are reported, not "fixed".</description></item>
///   <item><description><b>Additive.</b> Never updates or deletes a row and never creates the department.</description></item>
///   <item><description><b>No assignment rule, no SLA row.</b> The ticket is owned by a CS Agent through the existing confirmed-agent / sequential
///   Round-Robin rules - never by a department employee through a rule configured here.</description></item>
/// </list>
/// Nothing calls this automatically: it is run deliberately (the equivalent SQL is <c>EnsureDefaultGeneralClassification.sql</c>).
/// </summary>
public static class GeneralFallbackImporter
{
    public const string DepartmentCode = "CS";
    public const string Name = "General";
    public const string WorkflowCode = "CS-GEN-001";

    public static async Task<GeneralFallbackImportResult> ImportAsync(
        TigerCsDbContext dbContext, DateTime utcNow, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(dbContext);

        var department = await dbContext.Departments.AsNoTracking()
            .FirstOrDefaultAsync(d => d.Code == DepartmentCode, cancellationToken);
        if (department is null)
        {
            return new GeneralFallbackImportResult(GeneralFallbackImportOutcome.DepartmentMissing, null, null, false, false);
        }

        var departmentId = department.DepartmentId;
        var categories = await dbContext.Categories.AsNoTracking()
            .Where(c => c.DepartmentId == departmentId && c.Name == Name).ToListAsync(cancellationToken);
        var requestTypes = await dbContext.RequestTypes.AsNoTracking()
            .Where(r => r.DepartmentId == departmentId && r.Name == Name).ToListAsync(cancellationToken);

        if (categories.Count > 1 || requestTypes.Count > 1)
        {
            return new GeneralFallbackImportResult(GeneralFallbackImportOutcome.DuplicatesNeedReview, null, null, false, false);
        }

        var existingType = requestTypes.SingleOrDefault();
        if (existingType is not null)
        {
            var published = await dbContext.WorkflowTemplates.AsNoTracking()
                .AnyAsync(t => t.WorkflowId == existingType.WorkflowId && t.Status == WorkflowVersionStatus.Published, cancellationToken);
            if (!published)
            {
                return new GeneralFallbackImportResult(
                    GeneralFallbackImportOutcome.ExistingWorkflowNotPublished, categories.SingleOrDefault()?.CategoryId, existingType.RequestTypeId, false, false);
            }
        }
        else if (await dbContext.Workflows.AnyAsync(w => w.Code == WorkflowCode, cancellationToken)
            || await dbContext.WorkflowTemplates.AnyAsync(t => t.Code == WorkflowCode, cancellationToken))
        {
            return new GeneralFallbackImportResult(GeneralFallbackImportOutcome.SkippedCodeInUse, categories.SingleOrDefault()?.CategoryId, null, false, false);
        }

        if (categories.Count == 1 && existingType is not null)
        {
            return new GeneralFallbackImportResult(
                GeneralFallbackImportOutcome.AlreadyConfigured, categories[0].CategoryId, existingType.RequestTypeId, false, false);
        }

        // Joins a transaction the caller already opened instead of starting a second one; only a transaction started here is committed here.
        await using var transaction = dbContext.Database.IsRelational() && dbContext.Database.CurrentTransaction is null
            ? await dbContext.Database.BeginTransactionAsync(cancellationToken)
            : null;

        var requestTypeId = existingType?.RequestTypeId;
        var requestTypeCreated = false;
        if (existingType is null)
        {
            var workflowName = $"{WorkflowCode} {Name}";
            const string description = "Default classification: Customer Services / General. CS Queue → Agent → Review → Resolve → Close. Used when a ticket has no valid classification.";
            var workflow = new Workflow(WorkflowCode, workflowName, description, utcNow);
            dbContext.Workflows.Add(workflow);
            await dbContext.SaveChangesAsync(cancellationToken);

            var version = new WorkflowTemplate(
                workflow.WorkflowId, versionNumber: 1, WorkflowCode, workflowName, description,
                allowsPendingCustomer: false, allowsPendingInternal: false, requiresApproval: false,
                utcNow, createdByEmployeeId: null);
            byte sequence = 1;
            version.AddStep(sequence++, "Ticket Created", WorkflowStepKind.Created, false, null);
            version.AddStep(sequence++, "CS Queue", WorkflowStepKind.Assigned, false, null);
            version.AddStep(sequence++, "Agent", WorkflowStepKind.Assigned, false, null);
            version.AddStep(sequence++, "Review", WorkflowStepKind.InProgress, false, null);
            version.AddStep(sequence++, "Resolve", WorkflowStepKind.Resolved, false, null);
            version.AddStep(sequence, "Close", WorkflowStepKind.Closed, false, null);
            version.Publish(utcNow, publishedByEmployeeId: null);
            dbContext.WorkflowTemplates.Add(version);

            var requestType = new RequestType(
                departmentId, Name, workflow.WorkflowId, (byte)PriorityLevel.Medium,
                allowAgentPriorityChange: false, allowPendingCustomer: true, allowPendingInternal: true, allowReopen: true,
                requiredFieldsJson: null, isActive: true);
            dbContext.RequestTypes.Add(requestType);
            await dbContext.SaveChangesAsync(cancellationToken);
            requestTypeId = requestType.RequestTypeId;
            requestTypeCreated = true;
        }

        var categoryId = categories.SingleOrDefault()?.CategoryId;
        var categoryCreated = false;
        if (categoryId is null)
        {
            var category = new Category(Name, departmentId);
            dbContext.Categories.Add(category);
            await dbContext.SaveChangesAsync(cancellationToken);
            categoryId = category.CategoryId;
            categoryCreated = true;
        }

        if (transaction is not null)
        {
            await transaction.CommitAsync(cancellationToken);
        }

        return new GeneralFallbackImportResult(GeneralFallbackImportOutcome.Created, categoryId, requestTypeId, categoryCreated, requestTypeCreated);
    }
}
