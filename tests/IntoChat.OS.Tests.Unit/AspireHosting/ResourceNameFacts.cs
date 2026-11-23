using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;

namespace IntoChat.OS.Aspire.Hosting.Tests.Unit;

public sealed class ResourceNameFacts
{
    // Names owned by the naming rule so far. The kernel (os, os-gateway) and the shell (shell-*) keep their old
    // names until tasks A3 and A4, which extend OwnedNames and drop their prefixes from NotYetRenamed.
    private static readonly string[] NotYetRenamed = ["OperatingSystem-", "__", "FlutterShell", "flutter-", "shell-"];

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
            "Modules-intochat-os-master-key", "Modules-searxng-secret",
        ];

        Assert.Equal(
            string.Join(", ", expected.Order(StringComparer.Ordinal)),
            string.Join(", ", OwnedNames(AppHostChainFacts.Compose("local"))));
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
}
