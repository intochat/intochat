namespace IntoChat.OS.Modules.Diagnostics;

// The read side of diagnostics: the Creator's reads of the running brain (a unit's state, view, signals and failures, and the
// brain's topology) and the trace reader. Both neurons are stateless and implement contracts that stay in the catalog's
// contracts assembly, which the `apps` module declares, so this module declares none of its own. The neurons keep the
// namespaces of those contracts (IntoChat.OS.Modules.Apps.Debugger and .Trace). The topology lists the compiled apps the
// catalog offers through ICompiledApps.
[DigitalBrain.Modules.Module(ModuleName)]
public sealed class DiagnosticsModule : DigitalBrain.Modules.Module
{
    internal const string ModuleName = "diagnostics";

    public static System.Reflection.Assembly? Contracts => null;

    public static DigitalBrain.Modules.ModuleManifest Manifest { get; } = new(
        Provides: [],
        Requires: [],
        Needs: [],
        Settings: [],
        Secrets: []);
}
