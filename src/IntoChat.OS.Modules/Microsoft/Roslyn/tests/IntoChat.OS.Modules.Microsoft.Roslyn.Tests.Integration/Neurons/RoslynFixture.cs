using DigitalBrain.Runtime.Orleans;
using DigitalBrain.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace IntoChat.OS.Modules.Microsoft.Roslyn.Tests;

// Integration tier: the module's plain neurons on TestOS and on SiloHost.Local.
public sealed partial class RoslynFixture : BothRuntimesFixture
{
    public static TheoryData<NeuronRuntimeKind> Kinds => NeuronRuntimeKinds.Both;

    protected override IReadOnlyList<Type> Modules { get; } = [typeof(RoslynModule)];

    // The neurons' [Signal] records live in the contracts assembly.
    protected override void ConfigureSilo(IServiceCollection services)
    {
        services.AddJournalPayloads(typeof(IntoChat.OS.Modules.Microsoft.Roslyn.IRoslyn).Assembly);
        ConfigureBoth(services);
    }

    protected override void ConfigureTestOS(IServiceCollection services) => ConfigureBoth(services);
}
