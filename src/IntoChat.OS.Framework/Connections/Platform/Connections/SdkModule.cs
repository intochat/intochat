using DigitalBrain.Modules;

namespace IntoChat.OS.Connections.Platform.Connections;

// OS module 'sdk': common capability and friend-style capability sharing across trusted apps.
[Module(Id)]
public sealed class SdkModule : Module
{
    public const string Id = "sdk";

    public static ModuleManifest Manifest { get; } = new(
        Provides: [],
        Requires: [],
        Needs: [],
        Settings: [],
        Secrets: []);
}
