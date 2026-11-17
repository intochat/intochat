using DigitalBrain.Modules;
namespace IntoChat.Cloud.Blob;

public sealed class BlobStorageOptions
{
    public string? Endpoint { get; set; }
}

// Cloud blob storage. Composed by the cloud host regardless of topology.
[DigitalBrain.Modules.Module(ManifestName)]
public sealed class BlobStorageModule : DigitalBrain.Modules.Module
{
    internal const string ManifestName = "blob";
    internal const string ConnectionSecret = "connection-string";

    public static DigitalBrain.Modules.ModuleManifest Manifest { get; } = new(
        Provides: [DigitalBrain.Capabilities.Capability.Storage.V1],
        Requires: [],
        Needs: [],
        Settings: [DigitalBrain.Modules.Setting.Of<BlobStorageOptions>("IntoChatOS:Modules:" + ManifestName + ":Options")],
        Secrets: [DigitalBrain.Modules.Secret.Named(ConnectionSecret)]);
}
