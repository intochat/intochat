using IntoChat.OS.Framework.Modules;
using IntoChat.OS.Modules.Microsoft.CSharp.Catalog;

namespace IntoChat.OS.Kernel.Tests.Unit;

// The OS composition and the script compiler must agree on which assemblies are contracts: a module's contracts
// that the compiler cannot see would compile nowhere, and the sandbox image carries exactly this set.
public sealed class ScriptVisibilityFacts
{
    [Fact]
    public void EveryModuleContractAssemblyIsScriptVisible()
    {
        var modules = OperatingSystemModules.WithWindowsEdge();
        var references = CSharpScriptCheck.ReferenceNames().ToHashSet(StringComparer.OrdinalIgnoreCase);
        var contracts = new ModuleInventory(modules).ContractAssemblies();
        Assert.NotEmpty(contracts);
        foreach (var assembly in contracts)
        {
            Assert.True(references.Contains(assembly.GetName().Name!), assembly.GetName().Name + " is a contract assembly of the composition but not in the script reference set.");
        }

        var declared = contracts.Select(assembly => assembly.GetName().Name!).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var module in modules)
        {
            foreach (var reference in module.Type.Assembly.GetReferencedAssemblies())
            {
                if (reference.Name is { } name && name.EndsWith(".Contracts", StringComparison.OrdinalIgnoreCase))
                {
                    Assert.True(declared.Contains(name), $"{module.Type.Name} references {name} but no composed module declares it as Contracts.");
                }
            }
        }
    }
}
