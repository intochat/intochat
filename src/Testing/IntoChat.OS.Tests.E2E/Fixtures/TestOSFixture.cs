using DigitalBrain;
using DigitalBrain.Access;
using DigitalBrain.Testing;
using Xunit;

namespace IntoChat.OS.Tests.E2E.Fixtures;

/// <summary>
/// Test fixture hosting the deterministic, in-memory <see cref="TestOS"/> simulation runtime.
/// Provides sub-millisecond execution with zero Orleans or network dependencies.
/// </summary>
public sealed class TestOSFixture : IAsyncLifetime
{
    private TestOS? _os;

    public TestOS OS => _os ?? throw new InvalidOperationException("TestOSFixture has not been initialized.");

    public EdgeAuthority Edge => OS.Edge;

    public ValueTask InitializeAsync()
    {
        _os = TestOS.Local();
        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        if (_os is not null)
        {
            await _os.DisposeAsync().ConfigureAwait(false);
            _os = null;
        }
    }

    public TestOS CreateIsolated(Action<TestOSOptions>? configure = null)
    {
        return TestOS.Local(configure);
    }
}
