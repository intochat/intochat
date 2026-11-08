using System.Reflection;
using System.Text;
using System.Text.Json;
using DigitalBrain;
using DigitalBrain.Spi;
using DigitalBrain.Testing.Synapses;
using IntoChat.OS.Modules.Apps.Trace;
using Microsoft.Extensions.DependencyInjection;

namespace IntoChat.OS.Modules.Apps.Debugger;

// The Creator's reads, run inside the brain. State and View forward to the unit's state neuron; Signals finds the journal's head
// and reads back a window of it; Topology is the loaded synapse catalog plus the compiled apps; RunScenario runs the kept
// scenarios through UnitScenario, the runner verify uses, on fakes in a throwaway TestOS.
[DigitalBrain.Neuron("creator.reads"), StatelessNeuron]
public sealed class DebuggerReadsNeuron : DigitalBrain.Neuron, IDebuggerReads
{
    // How far back from the head Signals looks for the alias.
    public const int Window = 5000;

    public async Task<string?> State(string alias)
    {
        if (!Loaded(Require(alias))) { return null; }
        var state = await Get<IStateReader>(StateNeuron(alias)).Read(alias);
        return state.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined ? null : state.GetRawText();
    }

    public async Task<string?> View(string alias)
    {
        if (!Loaded(Require(alias))) { return null; }
        var view = await Get<IViewReader>(StateNeuron(alias)).Read(alias);
        return view is null ? null : JsonSerializer.Serialize(view, JsonSerializerOptions.Web);
    }

    public async Task<TracePage> Signals(string alias, string? publisher, int limit)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(alias);
        var take = Math.Clamp(limit, 1, IDebuggerReads.MaxSignals);
        var history = Context.History;
        var head = await Head(history);
        var since = Math.Max(0, head - Window);
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
        if (Context.Services.GetService<ISynapseCatalogSource>()?.Current is { } catalog)
        {
            foreach (var alias in catalog.States.Keys.Order(StringComparer.Ordinal))
            {
                nodes.Add(new BrainTopologyNode(StateNeuron(alias).ToString(), alias, BrainGraph.Unit));
            }
            foreach (var synapse in catalog.Synapses.Where(s => s.Kind != SynapseKind.View))
            {
                var signal = synapse.SignalAlias ?? synapse.Signal?.GetCustomAttribute<SignalAttribute>()?.Alias ?? synapse.Signal?.Name ?? "";
                if (signal.Length == 0) { continue; }
                edges.Add(new BrainTopologyEdge("*", StateNeuron(synapse.StateAlias).ToString(), signal, synapse.Kind == SynapseKind.Gate ? "gate" : "reaction"));
            }
        }
        foreach (var assembly in Context.Services.GetServices<CompiledAppsRegistration>().Select(r => r.Assembly).Append(typeof(AppsModule).Assembly).Distinct())
        {
            foreach (var compiled in CompiledApps.Of(assembly))
            {
                var name = compiled.Bundle.Name;
                if (ShippedApps.Services.Contains(name)) { continue; }
                nodes.Add(new BrainTopologyNode(IApp.Key(brain, name), compiled.Bundle.Manifest.Title is { Length: > 0 } title ? title : name, BrainGraph.App));
            }
        }
        return Task.FromResult(new BrainTopology(nodes, [.. edges.Distinct()]));
    }

    public async Task<string> RunScenario(string alias, string? scenario)
    {
        Require(alias);
        var studio = Get<ISynapseStudio>(ISynapseStudio.Key(Id.Brain.Value));
        var code = UnitFiles.Code(await studio.SourceFiles(alias));
        var sidecars = await studio.Sidecars(alias);
        if (code.Count == 0) { return $"{alias} has no stored source."; }
        if (!sidecars.TryGetValue(UnitFiles.Scenarios, out var json)) { return $"{alias} keeps no {UnitFiles.Scenarios}."; }
        Scenario[] scenarios;
        try { scenarios = JsonSerializer.Deserialize<Scenario[]>(json, JsonSerializerOptions.Web) ?? []; }
        catch (JsonException error) { return $"{UnitFiles.Scenarios} does not read: {error.Message}"; }
        var chosen = scenarios.Where(s => string.IsNullOrWhiteSpace(scenario) || s.Name == scenario).ToArray();
        if (chosen.Length == 0) { return string.IsNullOrWhiteSpace(scenario) ? $"{alias} has no scenarios." : $"{alias} has no scenario '{scenario}'."; }
        var source = new SynapseSource(alias, code, null);
        var text = new StringBuilder();
        foreach (var each in chosen)
        {
            ScenarioSteps.Parsed parsed;
            try { parsed = ScenarioSteps.Parse(each); }
            catch (FormatException error) { text.Append($"✗ {each.Name}: {error.Message}\n"); continue; }
            var transcript = await UnitScenario.Run(source, parsed.Fakes, parsed.Steps, CancellationToken.None);
            text.Append($"{(transcript.Passed ? "✓" : "✗")} {each.Name}\n");
            foreach (var happened in transcript.Events)
            {
                var entry = TraceReaderNeuron.Entry(happened.Signal);
                text.Append($"  step {happened.Step}: {entry.Signal} by {entry.Publisher}\n");
            }
            foreach (var failure in transcript.Failures) { text.Append($"  failed: {failure}\n"); }
        }
        return text.ToString().TrimEnd();
    }

    // A host with a synapse catalog answers nothing for an alias no loaded unit declares, without activating a neuron for it.
    private bool Loaded(string alias) => Context.Services.GetService<ISynapseCatalogSource>() is not { } catalog || catalog.Current.States.ContainsKey(alias);

    // An alias is one path segment of a neuron id; anything else would address another neuron.
    private static string Require(string alias)
    {
        if (string.IsNullOrWhiteSpace(alias) || !alias.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_'))
        { throw new ArgumentException("Name a state alias.", nameof(alias)); }
        return alias;
    }

    private NeuronId StateNeuron(string alias) => new(Id.Brain, alias, "default");
}
