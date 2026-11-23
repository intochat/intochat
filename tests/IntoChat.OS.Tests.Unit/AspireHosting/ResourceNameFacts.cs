using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using IntoChat.OS.Modules.AI.Models;

namespace IntoChat.OS.Aspire.Hosting.Tests.Unit;

public sealed class ResourceNameFacts
{
    // Names owned by the naming rule so far. The shell (shell-*) keeps its old names until task A4, which
    // extends OwnedNames and drops its prefixes from NotYetRenamed.
    private static readonly string[] NotYetRenamed = ["__", "FlutterShell", "flutter-", "shell-"];

    private static string[] OwnedNames(IntoChatOSBuilder os)
        => [.. os.ApplicationBuilder.Resources
            .Select(static r => r.Name)
            .Where(name => !NotYetRenamed.Any(prefix => name.StartsWith(prefix, StringComparison.Ordinal)))
            .Order(StringComparer.Ordinal)];

    [Fact]
    public void TheModelHasExactlyTheAgreedNames()
    {
        string[] expected =
        [
            "intochat",
            "ai", "aspire", "devices", "github", "mcp", "orleans", "sandbox", "web",
            "ai-ollama", "ai-ollama-openwebui", "web-searxng", "sandbox-worker",
            "os", "os-gateway", "os-rebuilder",
            "Modules-intochat-os-master-key", "Modules-searxng-secret",
        ];

        Assert.Equal(
            string.Join(", ", expected.Order(StringComparer.Ordinal)),
            string.Join(", ", OwnedNames(AppHostChainFacts.Compose("local"))));
    }

    [Fact]
    public void TheKernelAndItsFrontAreOsAndOsGateway()
    {
        var names = AppHostChainFacts.Compose("local").ApplicationBuilder.Resources.Select(static r => r.Name).ToArray();
        Assert.Contains("os", names);
        Assert.Contains("os-gateway", names);
        Assert.DoesNotContain("OperatingSystem-a", names);
        Assert.DoesNotContain("OperatingSystem-front", names);
    }

    [Theory]
    [InlineData("local")]
    [InlineData("cloud")]
    public void ComponentNodesHangUnderTheRoot(string topology)
    {
        var os = AppHostChainFacts.Compose(topology);
        var resources = os.ApplicationBuilder.Resources;
        var nodes = resources.OfType<IntoChatOSModuleResource>().ToArray();
        Assert.NotEmpty(nodes);
        foreach (var node in nodes)
        {
            Assert.Equal("intochat", ParentOf(node)?.Name);
        }

        if (topology == "local")
        {
            Assert.Equal("ai", ParentOf(Named(resources, "ai-ollama"))?.Name);
            Assert.Equal("web", ParentOf(Named(resources, "web-searxng"))?.Name);
            Assert.Equal("sandbox", ParentOf(Named(resources, "sandbox-worker"))?.Name);
        }
    }

    [Fact]
    public void ServiceIdAndFrozenNamesDoNotMove()
    {
        var local = AppHostChainFacts.Compose("local");
        Assert.Equal("Modules", local.Name);
        Assert.Equal("os", local.ServiceId);

        var cloud = AppHostChainFacts.Compose("cloud");
        Assert.Equal("os", cloud.ServiceId);
        var names = cloud.ApplicationBuilder.Resources.Select(static r => r.Name).ToArray();
        Assert.Contains("Modules-grainstate", names);
        Assert.Contains("Modules-clustering", names);
        Assert.Contains("Modules-intochat-os-master-key", names);
    }

    [Theory]
    [InlineData("flutter", "shell")]
    [InlineData("webtools", "web")]
    public void AFrozenModuleIdStillSwitchesItsComponentOff(string moduleId, string shown)
    {
        var os = AppHostChainFacts.Compose("local", ($"IntoChatOS:Modules:{moduleId}:Enabled", "false"));
        Assert.DoesNotContain(moduleId, os.Modules);
        Assert.DoesNotContain(os.ApplicationBuilder.Resources, r => r.Name == shown || r.Name.StartsWith(shown + "-", StringComparison.Ordinal));
    }

    private static IResource Named(IEnumerable<IResource> resources, string name) => Assert.Single(resources, r => r.Name == name);

    private static IResource? ParentOf(IResource resource)
        => resource.Annotations.OfType<ResourceRelationshipAnnotation>().SingleOrDefault(static a => a.Type == "Parent")?.Resource;

    // Publish names deployed resources after the resource, so publish mode keeps every name master had.
    [Fact]
    public void PublishModeKeepsTheDeployedKernelNames()
    {
        var names = ComposeForPublish().ApplicationBuilder.Resources.Select(static r => r.Name).ToArray();
        Assert.Contains("OperatingSystem-a", names);
        Assert.Contains("OperatingSystem-front", names);
        Assert.DoesNotContain("os", names);
        Assert.DoesNotContain("os-gateway", names);
    }

    [Fact]
    public void PublishModeKeepsEveryNameItHadBefore()
    {
        var os = ComposeForPublish();
        Assert.True(os.ApplicationBuilder.ExecutionContext.IsPublishMode);

        var names = OwnedNames(os);
        string[] expected =
        [
            "Modules", "Modules-ai", "Modules-aspire", "Modules-github", "Modules-intochat-os-master-key",
            "Modules-mcp", "Modules-orleans", "Modules-sandbox", "Modules-searxng", "Modules-searxng-secret", "Modules-webtools", "sandbox",
            "OperatingSystem-a", "OperatingSystem-front",
        ];
        Assert.Equal(string.Join(", ", expected.Order(StringComparer.Ordinal)), string.Join(", ", names));
    }

    private static IntoChatOSBuilder ComposeForPublish()
    {
        var builder = DistributedApplication.CreateBuilder(new DistributedApplicationOptions
        {
            Args = ["--operation", "publish", "--output-path", Path.Combine(Path.GetTempPath(), "intochat-names-publish"), "--IntoChatOS:Topology=local", "--IntoChatOS:Shell:Enabled=false"],
            DisableDashboard = true,
        });
        var os = builder.AddIntoChatOS("Modules")
            .WithAI(ai => ai.Web(w => w.SearXng()))
            .WithGitHub()
            .WithSandbox();
        os.AddKernel(DeploymentSnapshot.ProjectPath("IntoChat.OS.Kernel"));
        return os;
    }
}
