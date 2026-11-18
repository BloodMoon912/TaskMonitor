using System.Reflection;

namespace task_monitor
{
    /// <summary>
    /// The single source of truth for the user-facing version: <c>&lt;InformationalVersion&gt;</c>
    /// in task_monitor.csproj ("2027.11.18.BM"), which the SDK bakes into the assembly attribute
    /// this class reads. Shown in 设置 → 关于; tools/pack.ps1 names the release artifact after it,
    /// and release.yml refuses a tag that matches neither it nor <c>&lt;Version&gt;</c> — so the
    /// three can never drift apart. Bump both properties together with the release tag.
    /// </summary>
    public static class VersionInfo
    {
        /// <summary>The informational version string, e.g. "2027.11.18.BM" (no v prefix).</summary>
        public static readonly string Current =
            typeof(VersionInfo).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()
                ?.InformationalVersion
            ?? typeof(VersionInfo).Assembly.GetName().Version?.ToString()   // fallback: AssemblyVersion
            ?? "0.0.0";
    }
}
