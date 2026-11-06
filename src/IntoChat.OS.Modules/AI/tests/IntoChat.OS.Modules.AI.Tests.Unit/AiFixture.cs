using DigitalBrain;
using DigitalBrain.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace IntoChat.OS.Modules.AI.Tests;

// Unit tier: the plain AI neurons on TestOS only, the model neurons over a gated fake inference.
public sealed class AiFixture : IAsyncDisposable
{
    private TestOS? _os;

    public GatedInference Inference { get; } = new();

    public TestOS Runtime => _os ??= CreateLocal();

    private TestOS CreateLocal()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IntoChat.OS.Modules.AI.Models.IModelInference>(Inference);
        var os = TestOS.Local(options => options.Services = services.BuildServiceProvider());
        os.Register(typeof(AIModule).Assembly);
        return os;
    }

    public async ValueTask DisposeAsync()
    {
        if (_os is not null)
        {
            await _os.DisposeAsync();
        }
    }
}
