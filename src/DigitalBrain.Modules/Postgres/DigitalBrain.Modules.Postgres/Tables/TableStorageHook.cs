using DigitalBrain.Apps;
using Microsoft.Extensions.DependencyInjection;

namespace DigitalBrain.Postgres.Tables;

// Retires an app's tables on uninstall. The Apps module drives this through IAppStorageHook
// without knowing Postgres exists.
internal sealed class TableStorageHook(IServiceProvider services) : IAppStorageHook
{
    // Resolved lazily: the hook is registered on service collections that are validated
    // before the Orleans runtime exists.
    private IGrainFactory Grains => services.GetRequiredService<IGrainFactory>();

    public async Task OnRetired(string brainId, string appKey, string[] storageKeys, CancellationToken cancellationToken)
    {
        await Grains.GetGrain<ITableIndex>(new TableOwner(brainId, appKey).Key).Retire();
        foreach (var key in storageKeys)
        {
            await Grains.GetGrain<ITableIndex>(new TableOwner(brainId, key).Key).Retire();
        }
    }

    // Tables are pinned where they were first defined, so the tables slot cannot move under them.
    public async Task RequireRebindable(string brainId, string appKey, string slot, CancellationToken cancellationToken)
    {
        if (slot != AppTablesSlot.Name) { return; }
        if ((await Grains.GetGrain<ITableIndex>(new TableOwner(brainId, appKey).Key).ReadTables()).Length > 0)
        { throw new InvalidOperationException("The tables slot cannot be rebound while this app owns tables."); }
    }
}
