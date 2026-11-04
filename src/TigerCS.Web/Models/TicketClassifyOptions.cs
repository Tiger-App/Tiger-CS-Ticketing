using TigerCS.Application.Modules.Administration.Dto;
using TigerCS.Application.Modules.ClassificationAndRouting.Dto;

namespace TigerCS.Web.Models;

/// <summary>Whether the Classify form has anything valid to offer for one ticket.</summary>
public enum ClassifyAvailability
{
    /// <summary>At least one active category of the ticket's department exists, so the form can be completed.</summary>
    Available,

    /// <summary>The category or request-type lists could not be read (service error) — not a statement about configuration.</summary>
    LoadFailed,

    /// <summary>The lists were read and the ticket's department has no active category: configuration, not a fault.</summary>
    NoCategories
}

/// <summary>A request type the employee may choose, shown by its name.</summary>
public sealed record ClassifyRequestTypeOption(int RequestTypeId, string Name);

/// <summary>A category the employee may choose, with the request types that belong under it.</summary>
public sealed record ClassifyCategoryOption(int CategoryId, string Name, IReadOnlyList<ClassifyRequestTypeOption> RequestTypes);

/// <summary>
/// What the Classify form offers for an Unclassified ticket, derived only from configuration the Api already
/// exposes — nothing is invented and nothing is fetched twice.
///
/// <para>
/// <b>Scope.</b> Classification names the request; it never re-routes the ticket (the Api refuses a category or request type
/// of another department). So only the ticket's own department's active categories and active request types with a
/// published workflow are offered.
/// </para>
///
/// <para>
/// <b>Dependency.</b> A request type belongs under the category of the same department with the same name — the rule New
/// Ticket's picker already uses (see <see cref="NewTicketRequestTypePicker"/>). A request type with no unique same-name
/// category cannot be reached through any category; it is reported in <see cref="UnreachableRequestTypes"/> instead of being
/// attached to an arbitrary one. A category with no request type of its own (a legacy routing-only category such as General
/// Inquiry) classifies on its own, exactly as the Api allows.
/// </para>
/// </summary>
public sealed record TicketClassifyOptions(
    ClassifyAvailability Availability,
    IReadOnlyList<ClassifyCategoryOption> Categories,
    IReadOnlyList<ClassifyRequestTypeOption> UnreachableRequestTypes)
{
    public static TicketClassifyOptions Failed { get; } = new(ClassifyAvailability.LoadFailed, [], []);

    public bool IsAvailable => Availability == ClassifyAvailability.Available;

    /// <summary>True when the named category is one with request types to choose from (so one must be chosen).</summary>
    public bool CategoryNeedsRequestType(int categoryId) =>
        Categories.FirstOrDefault(c => c.CategoryId == categoryId)?.RequestTypes.Count > 0;

    /// <summary>Whether this request type may be classified under this category.</summary>
    public bool Allows(int categoryId, int requestTypeId) =>
        Categories.FirstOrDefault(c => c.CategoryId == categoryId)?.RequestTypes.Any(r => r.RequestTypeId == requestTypeId) == true;

    /// <summary>The category whose name matches the request type the AI already set, if the configuration has exactly one.</summary>
    public ClassifyCategoryOption? CategoryForRequestType(string? requestTypeName) =>
        string.IsNullOrWhiteSpace(requestTypeName)
            ? null
            : Categories.SingleOrDefault(c => SameName(c.Name, requestTypeName));

    public static TicketClassifyOptions Build(
        int departmentId, IEnumerable<CategoryDto> categories, IEnumerable<RequestTypeOptionDto> requestTypes)
    {
        var departmentCategories = categories.Where(c => c.DepartmentId == departmentId).ToList();
        var usable = requestTypes.Where(r => r.DepartmentId == departmentId && r.HasPublishedWorkflow).ToList();

        var options = new List<ClassifyCategoryOption>();
        var reached = new HashSet<int>();
        foreach (var category in departmentCategories.OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase).ThenBy(c => c.CategoryId))
        {
            var sameName = departmentCategories.Count(c => SameName(c.Name, category.Name));
            var children = sameName == 1
                ? usable.Where(r => SameName(r.Name, category.Name)).OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase).ToList()
                : [];
            foreach (var child in children)
            {
                reached.Add(child.RequestTypeId);
            }

            options.Add(new ClassifyCategoryOption(
                category.CategoryId, category.Name, [.. children.Select(r => new ClassifyRequestTypeOption(r.RequestTypeId, r.Name))]));
        }

        var unreachable = usable
            .Where(r => !reached.Contains(r.RequestTypeId))
            .OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase)
            .Select(r => new ClassifyRequestTypeOption(r.RequestTypeId, r.Name))
            .ToList();

        return new TicketClassifyOptions(
            options.Count == 0 ? ClassifyAvailability.NoCategories : ClassifyAvailability.Available, options, unreachable);
    }

    /// <summary>
    /// The options for correcting a ticket's Department as well as its Category and Request Type: every department's categories (each with its own
    /// request types, paired within its department), labelled "Department - Category" when more than one department is offered.
    /// </summary>
    public static TicketClassifyOptions BuildAcrossDepartments(
        IEnumerable<CategoryDto> categories, IEnumerable<RequestTypeOptionDto> requestTypes)
    {
        var all = categories.ToList();
        var types = requestTypes.ToList();
        var groups = all.GroupBy(c => c.DepartmentId).OrderBy(g => g.First().DepartmentName, StringComparer.OrdinalIgnoreCase).ToList();
        var options = new List<ClassifyCategoryOption>();
        var unreachable = new List<ClassifyRequestTypeOption>();
        foreach (var group in groups)
        {
            var built = Build(group.Key, group, types);
            var label = group.First().DepartmentName;
            options.AddRange(groups.Count > 1
                ? built.Categories.Select(c => c with { Name = $"{label} \u2013 {c.Name}" })
                : built.Categories);
            unreachable.AddRange(built.UnreachableRequestTypes);
        }

        return new TicketClassifyOptions(
            options.Count == 0 ? ClassifyAvailability.NoCategories : ClassifyAvailability.Available, options, unreachable);
    }

    private static bool SameName(string left, string right) =>
        string.Equals(left.Trim(), right.Trim(), StringComparison.OrdinalIgnoreCase);
}
