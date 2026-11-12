using DigitalBrain.Modules;
namespace IntoChat.Cloud.Blob;

public sealed class BlobStorageOptions
{
    public string? Endpoint { get; set; }
}

// Cloud storage. Absent from a local topology: LoadsUnder is cloud and local+cloud only.
[DigitalBrain.Modules.Module(ManifestName)]
public sealed class BlobStorageModule : DigitalBrain.Modules.Module
{
    internal const string ManifestName = "blob";
    internal const string ConnectionSecret = "connection-string";

    public static bool LoadsUnder(string? topology)
    {
        var value = topology?.Trim() ?? "";
        return value.Equals("cloud", StringComparison.OrdinalIgnoreCase)
            || value.Equals("local+cloud", StringComparison.OrdinalIgnoreCase)
            || value.Equals("local-cloud", StringComparison.OrdinalIgnoreCase);
    }

    public static DigitalBrain.Modules.ModuleManifest Manifest { get; } = new(
        Provides: [DigitalBrain.Capabilities.Capability.Storage.V1],
        Requires: [],
        Needs: [],
        Settings: [DigitalBrain.Modules.Setting.Of<BlobStorageOptions>("IntoChatOS:Modules:" + ManifestName + ":Options")],
        Secrets: [DigitalBrain.Modules.Secret.Named(ConnectionSecret)]);
}
