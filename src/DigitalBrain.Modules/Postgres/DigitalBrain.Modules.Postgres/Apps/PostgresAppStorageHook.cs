using System.Text.Json;
using DigitalBrain.Apps;
using Microsoft.Extensions.DependencyInjection;

namespace DigitalBrain.Postgres;

// Retires an app's tables on uninstall. The Apps module drives this through IAppStorageHook
// without knowing Postgres exists.
internal sealed class PostgresAppStorageHook(IServiceProvider services) : IAppStorageHook
{
    // Resolved lazily: the hook is registered on service collections that are validated
    // before the Orleans runtime exists.
    private IGrainFactory Grains => services.GetRequiredService<IGrainFactory>();

    public async Task OnRetired(string brainId, string appKey, string[] storageKeys, CancellationToken cancellationToken)
    {
        await Grains.GetGrain<IPostgresTables>(JsonSerializer.Serialize(new[] { brainId, appKey })).Retire();
        foreach (var key in storageKeys)
        {
            await Grains.GetGrain<IPostgresTables>(JsonSerializer.Serialize(new[] { brainId, key })).Retire();
        }
    }
}
