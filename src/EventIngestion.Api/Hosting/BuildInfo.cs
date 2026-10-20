using System.Reflection;
using System.Runtime.InteropServices;

namespace EventIngestion.Api.Hosting;

/// <summary>What is running: version (SemVer from Directory.Build.props), full informational version (+git sha) and runtime.</summary>
public sealed record BuildInfo(string Name, string Version, string InformationalVersion, string Runtime)
{
    public static BuildInfo Current { get; } = Create();

    private static BuildInfo Create()
    {
        var assembly = typeof(BuildInfo).Assembly;
        string informational = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "unknown";
        string version = assembly.GetName().Version is { } v ? $"{v.Major}.{v.Minor}.{v.Build}" : "unknown";
        return new BuildInfo(assembly.GetName().Name ?? "EventIngestion.Api", version, informational, RuntimeInformation.FrameworkDescription);
    }
}
