extern alias AppHost;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Testing;
using IntoChat.OS.Aspire.Hosting;
using IntoChat.OS.Connections.Identity.Configuration;
using IntoChat.OS.Framework.Configuration;
using IntoChat.OS.Microsoft.CSharp;

namespace IntoChat.OS.Kernel.Tests.Unit;

// What the local topology starts: the C# sandbox, nothing else (no Azurite), and nothing that opens the host.
[Collection(AppHostComposition.Collection)]
public sealed class LocalProfileCompositionFacts
{
    private static readonly string[] Excluded = ["postgres", "qdrant", "clickhouse", "supabase", "artcraft", "playwright", "foundry", "jev", "ollama"];

    private static Task<IDistributedApplicationTestingBuilder> LocalAsync()
        => DistributedApplicationTestingBuilder.CreateAsync<AppHost::Projects.IntoChat_OS_AppHost>(
            ["Parameters:Modules-intochat-os-master-key=synthetic-composition-test-key-32chars", "--" + IntoChat.OS.Framework.IntoChatOSNames.TopologyKey + "=" + IntoChat.OS.Framework.IntoChatOSNames.TopologyLocal],
            TestContext.Current.CancellationToken);

    [Fact]
    public async Task TheSandboxReachesTheHostWithOnlyAHostMappingAndANamedVolume()
    {
        await using var host = await LocalAsync();
        var sandbox = Assert.Single(host.Resources.OfType<ContainerResource>(), resource => resource.Name == CSharpModuleHosting.ResourceName);

        var arguments = new List<object>();
        var context = new ContainerRuntimeArgsCallbackContext(arguments, TestContext.Current.CancellationToken);
        foreach (var callback in sandbox.Annotations.OfType<ContainerRuntimeArgsCallbackAnnotation>()) { await callback.Callback(context); }
        var text = arguments.Select(argument => argument.ToString()!).ToArray();
        var addHost = Array.IndexOf(text, "--add-host");
        Assert.True(addHost >= 0 && text[addHost + 1] == "host.docker.internal:host-gateway");
        Assert.DoesNotContain(text, argument => argument.Contains("privileged", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(text, argument => argument.Contains("cap-add", StringComparison.OrdinalIgnoreCase));

        var mount = Assert.Single(sandbox.Annotations.OfType<ContainerMountAnnotation>());
        Assert.Equal(ContainerMountType.Volume, mount.Type);
        Assert.Equal("intochat-os-csharp-cache", mount.Source);
        Assert.Equal("/cache", mount.Target);
        Assert.DoesNotContain(sandbox.Annotations.OfType<ContainerMountAnnotation>(), annotation =>
            annotation.Type == ContainerMountType.BindMount || annotation.Source?.Contains("docker.sock", StringComparison.OrdinalIgnoreCase) == true);
    }

    [Fact]
    public async Task TheLocalProfileHostsNoDataStoreBrowserOrModelResource()
    {
        await using var host = await LocalAsync();
        Assert.DoesNotContain(host.Resources, resource =>
            Excluded.Any(name => resource.Name.Contains(name, StringComparison.OrdinalIgnoreCase)));
        Assert.Contains(host.Resources, resource => resource.Name == CSharpModuleHosting.ResourceName);
    }

    [Fact]
    public async Task TheLocalProfileNeverOpensTheKernelToTheNetwork()
    {
        await using var host = await LocalAsync();
        var openOnNetwork = AuthOptions.Key(nameof(AuthOptions.AllowOpenOnNetwork));
        foreach (var resource in host.Resources.OfType<ProjectResource>())
        {
            var environment = await LocalProfileOllamaFacts.EnvironmentOf(host, resource);
            Assert.DoesNotContain(openOnNetwork, environment.Keys);
        }
    }

    [Fact]
    public async Task TheDisabledModulesReachTheKernelAndMcpAsEnabledFalse()
    {
        await using var host = await LocalAsync();
        foreach (var name in new[] { "OperatingSystem-a", "intochat-os-mcp-a" })
        {
            var resource = Assert.Single(host.Resources.OfType<ProjectResource>(), project => project.Name == name);
            var environment = await LocalProfileOllamaFacts.EnvironmentOf(host, resource);
            foreach (var id in new[] { "postgres", "qdrant", "clickhouse", "supabase", "artcraft", "playwright", "github", "azure", "roslyn", "time", "windows", "salesforce", "gmail" })
            {
                Assert.Equal("false", environment[IntoChatOSConfiguration.ModuleSection(id) + ":Enabled"]);
            }
            foreach (var id in new[] { "ai", "apps", "circles", "flame", "tools", "csharp", "orleans", "aspire", "mcp" })
            {
                Assert.DoesNotContain(IntoChatOSConfiguration.ModuleSection(id) + ":Enabled", environment.Keys);
            }
        }
    }

    [Fact]
    public async Task TheLocalProfileKeepsTheFlutterModuleWhileTheShellIsEnabled()
    {
        await using var host = await LocalAsync();
        var resource = Assert.Single(host.Resources.OfType<ProjectResource>(), project => project.Name == "OperatingSystem-a");
        var environment = await LocalProfileOllamaFacts.EnvironmentOf(host, resource);
        Assert.DoesNotContain(IntoChatOSConfiguration.ModuleSection("flutter") + ":Enabled", environment.Keys);
    }
}
