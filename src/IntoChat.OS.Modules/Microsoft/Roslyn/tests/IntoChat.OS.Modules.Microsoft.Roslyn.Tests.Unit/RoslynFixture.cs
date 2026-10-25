using IntoChat.OS.Testing.Neurons;
using Microsoft.Extensions.DependencyInjection;

namespace IntoChat.OS.Modules.Microsoft.Roslyn.Tests;

// Unit tier: the module's plain neurons on TestOS only.
public sealed partial class RoslynFixture : BothRuntimesFixture
{
    public static TheoryData<NeuronRuntimeKind> Kinds => new(NeuronRuntimeKind.TestOS);

    protected override IReadOnlyList<Type> Modules { get; } = [typeof(RoslynNeurons)];

    protected override void ConfigureTestOS(IServiceCollection services) => ConfigureBoth(services);
}
