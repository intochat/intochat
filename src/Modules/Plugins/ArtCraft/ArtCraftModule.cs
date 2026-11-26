using DigitalBrain.Modules;
using DigitalBrain.Modules.Configuration;
namespace IntoChat.OS.Modules.ArtCraft;

[DigitalBrain.Modules.Module(ManifestName)]
public sealed class ArtCraftModule : DigitalBrain.Modules.Module
{
    internal const string ManifestName = "artcraft";

    public static DigitalBrain.Modules.ModuleManifest Manifest { get; } = new(
        Provides: [],
        Requires: [],
        Needs: [],
        Settings: [DigitalBrain.Modules.Setting.Of<ArtCraftModuleOptions>(DigitalBrain.Modules.Configuration.ModuleConfiguration.ModuleOptionsSection(ManifestName))],
        Secrets: []);

    public static bool RequiresContainer => true;

    public static System.Reflection.Assembly? Contracts => typeof(IArtCraftStudio).Assembly;

    // The studio neuron reads its options; the apps themselves run as containers the hosting adds.
}
