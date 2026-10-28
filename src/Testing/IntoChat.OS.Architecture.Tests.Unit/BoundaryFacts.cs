using System.Reflection;
using ArchUnitNET.Fluent;
using ArchUnitNET.xUnitV3;
using IntoChat.OS.Framework.Modules.Platform.Identity;
using IntoChat.OS.Connections.Platform.Integrations;
using IntoChat.OS.Connections.Platform.Secrets;

namespace IntoChat.OS.Architecture.Tests.Unit;

public sealed class BoundaryFacts
{
    // DigitalBrain (the core package) is the lowest layer; IntoChat.OS.Contracts.Configuration is the innermost os layer:
    // configuration names with no dependencies, which deployments share.
    private static readonly string[] KernelLayers =
    [
        "DigitalBrain", "IntoChat.OS.Contracts.Configuration", "IntoChat.OS", "IntoChat.OS.Contracts.Compute", "IntoChat.OS.Framework",
        "IntoChat.OS.Client", "IntoChat.OS.Client.Orleans", "IntoChat.OS.Modules.Identity.Contracts", "IntoChat.OS.Modules.Identity"
    ];

    [Theory]
    [InlineData("DigitalBrain", "IntoChat.OS")]
    [InlineData("IntoChat.OS", "IntoChat.OS.Framework")]
    [InlineData("IntoChat.OS.Framework", "IntoChat.OS.Client")]
    [InlineData("IntoChat.OS.Framework", "IntoChat.OS.Client.Orleans")]
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
        var implementations = Repository.References(Repository.ProjectNamed("IntoChat.OS.Modules.Circles").Path)
            .Where(reference => reference.StartsWith("IntoChat.OS.Modules.", StringComparison.Ordinal) && !reference.EndsWith(".Contracts", StringComparison.Ordinal))
            .ToArray();
        Assert.Equal(["IntoChat.OS.Modules.Apps", "IntoChat.OS.Modules.Identity"], implementations);
        var circles = Assert.Single(ProductionArchitecture.Assemblies, assembly => assembly.GetName().Name == "IntoChat.OS.Modules.Circles");
        Assert.Contains(circles.GetTypes(), type => type.GetConstructors(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
            .Any(constructor => constructor.GetParameters().Any(parameter => parameter.ParameterType.FullName == "IntoChat.OS.Modules.Apps.FeatureLoader")));
    }

    [Fact]
    public void FeatureModulesNeverDependOnTheCredentialImplementation()
    {
        var architecture = ProductionArchitecture.Graph.Value;
        var platform = ArchRuleDefinition.Types().That().ResideInAssembly("IntoChat.OS.Modules.Identity");
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
            Assert.NotEmpty(contractTypes);
            var own = host.GetTypes().Where(type => !contractTypes.Contains(type.FullName ?? "")
                && type.Namespace?.StartsWith("OrleansCodeGen", StringComparison.Ordinal) != true && !type.IsDefined(typeof(System.Runtime.CompilerServices.CompilerGeneratedAttribute), false))
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
            ArchRuleDefinition.Interfaces().That().Are(contract).Should().ResideInAssembly(typeof(ISecrets).Assembly, typeof(IntoChat.OS.Framework.Modules.OsModule).Assembly, typeof(IIdentityProvisioning).Assembly)
                .Check(ProductionArchitecture.Graph.Value);
        }
    }

    internal static bool IsCredentialContract(Type type) => type.IsInterface && (
        InNamespace(type, "IntoChat.OS.Connections.Platform.Secrets")
        || InNamespace(type, "IntoChat.OS.Connections.Platform.Identity") || InNamespace(type, "IntoChat.OS.Framework.Modules.Platform.Identity")
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
        if (type.GetCustomAttribute<DigitalBrain.ModuleAttribute>() is null || !typeof(DigitalBrain.Module).IsAssignableFrom(type)) { yield break; }
        foreach (var setting in DigitalBrain.ModuleManifest.Of(type).Settings) { yield return setting.OptionsType; }
    }

    [Fact]
    public void ProductionInterfacesContainNoTestHooks()
    {
        var interfaces = ProductionArchitecture.Types.Where(type => type.IsInterface).ToArray();
        Assert.NotEmpty(interfaces);
        Assert.Empty(interfaces.SelectMany(ContractRules.TestHooks));
    }
}
