using IntoChat.OS.Modules.Flutter.Aspire.Hosting;

namespace IntoChat.OS.Modules.Flutter.Tests.Hosting;


public sealed class FlutterHostResolutionFacts
{
    [Fact]
    public void FlutterPackageResolvesFromAnyDepthUnderTheRepository()
    {
        var fromTestOutput = ShellHostingExtensions.ResolveFlutterWorkingDirectory(AppContext.BaseDirectory, configured: null);
        Assert.True(File.Exists(Path.Combine(fromTestOutput, "pubspec.yaml")), $"No Flutter package at '{fromTestOutput}'.");
        Assert.EndsWith(Path.Combine("ui", "core"), fromTestOutput, StringComparison.OrdinalIgnoreCase);

        var deeper = Path.Combine(AppContext.BaseDirectory, "nested", "deeper", "still");
        Assert.Equal(fromTestOutput, ShellHostingExtensions.ResolveFlutterWorkingDirectory(deeper, configured: null));
    }

    [Fact]
    public void UiWorkspaceContainsPureDartPackagesWithNoMsBuildProjects()
    {
        var coreRoot = ShellHostingExtensions.ResolveFlutterWorkingDirectory(AppContext.BaseDirectory, configured: null);
        var uiWorkspaceRoot = Path.GetFullPath(Path.Combine(coreRoot, ".."));
        Assert.True(File.Exists(Path.Combine(uiWorkspaceRoot, "pubspec.yaml")));
        Assert.True(File.Exists(Path.Combine(uiWorkspaceRoot, "core", "pubspec.yaml")));
        Assert.True(File.Exists(Path.Combine(uiWorkspaceRoot, "ui", "pubspec.yaml")));
        Assert.True(File.Exists(Path.Combine(uiWorkspaceRoot, "shell", "pubspec.yaml")));
        Assert.Empty(Directory.EnumerateFiles(uiWorkspaceRoot, "*.csproj", SearchOption.AllDirectories));
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
        var coreRoot = ShellHostingExtensions.ResolveFlutterWorkingDirectory(AppContext.BaseDirectory, configured: null);
        var launch = FlutterHostLaunch.Resolve(FlutterHostKind.Window, coreRoot, new FlutterHostOptions());

        Assert.EndsWith(Path.Combine("ui", "shell"), launch.WorkingDirectory, StringComparison.OrdinalIgnoreCase);
        Assert.True(Directory.Exists(Path.Combine(launch.WorkingDirectory, "windows")));
        Assert.Equal("windows", launch.DeviceTarget);
        Assert.Equal(["run", "-d", "windows"], launch.Args);
        if (OperatingSystem.IsWindows() && Path.IsPathRooted(launch.Command))
        {
            Assert.NotEmpty(Path.GetExtension(launch.Command));
        }
    }
}
