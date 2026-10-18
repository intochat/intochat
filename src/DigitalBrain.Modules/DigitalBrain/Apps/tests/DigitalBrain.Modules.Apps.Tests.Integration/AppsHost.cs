using DigitalBrain.Apps;

[assembly: AssemblyFixture(typeof(DigitalBrain.Modules.Apps.Tests.Integration.AppsHost))]

namespace DigitalBrain.Modules.Apps.Tests.Integration;

// The shared host composes the Apps module alone. Facts that add an app runtime (fake or scripted), a fake model,
// other modules, options or replaced services, or restart the host keep their own TestBrain ([Trait("host", "own")]).
public sealed class AppsHost : BrainHostFixture
{
    protected override TestBrain Configure(TestBrain brain) => brain.WithModule<AppsModule>();
}
