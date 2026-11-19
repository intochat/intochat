using DigitalBrain.Runtime.Orleans;

namespace IntoChat.OS.Kernel.Composition.Kernel;

public static class OsRuntimeOptions
{
    static OsRuntimeOptions()
    {
        DigitalBrain.Modules.Configuration.ModuleConfigurationOptions.Current = new() { Root = "IntoChatOS" };
    }

    public static RuntimeOptions Value { get; } = new()
    {
        TopologyKey = "IntoChatOS:Topology",
        HomeKey = "IntoChatOS:Home",
        BindKey = "IntoChatOS:Bind",
        CloudStorageKey = "IntoChatOS:Cloud:Storage",
        CloudKeyVaultKey = "IntoChatOS:Cloud:KeyVault",
        CloudServerAddressKey = "IntoChatOS:Cloud:ServerAddress",
        MasterKeyKey = "IntoChatOS:MasterKey",
        CustodyConfigurationKey = "IntoChatOS:Platform:Storage:Custody",
        CloudSectionPrefix = "IntoChatOS:Cloud:",
        SignallingSection = "IntoChatOS:Signalling",
        RetentionSection = "IntoChatOS:Signalling:Retention",
        DefaultHomeDirectoryName = ".intochat",
        AuthPostureKey = "IntoChatOS:Auth:Posture",
        OsImage = "ghcr.io/intochat/os",
        PlatformGrainStorage = "platform",
        ClusterGrainStorage = "cluster",
        GrainStateContainer = "os-state",
    };
}
