namespace IntoChat.OS.Modules.Runs;

// The runs read model of a brain: the runs index and the synapse definitions read from it. Both neurons implement contracts
// that stay in the catalog's contracts assembly, which the `apps` module declares, so this module declares none of its own.
// The neurons keep the namespaces of those contracts (IntoChat.OS.Modules.Apps.Runs and .Synapses).
[DigitalBrain.Modules.Module(ModuleName)]
public sealed class RunsModule : DigitalBrain.Modules.Module
{
    internal const string ModuleName = "runs";

    public static System.Reflection.Assembly? Contracts => null;

    public static DigitalBrain.Modules.ModuleManifest Manifest { get; } = new(
        Provides: [],
        Requires: [],
        Needs: [],
        Settings: [],
        Secrets: []);
}
