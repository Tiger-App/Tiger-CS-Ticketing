using TigerCS.Application.Abstractions;
using TigerCS.Application.Modules.IdentityAndAccess.Abstractions;
using TigerCS.Application.Modules.Ticketing.Abstractions;
using TigerCS.Application.Modules.WorkflowConfiguration.Abstractions;
using TigerCS.Domain.Modules.ClassificationAndRouting;
using TigerCS.Domain.Modules.IdentityAndAccess;
using TigerCS.Domain.Modules.Ticketing;
using TigerCS.Domain.Modules.WorkflowConfiguration;

namespace TigerCS.Application.Modules.Ticketing.Services;

/// <summary>The approved default classification, resolved from configuration: Customer Services / General / General plus its published workflow.</summary>
public sealed record DefaultClassification(Department Department, Category Category, RequestType RequestType, WorkflowTemplate PublishedWorkflow)
{
    /// <summary>The priority the General request type is configured with - used only when a brand-new ticket needs one and the caller supplied none.</summary>
    public byte DefaultPriorityId => RequestType.DefaultPriorityId;
}

/// <summary>Why the default classification could not be resolved. Every gap is reported, never papered over by creating duplicates at runtime.</summary>
public enum DefaultClassificationGap
{
    None,
    DepartmentMissing,
    DepartmentAmbiguous,
    CategoryMissing,
    CategoryAmbiguous,
    RequestTypeMissing,
    RequestTypeAmbiguous,
    WorkflowNotPublished
}

/// <summary>The default classification, or the precise configuration gap that prevents it.</summary>
public sealed record DefaultClassificationResolution(DefaultClassification? Value, DefaultClassificationGap Gap, string? Detail)
{
    public bool IsAvailable => Value is not null;

    public static DefaultClassificationResolution Available(DefaultClassification value) => new(value, DefaultClassificationGap.None, null);

    public static DefaultClassificationResolution Unavailable(DefaultClassificationGap gap, string detail) => new(null, gap, detail);
}

/// <summary>How well a ticket's stored classification stands up. Anything other than <see cref="Valid"/> is a candidate for the fallback.</summary>
public enum TicketClassificationState
{
    /// <summary>Category and request type exist and belong together.</summary>
    Valid,

    /// <summary>Neither a category nor a request type.</summary>
    NoClassification,

    /// <summary>A category that resolves but no request type.</summary>
    CategoryOnly,

    /// <summary>A request type but no category.</summary>
    RequestTypeOnly,

    /// <summary>A category or request type id that no longer resolves to a row.</summary>
    InvalidReference,

    /// <summary>The category and the request type belong to different departments, so they cannot both be right.</summary>
    Inconsistent
}

/// <summary>What <c>DefaultClassificationService.EnsureClassifiedAsync</c> did.</summary>
public enum DefaultClassificationApplyOutcome
{
    /// <summary>The fallback was recorded on the ticket.</summary>
    Applied,

    /// <summary>The ticket already has a valid classification (or already is on the fallback): nothing was changed.</summary>
    NotNeeded,

    /// <summary>The ticket is outside the fallback's scope (Collections): nothing was changed.</summary>
    Excluded,

    /// <summary>The fallback is not configured: nothing was changed.</summary>
    ConfigurationMissing,

    TicketNotFound
}

