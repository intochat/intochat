using DigitalBrain.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace IntoChat.OS.Modules.Microsoft.Roslyn.Tests;

// Unit tier: the module's plain neurons on TestOS only.
public sealed partial class RoslynFixture : IAsyncDisposable
{
    private TestOS? _os;

    public TestOS Runtime => _os ??= CreateLocal();

    private static TestOS CreateLocal()
    {
        var services = new ServiceCollection();
        ConfigureBoth(services);
        var os = TestOS.Local(options => options.Services = services.BuildServiceProvider());
        os.Register(typeof(RoslynModule).Assembly);
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
