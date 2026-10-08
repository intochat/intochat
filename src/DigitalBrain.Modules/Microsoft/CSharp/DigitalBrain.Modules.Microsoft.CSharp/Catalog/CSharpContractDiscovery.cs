using System.Text.Json;
using DigitalBrain;
using DigitalBrain.Framework;
using DigitalBrain.Framework.Contracts;
using DigitalBrain.Framework.Modules;

namespace DigitalBrain.Microsoft.CSharp.Catalog;

public sealed record CSharpContractModule(string Id);

public sealed record CSharpContractCatalogSnapshot(IReadOnlyList<CSharpContractModule> Modules, IReadOnlyList<string> Contracts, string Example, bool Truncated = false);

// What a script author (human or agent) sees: the shared neuron catalog by module.
public sealed class CSharpContractDiscovery(ContractCatalog catalog)
{
    private const int ResponseBudget = 24000;

    public const string Example = """
        // Plain C#: every installed contract is referenced; name the ones you use with usings.
        // System, collections, LINQ, tasks, DigitalBrain and DigitalBrain.Client are imported.
        using ITimer = DigitalBrain.Time.Timers.ITimer;
        using DigitalBrain.Time.Timers.Signals;

        await using var brain = await DigitalBrainClient.ConnectAsync(args);
        var timer = brain.Get<ITimer>(brain.Setting("TimerId") ?? "tea");
        await foreach (var tick in brain.On<TimerTick>(timer, brain.Stopping))
        {
            Console.WriteLine($"TimerTick {tick.TimerId} {tick.ObservedAt:O}");
        }
        // Top-level statements run once; loop over brain.On<T>() to keep reacting to signals. A behavior stays
        // loaded in its brain's session; signals that arrive while it reloads are buffered, not lost.
        // Console output is the log. Settings arrive through brain.Setting(name).
        """;

    public Task<CSharpContractCatalogSnapshot> Read(IReadOnlyList<string> moduleIds, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(moduleIds);
        cancellationToken.ThrowIfCancellationRequested();
        var types = catalog.Read();
        var installed = types.GroupBy(type => type.ModuleId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(module => module.Key, module => module.ToArray(), StringComparer.OrdinalIgnoreCase);
        var unknown = moduleIds.Where(id => !installed.ContainsKey(id)).ToArray();
        if (unknown.Length > 0)
        { throw new ArgumentException($"Unknown module IDs: {string.Join(", ", unknown)}. Installed module IDs: {string.Join(", ", installed.Keys.Order(StringComparer.Ordinal))}."); }
        var modules = installed.OrderBy(module => module.Key, StringComparer.Ordinal)
            .Select(module => new CSharpContractModule(module.Key)).ToArray();
        // Discovery with no modules selected lists the module ids only; select ids to read their contracts.
        var contracts = moduleIds.SelectMany(id => installed[id]
                .OrderBy(type => type.Contract, StringComparer.Ordinal)
                .Select(type => $"{type.Contract} [neuron id: {type.Id}/<key>] — {type.Description} {{ {string.Join("; ", type.Methods)} }}")
                .Concat(catalog.Signals.Where(signal => string.Equals(signal.ModuleId, id, StringComparison.OrdinalIgnoreCase)).Select(signal => signal.Shape)))
            .Distinct(StringComparer.Ordinal)
            .ToList();
        var snapshot = new CSharpContractCatalogSnapshot(modules, contracts, Example);
        while (JsonSerializer.Serialize(snapshot).Length > ResponseBudget && contracts.Count > 0)
        {
            contracts.RemoveAt(contracts.Count - 1);
            snapshot = snapshot with { Contracts = [.. contracts], Truncated = true };
        }
        return Task.FromResult(snapshot);
    }
}
