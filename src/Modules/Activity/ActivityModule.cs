namespace IntoChat.OS.Modules.Apps.Runs;

// The activity read model of a brain: the runs index and the synapse definitions read from it. Both neurons implement contracts
// that stay in the catalog's contracts assembly, which the `apps` module declares, so this module declares none of its own.
// The namespace is the one its contracts keep: a namespace named after the assembly would hide System.Diagnostics.Activity from
// every type under IntoChat.OS.Modules.
[DigitalBrain.Modules.Module(ModuleName)]
public sealed class ActivityModule : DigitalBrain.Modules.Module
{
    internal const string ModuleName = "activity";

    public static System.Reflection.Assembly? Contracts => null;

    public static DigitalBrain.Modules.ModuleManifest Manifest { get; } = new(
        Provides: [],
        Requires: [],
        Needs: [],
        Settings: [],
        Secrets: []);
}
