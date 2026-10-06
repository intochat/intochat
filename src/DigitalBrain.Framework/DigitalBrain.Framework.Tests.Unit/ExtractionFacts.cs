using System.Reflection;
using DigitalBrain;
using DigitalBrain.Framework;
using Xunit;

namespace DigitalBrain.Framework.Tests.Unit;

public sealed class ExtractionFacts
{
    [Fact]
    public void TheDigitalBrainAssemblyExportsOnlyTheWords()
    {
        var exported = typeof(INeuron).Assembly.GetExportedTypes()
            .Where(type => type.Namespace == "DigitalBrain" && !type.Name.Contains('_', StringComparison.Ordinal))
            .Select(type => type.Name)
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.Equal("DigitalBrain", typeof(INeuron).Assembly.GetName().Name);
        Assert.Equal(
            ["Activated", "BrainSnapshot", "EstablishBrain", "IBrain", "IDigitalBrain", "INeuron", "INeuronObserver", "NeuronActivated", "NeuronActivity", "NeuronDeactivated", "PlatformAssemblyAttribute", "PlatformOnlyAttribute", "Signal"],
            exported);
    }

    [Fact]
    public void AnEmptyCompositionStillExposesTheWordsAndTheKernelContracts()
    {
        var assemblies = new ModuleInventory([]).ContractAssemblies().Select(assembly => assembly.GetName().Name).Order(StringComparer.Ordinal).ToArray();

        Assert.Equal(["DigitalBrain", "DigitalBrain.Platform.Contracts"], assemblies);
        Assert.DoesNotContain(new ModuleInventory([]).ContractAssemblies(), PlatformAssemblyAttribute.IsPlatform);
    }
}
