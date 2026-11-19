using TigerCS.Application.Modules.Ticketing.Abstractions;
using TigerCS.Domain.Modules.Ticketing;

namespace TigerCS.Application.Modules.Ticketing.Services;

/// <summary>
/// The one rule for a project / unit pair entered by hand - used by the customer's primary project and unit and by the properties added
/// to a customer, so both accept and refuse the same things.
///
/// <para>
/// The project is matched to the catalogue of projects Tiger Ticketing already knows (case-insensitively, keeping the catalogue's
/// spelling). Where the catalogue can say something, the unit is checked against it: a unit recorded only under a different project is
/// refused; a unit the catalogue has never seen cannot be judged and is accepted. Nothing is invented to fill the gap.
/// </para>
/// </summary>
public sealed class ProjectUnitValidator(IProjectCatalogRepository projectCatalog)
{
    /// <summary>Both blank is "nothing entered" (<c>(null, null)</c>, no error); one without the other, or an invalid value, adds to <paramref name="errors"/>.</summary>
    public async Task<(string? Project, string? Unit)> ValidateAsync(
        string? projectInput, string? unitInput, string projectField, string unitField, Dictionary<string, string[]> errors,
        CancellationToken cancellationToken)
    {
        var project = string.IsNullOrWhiteSpace(projectInput) ? null : projectInput.Trim();
        var unit = string.IsNullOrWhiteSpace(unitInput) ? null : unitInput.Trim();
        if (project is null && unit is null)
        {
            return (null, null);
        }

        if (project is null || unit is null)
        {
            errors[project is null ? projectField : unitField] = ["Project and unit are entered together: add the missing one, or clear both."];
            return (null, null);
        }

        var valid = true;
        if (project.Length > CustomerProfileOverride.ProjectNameMaxLength || CustomerFieldText.Clean(project) is null)
        {
            errors[projectField] = [project.Length > CustomerProfileOverride.ProjectNameMaxLength
                ? $"A project name is at most {CustomerProfileOverride.ProjectNameMaxLength} characters."
                : "Enter the project's real name."];
            valid = false;
        }

        if (unit.Length > CustomerProfileOverride.UnitNumberMaxLength || CustomerFieldText.Clean(unit) is null)
        {
            errors[unitField] = [unit.Length > CustomerProfileOverride.UnitNumberMaxLength
                ? $"A unit number is at most {CustomerProfileOverride.UnitNumberMaxLength} characters."
                : "Enter the unit's real number."];
            valid = false;
        }

        if (!valid)
        {
            return (null, null);
        }

        var projects = await projectCatalog.ListProjectsAsync(cancellationToken);
        var known = projects.FirstOrDefault(p => string.Equals(p.Name, project, StringComparison.OrdinalIgnoreCase));
        if (known is null)
        {
            return (project, unit);   // a project the catalogue has not seen: nothing to judge the unit against
        }

        project = known.Name;
        var unitsOfProject = await projectCatalog.ListUnitNumbersAsync(project, cancellationToken);
        var match = unitsOfProject.FirstOrDefault(u => string.Equals(u, unit, StringComparison.OrdinalIgnoreCase));
        if (match is not null)
        {
            return (project, match);
        }

        foreach (var other in projects.Where(p => !string.Equals(p.Name, project, StringComparison.OrdinalIgnoreCase)))
        {
            var otherUnits = await projectCatalog.ListUnitNumbersAsync(other.Name, cancellationToken);
            if (otherUnits.Any(u => string.Equals(u, unit, StringComparison.OrdinalIgnoreCase)))
            {
                errors[unitField] = [$"Unit {unit} belongs to project {other.Name}, not {project}. Choose the right project or unit."];
                return (null, null);
            }
        }

        return (project, unit);   // not recorded under any project: it cannot be judged, so it is accepted as entered
    }
}
