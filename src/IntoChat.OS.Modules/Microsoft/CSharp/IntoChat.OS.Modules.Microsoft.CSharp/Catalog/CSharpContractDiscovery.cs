using System.Text.Json;
using IntoChat.OS;
using IntoChat.OS.Framework;

namespace IntoChat.OS.Microsoft.CSharp.Catalog;

public sealed record CSharpContractModule(string Id);

public sealed record CSharpContractCatalogSnapshot(IReadOnlyList<CSharpContractModule> Modules, IReadOnlyList<string> Contracts, string Example, bool Truncated = false);

// What a script author (human or agent) sees: the kernel's types for the caller's brain, by provider.
public sealed class CSharpContractDiscovery(IKernelView kernel)
{
    private const int ResponseBudget = 24000;

    public const string Example = """
        // Plain C#: every installed contract is referenced; name the ones you use with usings.
        // System, collections, LINQ, tasks, IntoChat.OS and IntoChat.OS.Client are imported.
        using ITimer = IntoChat.OS.Time.Timers.ITimer;
        using IntoChat.OS.Time.Timers.Signals;

        await using var brain = await IntoChatOSClient.ConnectAsync(args);
        var timer = brain.Get<ITimer>(brain.Setting("TimerId") ?? "tea");
        await foreach (var tick in brain.On<TimerTick>(timer, brain.Stopping))
        {
            Console.WriteLine($"TimerTick {tick.TimerId} {tick.ObservedAt:O}");
        }
        // Top-level statements run once; loop over brain.On<T>() to keep reacting to signals. A behavior stays
        // loaded in its brain's session; signals that arrive while it reloads are buffered, not lost.
        // Console output is the log. Settings arrive through brain.Setting(name).
        """;

    public async Task<CSharpContractCatalogSnapshot> Read(IReadOnlyList<string> moduleIds, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(moduleIds);
        var types = (await kernel.Types(cancellationToken).ConfigureAwait(false)).Where(type => type.Kind != TypeKind.Connector).ToArray();
        var installed = types.Where(type => type.Kind == TypeKind.Neuron).GroupBy(type => type.Provider, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(module => module.Key, module => module.ToArray(), StringComparer.OrdinalIgnoreCase);
        var unknown = moduleIds.Where(id => !installed.ContainsKey(id)).ToArray();
        if (unknown.Length > 0)
        { throw new ArgumentException($"Unknown module IDs: {string.Join(", ", unknown)}. Installed module IDs: {string.Join(", ", installed.Keys.Order(StringComparer.Ordinal))}."); }
        var modules = installed.OrderBy(module => module.Key, StringComparer.Ordinal)
            .Select(module => new CSharpContractModule(module.Key)).ToArray();
        // Discovery with no modules selected lists the module ids only; select ids to read their contracts.
        var contracts = moduleIds.SelectMany(id => installed[id]
                .OrderBy(type => type.Name, StringComparer.Ordinal)
                .Select(type => $"{type.Name} [neuron id: {type.Alias}/<key>] — {type.Description} {{ {string.Join("; ", type.Shape)} }}")
                .Concat(types.Where(type => type.Kind == TypeKind.Signal && string.Equals(type.Provider, id, StringComparison.OrdinalIgnoreCase))
                    .OrderBy(signal => signal.Name, StringComparer.Ordinal)
                    .Select(signal => signal.Name + " { " + string.Join("; ", signal.Shape) + " }")))
            .Distinct(StringComparer.Ordinal)
            .ToList();
        var snapshot = new CSharpContractCatalogSnapshot(modules, contracts, Example);
        while (JsonSerializer.Serialize(snapshot).Length > ResponseBudget && contracts.Count > 0)
        {
            contracts.RemoveAt(contracts.Count - 1);
            snapshot = snapshot with { Contracts = [.. contracts], Truncated = true };
        }
        return snapshot;
    }
}
