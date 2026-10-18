using DigitalBrain.Testing.Neurons;

[assembly: AssemblyFixture(typeof(DigitalBrain.Testing.Tests.Integration.TestingHost))]

namespace DigitalBrain.Testing.Tests.Integration;

// The shared host with the pulse module. Facts that test the harness itself (restarts, start retries, cancellation,
// budgets, side-by-side hosts, configuration) start their own ([Trait("host", "own")]).
public sealed class TestingHost : BrainHostFixture
{
    protected override TestBrain Configure(TestBrain brain) => brain.WithModule<PulseModule>();
}
