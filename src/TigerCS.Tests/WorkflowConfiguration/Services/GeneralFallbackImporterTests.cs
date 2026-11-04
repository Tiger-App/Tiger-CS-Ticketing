using Microsoft.EntityFrameworkCore;
using TigerCS.Domain.Modules.ClassificationAndRouting;
using TigerCS.Domain.Modules.SlaAndEscalation;
using TigerCS.Domain.Modules.WorkflowConfiguration;
using TigerCS.Infrastructure.Modules.WorkflowConfiguration.Seed;

namespace TigerCS.Tests.WorkflowConfiguration.Services;

/// <summary>Customer Services / General is created once, reused when it exists, never duplicated, and never assigned to anyone through a rule.</summary>
public class GeneralFallbackImporterTests
{
    private static readonly DateTime Now = new(2026, 11, 4, 8, 0, 0, DateTimeKind.Utc);

    [Fact]
    public async Task Creates_the_General_category_and_request_type_with_a_published_workflow_and_no_assignment_rule_or_sla()
    {
        await using var db = await WorkflowConfigurationTestDb.CreateSeededContextAsync();

        var result = await GeneralFallbackImporter.ImportAsync(db, Now);

        Assert.Equal(GeneralFallbackImportOutcome.Created, result.Outcome);
        Assert.True(result.CategoryCreated);
        Assert.True(result.RequestTypeCreated);
        var cs = await db.Departments.SingleAsync(d => d.Code == "CS");
        var type = await db.RequestTypes.SingleAsync(r => r.RequestTypeId == result.RequestTypeId);
        Assert.Equal("General", type.Name);
        Assert.Equal(cs.DepartmentId, type.DepartmentId);
        Assert.True(type.IsActive);
        Assert.Equal((byte)PriorityLevel.Medium, type.DefaultPriorityId);
        var published = await db.WorkflowTemplates.SingleAsync(w => w.WorkflowId == type.WorkflowId && w.Status == WorkflowVersionStatus.Published);
        Assert.Equal(1, published.VersionNumber);
        Assert.Equal("General", (await db.Categories.SingleAsync(c => c.CategoryId == result.CategoryId)).Name);
        Assert.Empty(await db.RequestTypeAssignmentRules.Where(r => r.RequestTypeId == type.RequestTypeId).ToListAsync());
        Assert.Empty(await db.RequestTypeSlaPolicies.Where(p => p.RequestTypeId == type.RequestTypeId).ToListAsync());
    }

    [Fact]
    public async Task A_second_run_creates_nothing()
    {
        await using var db = await WorkflowConfigurationTestDb.CreateSeededContextAsync();
        var first = await GeneralFallbackImporter.ImportAsync(db, Now);
        var types = await db.RequestTypes.CountAsync();
        var workflows = await db.Workflows.CountAsync();
        var templates = await db.WorkflowTemplates.CountAsync();
        var categories = await db.Categories.CountAsync();

        var second = await GeneralFallbackImporter.ImportAsync(db, Now);

        Assert.Equal(GeneralFallbackImportOutcome.AlreadyConfigured, second.Outcome);
        Assert.Equal(first.CategoryId, second.CategoryId);
        Assert.Equal(first.RequestTypeId, second.RequestTypeId);
        Assert.Equal(types, await db.RequestTypes.CountAsync());
        Assert.Equal(workflows, await db.Workflows.CountAsync());
        Assert.Equal(templates, await db.WorkflowTemplates.CountAsync());
        Assert.Equal(categories, await db.Categories.CountAsync());
    }

    [Fact]
    public async Task An_existing_General_category_is_reused_and_only_the_missing_request_type_is_created()
    {
        await using var db = await WorkflowConfigurationTestDb.CreateSeededContextAsync();
        var cs = await db.Departments.SingleAsync(d => d.Code == "CS");
        var existing = new Category("General", cs.DepartmentId);
        db.Categories.Add(existing);
        await db.SaveChangesAsync();

        var result = await GeneralFallbackImporter.ImportAsync(db, Now);

        Assert.Equal(GeneralFallbackImportOutcome.Created, result.Outcome);
        Assert.False(result.CategoryCreated);
        Assert.True(result.RequestTypeCreated);
        Assert.Equal(existing.CategoryId, result.CategoryId);
        Assert.Single(await db.Categories.Where(c => c.DepartmentId == cs.DepartmentId && c.Name == "General").ToListAsync());
    }

    [Fact]
    public async Task Duplicate_General_records_are_reported_and_nothing_is_written()
    {
        await using var db = await WorkflowConfigurationTestDb.CreateSeededContextAsync();
        var cs = await db.Departments.SingleAsync(d => d.Code == "CS");
        db.Categories.Add(new Category("General", cs.DepartmentId));
        db.Categories.Add(new Category("General", cs.DepartmentId));
        await db.SaveChangesAsync();
        var categories = await db.Categories.CountAsync();
        var types = await db.RequestTypes.CountAsync();

        var result = await GeneralFallbackImporter.ImportAsync(db, Now);

        Assert.Equal(GeneralFallbackImportOutcome.DuplicatesNeedReview, result.Outcome);
        Assert.Equal(categories, await db.Categories.CountAsync());
        Assert.Equal(types, await db.RequestTypes.CountAsync());
    }

    [Fact]
    public async Task A_missing_Customer_Services_department_is_never_created()
    {
        await using var db = WorkflowConfigurationTestDb.CreateContext();

        var result = await GeneralFallbackImporter.ImportAsync(db, Now);

        Assert.Equal(GeneralFallbackImportOutcome.DepartmentMissing, result.Outcome);
        Assert.Empty(await db.Departments.ToListAsync());
        Assert.Empty(await db.RequestTypes.ToListAsync());
    }

    [Fact]
    public async Task An_existing_General_request_type_without_a_published_workflow_is_reported_not_modified()
    {
        await using var db = await WorkflowConfigurationTestDb.CreateSeededContextAsync();
        var cs = await db.Departments.SingleAsync(d => d.Code == "CS");
        var workflow = new Workflow("X-GEN", "X General", null, Now);
        db.Workflows.Add(workflow);
        await db.SaveChangesAsync();
        db.RequestTypes.Add(new RequestType(cs.DepartmentId, "General", workflow.WorkflowId, (byte)PriorityLevel.Medium, false, true, true, true));
        await db.SaveChangesAsync();
        var templates = await db.WorkflowTemplates.CountAsync();

        var result = await GeneralFallbackImporter.ImportAsync(db, Now);

        Assert.Equal(GeneralFallbackImportOutcome.ExistingWorkflowNotPublished, result.Outcome);
        Assert.Equal(templates, await db.WorkflowTemplates.CountAsync());
        Assert.Empty(await db.Categories.Where(c => c.Name == "General").ToListAsync());
    }
}
