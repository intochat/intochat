[assembly: AssemblyFixture(typeof(IntoChat.OS.Circles.Tests.CirclesHost))]

namespace IntoChat.OS.Circles.Tests;

// The shared host composes Apps and Circles over the features folder with the scripted connector; each fact births
// its circles for fresh people. Facts that add services (a price list, a sandbox, a failing runtime), compose Flutter,
// or run members on separate machines keep their own TestBrain ([Trait("host", "own")]).
public sealed class CirclesHost : BrainHostFixture
{
    protected override TestBrain Configure(TestBrain brain) => CircleBrainRooms.Compose(brain);
}
