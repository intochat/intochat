using System.Reflection;
using DigitalBrain.Capabilities;
using Capability = DigitalBrain.Capabilities.Capability;
using DigitalBrain.Runtime.Orleans.Capabilities;
using DigitalBrain.Testing.Conformance.Identity;
using DigitalBrain.Testing.Conformance.Secrets;
using DigitalBrain.Testing.Conformance.Storage;

namespace IntoChat.OS.Kernel.Tests.Unit.Capabilities;

// D-06, R4-04: a module whose ModuleManifest.Provides lists a capability has a concrete subclass of that capability's kit in a
// test project of its own tests/ folder; the kernel's single-connection capabilities (secrets@1, identity@1) have theirs in the
// kernel's integration tests. A module that cannot pass its kit cannot declare the capability.
public sealed class CapabilityKitCoverageFacts
{
    private static readonly Dictionary<CapabilityId, Type> Kits = new()
    {
        [Capability.Storage.V1] = typeof(StorageConformance<>),
        [Capability.Secrets.V1] = typeof(SecretsConformance<>),
        [Capability.Identity.V1] = typeof(IdentityConformance<>),
    };

    // The kernel registers secrets@1 and identity@1 itself (keyed singletons over the Authority ports), not through a module.
    private const string KernelTests = "IntoChat.OS.Kernel.Tests.Integration";

    [Fact]
    public void EveryProvidedCapabilityHasAKitSubclassInTheModulesOwnTests()
    {
        var modules = KernelModules();
        Assert.Contains(modules, module => DigitalBrain.ModuleManifest.Of(module).Provides.Count > 0);
        var gaps = KitCoverage.Missing(modules, modules.Select(module => (module, TestsOf(module))).Where(pair => pair.Item2 is not null)
            .ToDictionary(pair => pair.module, pair => pair.Item2!), Kits);
        var open = gaps.Select(gap => (gap.ModuleId, gap.Capability.ToString())).ToList();
        Assert.True(open.Count == 0, "Modules provide capabilities without a kit subclass in their own tests/: "
            + string.Join(", ", open.Select(gap => $"{gap.ModuleId} {gap.Item2}")));
    }

    [Fact]
    public void TheKernelsSingleConnectionCapabilitiesHaveKitSubclassesInTheKernelTests()
    {
        var types = TestAssembly(KernelTests) is { } tests ? Types(tests) : [];
        var open = new[] { Capability.Secrets.V1, Capability.Identity.V1 }
            .Where(capability => !KitCoverage.HasKitSubclass(types, Kits[capability]))
            .Select(capability => ("kernel", capability.ToString())).ToList();
        Assert.True(open.Count == 0, "The kernel registers without a kit subclass: " + string.Join(", ", open.Select(gap => gap.Item2)));
    }

    // Every DigitalBrain module the kernel composes: the Framework modules' neuron modules plus the Authority's plain neurons.
    internal static List<Type> KernelModules()
        => [.. OsCatalog.WithWindowsEdge().Select(module => module.Neurons).OfType<Type>()
            .Append(typeof(IntoChat.OS.Authority.AuthorityPlainNeurons)).Distinct()];

    // The tests of a module: the assemblies built from its own tests/ folder, named "<module assembly>.Tests.*". The csproj copies
    // those of every module that provides a capability to kits/ (KitTestProject); a missing one is a gap.
    private static Assembly? TestsOf(Type module)
    {
        var prefix = module.Assembly.GetName().Name + ".Tests.";
        var files = Directory.EnumerateFiles(KitFolder, prefix + "*.dll").Order(StringComparer.Ordinal).ToList();
        if (files.Count == 0) { return null; }
        // KitCoverage takes one assembly per module: the one holding a kit subclass, else the first.
        var assemblies = files.Select(Assembly.LoadFrom).ToList();
        return assemblies.FirstOrDefault(assembly => Kits.Values.Any(kit => KitCoverage.HasKitSubclass(Types(assembly), kit))) ?? assemblies[0];
    }

    private static Assembly? TestAssembly(string name)
        => File.Exists(Path.Combine(KitFolder, name + ".dll")) ? Assembly.LoadFrom(Path.Combine(KitFolder, name + ".dll")) : null;

    private static string KitFolder => Directory.CreateDirectory(Path.Combine(AppContext.BaseDirectory, "kits")).FullName;

    private static Type[] Types(Assembly assembly)
    {
        try { return assembly.GetTypes(); }
        catch (ReflectionTypeLoadException partial) { return [.. partial.Types.OfType<Type>()]; }
    }
}
