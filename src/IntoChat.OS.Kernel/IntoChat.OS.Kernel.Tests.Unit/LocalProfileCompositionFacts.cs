extern alias AppHost;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Testing;
using IntoChat.OS.Aspire.Hosting;
using IntoChat.OS.Aspire.Server;
using IntoChat.OS.Connections.Identity.Configuration;
using IntoChat.OS.Contracts.Configuration;
using IntoChat.OS.Framework;
using IntoChat.OS.Framework.Configuration;
using IntoChat.OS.Framework.Modules;
using IntoChat.OS.Framework.Modules.Configuration;
using IntoChat.OS.Modules.Microsoft.CSharp;
using IntoChat.OS.Modules.Time.Reminders;
using Microsoft.Extensions.Configuration;

namespace IntoChat.OS.Kernel.Tests.Unit;

// What the local topology starts: the C# sandbox, nothing else (no Azurite), and nothing that opens the host.
[Collection(AppHostComposition.Collection)]
public sealed class LocalProfileCompositionFacts
{
    private static readonly string[] Excluded = ["postgres", "qdrant", "clickhouse", "supabase", "artcraft", "playwright", "foundry", "jev", "ollama"];

    private static Task<IDistributedApplicationTestingBuilder> LocalAsync(string topology = IntoChatOSNames.TopologyLocal)
        => DistributedApplicationTestingBuilder.CreateAsync<AppHost::Projects.IntoChat_OS_AppHost>(
            ["Parameters:Modules-intochat-os-master-key=synthetic-composition-test-key-32chars", "--" + IntoChatOSNames.TopologyKey + "=" + topology],
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
            foreach (var id in new[] { "postgres", "qdrant", "clickhouse", "supabase", "artcraft", "playwright", "github", "azure", "roslyn", "windows", "salesforce", "gmail" })
            {
                Assert.Equal("false", environment[IntoChatOSConfiguration.ModuleSection(id) + ":Enabled"]);
            }
            foreach (var id in new[] { "ai", "apps", "circles", "flame", "tools", "csharp", "orleans", "aspire", "mcp", "time" })
            {
                Assert.DoesNotContain(IntoChatOSConfiguration.ModuleSection(id) + ":Enabled", environment.Keys);
            }
        }
    }

    [Theory]
    [InlineData(IntoChatOSNames.TopologyLocal)]
    [InlineData(IntoChatOSNames.TopologyLocalCloud)]
    public async Task TheLocalProfileKeepsReminderContractsAvailableToKernelAndMcp(string topology)
    {
        await using var host = await LocalAsync(topology);
        foreach (var name in new[] { "OperatingSystem-a", "intochat-os-mcp-a" })
        {
            var resource = Assert.Single(host.Resources.OfType<ProjectResource>(), project => project.Name == name);
            var environment = await LocalProfileOllamaFacts.EnvironmentOf(host, resource);
            var configuration = new ConfigurationBuilder().AddInMemoryCollection(environment.Select(pair =>
                new KeyValuePair<string, string?>(pair.Key, pair.Value))).Build();
            var selection = new IntoChatOSServerBuilder().AddOperatingSystem().SelectModules(configuration);
            var contracts = new CompiledTypes(new ModuleInventory(selection.Modules), []);
            Assert.Same(typeof(IReminder), contracts.Contract(typeof(IReminder).FullName!));
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
