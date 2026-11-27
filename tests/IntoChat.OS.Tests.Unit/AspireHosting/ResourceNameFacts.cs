using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using IntoChat.OS.Modules.AI.Models;

namespace IntoChat.OS.Aspire.Hosting.Tests.Unit;

public sealed class ResourceNameFacts
{
    // Aspire's own internal resource names start with a double underscore; every other resource is covered by the naming rule.
    private static readonly string[] NotYetRenamed = ["_" + "_"];

    private static IntoChatOSBuilder ComposeWithAutoOllama(string topology)
        => topology == "local"
            ? AppHostChainFacts.Compose(
                topology,
                ("IntoChatOS:Ollama:AutoContainer", "true"),
                ("IntoChatOS:Modules:devices:Options:Ollama:Endpoint", "http://127.0.0.1:1"))
            : AppHostChainFacts.Compose(topology);

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
            "ai", "aspire", "devices", "mcp", "orleans", "sandbox", "web",
            "ai-ollama", "ai-ollama-openwebui", "web-searxng", "sandbox-worker",
            "os", "os-gateway", "os-rebuilder",
            "Modules-intochat-os-master-key", "Modules-searxng-secret",
        ];

        Assert.Equal(
            string.Join(", ", expected.Order(StringComparer.Ordinal)),
            string.Join(", ", OwnedNames(ComposeWithAutoOllama("local"))));
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
        var os = ComposeWithAutoOllama(topology);
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

    [Fact]
    public void TheFlutterTargetsHangDirectlyUnderFlutter()
    {
        var resources = ComposeWithShell().ApplicationBuilder.Resources;
        foreach (var child in new[] { "flutter-windows", "flutter-linux", "flutter-macos", "flutter-android", "flutter-ios", "flutter-web" })
        {
            Assert.Equal("flutter", ParentOf(Named(resources, child))?.Name);
        }
    }

    [Fact]
    public void FlutterHasAllSixPlatformTargets()
    {
        string[] expected =
        [
            "flutter",
            "flutter-android",
            "flutter-ios",
            "flutter-linux",
            "flutter-macos",
            "flutter-web",
            "flutter-windows",
        ];

        var names = ComposeWithShell().ApplicationBuilder.Resources.Select(static r => r.Name)
            .Where(static name => name == "flutter" || name.StartsWith("flutter-", StringComparison.Ordinal) || name.StartsWith("shell", StringComparison.Ordinal));
        Assert.Equal(string.Join(", ", expected.Order(StringComparer.Ordinal)), string.Join(", ", names.Order(StringComparer.Ordinal)));
    }

    [Fact]
    public void TheWebTargetIsFlutterWeb()
    {
        var resources = ComposeWithShell(("IntoChatOS:Modules:flutter:Options:Hosting:Kind", "Web")).ApplicationBuilder.Resources;
        Assert.Equal("flutter", ParentOf(Named(resources, "flutter-web"))?.Name);
        Assert.DoesNotContain(resources, static r => r.Name == "shell-desktop" || r.Name == "FlutterShell");
    }

    [Theory]
    [InlineData("flutter", "flutter", "flutter-windows", "IntoChatOS:Shell:Enabled")]
    [InlineData("webtools", "web", "web-searxng", "IntoChatOS:Modules:webtools:Enabled")]
    public void AFrozenModuleIdStillSwitchesItsComponentOff(string moduleId, string shown, string child, string switchKey)
    {
        // The shell is switched by its own deployment key; configuration under the frozen module id does not remove it.
        var on = ComposeWithShell((switchKey, "true"));
        Assert.Contains(moduleId, on.Modules);
        var onNames = on.ApplicationBuilder.Resources.Select(static r => r.Name).ToArray();
        Assert.Contains(shown, onNames);
        Assert.Contains(child, onNames);

        var off = ComposeWithShell((switchKey, "false"));
        Assert.DoesNotContain(moduleId, off.Modules);
        Assert.DoesNotContain(off.ApplicationBuilder.Resources, r => r.Name == shown || r.Name.StartsWith(shown + "-", StringComparison.Ordinal));
    }

    private static IntoChatOSBuilder ComposeWithShell(params (string Key, string Value)[] settings)
        => AppHostChainFacts.Compose("local", [("IntoChatOS:Shell:Enabled", "true"), .. settings]);

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
        // The shell keeps FlutterShell in publish; shell-* and the client targets are run-mode only.
        string[] expected =
        [
            "Modules", "Modules-ai", "Modules-aspire", "Modules-github", "Modules-intochat-os-master-key",
            "Modules-flutter", "FlutterShell",
            "Modules-mcp", "Modules-orleans", "Modules-sandbox", "Modules-searxng", "Modules-searxng-secret", "Modules-webtools", "sandbox",
            "OperatingSystem-a", "OperatingSystem-front",
        ];
        Assert.Equal(string.Join(", ", expected.Order(StringComparer.Ordinal)), string.Join(", ", names));
    }

    private static IntoChatOSBuilder ComposeForPublish()
    {
        var builder = DistributedApplication.CreateBuilder(new DistributedApplicationOptions
        {
            Args = ["--operation", "publish", "--output-path", Path.Combine(Path.GetTempPath(), "intochat-names-publish"), "--IntoChatOS:Topology=local", "--IntoChatOS:Shell:Enabled=true"],
            DisableDashboard = true,
        });
        var os = builder.AddIntoChatOS("Modules")
            .WithAI(ai => ai.Web(w => w.SearXng()))
            .WithModule<IntoChat.OS.Modules.Microsoft.GitHub.GitHubModule>()
            .WithInfrastructure(infra => infra.Sandbox(ResourceBacking.LocalContainer));
        os.AddKernel(DeploymentSnapshot.ProjectPath("IntoChat.OS.Kernel"));
        return os;
    }

    // The aspire module's KernelNames accepts exactly these strings (AspireFacts.TheKernelRuleNamesTheSameStringsAsTheAppHost).
    [Fact]
    public void OneKernelInstanceIsOsAndSeveralAreOsDashLetter()
    {
        var builder = DistributedApplication.CreateBuilder([]);
        Assert.Equal(["os"], ResourceNames.OsInstances(builder, ["a"]));
        Assert.Equal(["os-a", "os-b"], ResourceNames.OsInstances(builder, ["a", "b"]));
    }
}