/// <summary>
/// The one place the approved default classification lives: <b>every ticket has a valid classification</b>, and a ticket with none that can
/// be trusted is classified <b>Customer Services / General</b> (department, category and request type) - never left "Unclassified".
///
/// <list type="bullet">
///   <item><description><b>Configuration, not invention.</b> The department (code <c>CS</c>, else the unique "Customer Service(s)" by name), its
///   <c>General</c> category and its <c>General</c> request type with a Published workflow are looked up. Nothing is created here - a missing or
///   duplicated record is reported as a <see cref="DefaultClassificationGap"/> and the caller keeps its previous behaviour. The idempotent
///   <c>GeneralFallbackImporter</c> / SQL script creates the records once, without duplicates.</description></item>
///   <item><description><b>Valid classifications are never touched.</b> <see cref="AssessAsync"/> decides; only a missing, partial or dangling
///   classification takes the fallback. A ticket already on the fallback stays as it is.</description></item>
///   <item><description><b>Classification and ownership are independent.</b> Applying the fallback to an existing ticket changes the category,
///   request type and pinned workflow only - not the owner, department, priority, SLA, status or history.</description></item>
///   <item><description><b>Collections is out of scope.</b> Tickets that belong to the Collections department are never classified here.</description></item>
/// </list>
/// </summary>
public sealed class DefaultClassificationService(
    IDepartmentRepository departmentRepository,
    ICategoryRepository categoryRepository,
    IRequestTypeRepository requestTypeRepository,
    IWorkflowTemplateRepository workflowTemplateRepository,
    ITicketRepository? ticketRepository = null,
    ITicketingUnitOfWork? unitOfWork = null,
    IAuditEntryWriter? auditWriter = null)
{
    public const string DepartmentCode = "CS";
    public const string CategoryName = "General";
    public const string RequestTypeName = "General";

    /// <summary>What the UI shows for a ticket on the fallback.</summary>
    public const string DisplayLabel = "Customer Services / General";

    /// <summary>The Collections department code: its tickets are never given the fallback.</summary>
    public const string CollectionsDepartmentCode = "COL";

    private static readonly string[] DepartmentNames = ["Customer Services", "Customer Service"];

    /// <summary>True for the Collections department, whose tickets are out of scope of this rule.</summary>
    public static bool IsExcluded(Department? department) =>
        department is not null && string.Equals(department.Code?.Trim(), CollectionsDepartmentCode, StringComparison.OrdinalIgnoreCase);

    /// <summary>Finds Customer Services / General, reusing the existing records. Never creates anything.</summary>
    public async Task<DefaultClassificationResolution> ResolveAsync(CancellationToken cancellationToken = default)
    {
        var departments = await departmentRepository.ListAsync(activeOnly: true, cancellationToken);

        var byCode = departments.Where(d => string.Equals(d.Code?.Trim(), DepartmentCode, StringComparison.OrdinalIgnoreCase)).ToList();
        if (byCode.Count == 0)
        {
            byCode = departments
                .Where(d => DepartmentNames.Any(n => string.Equals(d.Name?.Trim(), n, StringComparison.OrdinalIgnoreCase)))
                .ToList();
        }

        if (byCode.Count == 0)
        {
            return DefaultClassificationResolution.Unavailable(
                DefaultClassificationGap.DepartmentMissing, $"No active department with code '{DepartmentCode}' (Customer Services) exists.");
        }

        if (byCode.Count > 1)
        {
            return DefaultClassificationResolution.Unavailable(
                DefaultClassificationGap.DepartmentAmbiguous, "More than one active department matches Customer Services.");
        }

        var department = byCode[0];

        var categories = (await categoryRepository.ListAsync(activeOnly: true, department.DepartmentId, cancellationToken))
            .Where(c => string.Equals(c.Name?.Trim(), CategoryName, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (categories.Count == 0)
        {
            return DefaultClassificationResolution.Unavailable(
                DefaultClassificationGap.CategoryMissing, $"Department {department.Code} has no active '{CategoryName}' category.");
        }

        if (categories.Count > 1)
        {
            return DefaultClassificationResolution.Unavailable(
                DefaultClassificationGap.CategoryAmbiguous, $"Department {department.Code} has more than one active '{CategoryName}' category.");
        }

        var requestTypes = (await requestTypeRepository.ListActiveByDepartmentAsync(department.DepartmentId, cancellationToken))
            .Where(r => string.Equals(r.Name?.Trim(), RequestTypeName, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (requestTypes.Count == 0)
        {
            return DefaultClassificationResolution.Unavailable(
                DefaultClassificationGap.RequestTypeMissing, $"Department {department.Code} has no active '{RequestTypeName}' request type.");
        }

        if (requestTypes.Count > 1)
        {
            return DefaultClassificationResolution.Unavailable(
                DefaultClassificationGap.RequestTypeAmbiguous, $"Department {department.Code} has more than one active '{RequestTypeName}' request type.");
        }

        var workflow = await workflowTemplateRepository.GetPublishedAsync(requestTypes[0].WorkflowId, cancellationToken);
        if (workflow is null)
        {
            return DefaultClassificationResolution.Unavailable(
                DefaultClassificationGap.WorkflowNotPublished, $"The '{RequestTypeName}' request type has no published workflow version.");
        }

        return DefaultClassificationResolution.Available(new DefaultClassification(department, categories[0], requestTypes[0], workflow));
    }

    /// <summary>True when the stored ids ARE the fallback (Customer Services / General / General).</summary>
    public async Task<bool> IsDefaultAsync(int? categoryId, int? requestTypeId, CancellationToken cancellationToken = default)
    {
        if (categoryId is null || requestTypeId is null)
        {
            return false;
        }

        var resolution = await ResolveAsync(cancellationToken);
        return resolution.Value is { } d && d.Category.CategoryId == categoryId && d.RequestType.RequestTypeId == requestTypeId;
    }

    /// <summary>
    /// Judges a stored classification. Existence is what matters, not "active": a historical ticket keeps a category or request type that
    /// was retired later, and that is still a valid classification.
    /// </summary>
    public async Task<TicketClassificationState> AssessAsync(int? categoryId, int? requestTypeId, CancellationToken cancellationToken = default)
    {
        if (categoryId is null && requestTypeId is null)
        {
            return TicketClassificationState.NoClassification;
        }

        Category? category = null;
        if (categoryId is { } cid)
        {
            category = await categoryRepository.GetByIdAsync(cid, cancellationToken);
            if (category is null)
            {
                return TicketClassificationState.InvalidReference;
            }
        }

        RequestType? requestType = null;
        if (requestTypeId is { } rid)
        {
            requestType = await requestTypeRepository.GetByIdAsync(rid, cancellationToken);
            if (requestType is null)
            {
                return TicketClassificationState.InvalidReference;
            }
        }

        if (category is null)
        {
            return TicketClassificationState.RequestTypeOnly;
        }

        if (requestType is null)
        {
            return TicketClassificationState.CategoryOnly;
        }

        return requestType.DepartmentId == category.DepartmentId ? TicketClassificationState.Valid : TicketClassificationState.Inconsistent;
    }

    /// <summary>
    /// Applies the fallback to an EXISTING ticket whose classification is missing, partial or invalid, and to nothing else. Changes the
    /// category, request type and pinned workflow only; the owner, department, priority, SLA, status and history are untouched, and the
    /// change is audited. Does not save when <paramref name="save"/> is false (the caller owns the transaction).
    /// </summary>
    public async Task<DefaultClassificationApplyOutcome> EnsureClassifiedAsync(
        long ticketId, string reason, Guid? actorEmployeeId = null, bool save = true, CancellationToken cancellationToken = default)
    {
        if (ticketRepository is null || unitOfWork is null || auditWriter is null)
        {
            throw new InvalidOperationException("EnsureClassifiedAsync needs the ticket repository, unit of work and audit writer.");
        }

        var ticket = await ticketRepository.GetByIdAsync(ticketId, cancellationToken);
        if (ticket is null)
        {
            return DefaultClassificationApplyOutcome.TicketNotFound;
        }

        return await EnsureClassifiedAsync(ticket, reason, actorEmployeeId, save, cancellationToken);
    }

    /// <inheritdoc cref="EnsureClassifiedAsync(long, string, Guid?, bool, CancellationToken)"/>
    public async Task<DefaultClassificationApplyOutcome> EnsureClassifiedAsync(
        Ticket ticket, string reason, Guid? actorEmployeeId = null, bool save = true, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(ticket);
        if (unitOfWork is null || auditWriter is null)
        {
            throw new InvalidOperationException("EnsureClassifiedAsync needs the unit of work and audit writer.");
        }

        var resolution = await ResolveAsync(cancellationToken);
        if (resolution.Value is not { } fallback)
        {
            return DefaultClassificationApplyOutcome.ConfigurationMissing;
        }

        if (await IsCollectionsTicketAsync(ticket, cancellationToken))
        {
            return DefaultClassificationApplyOutcome.Excluded;
        }

        var state = await AssessAsync(ticket.CategoryId, ticket.RequestTypeId, cancellationToken);
        if (state == TicketClassificationState.Valid
            || (ticket.CategoryId == fallback.Category.CategoryId && ticket.RequestTypeId == fallback.RequestType.RequestTypeId))
        {
            return DefaultClassificationApplyOutcome.NotNeeded;
        }

        var before = $"CategoryId={ticket.CategoryId?.ToString() ?? "(none)"};RequestTypeId={ticket.RequestTypeId?.ToString() ?? "(none)"}";
        ticket.ApplyDefaultClassification(
            fallback.Category.CategoryId, fallback.RequestType.RequestTypeId, fallback.PublishedWorkflow.WorkflowTemplateId);

        await auditWriter.WriteAsync(
            actorEmployeeId, "ApplyDefaultClassification", "Ticket", ticket.TicketId.ToString(),
            beforeValue: before,
            afterValue:
                $"CategoryId={fallback.Category.CategoryId};RequestTypeId={fallback.RequestType.RequestTypeId};Default={DisplayLabel};"
                + $"State={state};Reason={reason};DepartmentId={ticket.CurrentDepartmentId} (unchanged);Owner={ticket.CurrentOwnerEmployeeId?.ToString() ?? "(none)"} (unchanged)",
            Guid.NewGuid(), cancellationToken);

        if (save)
        {
            await unitOfWork.SaveChangesAsync(cancellationToken);
        }

        return DefaultClassificationApplyOutcome.Applied;
    }

    /// <summary>A ticket that belongs to Collections (now, or where it originated) is out of scope.</summary>
    public async Task<bool> IsCollectionsTicketAsync(Ticket ticket, CancellationToken cancellationToken = default)
    {
        foreach (var departmentId in new[] { ticket.CurrentDepartmentId, ticket.OriginatingDepartmentId }.Distinct())
        {
            if (IsExcluded(await departmentRepository.GetByIdAsync(departmentId, cancellationToken)))
            {
                return true;
            }
        }

        return false;
    }
}
