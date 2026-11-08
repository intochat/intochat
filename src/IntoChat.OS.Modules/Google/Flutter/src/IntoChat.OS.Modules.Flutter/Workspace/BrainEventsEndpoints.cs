using DigitalBrain.Brains;
using DigitalBrain.Signals;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading.Channels;
using IntoChat.OS.Connections;
using IntoChat.OS.Framework;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace IntoChat.OS.Modules.Flutter.Workspace;

// GET /brains/{brainId}/brain/events: the whole brain as `brain-snapshot` server-sent events. The first
// snapshot lists every neuron the kernel knows; another follows each time one is created or retired.
internal static class BrainEventsEndpoints
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static void Map(IEndpointRouteBuilder endpoints)
        => BrainRoutes.Group(endpoints).MapGet("/brain/events", Events);

    private static async Task Events(HttpContext http, CancellationToken ct)
    {
        var brain = BrainEdge.Open(http.RequestServices, BrainScope.CurrentId());
        http.Response.ContentType = "text/event-stream";
        http.Response.Headers.CacheControl = "no-cache";
        await http.Response.Body.FlushAsync(ct);
        await foreach (var snapshot in Snapshots(brain, BrainScope.CurrentId(), ct))
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
    // Activity and synapse data return with the journal tail (Task 21).
    internal static async IAsyncEnumerable<string> Snapshots(BrainEdge brain, string brainId, [EnumeratorCancellation] CancellationToken ct)
    {
        // A change flag: one pending change says everything a hundred would.
        var changes = Channel.CreateBounded<bool>(new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropOldest });
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var waiting = 2;
        void Subscribed() { if (Interlocked.Decrement(ref waiting) == 0) { changes.Writer.TryWrite(true); } }
        _ = Watch(brain.On<InstanceCreated>(brain.Self(brainId), () => { Subscribed(); return Task.CompletedTask; }, stop.Token));
        _ = Watch(brain.On<InstanceRetired>(brain.Self(brainId), () => { Subscribed(); return Task.CompletedTask; }, stop.Token));
        try
        {
            var (json, key) = await Snapshot(brain, brainId, ct);
            var last = key;
            yield return json;
            await foreach (var _tick in changes.Reader.ReadAllAsync(ct))
            {
                await Task.Delay(Burst, ct);
                while (changes.Reader.TryRead(out var more)) { _ = more; }
                (json, key) = await Snapshot(brain, brainId, ct);
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
            catch (Exception error) { changes.Writer.TryComplete(error); }
        }
    }

    // Every neuron the kernel knows, Idle: runtime-authored code is a synapse unit, which this graph does not draw yet.
    private static async Task<(string Json, string Key)> Snapshot(BrainEdge brain, string brainId, CancellationToken ct)
    {
        List<object> nodes = [];
        await foreach (var instance in brain.Kernel.Instances(null, ct))
        {
            var name = instance.Key[(instance.Key.LastIndexOf('/') + 1)..];
            nodes.Add(new { id = instance.Key, type = instance.Type, name, label = name, module = "", role = "observed", status = "Idle", handledSignals = Array.Empty<string>() });
        }
        var synapses = Array.Empty<object>();
        var identity = JsonSerializer.Serialize(new { nodes, synapses }, Json);
        var json = JsonSerializer.Serialize(new
        {
            rootId = brainId,
            observedAt = DateTimeOffset.UtcNow,
            scope = "brain",
            truncated = false,
            nodes,
            synapses,
            activity = Array.Empty<object>(),
            correlations = Array.Empty<object>(),
        }, Json);
        return (json, identity);
    }
}
