using DigitalBrain.Runtime.Orleans;
using DigitalBrain.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace IntoChat.OS.Modules.Flame.Tests;

// Shared Flame test fixture (R4-05): hosts the plain scene neuron on TestOS (Unit tier) or on both TestOS and SiloHost.Local (Integration tier).
public class FlameFixture : BothRuntimesFixture
{
    private TestOS? _os;

    public FlameFixture()
        : this(NeuronRuntimeKinds.Both)
    {
    }

    protected FlameFixture(TheoryData<NeuronRuntimeKind> kinds)
    {
        SupportedKinds = kinds;
    }

    public static TheoryData<NeuronRuntimeKind> Kinds => NeuronRuntimeKinds.Both;

    public TheoryData<NeuronRuntimeKind> SupportedKinds { get; }

    public new TestOS Runtime => _os ??= CreateLocal();

    protected override IReadOnlyList<Type> Modules { get; } = [typeof(FlameModule)];

    // The scene's [Signal] records live in the contracts assembly, beside the module's.
    protected override void ConfigureSilo(IServiceCollection services) => services.AddJournalPayloads(typeof(Scene.IScene).Assembly);

    public new async ValueTask DisposeAsync()
    {
        if (_os is not null)
        {
            await _os.DisposeAsync();
        }
        await base.DisposeAsync();
    }

    private static TestOS CreateLocal()
    {
        var os = TestOS.Local();
        os.Register<SceneNeuron>();
        return os;
    }
}
