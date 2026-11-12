using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Channels;
using DigitalBrain.Kernel;
using DigitalBrain.Runtime.Orleans;
using DigitalBrain.Runtime.Orleans.Capabilities;
using DigitalBrain.Runtime.Orleans.Signalling;
using DigitalBrain.Signals.Watch;
using Google.Protobuf;
using Grpc.Core;
using IntoChat.OS.Authority.Neurons.Proto;
using IntoChat.OS.Connections.Surface;
using IntoChat.OS.Contracts.Configuration;
using IntoChat.OS.Framework;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using CallRequest = IntoChat.OS.Authority.Neurons.Proto.CallRequest;

namespace IntoChat.OS.Authority.Neurons;

// The gRPC edge of the operations every caller has: neurons_call, brain.On<T>, and neuron state inspection. The brain
// rides in the message, so each call decides access the way a /brains/{brainId} route does.
internal sealed class NeuronsService(NeuronSurface surface, IBrainObserver kernel, ILogger<NeuronsService> logger, IServiceProvider services)
    : Proto.Neurons.NeuronsBase
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) } };

    // A watcher that stops reading (a backgrounded tab) keeps this many envelopes; older ones are
    // dropped, which loses nothing because a change signal only tells the view to read again.
    internal const int BufferedEnvelopes = 256;

    // One screen watches its nodes; more than this in one call is a mistake, not a screen.
    internal const int MaximumWatches = 64;

    public override async Task<CallReply> Call(CallRequest request, ServerCallContext context)
    {
        RuntimeCallerContext.Stamp(await BrainCall.Enter(context, request.BrainId));
        try
        {
            var arguments = request.Arguments.IsEmpty ? "[]" : request.Arguments.ToStringUtf8();
            var result = await surface.Call(request.Contract, request.Key, request.Method, arguments, context.CancellationToken);
            return new CallReply { Result = result is { } json ? ByteString.CopyFromUtf8(json.GetRawText()) : ByteString.Empty };
        }
        catch (Exception error) when (error is not RpcException and not OperationCanceledException) { throw BrainCall.Translate(error, logger); }
    }

    public override async Task<NeuronStateReply> ReadState(NeuronStateRequest request, ServerCallContext context)
    {
        RuntimeCallerContext.Stamp(await BrainCall.Enter(context, request.BrainId));
        try
        {
            var id = await surface.PlainId(request.Contract, request.Key, context.CancellationToken);
            if (id is null)
            {
                return new NeuronStateReply { StateJson = ByteString.Empty };
            }

            System.Text.Json.JsonElement? state = null;
            if (kernel.GetType().GetMethod("State", [typeof(DigitalBrain.NeuronId), typeof(CancellationToken)]) is { } inspectState)
            {
                var stateTask = (Task<System.Text.Json.JsonElement?>)inspectState.Invoke(kernel, [id.Value, context.CancellationToken])!;
                state = await stateTask.ConfigureAwait(false);
            }
            return new NeuronStateReply
            {
                StateJson = state is { } json ? ByteString.CopyFromUtf8(json.GetRawText()) : ByteString.Empty
            };
        }
        catch (Exception error) when (error is not RpcException and not OperationCanceledException) { throw BrainCall.Translate(error, logger); }
    }

    // Retained last signal per source and type first, then live ones, until the client goes away.
    public override async Task Watch(WatchRequest request, IServerStreamWriter<SignalEnvelope> responseStream, ServerCallContext context)
    {
        RuntimeCallerContext.Stamp(await BrainCall.Enter(context, request.BrainId));
        if (request.Sources.Count == 0) { throw new RpcException(new Status(StatusCode.InvalidArgument, "Name at least one source.")); }
        if (request.Sources.Sum(source => Math.Max(1, source.SignalTypes.Count)) > MaximumWatches)
        { throw new RpcException(new Status(StatusCode.InvalidArgument, $"At most {MaximumWatches} source signals per watch.")); }
        var ct = context.CancellationToken;
        var channel = Channel.CreateBounded<SignalEnvelope>(new BoundedChannelOptions(BufferedEnvelopes) { FullMode = BoundedChannelFullMode.DropOldest });
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var pumps = new List<Task>();
        try
        {
            var types = await kernel.Types(ct);
            foreach (var source in request.Sources)
            {
                // One session on the session synapse neuron for the source's types.
                var id = await surface.PlainId(source.Contract, source.Key, ct)
                    ?? throw new RpcException(new Status(StatusCode.InvalidArgument, $"{source.Contract} is not a neuron contract this brain can watch."));
                var wanted = source.SignalTypes.Distinct(StringComparer.Ordinal).Select(name => SignalTypes.Find(name, types)).ToArray();
                pumps.Add(Task.Run(() => PlainPump(id, source.Key, wanted, channel.Writer, stop.Token), CancellationToken.None));
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

    // The retained last signal of each type first, then live ones. Only [Signal] records are
    // published by a plain neuron; a gap is sent as nothing, because a change signal only tells the view to read again.
    private async Task PlainPump(DigitalBrain.NeuronId id, string key, Type[] wanted, ChannelWriter<SignalEnvelope> writer, CancellationToken ct)
    {
        try
        {
            var journal = services.GetService<IBrainJournalWriter>()
                ?? throw new NotSupportedException("This host cannot watch plain neurons: it registers no brain journal.");
            var types = wanted.Where(type => typeof(DigitalBrain.Signal).IsAssignableFrom(type))
                .ToDictionary(SignalLog.Alias, StringComparer.Ordinal);
            if (types.Count != wanted.Length) { throw new RpcException(new Status(StatusCode.InvalidArgument, $"{key} is a plain neuron: watch its [Signal] types.")); }
            async Task Replay()
            {
                foreach (var (alias, type) in types)
                {
                    if (await SignalLog.Newest(journal, id, alias, ct) is { } retained) { await writer.WriteAsync(PlainEnvelope(key, id, type, retained), ct); }
                }
            }
            await foreach (var entry in SignalLog.Live(services, id, [.. types.Keys], "watch/" + Guid.NewGuid().ToString("N"), Replay, ct))
            {
                if (!SignalLog.IsGap(entry) && types.TryGetValue(entry.Alias, out var type)) { await writer.WriteAsync(PlainEnvelope(key, id, type, entry), ct); }
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
    private static SignalEnvelope PlainEnvelope(string key, DigitalBrain.NeuronId id, Type type, SessionSignal entry) => new()
    {
        Source = key,
        Type = type.Name,
        Publisher = id.ToString(),
        Sequence = entry.Sequence,
        Payload = ByteString.CopyFromUtf8(JsonSerializer.Serialize(JsonSerializer.Deserialize(entry.Payload.GetRawText(), type, JsonSerializerOptions.Web), type, Json)),
        ServerTimeMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
    };
}
