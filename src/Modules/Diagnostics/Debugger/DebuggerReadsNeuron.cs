using System.Reflection;
using System.Text.Json;
using DigitalBrain;
using DigitalBrain.Kernel;
using DigitalBrain.Neurons;
using DigitalBrain.Units;
using DigitalBrain.Log;
using IntoChat.OS.Modules.Apps.Trace;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using IntoChat.OS.Host.Logging;

namespace IntoChat.OS.Modules.Apps.Debugger;

// The Creator's reads, run inside the brain. State and View forward to the unit's state neuron; Signals finds the journal's head
// and reads back a window of it; Topology is the loaded synapse catalog plus the compiled apps.
[Neuron("creator.reads"), StatelessNeuron]
internal sealed class DebuggerReadsNeuron(
    ISignalHistory history,
    ISynapseCatalogSource? catalogSource = null,
    ICompiledApps? compiledApps = null,
    UnitResolver? unitResolver = null,
    ILogger<DebuggerReadsNeuron>? logger = null) : Neuron, IDebuggerReads
{
    private readonly ISignalHistory _history = history ?? throw new ArgumentNullException(nameof(history));
    private readonly ISynapseCatalogSource? _catalogSource = catalogSource;
    private readonly ICompiledApps? _compiledApps = compiledApps;
    private readonly UnitResolver? _unitResolver = unitResolver;
    private readonly ILogger _logger = (ILogger?)logger ?? OsConsoleLog.For<DebuggerReadsNeuron>();

    public async Task<string?> State(string alias)
    {
        if (!await IsLoaded(Require(alias))) { return null; }
        var state = await Get<IStateReader>(StateNeuron(alias)).Read(alias);
        return state.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined ? null : state.GetRawText();
    }

    public async Task<string?> View(string alias)
    {
        if (!await IsLoaded(Require(alias))) { return null; }
        var view = await Get<IViewReader>(StateNeuron(alias)).Read(alias);
        return view is null ? null : JsonSerializer.Serialize(view, JsonSerializerOptions.Web);
    }

    public async Task<TracePage> Signals(string alias, string? publisher, int limit)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(alias);
        var take = Math.Clamp(limit, 1, IDebuggerReads.MaxSignals);
        if (!await Consumed(alias)) { return new TracePage([]); }
        var head = await Head(_history);
        var since = Math.Max(0, head - IDebuggerReads.Window);
        var kept = new Queue<TraceEntry>();
        while (since < head)
        {
            var page = await history.Read(since, SignalPage.MaxLimit, CancellationToken.None);
            if (page.Signals.Count == 0) { break; }
            foreach (var signal in page.Signals)
            {
                var entry = TraceReaderNeuron.Entry(signal);
                if (entry.Signal != alias || publisher is { } from && entry.Publisher != from) { continue; }
                kept.Enqueue(entry);
                if (kept.Count > take) { kept.Dequeue(); }
            }
            since = page.Signals[^1].Sequence;
        }
        return new TracePage([.. kept]);
    }

    public async Task<TracePage> Failures(string alias, int limit)
    {
        var take = Math.Clamp(limit, 1, IDebuggerReads.MaxSignals);
        var failed = (await Own(alias)).Where(entry => entry.Signal == "synapse.failed").TakeLast(take).ToArray();
        return new TracePage(failed);
    }

    public async Task<TraceEntry?> Failing(string alias)
        => (await Own(alias)) is [.., { Signal: "synapse.failed" } newest] ? newest : null;

    private async Task<List<TraceEntry>> Own(string alias)
    {
        if (!await IsLoaded(Require(alias))) { return []; }
        var publisher = StateNeuron(alias);
        var history = _history;
        var head = await Head(history);
        var since = Math.Max(0, head - IDebuggerReads.Window);
        List<TraceEntry> entries = [];
        while (since < head)
        {
            var page = await history.Read(since, SignalPage.MaxLimit, CancellationToken.None);
            if (page.Signals.Count == 0) { break; }
            foreach (var signal in page.Signals)
            {
                if (signal.Publisher == publisher) { entries.Add(TraceReaderNeuron.Entry(signal)); }
            }
            since = page.Signals[^1].Sequence;
        }
        return entries;
    }

    public async Task<bool> Loaded(string alias) => await IsLoaded(Require(alias));

    public Task<long> Head() => Head(_history);

    // The highest journaled sequence: doubles until a read after it is empty, then halves back.
    internal static async Task<long> Head(ISignalHistory history)
    {
        ArgumentNullException.ThrowIfNull(history);
        long known = 0, beyond = 1;
        while ((await history.Read(beyond, 1, CancellationToken.None)).Signals.Count > 0 && beyond < long.MaxValue / 4)
        {
            known = beyond;
            beyond *= 2;
        }
        // A read after `known` holds something (or known is 0); a read after `beyond` holds nothing.
        long low = known, high = beyond;
        while (low < high)
        {
            var mid = low + (high - low) / 2;
            if ((await history.Read(mid, 1, CancellationToken.None)).Signals.Count > 0) { low = mid + 1; }
            else { high = mid; }
        }
        return low;
    }

    public Task<BrainTopology> Topology()
    {
        var brain = Id.Brain.Value;
        List<BrainTopologyNode> nodes = [];
        List<BrainTopologyEdge> edges = [];
        if (_catalogSource?.Static is { } catalog)
        {
            foreach (var alias in catalog.States.Keys.Order(StringComparer.Ordinal))
            {
                nodes.Add(new BrainTopologyNode(StateNeuron(alias).ToString(), alias, BrainTopologyNode.UnitRole));
            }
            foreach (var synapse in catalog.Synapses.Where(s => s.Kind != SynapseKind.View))
            {
                var signal = synapse.SignalAlias ?? synapse.Signal?.GetCustomAttribute<SignalAttribute>()?.Alias ?? synapse.Signal?.Name ?? "";
                if (signal.Length == 0) { continue; }
                edges.Add(new BrainTopologyEdge("*", StateNeuron(synapse.StateAlias).ToString(), signal, "reaction"));
            }
        }
        foreach (var compiled in _compiledApps?.List() ?? [])
        {
            var name = compiled.Name;
            if (ShippedApps.Services.Contains(name)) { continue; }
            nodes.Add(new BrainTopologyNode(IntoChat.OS.Host.Primitives.AppRef.Of(new DigitalBrain.BrainId(brain), name).Key, compiled.Title is { Length: > 0 } title ? title : name, BrainTopologyNode.AppRole));
        }
        return Task.FromResult(new BrainTopology(nodes, [.. edges.Distinct()]));
    }

    // A host with a synapse catalog answers nothing for an alias no loaded unit declares, without activating a neuron for it.
    private async Task<bool> IsLoaded(string alias)
    {
        if (_catalogSource is not { } catalog || catalog.Static.States.ContainsKey(alias))
        {
            return true;
        }

        if (_unitResolver is { } resolver)
        {
            try
            {
                var catalogs = await resolver.Catalogs(Id.Brain);
                if (catalogs.Any(c => c.States.ContainsKey(alias)))
                {
                    return true;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException and not OutOfMemoryException)
            {
                _logger.LogSwallowed(ex, "Failed to inspect unit resolver catalogs for alias.");
            }
        }

        try
        {
            var installs = Context.Get<IInstalls>(new NeuronId(Id.Brain, "synapse.installs", "default"));
            if (await installs.Describe(alias, CancellationToken.None) is not null)
            {
                return true;
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not OutOfMemoryException)
        {
            _logger.LogSwallowed(ex, "Failed to describe install for alias.");
        }

        return false;
    }

    // Whether a loaded synapse reacts to or gates on the signal alias (true without a catalog).
    private async Task<bool> Consumed(string signal)
    {
        if (_catalogSource is not { } catalog)
        {
            return true;
        }

        if (catalog.Static.Synapses.Any(s => s.Kind != SynapseKind.View && (s.SignalAlias ?? s.Signal?.GetCustomAttribute<SignalAttribute>()?.Alias ?? s.Signal?.Name) == signal))
        {
            return true;
        }

        if (_unitResolver is { } resolver)
        {
            try
            {
                var catalogs = await resolver.Catalogs(Id.Brain);
                foreach (var c in catalogs)
                {
                    if (c.Synapses.Any(s => s.Kind != SynapseKind.View && (s.SignalAlias ?? s.Signal?.GetCustomAttribute<SignalAttribute>()?.Alias ?? s.Signal?.Name) == signal))
                    {
                        return true;
                    }
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException and not OutOfMemoryException)
            {
                _logger.LogSwallowed(ex, "Failed to inspect resolver catalogs for consumed signal.");
            }
        }

        if (signal == "time.tick")
        {
            try
            {
                var installs = Context.Get<IInstalls>(new NeuronId(Id.Brain, "synapse.installs", "default"));
                var list = await installs.List(CancellationToken.None);
                if (list.Any(r => r.Schedules.Count > 0))
                {
                    return true;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException and not OutOfMemoryException)
            {
                _logger.LogSwallowed(ex, "Failed to list schedules for consumed time.tick signal.");
            }
        }

        return false;
    }

    // An alias is one path segment of a neuron id; anything else would address another neuron.
    private static string Require(string alias)
    {
        if (string.IsNullOrWhiteSpace(alias) || !alias.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_'))
        { throw new ArgumentException("Name a state alias.", nameof(alias)); }
        return alias;
    }

    private NeuronId StateNeuron(string alias) => new(Id.Brain, alias, "default");
}
