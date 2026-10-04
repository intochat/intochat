using System.Text.Json;
using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using DigitalBrain.Flutter;
using DigitalBrain.Flutter.Aspire.Hosting;

namespace DigitalBrain.Aspire.Hosting.Tests.Unit.Flutter;

public sealed class ShellContainerFacts
{
    [Fact]
    public void WebContainerConfigurationServesTheBakedBundleInsteadOfTheDevServer()
    {
        var builder = DistributedApplication.CreateBuilder(new DistributedApplicationOptions { Args = [], DisableDashboard = true });
        builder.Configuration["DigitalBrain:Flutter:Hosting:WebContainer"] = "true";
        builder.AddDigitalBrain("modules", persistentStorage: false)
            .WithModule<FlutterModuleHosting, FlutterModuleOptions>(flutter => flutter.RunWebApp());

        var shell = Assert.Single(builder.Resources, resource => resource.Annotations.OfType<BrainBrowserAnnotation>().Any());
        Assert.IsType<ContainerResource>(shell, exactMatch: false);
        Assert.DoesNotContain(builder.Resources, resource => resource is ExecutableResource executable
            && executable.Command.Contains("flutter", StringComparison.OrdinalIgnoreCase));
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
    }
}
