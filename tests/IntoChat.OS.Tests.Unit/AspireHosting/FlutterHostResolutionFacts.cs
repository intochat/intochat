using IntoChat.OS.Modules.Flutter.Aspire.Hosting;

namespace IntoChat.OS.Modules.Flutter.Tests.Hosting;

public sealed class FlutterHostResolutionFacts
{
    [Fact]
    public void FlutterPackageResolvesFromAnyDepthUnderTheRepository()
    {
        var fromTestOutput = ShellHostingExtensions.ResolveFlutterWorkingDirectory(AppContext.BaseDirectory, configured: null);
        Assert.True(File.Exists(Path.Combine(fromTestOutput, "pubspec.yaml")), $"No Flutter package at '{fromTestOutput}'.");
        Assert.EndsWith(Path.DirectorySeparatorChar + "ui", fromTestOutput, StringComparison.OrdinalIgnoreCase);

        var deeper = Path.Combine(AppContext.BaseDirectory, "nested", "deeper", "still");
        Assert.Equal(fromTestOutput, ShellHostingExtensions.ResolveFlutterWorkingDirectory(deeper, configured: null));
    }

    [Fact]
    public void UiWorkspaceContainsPureDartPackagesWithNoMsBuildProjects()
    {
        var uiRoot = ShellHostingExtensions.ResolveFlutterWorkingDirectory(AppContext.BaseDirectory, configured: null);
        Assert.True(File.Exists(Path.Combine(uiRoot, "pubspec.yaml")));
        Assert.Empty(Directory.EnumerateFiles(uiRoot, "*.csproj", SearchOption.AllDirectories));
    }

    [Fact]
    public void AnExplicitWorkingDirectoryWins()
    {
        var absolute = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "flutter-package"));
        Assert.Equal(absolute, ShellHostingExtensions.ResolveFlutterWorkingDirectory(AppContext.BaseDirectory, absolute));
        Assert.Equal(Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "relative")),
            ShellHostingExtensions.ResolveFlutterWorkingDirectory(AppContext.BaseDirectory, "relative"));
    }

    [Fact]
    public void WindowHostResolvesShellPackageAndWindowsTarget()
    {
        var uiRoot = ShellHostingExtensions.ResolveFlutterWorkingDirectory(AppContext.BaseDirectory, configured: null);
        var launch = FlutterHostLaunch.Resolve(FlutterHostKind.Window, uiRoot, new FlutterHostOptions());

        Assert.EndsWith(Path.DirectorySeparatorChar + "ui", launch.WorkingDirectory, StringComparison.OrdinalIgnoreCase);
        Assert.True(Directory.Exists(Path.Combine(launch.WorkingDirectory, "windows")));
        Assert.Equal("windows", launch.DeviceTarget);
        Assert.Equal(["run", "-d", "windows"], launch.Args);
        if (OperatingSystem.IsWindows() && Path.IsPathRooted(launch.Command))
        {
            Assert.NotEmpty(Path.GetExtension(launch.Command));
        }
    }

    [Fact]
    public void WindowHostHealsRelocatedEphemeralCmakeConfig()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "flutter-heal-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(Path.Combine(tempRoot, "lib"));
            File.WriteAllText(Path.Combine(tempRoot, "lib", "main.dart"), "void main() {}");
            var ephemeralDir = Path.Combine(tempRoot, "windows", "flutter", "ephemeral");
            Directory.CreateDirectory(ephemeralDir);
            File.WriteAllText(
                Path.Combine(ephemeralDir, "generated_config.cmake"),
                "list(APPEND FLUTTER_TOOL_ENVIRONMENT\n  \"PROJECT_DIR=E:\\\\old\\\\ui\\\\shell\"\n)\n");
            var buildDir = Path.Combine(tempRoot, "build", "windows");
            Directory.CreateDirectory(buildDir);
            File.WriteAllText(Path.Combine(buildDir, "CMakeCache.txt"), "// stale");

            var launch = FlutterHostLaunch.Resolve(FlutterHostKind.Window, tempRoot, new FlutterHostOptions());

            Assert.Equal("windows", launch.DeviceTarget);
            Assert.False(Directory.Exists(ephemeralDir));
            Assert.False(Directory.Exists(buildDir));
        }
        finally
        {
            if (Directory.Exists(tempRoot))
            {
                Directory.Delete(tempRoot, recursive: true);
            }
        }
    }
}
