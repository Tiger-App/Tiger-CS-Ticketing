using System.Reflection;

namespace TigerCS.Web;

/// <summary>The running assembly's informational version: <c>x.y.z+&lt;git commit&gt;</c> when published with <c>-p:SourceRevisionId</c>.</summary>
public static class BuildInfo
{
    public static readonly string Version =
        typeof(BuildInfo).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "unknown";
}
