[assembly: AssemblyFixture(typeof(IntoChat.OS.Modules.Flutter.Tests.Integration.FlutterHost))]

namespace IntoChat.OS.Modules.Flutter.Tests.Integration;

// The shared host composes the Flutter module backend-only; facts register their own person or use neuron keys no
// other shared fact uses. Facts that compose Apps, Flame or other modules, script a browser provider, restart, or
// read the owner's brain keep their own TestBrain ([Trait("host", "own")]).
public sealed class FlutterHost : BrainHostFixture
{
    // Apps are plain neurons (06-08): a host serves them only with the Apps module's DigitalBrain module, which facts that
    // install or read apps (shipped settings, aliases) need even without the Apps backend.
    protected override TestBrain Configure(TestBrain brain) => brain.WithModule<FlutterModule, FlutterModuleOptions>(flutter => flutter.BackendOnly())
        .WithNeuronModule<IntoChat.OS.Modules.Apps.AppsNeurons>()
        .ConfigureServices(services => global::DigitalBrain.Runtime.Orleans.BrainStorageHosting.AddJournalPayloads(services, typeof(IntoChat.OS.Modules.Apps.IApp).Assembly));
}
