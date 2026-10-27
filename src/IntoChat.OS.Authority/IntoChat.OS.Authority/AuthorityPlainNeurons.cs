namespace IntoChat.OS.Authority;

// The DigitalBrain module of the Authority's plain neurons (06-08): the connection index and the connection requests. The kernel
// host composes it on every silo; the Authority's raw grains stay grains (NeuronsStillGrains.json).
[DigitalBrain.Module("authority")]
public sealed class AuthorityPlainNeurons : DigitalBrain.Module
{
    public static DigitalBrain.ModuleManifest Manifest { get; } = new(Provides: [], Requires: [], Needs: [], Settings: [], Secrets: []);
}
