using System.Diagnostics;

namespace IntoChat.OS.Testing;

// Child builds against disposable test feeds must release loaded tasks when they exit,
// so the fixture can remove its package cache without changing the developer's build servers.
public static class DotNetTestProcess
{
    public static void DisableBuildServerReuse(ProcessStartInfo start)
    {
        ArgumentNullException.ThrowIfNull(start);
        start.Environment["MSBUILDDISABLENODEREUSE"] = "1";
        start.Environment["DOTNET_CLI_USE_MSBUILD_SERVER"] = "0";
    }

    // A pack-and-build child process: quiet CLI, none of the test host's own MSBuild or runner settings, and an optional package cache.
    public static void IsolateChildBuild(ProcessStartInfo start, string? packages)
    {
        ArgumentNullException.ThrowIfNull(start);
        start.Environment["DOTNET_NOLOGO"] = "1";
        start.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";
        start.Environment["DOTNET_SKIP_FIRST_TIME_EXPERIENCE"] = "1";
        foreach (var name in new[] { "MSBuildSDKsPath", "MSBUILD_EXE_PATH", "MSBuildExtensionsPath", "DOTNET_HOST_PATH", "TESTINGPLATFORM_DOTNETTEST_EXECUTIONID" })
        {
            start.Environment.Remove(name);
        }
        if (packages is not null) { start.Environment["NUGET_PACKAGES"] = packages; }
    }

    // CI hands the build job's packed DigitalBrain packages to the template fact through this folder.
    public static string? TemplatePackages() => Environment.GetEnvironmentVariable("DIGITALBRAIN_TEMPLATE_PACKAGES");

    // The machine's NuGet package cache, an offline source for a child build.
    public static string PackageCache()
        => Environment.GetEnvironmentVariable("NUGET_PACKAGES") is { Length: > 0 } set
            ? set
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".nuget", "packages");
}
