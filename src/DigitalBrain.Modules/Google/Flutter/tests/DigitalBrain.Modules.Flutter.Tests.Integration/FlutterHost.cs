using DigitalBrain.Flutter;

[assembly: AssemblyFixture(typeof(DigitalBrain.Modules.Flutter.Tests.Integration.FlutterHost))]

namespace DigitalBrain.Modules.Flutter.Tests.Integration;

// The shared host composes the Flutter module backend-only; facts register their own person or use neuron keys no
// other shared fact uses. Facts that compose Apps, Flame or other modules, script a browser provider, restart, or
// read the owner's brain keep their own TestBrain ([Trait("host", "own")]).
public sealed class FlutterHost : BrainHostFixture
{
    protected override TestBrain Configure(TestBrain brain) => brain.WithModule<FlutterModule, FlutterModuleOptions>(flutter => flutter.BackendOnly());
}
