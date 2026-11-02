using DigitalBrain;

namespace IntoChat.OS.Connections.Platform.Connections;

// OS module 'sdk': common capability and friend-style capability sharing across trusted apps.
[DigitalBrain.Module(Id)]
public sealed class SdkModule : DigitalBrain.Module
{
    public const string Id = "sdk";

    public static DigitalBrain.ModuleManifest Manifest { get; } = new(
        Provides: [],
        Requires: [],
        Needs: [],
        Settings: [],
        Secrets: []);
}
