using ArchUnitNET.Fluent;
using ArchUnitNET.xUnitV3;
using DigitalBrain.Framework.Platform.Identity;
using DigitalBrain.Framework.Platform.Integrations;
using DigitalBrain.Framework.Platform.Secrets;

namespace DigitalBrain.Architecture.Tests.Unit;

public sealed class BoundaryFacts
{
    private static readonly string[] KernelLayers =
    [
        "DigitalBrain", "DigitalBrain.Authority.Contracts", "DigitalBrain.Client", "DigitalBrain.Framework",
        "DigitalBrain.Client.Orleans", "DigitalBrain.Authority"
    ];

    [Theory]
    [InlineData("DigitalBrain", "DigitalBrain.Framework")]
    [InlineData("DigitalBrain.Framework", "DigitalBrain.Client")]
    [InlineData("DigitalBrain.Framework", "DigitalBrain.Client.Orleans")]
    [InlineData("DigitalBrain.Framework", "DigitalBrain.Authority")]
    [InlineData("DigitalBrain.Client", "DigitalBrain.Authority")]
    [InlineData("DigitalBrain.Client.Orleans", "DigitalBrain.Authority")]
    public void CoreBoundariesDoNotInvert(string source, string forbidden)
        => ArchRuleDefinition.Types().That().ResideInAssembly(ProductionArchitecture.Assemblies.Single(a => a.GetName().Name == source)).Should().NotDependOnAny(ArchRuleDefinition.Types().That().ResideInAssembly(ProductionArchitecture.Assemblies.Single(a => a.GetName().Name == forbidden)))
            .Check(ProductionArchitecture.Graph.Value);

    [Fact]
    public void KernelDependenciesPointInward()
    {
        var architecture = ProductionArchitecture.Graph.Value;
        foreach (var (name, index) in KernelLayers.Select((name, index) => (name, index)))
        {
            var assembly = Assert.Single(ProductionArchitecture.Assemblies, assembly => assembly.GetName().Name == name);
            var layer = ArchRuleDefinition.Types().That().ResideInAssembly(assembly);
            Assert.NotEmpty(layer.GetObjects(architecture));
            foreach (var forbidden in ProductionArchitecture.Assemblies.Where(candidate =>
                !KernelLayers.Take(index + 1).Contains(candidate.GetName().Name)))
            {
                ArchRuleDefinition.Types().That().ResideInAssembly(assembly).Should().NotDependOnAny(ArchRuleDefinition.Types().That().ResideInAssembly(forbidden))
                    .Because("kernel layers only depend on inward layers, never feature modules").Check(architecture);
            }
        }
    }

    // R124: a circle's shared apps and its room load through the Apps module's one load path, FeatureLoader; that is the
    // Circles module's only reference to another module's implementation.
    [Fact]
    public void TheCirclesModuleLoadsThroughTheAppsFeatureLoader()
    {
        var implementations = Repository.References(Repository.ProjectNamed("DigitalBrain.Modules.Circles").Path)
            .Where(reference => reference.StartsWith("DigitalBrain.Modules.", StringComparison.Ordinal) && !reference.EndsWith(".Contracts", StringComparison.Ordinal))
            .ToArray();
        Assert.Equal(["DigitalBrain.Modules.Apps"], implementations);
        var circles = Assert.Single(ProductionArchitecture.Assemblies, assembly => assembly.GetName().Name == "DigitalBrain.Modules.Circles");
        Assert.Contains(circles.GetTypes(), type => type.GetConstructors(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
            .Any(constructor => constructor.GetParameters().Any(parameter => parameter.ParameterType.FullName == "DigitalBrain.Apps.FeatureLoader")));
    }

    [Fact]
    public void FeatureModulesNeverDependOnTheCredentialImplementation()
    {
        var architecture = ProductionArchitecture.Graph.Value;
        var platform = ArchRuleDefinition.Types().That().ResideInAssembly("DigitalBrain.Authority");
        var modules = ProductionArchitecture.Assemblies.Where(assembly => !KernelLayers.Contains(assembly.GetName().Name)).ToArray();
        Assert.NotEmpty(modules);
        foreach (var assembly in modules)
        {
            ArchRuleDefinition.Types().That().ResideInAssembly(assembly).Should().NotDependOnAny(platform).Check(architecture);
        }
    }

    [Fact]
    public void ScriptVisibleContractsOnlyDependOnOtherContractsAndTheNeuronAbstractions()
    {
        var contracts = ProductionArchitecture.Assemblies
            .Where(assembly => assembly.GetName().Name!.EndsWith(".Contracts", StringComparison.Ordinal)).ToArray();
        var runtime = ProductionArchitecture.Assemblies.Except(contracts)
            .Where(assembly => assembly.GetName().Name != "DigitalBrain").ToArray();
        Assert.NotEmpty(contracts);
        foreach (var contract in contracts)
        {
            foreach (var implementation in runtime)
            {
                ArchRuleDefinition.Types().That().ResideInAssembly(contract).Should()
                    .NotDependOnAny(ArchRuleDefinition.Types().That().ResideInAssembly(implementation))
                    .Check(ProductionArchitecture.Graph.Value);
            }
        }
    }

    [Fact]
    public void CredentialAndIdentityContractsResideInPlatformContracts()
    {
        var contracts = ProductionArchitecture.Types.Where(IsCredentialContract).ToArray();
        Assert.Contains(typeof(IIntegration), contracts);
        Assert.Contains(typeof(IIdentityDirectory), contracts);
        foreach (var contract in contracts)
        {
            ArchRuleDefinition.Interfaces().That().Are(contract).Should().ResideInAssembly(typeof(ISecrets).Assembly)
                .Check(ProductionArchitecture.Graph.Value);
        }
    }

    internal static bool IsCredentialContract(Type type) => type.IsInterface && (
        InNamespace(type, "DigitalBrain.Framework.Platform.Secrets") || InNamespace(type, "DigitalBrain.Framework.Platform.Identity")
        || InNamespace(type, "DigitalBrain.Framework.Platform.Integrations"));

    private static bool InNamespace(Type type, string name) =>
        type.Namespace == name || type.Namespace?.StartsWith(name + ".", StringComparison.Ordinal) == true;

    [Fact]
    public void ModuleOptionsContainNoCredentialMembers()
    {
        var options = ProductionArchitecture.Types.Where(type => !type.IsAbstract)
            .Select(ModuleOptionsType)
            .Where(type => type is not null)
            .Select(type => type!)
            .Distinct().ToArray();
        Assert.NotEmpty(options);
        Assert.Empty(options.SelectMany(ContractRules.CredentialMembers));
    }

    // Module options are the generic argument of a concrete module's Module<TOptions> base.
    private static Type? ModuleOptionsType(Type type)
    {
        for (var current = type.BaseType; current is not null; current = current.BaseType)
        {
            if (current.IsGenericType && current.GetGenericTypeDefinition() == typeof(DigitalBrain.Framework.Modules.Module<>))
            { return current.GetGenericArguments()[0]; }
        }
        return null;
    }

    [Fact]
    public void ProductionInterfacesContainNoTestHooks()
    {
        var interfaces = ProductionArchitecture.Types.Where(type => type.IsInterface).ToArray();
        Assert.NotEmpty(interfaces);
        Assert.Empty(interfaces.SelectMany(ContractRules.TestHooks));
    }
}
