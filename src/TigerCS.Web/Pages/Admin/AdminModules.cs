namespace TigerCS.Web.Pages.Admin;

/// <summary>
/// One module of the Administration console, described once.
/// </summary>
/// <param name="Key">The value a page puts in <c>ViewData["AdminSection"]</c>; the sub-nav marks the matching tab.</param>
/// <param name="Label">The module's name, in navigation, on its card and in the breadcrumb.</param>
/// <param name="Href">The module's route. Routes are fixed — this record describes them, it does not decide them.</param>
/// <param name="Icon">The key <c>_AdminIcon</c> draws.</param>
/// <param name="Tone">The <c>tone-*</c> class carrying the module's accent colour (see the Administration block of site.css).</param>
/// <param name="Tagline">Three or four words on what the module manages — the line under its name on the overview.</param>
/// <param name="CountSingular">What one of the module's records is, e.g. "active user".</param>
/// <param name="CountPlural">What several of them are, e.g. "active users".</param>
public sealed record AdminModule(
    string Key,
    string Label,
    string Href,
    string Icon,
    string Tone,
    string Tagline,
    string CountSingular,
    string CountPlural)
{
    /// <summary>The label for a given count — "1 active user", never "1 active users".</summary>
    public string CountLabel(int count) => count == 1 ? CountSingular : CountPlural;
}

/// <summary>
/// The Administration console's five modules and the one accent each owns.
/// Overview, the sub-navigation, every page header and the module cards all
/// read this list, so a module is named, coloured and iconed in exactly one
/// place. Nothing here grants access or decides a route: the folder's
/// authorization convention gates the pages and the Api enforces every
/// write, exactly as before.
/// </summary>
public static class AdminModules
{
    /// <summary>The overview's own section key — it is a destination, not a module.</summary>
    public const string OverviewKey = "overview";

    public static AdminModule Users { get; } =
        new("users", "Users", "/Admin/Users", "users", "tone-info", "Accounts, roles and membership", "active user", "active users");

    public static AdminModule Departments { get; } =
        new("departments", "Departments", "/Admin/Departments", "departments", "tone-progress", "Responsible teams and members", "active department", "active departments");

    public static AdminModule RequestTypes { get; } =
        new("requesttypes", "Request Types", "/Admin/RequestTypes", "request-types", "tone-secondary", "Requests, routing and SLA", "active request type", "active request types");

    public static AdminModule Workflows { get; } =
        new("workflows", "Workflows", "/Admin/Workflows", "workflows", "tone-purple", "Versioned steps and approvals", "active workflow", "active workflows");

    public static AdminModule Channels { get; } =
        new("channels", "Channels", "/Admin/Channels", "channels", "tone-success", "Customer entry points", "active channel", "active channels");

    /// <summary>
    /// Review Tablets (Employee Review feature). Deliberately not in <see cref="All"/>: the console's five modules and their overview cards are
    /// unchanged; this page is reached from the sub-navigation like Collections Tower Assignments is, and resolves for its breadcrumb and eyebrow.
    /// </summary>
    public static AdminModule ReviewTablets { get; } =
        new("reviewtablets", "Review Tablets", "/Admin/EmployeeReviewDevices", "tablet", "tone-secondary", "Customer review devices and their employees", "review tablet", "review tablets");

    /// <summary>The modules in the order the console presents them.</summary>
    public static IReadOnlyList<AdminModule> All { get; } = [Users, Departments, RequestTypes, Workflows, Channels];

    /// <summary>The module a section key belongs to, or <c>null</c> for the overview and anything unrecognised.</summary>
    public static AdminModule? Find(string? key) =>
        key is null
            ? null
            : All.FirstOrDefault(m => string.Equals(m.Key, key, StringComparison.OrdinalIgnoreCase))
              ?? (string.Equals(ReviewTablets.Key, key, StringComparison.OrdinalIgnoreCase) ? ReviewTablets : null);

    /// <summary>The tone class for a section key; the overview carries the brand accent.</summary>
    public static string ToneFor(string? key) => Find(key)?.Tone ?? "tone-brand";
}

/// <summary>Small display helpers shared by the Administration views.</summary>
public static class AdminDisplay
{
    /// <summary>
    /// Up to two initials for an avatar. A person is shown by their initials
    /// rather than by a generic silhouette, and a name that yields nothing
    /// usable falls back to a dash so the circle is never blank.
    /// </summary>
    public static string Initials(string? name)
    {
        var parts = (name ?? string.Empty)
            .Split([' ', '\t', '-', '.', '_'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(p => char.IsLetterOrDigit(p[0]))
            .ToList();

        return parts.Count switch
        {
            0 => "—",
            1 => parts[0][..Math.Min(2, parts[0].Length)].ToUpperInvariant(),
            _ => $"{parts[0][0]}{parts[^1][0]}".ToUpperInvariant()
        };
    }

    /// <summary>"9 users" / "1 user" / "No users" — a count that reads as a sentence fragment.</summary>
    public static string Count(int value, string singular, string plural) =>
        value switch { 0 => $"No {plural}", 1 => $"1 {singular}", _ => $"{value} {plural}" };
}
