using System.Reflection;

namespace MultiPlayer;

/// <summary>
/// What build this is. Surfaced in the window title and as the first line of the trace, so
/// a report of the form "I ran a built copy and..." can be tied to an actual build.
/// Without it every copy reports 1.0.0.0 and there is nothing to go on.
/// </summary>
public static class BuildInfo
{
    /// <summary>e.g. "0.1.0-dev", or "0.1.0+a1b2c3d" from a CI build.</summary>
    public static string Version { get; } = Read();

    public static string Title => $"Multi-Video Player {Version}";

    private static string Read()
    {
        try
        {
            var assembly = Assembly.GetEntryAssembly() ?? typeof(BuildInfo).Assembly;
            var informational = assembly
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
            if (!string.IsNullOrWhiteSpace(informational)) return informational!;
            return assembly.GetName().Version?.ToString() ?? "unknown";
        }
        catch
        {
            return "unknown";
        }
    }
}
