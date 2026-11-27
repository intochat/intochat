using System.Reflection;
using ArchUnitNET.Fluent;
using ArchUnitNET.xUnitV3;
using DigitalBrain.Modules;
using IntoChat.OS.Modules.Identity.Contracts.Integrations;
using IntoChat.OS.Modules.Identity.Directory;

namespace IntoChat.OS.Architecture.Tests.Unit;

public sealed class BoundaryFacts
{
    // DigitalBrain (the core package) is the lowest layer; IntoChat.OS.Contracts.Configuration is the innermost os layer:
    // configuration names with no dependencies, which deployments share.
    private static readonly string[] KernelLayers =
    [
        "DigitalBrain", "IntoChat.OS", "IntoChat.OS.Host", "IntoChat.OS.Modules.Identity.Contracts", "IntoChat.OS.Modules.Identity"
    ];

    [Theory]
    [InlineData("DigitalBrain", "IntoChat.OS")]
    [InlineData("IntoChat.OS", "IntoChat.OS.Host")]
    [InlineData("IntoChat.OS.Host", "IntoChat.OS.Modules.Identity")]
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
            var allowed = KernelLayers.Take(index + 1).ToHashSet(StringComparer.Ordinal);
            var forbidden = ProductionArchitecture.Assemblies
                .Where(candidate => !allowed.Contains(candidate.GetName().Name!))
                .ToArray();
            if (forbidden.Length > 0)
            {
                layer.Should().NotDependOnAny(ArchRuleDefinition.Types().That().ResideInAssembly(forbidden[0], forbidden[1..]))
                    .Because("kernel layers only depend on inward layers, never feature modules")
                    .Check(architecture);
            }
        }
    }

    [Fact]
    public void FeatureModulesNeverDependOnTheCredentialImplementation()
    {
        var architecture = ProductionArchitecture.Graph.Value;
        var forbiddenAssemblies = ProductionArchitecture.Assemblies
            .Where(a => a.GetName().Name is "IntoChat.OS.Kernel" or "IntoChat.OS.AppHost")
            .ToArray();
        Assert.Equal(2, forbiddenAssemblies.Length);
        var forbidden = ArchRuleDefinition.Types().That().ResideInAssembly(forbiddenAssemblies[0], forbiddenAssemblies[1..]);
        var modules = ProductionArchitecture.Assemblies
            .Where(assembly => assembly.GetName().Name?.StartsWith("IntoChat.OS.Modules.", StringComparison.Ordinal) == true)
            .ToArray();
        Assert.NotEmpty(modules);
        ArchRuleDefinition.Types().That().ResideInAssembly(modules[0], modules[1..])
            .Should().NotDependOnAny(forbidden)
            .Check(architecture);
    }

    [Fact]
    public void ScriptVisibleContractsOnlyDependOnOtherContractsAndTheNeuronAbstractions()
    {
        var architecture = ProductionArchitecture.Graph.Value;
        var contracts = ProductionArchitecture.Assemblies.Where(ContractRing.IsContractAssembly).ToArray();
        var runtime = ProductionArchitecture.Assemblies.Except(contracts)
            .Where(assembly => assembly.GetName().Name is not ("IntoChat.OS" or "DigitalBrain" or "DigitalBrain.UI")).ToArray();
        Assert.NotEmpty(contracts);
        var contractProjects = contracts.Where(ContractRing.IsContractsProject).ToArray();

        // Standard contract projects (except Identity.Contracts which has the host-layer exemption below).
        var standardContracts = contractProjects.Where(c => c.GetName().Name != "IntoChat.OS.Modules.Identity.Contracts").ToArray();
        if (standardContracts.Length > 0 && runtime.Length > 0)
        {
            ArchRuleDefinition.Types().That().ResideInAssembly(standardContracts[0], standardContracts[1..])
                .Should().NotDependOnAny(ArchRuleDefinition.Types().That().ResideInAssembly(runtime[0], runtime[1..]))
                .Check(architecture);
        }

        // Identity.Contracts may use IntoChat.OS.Host (guarded by AppLoadContext sandbox check), never other runtime assemblies.
        var identityContracts = contractProjects.SingleOrDefault(c => c.GetName().Name == "IntoChat.OS.Modules.Identity.Contracts");
        if (identityContracts is not null)
        {
            var forbiddenForIdentity = runtime.Where(implementation => !IdentityContractsMayUseTheHostLayer(identityContracts, implementation)).ToArray();
            if (forbiddenForIdentity.Length > 0)
            {
                ArchRuleDefinition.Types().That().ResideInAssembly(identityContracts)
                    .Should().NotDependOnAny(ArchRuleDefinition.Types().That().ResideInAssembly(forbiddenForIdentity[0], forbiddenForIdentity[1..]))
                    .Check(architecture);
            }
        }

        // A hosting module assembly mixes its contracts with its implementation: only the contract types are judged,
        // against the other runtime assemblies and against the module's own implementation types.
        var runtimeTypes = runtime.Length > 0
            ? ArchRuleDefinition.Types().That().ResideInAssembly(runtime[0], runtime[1..])
            : null;
        foreach (var host in contracts.Where(assembly => !ContractRing.IsContractsProject(assembly)))
        {
            var contractRuntimeTypes = ContractRing.ContractTypes(host);
            if (contractRuntimeTypes.Length == 0) { continue; }
            var contractProvider = ArchRuleDefinition.Types().That().Are(contractRuntimeTypes);
            if (runtimeTypes is not null)
            {
                contractProvider.Should().NotDependOnAny(runtimeTypes).Check(architecture);
            }

            var ownProvider = ArchRuleDefinition.Types().That().ResideInAssembly(host).And().AreNot(contractRuntimeTypes);
            contractProvider.Should()
                .NotDependOnAny(ownProvider)
                .Check(architecture);
        }
    }

    // Owner decision 2026-11-20 (identity break-up, option A): the OS primitives live in the host layer, and the identity
    // contracts use them (ShippedGrants builds the assistant's key with AppRef). Scripts never see either: the sandbox
    // refuses every IntoChat.OS.Modules.Identity* assembly, which this exemption checks before it applies.
    private static bool IdentityContractsMayUseTheHostLayer(System.Reflection.Assembly contract, System.Reflection.Assembly implementation)
    {
        if (contract.GetName().Name != "IntoChat.OS.Modules.Identity.Contracts" || implementation.GetName().Name != "IntoChat.OS.Host") { return false; }
        var guard = File.ReadAllText(Path.Combine(Repository.ProjectNamed("IntoChat.OS.Sandbox").Directory, "AppLoadContext.cs"));
        Assert.Contains("\"IntoChat.OS.Modules.Identity\",", guard, StringComparison.Ordinal);
        return true;
    }

    [Fact]
    public void CredentialAndIdentityContractsResideInPlatformContracts()
    {
        var contracts = ProductionArchitecture.Types.Where(IsCredentialContract).ToArray();
        Assert.Contains(typeof(IIntegration), contracts);
        Assert.Contains(typeof(IntoChat.OS.Modules.Identity.Contracts.Accounts.IIdentityClient), contracts);
        ArchRuleDefinition.Interfaces().That().Are(contracts)
            .Should().ResideInAssembly(typeof(IIntegration).Assembly, typeof(IntoChat.OS.Host.Modules.OsModule).Assembly)
            .Check(ProductionArchitecture.Graph.Value);
    }

    internal static bool IsCredentialContract(Type type) => type.IsInterface && (
        InNamespace(type, "IntoChat.OS.Modules.Identity.Contracts.Accounts") || InNamespace(type, "IntoChat.OS.Modules.Identity.Directory")
        || InNamespace(type, "IntoChat.OS.Modules.Identity.Contracts.Integrations"));

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
