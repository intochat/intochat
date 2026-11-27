using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;

namespace IntoChat.OS.Aspire.Hosting.Tests.Unit;

public sealed class ResourceIconFacts
{
    [Theory]
    [InlineData("ai", "BrainCircuit")]
    [InlineData("aspire", "Pulse")]
    [InlineData("devices", "Phone")]
    [InlineData("github", "BranchFork")]
    [InlineData("mcp", "PlugConnected")]
    [InlineData("orleans", "Organization")]
    [InlineData("sandbox", "Box")]
    [InlineData("flutter", "Desktop")]
    [InlineData("webtools", "Globe")]
    [InlineData("postgres", "Database")]
    [InlineData("clickhouse", "Database")]
    [InlineData("playwright", "WindowDevTools")]
    [InlineData("artcraft", "Color")]
    [InlineData("something-new", "Apps")]
    public void EachModuleIdHasItsIcon(string moduleId, string icon)
        => Assert.Equal(icon, ResourceNames.Icon(moduleId));

    [Fact]
    public void EveryComponentNodeTheRootAndTheKernelHaveAnIcon()
    {
        var os = ComposeWithShell();
        var resources = os.ApplicationBuilder.Resources;

        var nodes = resources.OfType<IntoChatOSModuleResource>().ToArray();
        Assert.NotEmpty(nodes);
        foreach (var moduleId in os.Modules)
        {
            var node = Assert.Single(nodes, n => n.Name == ResourceNames.Shown(moduleId));
            Assert.Equal(ResourceNames.Icon(moduleId), IconOf(node));
        }

        Assert.Equal("Home", IconOf(Assert.Single(resources.OfType<IntoChatOSResource>())));
        Assert.Equal("Server", IconOf(Assert.Single(resources, r => r.Name == ResourceNames.Os)));
        Assert.Equal("ArrowRouting", IconOf(Assert.Single(resources, r => r.Name == ResourceNames.OsGateway)));
    }

    [Fact]
    public void PublishModeAlsoCarriesTheIcons()
    {
        var builder = DistributedApplication.CreateBuilder(new DistributedApplicationOptions
        {
            Args = ["--operation", "publish", "--output-path", Path.Combine(Path.GetTempPath(), "intochat-icons-publish"), "--IntoChatOS:Topology=local"],
            DisableDashboard = true,
        });
        var os = builder.AddIntoChatOS("Modules").WithModule<IntoChat.OS.Modules.Microsoft.GitHub.GitHubModule>();
        os.AddKernel(DeploymentSnapshot.ProjectPath("IntoChat.OS.Kernel"));
        var resources = builder.Resources;

        Assert.Equal("Home", IconOf(Assert.Single(resources.OfType<IntoChatOSResource>())));
        Assert.Equal("Server", IconOf(Assert.Single(resources, r => r.Name == "OperatingSystem-a")));
        Assert.Equal("ArrowRouting", IconOf(Assert.Single(resources, r => r.Name == "OperatingSystem-front")));
        Assert.Equal("BranchFork", IconOf(Assert.Single(resources.OfType<IntoChatOSModuleResource>(), n => n.Name == "Modules-github")));
    }

    private static IntoChatOSBuilder ComposeWithShell()
        => AppHostChainFacts.Compose("local", [("IntoChatOS:Shell:Enabled", "true")]);

    private static string? IconOf(IResource resource)
        => resource.Annotations.OfType<ResourceIconAnnotation>().SingleOrDefault()?.IconName;
}
