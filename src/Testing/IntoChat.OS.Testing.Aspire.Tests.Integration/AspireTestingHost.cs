[assembly: AssemblyFixture(typeof(IntoChat.OS.Testing.Aspire.Tests.Integration.AspireTestingHost))]

namespace IntoChat.OS.Testing.Aspire.Tests.Integration;

// The integration tier's one shared host. The Aspire target facts here start no host of their own: they assert the
// target's refusals and the dashboard client against stub handlers, so the shared brain stays bare.
public sealed class AspireTestingHost : BrainHostFixture
{
    protected override TestBrain Configure(TestBrain brain) => brain;
}
