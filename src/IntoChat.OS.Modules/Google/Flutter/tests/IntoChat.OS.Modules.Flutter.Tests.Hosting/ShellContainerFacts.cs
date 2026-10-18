using System.Text.Json;
using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using IntoChat.OS.Aspire.Hosting;
using IntoChat.OS.Flutter;
using IntoChat.OS.Flutter.Aspire.Hosting;

namespace IntoChat.OS.Modules.Flutter.Tests.Hosting;

public sealed class ShellContainerFacts
{
    [Fact]
    public void WebContainerConfigurationServesTheBakedBundleInsteadOfTheDevServer()
    {
        var builder = DistributedApplication.CreateBuilder(new DistributedApplicationOptions { Args = [], DisableDashboard = true });
        builder.Configuration[FlutterHostOptions.WebContainerKey] = "true";
        builder.AddIntoChatOS("modules", new IntoChatOSHostingOptions { PersistentStorage = false })
            .WithHosting<FlutterModuleHosting, FlutterModuleOptions>(flutter => flutter.RunWebApp());

        var shell = Assert.Single(builder.Resources, resource => resource.Annotations.OfType<BrainBrowserAnnotation>().Any());
        Assert.IsType<ContainerResource>(shell, exactMatch: false);
        Assert.DoesNotContain(builder.Resources, resource => resource is ExecutableResource executable
            && executable.Command.Contains("flutter", StringComparison.OrdinalIgnoreCase));
        // The tag is stable across sessions: staleness is the docker layer cache's job.
        var image = Assert.Single(shell.Annotations.OfType<ContainerImageAnnotation>());
        Assert.Equal("latest", image.Tag);
    }

    [Fact]
    public void ServedConfigCarriesTheHostVariablesTheShellResolves()
    {
        var payload = JsonSerializer.Deserialize<Dictionary<string, string>>(
            ShellServedConfig.Payload("http://localhost:5000", "desk", "main"));

        Assert.NotNull(payload);
        Assert.Equal("http://localhost:5000", payload[ShellNames.UIBaseEnvironmentVariable]);
        Assert.Equal("desk", payload[ShellNames.ShellEnvironmentVariable]);
        Assert.Equal("main", payload[ShellNames.ChatEnvironmentVariable]);
    }

    [Fact]
    public void TheShellPackageShipsTheContainerDockerfileInsideThePubWorkspace()
    {
        var packageRoot = ShellHostingExtensions.ResolveFlutterWorkingDirectory(AppContext.BaseDirectory, configured: null);
        var shellDirectory = FlutterHostLaunch.ResolveWebPackageDirectory(packageRoot);

        Assert.NotNull(shellDirectory);
        Assert.True(File.Exists(Path.Combine(shellDirectory, "Dockerfile")),
            $"No Dockerfile at '{shellDirectory}' — the WebContainer host cannot build the shell image.");
        var workspaceRoot = Path.GetFullPath(Path.Combine(shellDirectory, ".."));
        Assert.True(File.Exists(Path.Combine(workspaceRoot, "pubspec.yaml")),
            $"'{workspaceRoot}' is not the pub workspace root the docker build context expects.");
        Assert.True(File.Exists(Path.Combine(workspaceRoot, ".dockerignore")),
            $"No .dockerignore at '{workspaceRoot}' — local build output would bust the layer cache every run.");
    }
}
