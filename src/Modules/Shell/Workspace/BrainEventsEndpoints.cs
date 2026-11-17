using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading.Channels;
using DigitalBrain;
using DigitalBrain.Brains;
using DigitalBrain.Log;
using DigitalBrain.Runtime.Orleans.Signalling;
using DigitalBrain.Signals;
using DigitalBrain.Units;
using IntoChat.OS.Connections;
using IntoChat.OS.Framework;
using IntoChat.OS.Framework.Logging;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace IntoChat.OS.Modules.Flutter.Workspace;

// GET /brains/{brainId}/brain/events: the brain's topology as `brain-snapshot` server-sent events. The first
// snapshot lists every neuron the kernel knows, the state neurons of the installed units, and the edges of those
// units' reactions and gates; another follows each time a neuron is created or retired or a unit is installed or
// retired. It carries no activity: what crossed an edge, and when, is the journal tail's (GET .../journal/tail).
internal static class BrainEventsEndpoints
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static void Map(IEndpointRouteBuilder endpoints)
        => OsBrainRoutes.Group(endpoints).MapGet("/brain/events", Events);

    private static async Task Events(HttpContext http, CancellationToken ct)
    {
        var brain = BrainEdge.Open(http.RequestServices, BrainScope.CurrentId());
        http.Response.ContentType = "text/event-stream";
        http.Response.Headers.CacheControl = "no-cache";
        await http.Response.Body.FlushAsync(ct);
        await foreach (var snapshot in Snapshots(brain, BrainScope.CurrentId(), http.RequestServices.GetService<ISynapseCatalogSource>(), ct))
        {
            await http.Response.WriteAsync("event: brain-snapshot\ndata: " + snapshot + "\n\n", ct);
            await http.Response.Body.FlushAsync(ct);
        }
    }

    private static readonly TimeSpan Burst = TimeSpan.FromMilliseconds(100);

    // The first snapshot is written at once. Watches on the kernel's instance announcements follow; once both are
    // registered the brain is read again, so nothing that happened in between is missed, and a snapshot identical to
    // the last one is not sent. A burst of changes becomes one snapshot. A faulted watch ends the stream with its
    // error, so the client sees a failure, never a clean end.
    internal static async IAsyncEnumerable<string> Snapshots(BrainEdge brain, string brainId, ISynapseCatalogSource? catalog, [EnumeratorCancellation] CancellationToken ct)
    {
        // A change flag: one pending change says everything a hundred would.
        var changes = Channel.CreateBounded<bool>(new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropOldest });
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var waiting = 4;
        var installs = brain.Get<IInstalls>(Installs(brainId));
        void Subscribed() { if (Interlocked.Decrement(ref waiting) == 0) { changes.Writer.TryWrite(true); } }
        _ = Watch(brain.On<InstanceCreated>(brain.Self(brainId), () => { Subscribed(); return Task.CompletedTask; }, stop.Token));
        _ = Watch(brain.On<InstanceRetired>(brain.Self(brainId), () => { Subscribed(); return Task.CompletedTask; }, stop.Token));
        _ = Watch(brain.On<Installed>(installs, () => { Subscribed(); return Task.CompletedTask; }, stop.Token));
        _ = Watch(brain.On<DigitalBrain.Units.Retired>(installs, () => { Subscribed(); return Task.CompletedTask; }, stop.Token));
        try
        {
            var (json, key) = await Snapshot(brain, brainId, catalog, installs, ct);
            var last = key;
            yield return json;
            await foreach (var _tick in changes.Reader.ReadAllAsync(ct))
            {
                await Task.Delay(Burst, ct);
                while (changes.Reader.TryRead(out var more)) { _ = more; }
                (json, key) = await Snapshot(brain, brainId, catalog, installs, ct);
                if (key == last) { continue; }
                last = key;
                yield return json;
            }
        }
        finally { await stop.CancelAsync(); }

        async Task Watch<T>(IAsyncEnumerable<T> signals)
        {
            try { await foreach (var _ in signals) { changes.Writer.TryWrite(true); } }
            catch (OperationCanceledException) { }
            catch (Exception error)
            {
                OsConsoleLog.For(typeof(BrainEventsEndpoints)).LogSwallowed(error, "watching brain events stream");
                changes.Writer.TryComplete(error);
            }
        }
    }

    private static NeuronId Installs(string brainId) => new(new BrainId(brainId), "synapse.installs", "default");

    private static async Task<(string Json, string Key)> Snapshot(BrainEdge brain, string brainId, ISynapseCatalogSource? catalog, IInstalls installs, CancellationToken ct)
    {
        List<(string Key, string Type)> instances = [];
        await foreach (var instance in brain.Kernel.Instances(null, ct)) { instances.Add((instance.Key, instance.Type)); }
        var aliases = (await installs.List(ct)).Select(record => record.Alias);
        return Topology(brainId, instances, aliases, catalog?.Static, DateTimeOffset.UtcNow);
    }

    // The wire snapshot: a node per kernel neuron, one more per installed unit's state neuron (`{brain}/{alias}/default`) the
    // kernel does not list, and an edge per reaction or gate of an installed unit. A unit hears by journal alias, from any
    // publisher of the brain, so an edge's source is "*" and its signalType is the alias; the journal tail names who published.
    // The key is the snapshot without its time, so an unchanged brain is not sent twice.
    internal static (string Json, string Key) Topology(string brainId, IEnumerable<(string Key, string Type)> instances,
        IEnumerable<string> installedAliases, SynapseCatalog? catalog, DateTimeOffset observedAt)
    {
        List<object> nodes = [];
        HashSet<string> known = [];
        foreach (var (instanceKey, type) in instances)
        {
            if (!known.Add(instanceKey)) { continue; }
            var name = instanceKey[(instanceKey.LastIndexOf('/') + 1)..];
            nodes.Add(new { id = instanceKey, type, name, label = name, module = "", role = "observed", status = "Idle", handledSignals = Array.Empty<string>() });
        }
        List<object> synapses = [];
        foreach (var alias in installedAliases.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal))
        {
            var descriptors = catalog is not null && catalog.States.ContainsKey(alias) ? catalog.For(alias).Where(d => d.Kind != SynapseKind.View).ToArray() : [];
            var id = (IntoChat.OS.Primitives.UnitAlias.TryParse(alias, out var unitAlias)
                ? unitAlias.StateNeuron(new BrainId(brainId))
                : new NeuronId(new BrainId(brainId), alias, "default")).ToString();
            var handled = descriptors.Select(SignalOf).Distinct(StringComparer.Ordinal).ToArray();
            if (known.Add(id))
            {
                nodes.Add(new { id, type = alias, name = alias, label = alias, module = "", role = "unit", status = "Idle", handledSignals = handled });
            }
            foreach (var d in descriptors)
            {
                var kind = "reaction";
                var signal = SignalOf(d);
                synapses.Add(new { id = $"{signal}>{id}#{kind}:{d.Function.Name}", sourceId = "*", targetId = id, signalType = signal, kind, isBlocking = false });
            }
        }
        var key = JsonSerializer.Serialize(new { nodes, synapses }, Json);
        var json = JsonSerializer.Serialize(new
        {
            rootId = brainId,
            observedAt,
            scope = "brain",
            truncated = false,
            nodes,
            synapses,
        }, Json);
        return (json, key);
    }

    private static string SignalOf(SynapseDescriptor d) => d.SignalAlias ?? SignalLog.Alias(d.Signal!);
}
