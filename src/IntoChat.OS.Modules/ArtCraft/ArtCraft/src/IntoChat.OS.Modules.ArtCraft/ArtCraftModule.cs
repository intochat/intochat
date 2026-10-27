using IntoChat.OS.Framework.Modules;

namespace IntoChat.OS.Modules.ArtCraft;

public sealed class ArtCraftModule() : Module<ArtCraftModuleOptions>(new ModuleId("artcraft"))
{
    internal const string ManifestName = "artcraft";

    // R4-04: what the module needs from its host (ArtCraftNeurons).
    public static DigitalBrain.ModuleManifest Manifest => ArtCraftNeurons.Manifest;

    // The module's neurons are plain neurons (06-06a) on the runtime's host grains.
    public override Type? Neurons => typeof(ArtCraftNeurons);

    public override bool RequiresContainer => true;

    public override System.Reflection.Assembly? Contracts => typeof(IArtCraftStudio).Assembly;

    // The studio neuron reads its options; the apps themselves run as containers the hosting adds.
}

// The DigitalBrain module of ArtCraft (06-06a): its assembly's [Neuron] classes run on the runtime's host grains, on TestOS and on
// the silo alike.
[DigitalBrain.Module(ArtCraftModule.ManifestName)]
public sealed class ArtCraftNeurons : DigitalBrain.Module
{
    public static DigitalBrain.ModuleManifest Manifest { get; } = new(
        Provides: [],
        Requires: [],
        Needs: [],
        Settings: [DigitalBrain.Setting.Of<ArtCraftModuleOptions>(IntoChat.OS.Framework.Modules.Configuration.IntoChatOSConfiguration.ModuleOptionsSection(ArtCraftModule.ManifestName))],
        Secrets: []);
}
