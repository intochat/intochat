using DigitalBrain;
using DigitalBrain.Capabilities.Connections;
using DigitalBrain.Components;
using DigitalBrain.Neurons;
using DigitalBrain.Runtime.Orleans.Hosting;
using DigitalBrain.Units;
using IntoChat.OS.Host.Primitives;
using IntoChat.OS.Modules.Apps;
using Microsoft.Extensions.DependencyInjection;

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
        if (!TryResolveApp(owner, out var brain, out var componentAlias, out var appNeuronId))
        {
            return null; // a bare behavior owns no slots
        }

        // Prefer the typed binding in IInstalls (single installation record).
        var installs = GetNeuron<IInstalls>(new NeuronId(brain, "synapse.installs", "default"));
        var record = await installs.Describe(componentAlias, CancellationToken.None);
        if (record is not null)
        {
            foreach (var binding in record.EffectiveBindings())
            {
                if (string.Equals(binding.Slot, Name, StringComparison.Ordinal)
                    && binding.Target is BindingTarget.Connection { ConnectionKey: { Length: > 0 } key })
                {
                    return ConnectionKeys.Is(key) ? key : ConnectionKeys.For(owner.BrainId, "postgres", key);
                }
            }
        }

        // Backward-compatibility fallback for persisted AppState not yet migrated to IInstalls.
        var app = await GetNeuron<IApp>(appNeuronId).Read();
        return app.Manifest.Slots.Any(slot => slot.Name == Name && slot.Connector == "postgres")
            && app.Connections.TryGetValue(Name, out var id)
            ? ConnectionKeys.For(owner.BrainId, "postgres", id) : null;
    }

    private T GetNeuron<T>(NeuronId id) where T : class
        => services.GetService<INeuronProxies>() is { } proxies
            ? proxies.Get<T>(id)
            : SiloNeurons.Get<T>(services, id);

    private static bool TryResolveApp(TableOwner owner, out BrainId brain, out string componentAlias, out NeuronId appNeuronId)
    {
        brain = new BrainId(owner.BrainId);
        if (IntoChat.OS.Host.Primitives.AppRef.TryParse(brain, owner.AppId, out var appRef))
        {
            componentAlias = "app." + appRef.Name;
            appNeuronId = appRef.Neuron;
            return true;
        }

        if (owner.AppId.StartsWith("app.", StringComparison.Ordinal)
            && IntoChat.OS.Host.Primitives.AppRef.TryOf(brain, owner.AppId["app.".Length..], out var componentApp))
        {
            componentAlias = owner.AppId;
            appNeuronId = componentApp.Neuron;
            return true;
        }

        componentAlias = string.Empty;
        appNeuronId = default;
        return false;
    }
}
