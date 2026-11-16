using DigitalBrain.Capabilities.Connections;
using DigitalBrain.Runtime.Orleans.Hosting;
using IntoChat.OS.Modules.Apps;

namespace IntoChat.OS.Modules.Postgres.Tables;

/// <summary>
/// An app's reserved <c>tables</c> slot: when the app declares it with the postgres connector and binds it, the app's
/// tables are placed in that connection's database instead of the brain's own.
/// </summary>
internal static class TablesSlot
{
    /// <summary>The reserved slot name.</summary>
    public const string Name = AppTablesSlot.Name;

    /// <summary>
    /// The connection key the owner's tables slot is bound to, or <see langword="null"/> when the owner is not an app
    /// of its brain, does not declare a postgres tables slot or has not bound it.
    /// </summary>
    public static Task<string?> BoundConnection(IServiceProvider services, TableOwner owner)
    {
        ArgumentNullException.ThrowIfNull(services);
        return new AppTablesSlot(services).BoundConnection(owner);
    }
}

internal sealed class AppTablesSlot(IServiceProvider services) : ITablesSlot
{
    public const string Name = "tables";

    public async Task<string?> BoundConnection(TableOwner owner)
    {
        if (!IntoChat.OS.Primitives.AppRef.TryParse(new DigitalBrain.BrainId(owner.BrainId), owner.AppId, out _)) { return null; } // a bare behavior owns no slots
        var app = await SiloNeurons.Get<IApp>(services, owner.AppId).Read();
        return app.Manifest.Slots.Any(slot => slot.Name == Name && slot.Connector == "postgres")
            && app.Connections.TryGetValue(Name, out var id)
            ? ConnectionKeys.For(owner.BrainId, "postgres", id) : null;
    }
}
