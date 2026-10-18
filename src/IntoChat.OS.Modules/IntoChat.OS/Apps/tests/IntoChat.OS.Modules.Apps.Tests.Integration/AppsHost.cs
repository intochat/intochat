using IntoChat.OS.Apps;

[assembly: AssemblyFixture(typeof(IntoChat.OS.Modules.Apps.Tests.Integration.AppsHost))]

namespace IntoChat.OS.Modules.Apps.Tests.Integration;

// The shared host composes the Apps module alone. Facts that add an app runtime (fake or scripted), a fake model,
// other modules, options or replaced services, or restart the host keep their own TestBrain ([Trait("host", "own")]).
public sealed class AppsHost : BrainHostFixture
{
    protected override TestBrain Configure(TestBrain brain) => brain.WithModule<AppsModule>();
}
