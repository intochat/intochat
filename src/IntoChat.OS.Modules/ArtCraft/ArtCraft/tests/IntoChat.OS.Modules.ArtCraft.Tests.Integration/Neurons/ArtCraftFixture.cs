using DigitalBrain.Runtime.Orleans;
using IntoChat.OS.Testing.Neurons;
using Microsoft.Extensions.DependencyInjection;

namespace IntoChat.OS.Modules.ArtCraft.Tests;

// Integration tier: the module's plain neurons on TestOS and on SiloHost.Local.
public sealed partial class ArtCraftFixture : BothRuntimesFixture
{
    public static TheoryData<NeuronRuntimeKind> Kinds => NeuronRuntimeKinds.Both;

    protected override IReadOnlyList<Type> Modules { get; } = [typeof(ArtCraftModule)];

    // The neurons' [Signal] records live in the contracts assembly.
    protected override void ConfigureSilo(IServiceCollection services)
    {
        services.AddJournalPayloads(typeof(IntoChat.OS.Modules.ArtCraft.IArtCraftStudio).Assembly);
        ConfigureBoth(services);
    }

    protected override void ConfigureTestOS(IServiceCollection services) => ConfigureBoth(services);
}
