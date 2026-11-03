using DigitalBrain.Testing;

namespace IntoChat.OS.Modules.Flame.Tests;

// Unit tier: the plain scene neuron on TestOS only.
public sealed class FlameFixture : IAsyncLifetime
{
    private TestOS? _os;

    public TestOS Runtime => _os ?? throw new InvalidOperationException("TestOS is not initialized.");

    public ValueTask InitializeAsync()
    {
        _os = TestOS.Local();
        _os.Register<SceneNeuron>();
        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        if (_os is not null)
        {
            await _os.DisposeAsync();
        }
    }
}
