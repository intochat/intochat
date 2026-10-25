using IntoChat.OS.Framework.Modules;

namespace IntoChat.OS.Modules.ArtCraft;

public sealed class ArtCraftModule() : Module<ArtCraftModuleOptions>(new ModuleId("artcraft"))
{
    public override bool RequiresContainer => true;

    public override System.Reflection.Assembly? Contracts => typeof(IArtCraftStudio).Assembly;

    // The studio neuron reads its options; the apps themselves run as containers the hosting adds.
    public override void Configure(ISiloBuilder silo) => ArgumentNullException.ThrowIfNull(silo);
}
