using DigitalBrain.Runtime.Orleans.Hosting;
using IntoChat.OS.Framework.Modules;
using IntoChat.OS.Modules.Apps;
using Microsoft.Extensions.DependencyInjection;
using IntoChat.OS.Host.Modules;

namespace IntoChat.OS.Modules.Postgres.Tables;

// Retires an app's tables on uninstall and reopens its storage when the app is created again. The Apps module drives this through IAppStorageHook
// without knowing Postgres exists.
internal sealed class TableStorageHook(IServiceProvider services) : IAppStorageHook
{
    private bool IsPostgresActive
        => services.GetService<IReadOnlyList<ModuleStatus>>() is not { } statuses
            || statuses.Any(status => status.Module.Value == "postgres" && status.State == ModuleState.Active);

    // Resolved lazily: the hook is registered on service collections that are validated
    // before the Orleans runtime exists. The index is a plain neuron (06-08).
    private ITableIndex Index(string brainId, string appKey)
        => SiloNeurons.Get<ITableIndex>(services, new TableOwner(brainId, appKey).Key);

    public async Task OnRetired(string brainId, string appKey, CancellationToken cancellationToken)
    {
        if (!IsPostgresActive) { return; }
        await Index(brainId, appKey).Retire();
    }

    public async Task OnCreated(string brainId, string appKey, CancellationToken cancellationToken)
    {
        if (!IsPostgresActive) { return; }
        await Index(brainId, appKey).Reopen();
    }

    // Tables are pinned where they were first defined, so the tables slot cannot move under them.
    public async Task RequireRebindable(string brainId, string appKey, string slot, CancellationToken cancellationToken)
    {
        if (!IsPostgresActive || slot != AppTablesSlot.Name) { return; }
        if ((await Index(brainId, appKey).ReadTables()).Length > 0)
        { throw new InvalidOperationException("The tables slot cannot be rebound while this app owns tables."); }
    }
}
