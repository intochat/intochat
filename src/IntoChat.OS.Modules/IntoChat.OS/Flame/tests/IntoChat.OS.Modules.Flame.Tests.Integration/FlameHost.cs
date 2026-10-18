using IntoChat.OS.Flame;

[assembly: AssemblyFixture(typeof(IntoChat.OS.Modules.Flame.Tests.Integration.FlameHost))]

namespace IntoChat.OS.Modules.Flame.Tests.Integration;

// The shared host composes the Flame module; scene facts keep their scene in a fresh person's brain. Facts that act
// as the owner's apps, scheduler or plain HTTP client keep their own TestBrain ([Trait("host", "own")]).
public sealed class FlameHost : BrainHostFixture
{
    protected override TestBrain Configure(TestBrain brain) => brain.WithModule<FlameModule>();
}
