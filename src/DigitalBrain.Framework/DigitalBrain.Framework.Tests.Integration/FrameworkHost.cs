[assembly: AssemblyFixture(typeof(DigitalBrain.Framework.Tests.Integration.FrameworkHost))]

namespace DigitalBrain.Framework.Tests.Integration;

// The shared default host. Facts that compose other modules, replace services or watch host-wide hubs keep their own
// TestBrain ([Trait("host", "own")]).
public sealed class FrameworkHost : BrainHostFixture
{
    protected override TestBrain Configure(TestBrain brain) => brain;
}
