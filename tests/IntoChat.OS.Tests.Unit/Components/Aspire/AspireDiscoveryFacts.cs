using IntoChat.Aspire;
using IntoChat.OS.Host.Kernel;
using IntoChat.OS.Host.Modules;
using IntoChat.OS.Kernel;

namespace IntoChat.OS.Tests.Unit.Components.Aspire;

// The kernel finds the Aspire component in its own assembly exactly as it found the module inside the Azure assembly:
// same id, same topology, its contracts assembly exposed and its contracts listed under the module's id.
public sealed class AspireDiscoveryFacts
{
    [Fact]
    public void TheKernelDiscoversTheAspireModuleInItsComponentAssembly()
    {
        var aspire = Assert.Single(OperatingSystemModules.Modules, module => module.Id.ToString() == "aspire");

        Assert.Equal(typeof(AspireModule), aspire.Type);
        Assert.Equal("IntoChat.Aspire", aspire.Type.Assembly.GetName().Name);
        Assert.True(OperatingSystemModules.DeclaresModule(aspire.Type.Assembly));
        Assert.Same(typeof(IAspire).Assembly, aspire.Contracts);
        Assert.Equal("IntoChat.Aspire", aspire.Contracts!.GetName().Name);
        Assert.True(aspire.LoadsUnder("local"));
        Assert.False(aspire.RequiresContainer);
        Assert.Contains(OperatingSystemModules.InProcessModules, module => module.Type == typeof(AspireModule));
    }

    [Fact]
    public void TheAzureModuleKeepsItsOwnContractsAndNoAspireType()
    {
        var azure = Assert.Single(OperatingSystemModules.Modules, module => module.Id.ToString() == "azure");

        Assert.NotSame(typeof(IAspire).Assembly, azure.Contracts);
        Assert.DoesNotContain(azure.Contracts!.GetExportedTypes(), type => type.Namespace?.Contains("Aspire", StringComparison.Ordinal) == true);
    }

    [Theory]
    [InlineData("aspire", typeof(IAspire))]
    [InlineData("aspire.candidate", typeof(ICandidate))]
    [InlineData("aspire.candidates", typeof(ICandidates))]
    [InlineData("aspire.deployment", typeof(IDeployment))]
    public void TheComposedTypeLayerListsTheAspireContractsUnderTheModule(string alias, Type contract)
    {
        var compiled = new CompiledTypes(new ModuleInventory(OperatingSystemModules.Modules), []);

        var entry = Assert.Single(compiled.Entries, entry => entry.Alias == alias);
        Assert.Equal(contract.FullName, entry.Name);
        Assert.Equal("aspire", entry.Origin);
        Assert.Same(contract, compiled.Contract(entry.Name));
    }

    [Fact]
    public void TheComposedTypeLayerListsTheAspireSignals()
    {
        var compiled = new CompiledTypes(new ModuleInventory(OperatingSystemModules.Modules), []);

        foreach (var signal in new[] { typeof(AspireTelemetryChanged), typeof(CandidatePromoted), typeof(CandidateFailed), typeof(DeploymentChanged) })
        {
            Assert.Contains(compiled.Entries, entry => entry.Name == signal.FullName);
        }
    }
}
