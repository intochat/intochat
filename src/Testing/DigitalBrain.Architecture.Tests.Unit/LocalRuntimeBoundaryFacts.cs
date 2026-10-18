using DigitalBrain.Platform;

namespace DigitalBrain.Architecture.Tests.Unit;

public sealed class LocalRuntimeBoundaryFacts
{
    [Fact]
    public void ThePlatformHasNoDistributedExecutionOrTrustAssembly()
    {
        var assembly = typeof(PlatformHosting).Assembly;
        Assert.DoesNotContain(assembly.GetReferencedAssemblies(), reference => reference.Name == "DigitalBrain.Platform.Trust");
        Assert.DoesNotContain(assembly.GetTypes(), type => type.Namespace is
            "DigitalBrain.Platform.Parts" or "DigitalBrain.Platform.Links" or "DigitalBrain.Platform.Mesh" or "DigitalBrain.Platform.Sequencer" or "DigitalBrain.Platform.Trust");
    }
}
