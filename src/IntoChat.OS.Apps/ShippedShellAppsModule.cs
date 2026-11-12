using DigitalBrain.Modules;

namespace IntoChat.OS.Apps;

[Module("os-apps")]
public sealed class ShippedShellAppsModule : Module
{
    public static ModuleManifest Manifest { get; } = new(Provides: [], Requires: [], Needs: [], Settings: [], Secrets: []);
}
