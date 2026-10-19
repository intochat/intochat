using IntoChat.OS.Framework.Modules;
using IntoChat.OS.Microsoft.CSharp.Catalog;

namespace IntoChat.OS.Kernel.Tests.Unit;

// The OS composition and the script compiler must agree on which assemblies are contracts: a module's contracts
// that the compiler cannot see would compile nowhere, and the sandbox image carries exactly this set.
public sealed class ScriptVisibilityFacts
{
    [Fact]
    public void EveryModuleContractAssemblyIsScriptVisible()
    {
        var references = CSharpScriptCheck.ReferenceNames().ToHashSet(StringComparer.OrdinalIgnoreCase);
        var contracts = new ModuleInventory(OsCatalog.WithWindowsEdge()).ContractAssemblies();
        Assert.NotEmpty(contracts);
        foreach (var assembly in contracts)
        {
            Assert.True(references.Contains(assembly.GetName().Name!), assembly.GetName().Name + " is a contract assembly of the composition but not in the script reference set.");
        }
    }
}
