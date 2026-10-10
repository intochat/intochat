using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading.Channels;
using DigitalBrain.Framework;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace DigitalBrain.Flutter.Workspace;

// GET /brains/{brainId}/brain/events: the whole brain as `brain-snapshot` server-sent events. The first
// snapshot lists every neuron the kernel knows; another follows each time one is created or retired.
internal static class BrainEventsEndpoints
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static void Map(IEndpointRouteBuilder endpoints)
        => BrainRoutes.Group(endpoints).MapGet("/brain/events", Events);

    private static async Task Events(HttpContext http, IDigitalBrain brain, CancellationToken ct)
    {
        http.Response.ContentType = "text/event-stream";
        http.Response.Headers.CacheControl = "no-cache";
        await http.Response.Body.FlushAsync(ct);
        await foreach (var snapshot in Snapshots(brain, BrainScope.CurrentId(), ct))
        {
            await http.Response.WriteAsync("event: brain-snapshot\ndata: " + snapshot + "\n\n", ct);
            await http.Response.Body.FlushAsync(ct);
        }
    }

    // The watches are registered before the first listing, so a neuron created in between is not missed.
    internal static async IAsyncEnumerable<string> Snapshots(IDigitalBrain brain, string brainId, [EnumeratorCancellation] CancellationToken ct)
    {
        var changes = Channel.CreateUnbounded<bool>();
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var subscribed = 0;
        void Subscribed() { if (Interlocked.Increment(ref subscribed) == 2) { changes.Writer.TryWrite(true); } }
        _ = Watch(brain.On<InstanceCreated>(brain.Self, () => { Subscribed(); return Task.CompletedTask; }, stop.Token));
        _ = Watch(brain.On<InstanceRetired>(brain.Self, () => { Subscribed(); return Task.CompletedTask; }, stop.Token));
        try
        {
            await foreach (var _ in changes.Reader.ReadAllAsync(ct)) { yield return await Snapshot(brain, brainId, ct); }
        }
        finally { await stop.CancelAsync(); }

        async Task Watch<T>(IAsyncEnumerable<T> signals)
        {
            try { await foreach (var _ in signals) { changes.Writer.TryWrite(true); } }
            catch (OperationCanceledException) { }
            catch (Exception error) { changes.Writer.TryComplete(error); }
        }
    }

    private static async Task<string> Snapshot(IDigitalBrain brain, string brainId, CancellationToken ct)
    {
        List<object> nodes = [];
        await foreach (var instance in brain.Kernel.Instances(null, ct))
        {
            var name = instance.Key[(instance.Key.LastIndexOf('/') + 1)..];
            nodes.Add(new { id = instance.Key, type = instance.Type, name, label = name, module = "", role = "observed", status = "Idle", handledSignals = Array.Empty<string>() });
        }
        return JsonSerializer.Serialize(new
        {
            rootId = brainId,
            observedAt = DateTimeOffset.UtcNow,
            scope = "brain",
            truncated = false,
            nodes,
            synapses = Array.Empty<object>(),
            activity = Array.Empty<object>(),
            correlations = Array.Empty<object>(),
        }, Json);
    }
}
