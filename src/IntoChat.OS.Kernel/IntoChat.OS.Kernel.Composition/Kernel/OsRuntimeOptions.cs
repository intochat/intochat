using DigitalBrain.Runtime.Orleans;

namespace IntoChat.OS.Kernel.Composition;

public static class OsRuntimeOptions
{
    public static RuntimeOptions Value { get; } = new()
    {
        TopologyKey = "IntoChatOS:Topology",
        HomeKey = "IntoChatOS:Home",
        BindKey = "IntoChatOS:Bind",
        CloudStorageKey = "IntoChatOS:Cloud:Storage",
        CloudKeyVaultKey = "IntoChatOS:Cloud:KeyVault",
        CloudServerAddressKey = "IntoChatOS:Cloud:ServerAddress",
        MasterKeyKey = "IntoChatOS:MasterKey",
        CloudSectionPrefix = "IntoChatOS:Cloud:",
        SignallingSection = "IntoChatOS:Signalling",
        RetentionSection = "IntoChatOS:Signalling:Retention",
        DefaultHomeDirectoryName = ".intochat",
        AuthPostureKey = "IntoChatOS:Auth:Posture",
        OsImage = "ghcr.io/intochat/os",
        SandboxImage = "ghcr.io/intochat/os-sandbox",
        PubSubStore = "PubSubStore",
        SignalStreams = "signals",
        PlatformGrainStorage = "platform",
        ClusterGrainStorage = "cluster",
        GrainStateContainer = "os-state",
    };
}
