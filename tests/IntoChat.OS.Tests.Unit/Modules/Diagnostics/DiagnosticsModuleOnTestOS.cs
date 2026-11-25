using System.Reflection;
using DigitalBrain;
using DigitalBrain.Neurons;
using DigitalBrain.Testing;
using IntoChat.OS.Host.Configuration;
using IntoChat.OS.Kernel;
using IntoChat.OS.Modules.Apps.Debugger;
using IntoChat.OS.Modules.Apps.Trace;
using IntoChat.OS.Modules.Diagnostics;
using Microsoft.Extensions.DependencyInjection;

namespace IntoChat.OS.Modules.Apps.Tests.Unit;

// The debugger reads and the trace reader left the catalog as they were: the same two stateless neuron types under the same
// contracts, hosted wherever the catalog (`apps`) is hosted.
public sealed class DiagnosticsModuleOnTestOS : ModuleOnTestOSFacts<DiagnosticsModule>
{
    private static readonly BrainId Owner = new("owner");

    [Fact]
    public void TheNeuronsKeepTheirTypeIdsAndKeys()
    {
        var neurons = typeof(DiagnosticsModule).Assembly.GetTypes()
            .Select(type => (Type: type, Neuron: type.GetCustomAttribute<NeuronAttribute>(false)))
            .Where(pair => pair.Neuron is not null)
            .Select(pair => $"{pair.Neuron!.Type}|{pair.Type.IsDefined(typeof(StatelessNeuronAttribute), false)}|{pair.Type.FullName}")
            .Order(StringComparer.Ordinal).ToArray();

        Assert.Equal(
            [
                "creator.reads|True|IntoChat.OS.Modules.Apps.Debugger.DebuggerReadsNeuron",
                "trace.reader|True|IntoChat.OS.Modules.Apps.Trace.TraceReaderNeuron",
            ],
            neurons);
        Assert.Equal("owner/creator.reads/creator/reads", IDebuggerReads.Key("owner").ToString());
        Assert.Equal("IntoChat.OS.Modules.Apps.Debugger.IDebuggerReads", typeof(IDebuggerReads).FullName);
        // Stored in agent definitions by this full name.
        Assert.Equal("IntoChat.OS.Modules.Apps.Trace.ITraceReader", typeof(ITraceReader).FullName);
        foreach (var contract in (Type[])[typeof(IDebuggerReads), typeof(ITraceReader), typeof(TraceEntry), typeof(TracePage), typeof(BrainTopology), typeof(ICompiledApps)])
        {
            Assert.Equal("IntoChat.OS.Modules.Catalog.Contracts", contract.Assembly.GetName().Name);
        }
        Assert.Equal("unit", BrainTopologyNode.UnitRole);
        Assert.Equal("app", BrainTopologyNode.AppRole);
    }

    [Fact]
    public void TheModuleIsOnWhereverTheCatalogIs()
    {
        var diagnostics = Assert.Single(OperatingSystemModules.Modules, module => module.Id.ToString() == "diagnostics");
        var catalog = Assert.Single(OperatingSystemModules.Modules, module => module.Id.ToString() == "apps");

        Assert.Equal(typeof(DiagnosticsModule), diagnostics.Type);
        Assert.True(OperatingSystemModules.DeclaresModule(diagnostics.Type.Assembly));
        Assert.Equal(catalog.RequiresContainer, diagnostics.RequiresContainer);
        Assert.Contains(OperatingSystemModules.InProcessModules, module => module.Type == typeof(DiagnosticsModule));
        foreach (var topology in (string[])[IntoChatOSNames.TopologyLocal, IntoChatOSNames.TopologyLocalCloud, IntoChatOSNames.TopologyCloud])
        {
            Assert.Equal(catalog.LoadsUnder(topology), diagnostics.LoadsUnder(topology));
            Assert.DoesNotContain(LocalRunDefaults.EnabledKey("diagnostics"), LocalRunDefaults.For(topology).Keys);
            Assert.DoesNotContain(LocalRunDefaults.EnabledKey("apps"), LocalRunDefaults.For(topology).Keys);
        }
    }

    // The topology lists the compiled apps the catalog offers through ICompiledApps: those of the registered assemblies and of
    // the catalog's own, by key, title and role, without the OS services.
    [Fact]
    public async Task TheTopologyListsTheCompiledAppsTheCatalogOffers()
    {
        var registrations = new[] { new CompiledAppsRegistration(typeof(IntoChat.OS.Apps.Settings.SettingsApp).Assembly) };
        ICompiledApps offered = new CompiledAppsList(registrations);
        var expected = registrations.Select(registration => registration.Assembly).Append(typeof(AppsModule).Assembly).Distinct()
            .SelectMany(CompiledApps.Of)
            .Where(compiled => !ShippedApps.Services.Contains(compiled.Bundle.Name))
            .Select(compiled => new BrainTopologyNode(
                IntoChat.OS.Host.Primitives.AppRef.Of(Owner, compiled.Bundle.Name).Key,
                compiled.Bundle.Manifest.Title is { Length: > 0 } title ? title : compiled.Bundle.Name,
                "app"))
            .ToArray();
        Assert.NotEmpty(expected);

        using var person = IntoChat.OS.Tests.TestEdge.Authority.Enter(IntoChat.OS.Tests.TestEdge.Authority.Person("owner", "owner", Owner));
        await using var os = TestOS.Local(options => options.Services = new ServiceCollection().AddSingleton(offered).BuildServiceProvider())
            .RegisterCatalog();
        var topology = await os.Get<IDebuggerReads>(IDebuggerReads.Key(Owner.Value)).Topology();

        Assert.Equal(expected, topology.Nodes.Where(node => node.Role == BrainTopologyNode.AppRole).ToArray());

        // A host without the catalog's services lists no compiled app.
        await using var bare = TestOS.Local().RegisterCatalog();
        Assert.DoesNotContain((await bare.Get<IDebuggerReads>(IDebuggerReads.Key(Owner.Value)).Topology()).Nodes, node => node.Role == BrainTopologyNode.AppRole);
    }

    [Fact]
    public void TraceReaderRedactsBearerAndApiKeysInPayloads()
    {
        var raw = """{"auth":"Bearer eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9","openai":"sk-proj-1234567890abcdef","github":"ghp_9876543210abcdef","safe":"hello"}""";
        var redacted = TraceReaderNeuron.Redact(raw);
        Assert.DoesNotContain("eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9", redacted, StringComparison.Ordinal);
        Assert.DoesNotContain("sk-proj-1234567890abcdef", redacted, StringComparison.Ordinal);
        Assert.DoesNotContain("ghp_9876543210abcdef", redacted, StringComparison.Ordinal);
        Assert.Contains("[REDACTED]", redacted, StringComparison.Ordinal);
        Assert.Contains("\"safe\":\"hello\"", redacted, StringComparison.Ordinal);
    }
}
