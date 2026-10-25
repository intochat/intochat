using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Channels;
using IntoChat.OS.Contracts.Configuration;
using IntoChat.OS.Framework;
using IntoChat.OS.Connections.Surface;
using IntoChat.OS.Authority.Neurons.Proto;
using Google.Protobuf;
using Grpc.Core;
using Microsoft.Extensions.Logging;
using CallRequest = IntoChat.OS.Authority.Neurons.Proto.CallRequest;

namespace IntoChat.OS.Authority.Neurons;

// The gRPC edge of the two operations every caller has: neurons_call and brain.On<T>. The brain
// rides in the message, so each call decides access the way a /brains/{brainId} route does.
internal sealed class NeuronsService(NeuronSurface surface, IIntoChatOS brain, ILogger<NeuronsService> logger,
    IntoChat.OS.Runtime.IPlainSignals? plain = null) : Proto.Neurons.NeuronsBase
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) } };

    // A watcher that stops reading (a backgrounded tab) keeps this many envelopes; older ones are
    // dropped, which loses nothing because a change signal only tells the view to read again.
    internal const int BufferedEnvelopes = 256;

    // One screen watches its nodes; more than this in one call is a mistake, not a screen.
    internal const int MaximumWatches = 64;

    public override async Task<CallReply> Call(CallRequest request, ServerCallContext context)
    {
        CallerContextStamper.Stamp(await BrainCall.Enter(context, request.BrainId));
        try
        {
            var arguments = request.Arguments.IsEmpty ? "[]" : request.Arguments.ToStringUtf8();
            var result = await surface.Call(request.Contract, request.Key, request.Method, arguments, context.CancellationToken);
            return new CallReply { Result = result is { } json ? ByteString.CopyFromUtf8(json.GetRawText()) : ByteString.Empty };
        }
        catch (Exception error) when (error is not RpcException and not OperationCanceledException) { throw BrainCall.Translate(error, logger); }
    }

    // Retained last signal per source and type first, then live ones, until the client goes away.
    public override async Task Watch(WatchRequest request, IServerStreamWriter<SignalEnvelope> responseStream, ServerCallContext context)
    {
        CallerContextStamper.Stamp(await BrainCall.Enter(context, request.BrainId));
        if (request.Sources.Count == 0) { throw new RpcException(new Status(StatusCode.InvalidArgument, "Name at least one source.")); }
        if (request.Sources.Sum(source => Math.Max(1, source.SignalTypes.Count)) > MaximumWatches)
        { throw new RpcException(new Status(StatusCode.InvalidArgument, $"At most {MaximumWatches} source signals per watch.")); }
        var ct = context.CancellationToken;
        var channel = Channel.CreateBounded<SignalEnvelope>(new BoundedChannelOptions(BufferedEnvelopes) { FullMode = BoundedChannelFullMode.DropOldest });
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var pumps = new List<Task>();
        try
        {
            var types = await brain.Kernel.Types(ct);
            foreach (var source in request.Sources)
            {
                // A plain neuron (06-07b): one session on the session synapse neuron for the source's types.
                if (await surface.PlainId(source.Contract, source.Key, ct) is { } id)
                {
                    var wanted = source.SignalTypes.Distinct(StringComparer.Ordinal).Select(name => SignalTypes.Find(name, types)).ToArray();
                    pumps.Add(Task.Run(() => PlainPump(id, source.Key, wanted, channel.Writer, stop.Token), CancellationToken.None));
                    continue;
                }
                var neuron = await surface.Reference(source.Contract, source.Key, ct);
                foreach (var name in source.SignalTypes.Distinct(StringComparer.Ordinal))
                {
                    var type = SignalTypes.Find(name, types);
                    pumps.Add(Task.Run(() => Pump(neuron, source.Key, type, channel.Writer, stop.Token), CancellationToken.None));
                }
            }
        }
        catch (Exception error) when (error is not RpcException and not OperationCanceledException)
        {
            await stop.CancelAsync();
            throw BrainCall.Translate(error, logger);
        }
        _ = Task.WhenAll(pumps).ContinueWith(_ => channel.Writer.TryComplete(), CancellationToken.None);
        try
        {
            await foreach (var envelope in channel.Reader.ReadAllAsync(ct)) { await responseStream.WriteAsync(envelope, ct); }
        }
        catch (Exception error) when (error is not RpcException and not OperationCanceledException) { throw BrainCall.Translate(error, logger); }
        finally
        {
            await stop.CancelAsync();
            // Pumps end on the cancel; waiting keeps a late failure observed instead of lost.
            try { await Task.WhenAll(pumps); } catch (Exception error) when (error is not OperationCanceledException) { logger.LogDebug(error, "A watch pump ended with an error after the stream closed"); }
        }
    }

    private async Task Pump(INeuron neuron, string key, Type type, ChannelWriter<SignalEnvelope> writer, CancellationToken ct)
    {
        try
        {
            // Retention is read once the watch is registered, so nothing published in between is missed.
            await foreach (var signal in SignalWatch.Open(brain, neuron, type, () => Replay(neuron, key, type.Name, writer, ct), ct))
            { await writer.WriteAsync(Envelope(key, signal, 0), ct); }
        }
        catch (OperationCanceledException) { }
        catch (Exception error) { writer.TryComplete(error); }
    }

    // The retained last signal of each type first, then live ones, as the legacy pump sends them. Only [Signal] records are
    // published by a plain neuron; a gap is sent as nothing, because a change signal only tells the view to read again.
    private async Task PlainPump(global::DigitalBrain.NeuronId id, string key, Type[] wanted, ChannelWriter<SignalEnvelope> writer, CancellationToken ct)
    {
        try
        {
            var signals = plain ?? throw new NotSupportedException("This host cannot watch plain neurons: it registers no IPlainSignals (the silo's kernel composition does).");
            var types = wanted.Where(type => typeof(global::DigitalBrain.Signal).IsAssignableFrom(type))
                .ToDictionary(IntoChat.OS.Runtime.PlainSignalTypes.AliasOf, StringComparer.Ordinal);
            if (types.Count != wanted.Length) { throw new RpcException(new Status(StatusCode.InvalidArgument, $"{key} is a plain neuron: watch its [Signal] types.")); }
            async Task Replay()
            {
                foreach (var (alias, type) in types)
                {
                    if (await IntoChat.OS.Runtime.PlainWatch.Retained(signals, id, alias, ct) is { } retained) { await writer.WriteAsync(PlainEnvelope(key, id, type, retained), ct); }
                }
            }
            await foreach (var entry in IntoChat.OS.Runtime.PlainWatch.Live(signals, id, [.. types.Keys], "watch/" + Guid.NewGuid().ToString("N"), Replay, ct))
            {
                if (!entry.IsGap && types.TryGetValue(entry.Alias, out var type)) { await writer.WriteAsync(PlainEnvelope(key, id, type, entry), ct); }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception error)
        {
            logger.LogDebug(error, "A plain watch of {Source} ended with an error", key);
            writer.TryComplete(error);
        }
    }

    // The payload re-encoded with this edge's options, so a plain signal reads as its grain predecessor did.
    private static SignalEnvelope PlainEnvelope(string key, global::DigitalBrain.NeuronId id, Type type, IntoChat.OS.Runtime.PlainSignalEntry entry) => new()
    {
        Source = key,
        Type = type.Name,
        Publisher = id.ToString(),
        Sequence = entry.Sequence,
        Payload = ByteString.CopyFromUtf8(JsonSerializer.Serialize(JsonSerializer.Deserialize(entry.Json, type, JsonSerializerOptions.Web), type, Json)),
        ServerTimeMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
    };

    // A source that retains nothing is quiet; a refused read is the watcher's to hear.
    private static async Task Replay(INeuron neuron, string key, string type, ChannelWriter<SignalEnvelope> writer, CancellationToken ct)
    {
        RetainedSignal[] retained;
        try { retained = await neuron.AsReference<IRetainsSignals>().Retained(type, 0).WaitAsync(ct); }
        catch (UnauthorizedAccessException error) { writer.TryComplete(error); return; }
        catch (Exception error) when (error is not OperationCanceledException) { return; }
        if (retained.LastOrDefault() is { } last) { await writer.WriteAsync(Envelope(key, last.Signal, last.Sequence), ct); }
    }

    private static SignalEnvelope Envelope(string key, Signal signal, long sequence) => new()
    {
        Source = key,
        Type = signal.GetType().Name,
        Publisher = signal.Publisher.ToString(),
        Sequence = sequence,
        Payload = ByteString.CopyFromUtf8(JsonSerializer.Serialize(signal, signal.GetType(), Json)),
        ServerTimeMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
    };
}
