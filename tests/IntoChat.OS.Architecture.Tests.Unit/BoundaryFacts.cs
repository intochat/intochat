using System.Reflection;
using ArchUnitNET.Fluent;
using ArchUnitNET.xUnitV3;
using DigitalBrain.Modules;
using IntoChat.OS.Connections.Platform.Integrations;
using IntoChat.OS.Framework.Modules.Platform.Identity;

namespace IntoChat.OS.Architecture.Tests.Unit;

public sealed class BoundaryFacts
{
    // DigitalBrain (the core package) is the lowest layer; IntoChat.OS.Contracts.Configuration is the innermost os layer:
    // configuration names with no dependencies, which deployments share.
    private static readonly string[] KernelLayers =
    [
        "DigitalBrain", "IntoChat.OS", "IntoChat.OS.Modules.Identity"
    ];

    [Theory]
    [InlineData("DigitalBrain", "IntoChat.OS")]
    [InlineData("IntoChat.OS", "IntoChat.OS.Modules.Identity")]
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

    [Fact]
    public void FeatureModulesNeverDependOnTheCredentialImplementation()
    {
        var architecture = ProductionArchitecture.Graph.Value;
        var kernel = ArchRuleDefinition.Types().That().ResideInAssembly("IntoChat.OS.Kernel");
        var appHost = ArchRuleDefinition.Types().That().ResideInAssembly("IntoChat.OS.AppHost");
        var modules = ProductionArchitecture.Assemblies
            .Where(assembly => assembly.GetName().Name?.StartsWith("IntoChat.OS.Modules.", StringComparison.Ordinal) == true)
            .ToArray();
        Assert.NotEmpty(modules);
        foreach (var assembly in modules)
        {
            ArchRuleDefinition.Types().That().ResideInAssembly(assembly).Should().NotDependOnAny(kernel).Check(architecture);
            ArchRuleDefinition.Types().That().ResideInAssembly(assembly).Should().NotDependOnAny(appHost).Check(architecture);
        }
    }

    [Fact]
    public void ScriptVisibleContractsOnlyDependOnOtherContractsAndTheNeuronAbstractions()
    {
        var contracts = ProductionArchitecture.Assemblies.Where(ContractRing.IsContractAssembly).ToArray();
        var runtime = ProductionArchitecture.Assemblies.Except(contracts)
            .Where(assembly => assembly.GetName().Name is not ("IntoChat.OS" or "DigitalBrain" or "DigitalBrain.UI")).ToArray();
        Assert.NotEmpty(contracts);
        foreach (var contract in contracts.Where(ContractRing.IsContractsProject))
        {
            foreach (var implementation in runtime)
            {
                ArchRuleDefinition.Types().That().ResideInAssembly(contract).Should()
                    .NotDependOnAny(ArchRuleDefinition.Types().That().ResideInAssembly(implementation))
                    .Check(ProductionArchitecture.Graph.Value);
            }
        }
        // A hosting module assembly mixes its contracts with its implementation: only the contract types are judged,
        // against the other runtime assemblies and against the module's own implementation types.
        foreach (var host in contracts.Where(assembly => !ContractRing.IsContractsProject(assembly)))
        {
            var contractTypes = ContractRing.ContractTypes(host).Select(type => type.FullName!).ToHashSet(StringComparer.Ordinal);
            if (contractTypes.Count == 0) { continue; }
            var own = host.GetTypes().Where(type => !contractTypes.Contains(type.FullName ?? "")
                && type.Namespace?.StartsWith("OrleansCodeGen", StringComparison.Ordinal) != true
                && type.Namespace?.StartsWith("System.Text.RegularExpressions.Generated", StringComparison.Ordinal) != true
                && !type.IsDefined(typeof(System.Runtime.CompilerServices.CompilerGeneratedAttribute), false))
                .Select(type => type.FullName!).ToArray();
            foreach (var name in contractTypes)
            {
                foreach (var implementation in runtime)
                {
                    ArchRuleDefinition.Types().That().HaveFullName(name).Should()
                        .NotDependOnAny(ArchRuleDefinition.Types().That().ResideInAssembly(implementation))
                        .Check(ProductionArchitecture.Graph.Value);
                }
                if (own.Length > 0)
                {
                    ArchRuleDefinition.Types().That().HaveFullName(name).Should()
                        .NotDependOnAny(ArchRuleDefinition.Types().That().HaveFullNameMatching("^(" + string.Join('|', own.Select(System.Text.RegularExpressions.Regex.Escape)) + ")$"))
                        .Check(ProductionArchitecture.Graph.Value);
                }
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
            ArchRuleDefinition.Interfaces().That().Are(contract).Should().ResideInAssembly(typeof(IIdentityDirectory).Assembly, typeof(IntoChat.OS.Framework.Modules.OsModule).Assembly)
                .Check(ProductionArchitecture.Graph.Value);
        }
    }

    internal static bool IsCredentialContract(Type type) => type.IsInterface && (
        InNamespace(type, "IntoChat.OS.Connections.Platform.Identity") || InNamespace(type, "IntoChat.OS.Framework.Modules.Platform.Identity")
        || InNamespace(type, "IntoChat.OS.Connections.Platform.Integrations") || InNamespace(type, "IntoChat.OS.Framework.Modules.Platform.Integrations"));

    private static bool InNamespace(Type type, string name) =>
        type.Namespace == name || type.Namespace?.StartsWith(name + ".", StringComparison.Ordinal) == true;

    [Fact]
    public void ModuleOptionsContainNoCredentialMembers()
    {
        var options = ProductionArchitecture.Types.Where(type => !type.IsAbstract)
            .SelectMany(ModuleOptionsTypes)
            .Distinct().ToArray();
        Assert.NotEmpty(options);
        Assert.Empty(options.SelectMany(ContractRules.CredentialMembers));
    }

    private static IEnumerable<Type> ModuleOptionsTypes(Type type)
    {
        if (type.GetCustomAttribute<DigitalBrain.Modules.ModuleAttribute>() is null || !typeof(DigitalBrain.Modules.Module).IsAssignableFrom(type)) { yield break; }
        foreach (var setting in DigitalBrain.Modules.ModuleManifest.Of(type).Settings) { yield return setting.OptionsType; }
    }

    [Fact]
    public void ProductionInterfacesContainNoTestHooks()
    {
        var interfaces = ProductionArchitecture.Types.Where(type => type.IsInterface).ToArray();
        Assert.NotEmpty(interfaces);
        Assert.Empty(interfaces.SelectMany(ContractRules.TestHooks));
    }
}
