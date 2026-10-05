using ArchUnitNET.xUnitV3;
using DigitalBrain.Kernel;
using DigitalBrain.Kernel.Platform.Identity;
using DigitalBrain.Kernel.Platform.Integrations;
using DigitalBrain.Kernel.Platform.Integrations.Accounts;
using DigitalBrain.Kernel.Platform.Secrets;
using static ArchUnitNET.Fluent.ArchRuleDefinition;

namespace DigitalBrain.Architecture.Tests;

public sealed class BoundaryFacts
{
    private static readonly string[] KernelLayers =
    [
        "DigitalBrain", "DigitalBrain.Contracts", "DigitalBrain.Kernel.Platform", "DigitalBrain.Client", "DigitalBrain.Client.Orleans",
        "DigitalBrain.Kernel", "DigitalBrain.Kernel", "DigitalBrain.Sdk", "DigitalBrain.Platform"
    ];

    [Theory]
    [InlineData("DigitalBrain.Kernel", "DigitalBrain.Client")]
    [InlineData("DigitalBrain.Kernel", "DigitalBrain.Client.Orleans")]
    [InlineData("DigitalBrain.Kernel", "DigitalBrain.Kernel.Platform")]
    [InlineData("DigitalBrain.Client", "DigitalBrain.Kernel.Platform")]
    [InlineData("DigitalBrain.Client.Orleans", "DigitalBrain.Kernel.Platform")]
    public void CoreBoundariesDoNotInvert(string source, string forbidden)
        => Types().That().ResideInAssembly(ProductionArchitecture.Assemblies.Single(a => a.GetName().Name == source)).Should().NotDependOnAny(Types().That().ResideInAssembly(ProductionArchitecture.Assemblies.Single(a => a.GetName().Name == forbidden)))
            .Check(ProductionArchitecture.Graph.Value);

    [Fact]
    public void KernelDependenciesPointInward()
    {
        var architecture = ProductionArchitecture.Graph.Value;
        foreach (var (name, index) in KernelLayers.Select((name, index) => (name, index)))
        {
            var assembly = Assert.Single(ProductionArchitecture.Assemblies, assembly => assembly.GetName().Name == name);
            var layer = Types().That().ResideInAssembly(assembly);
            Assert.NotEmpty(layer.GetObjects(architecture));
            foreach (var forbidden in ProductionArchitecture.Assemblies.Where(candidate =>
                !KernelLayers.Take(index + 1).Contains(candidate.GetName().Name)))
            {
                Types().That().ResideInAssembly(assembly).Should().NotDependOnAny(Types().That().ResideInAssembly(forbidden))
                    .Because("kernel layers only depend on inward layers, never feature modules").Check(architecture);
            }
        }
    }

    [Fact]
    public void FeatureModulesNeverDependOnTheCredentialImplementation()
    {
        var architecture = ProductionArchitecture.Graph.Value;
        var platform = Types().That().ResideInAssembly("DigitalBrain.Platform");
        var modules = ProductionArchitecture.Assemblies.Where(assembly => !KernelLayers.Contains(assembly.GetName().Name)).ToArray();
        Assert.NotEmpty(modules);
        foreach (var assembly in modules)
        {
            Types().That().ResideInAssembly(assembly).Should().NotDependOnAny(platform).Check(architecture);
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
                Types().That().ResideInAssembly(contract).Should()
                    .NotDependOnAny(Types().That().ResideInAssembly(implementation))
                    .Check(ProductionArchitecture.Graph.Value);
            }
        }
    }

    [Fact]
    public void CredentialAndIdentityContractsResideInPlatformContracts()
    {
        var contracts = ProductionArchitecture.Types.Where(IsCredentialContract).ToArray();
        Assert.Contains(typeof(IIntegrationRegistration), contracts);
        Assert.Contains(typeof(IAccountProbe), contracts);
        Assert.Contains(typeof(IIdentityDirectory), contracts);
        Assert.Contains(typeof(IGrantStore), contracts);
        foreach (var contract in contracts)
        {
            Interfaces().That().Are(contract).Should().ResideInAssembly(typeof(ISecrets).Assembly)
                .Check(ProductionArchitecture.Graph.Value);
        }
    }

    internal static bool IsCredentialContract(Type type) => type.IsInterface && (
        InNamespace(type, "DigitalBrain.Kernel.Platform.Secrets") || InNamespace(type, "DigitalBrain.Kernel.Platform.Identity")
        || InNamespace(type, "DigitalBrain.Kernel.Platform.Integrations"));

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
            if (current.IsGenericType && current.GetGenericTypeDefinition() == typeof(DigitalBrain.Contracts.Module<>))
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
