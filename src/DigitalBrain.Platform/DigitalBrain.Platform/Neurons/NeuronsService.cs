using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Channels;
using DigitalBrain.Framework.Surface;
using DigitalBrain.Platform.Neurons.Proto;
using Google.Protobuf;
using Grpc.Core;
using CallRequest = DigitalBrain.Platform.Neurons.Proto.CallRequest;

namespace DigitalBrain.Platform.Neurons;

// The gRPC edge of the two operations every caller has: neurons_call and brain.On<T>. The brain
// rides in the message, so each call decides access the way a /brains/{brainId} route does.
internal sealed class NeuronsService(NeuronSurface surface, IDigitalBrain brain) : Proto.Neurons.NeuronsBase
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) } };

    public override async Task<CallReply> Call(CallRequest request, ServerCallContext context)
    {
        await BrainCall.Enter(context, request.BrainId);
        try
        {
            var arguments = request.Arguments.IsEmpty ? "[]" : request.Arguments.ToStringUtf8();
            var result = await surface.Call(request.Contract, request.Key, request.Method, arguments, context.CancellationToken);
            return new CallReply { Result = result is { } json ? ByteString.CopyFromUtf8(json.GetRawText()) : ByteString.Empty };
        }
        catch (Exception error) when (error is not RpcException and not OperationCanceledException) { throw BrainCall.Translate(error); }
    }

    // Retained last signal per source and type first, then live ones, until the client goes away.
    public override async Task Watch(WatchRequest request, IServerStreamWriter<SignalEnvelope> responseStream, ServerCallContext context)
    {
        await BrainCall.Enter(context, request.BrainId);
        if (request.Sources.Count == 0) { throw new RpcException(new Status(StatusCode.InvalidArgument, "Name at least one source.")); }
        var ct = context.CancellationToken;
        var channel = Channel.CreateUnbounded<SignalEnvelope>();
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var pumps = new List<Task>();
        try
        {
            foreach (var source in request.Sources)
            {
                var neuron = await surface.Reference(source.Contract, source.Key, ct);
                foreach (var name in source.SignalTypes.Distinct(StringComparer.Ordinal))
                {
                    var type = SignalTypes.Find(name);
                    pumps.Add(Task.Run(() => Pump(neuron, source.Key, type, channel.Writer, stop.Token), CancellationToken.None));
                }
            }
        }
        catch (Exception error) when (error is not RpcException and not OperationCanceledException)
        {
            await stop.CancelAsync();
            throw BrainCall.Translate(error);
        }
        _ = Task.WhenAll(pumps).ContinueWith(_ => channel.Writer.TryComplete(), CancellationToken.None);
        try
        {
            await foreach (var envelope in channel.Reader.ReadAllAsync(ct)) { await responseStream.WriteAsync(envelope, ct); }
        }
        finally { await stop.CancelAsync(); }
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

    private static async Task Replay(INeuron neuron, string key, string type, ChannelWriter<SignalEnvelope> writer, CancellationToken ct)
    {
        RetainedSignal[] retained;
        try { retained = await neuron.AsReference<IRetainsSignals>().Retained(type, 0).WaitAsync(ct); }
        catch (Exception error) when (error is not OperationCanceledException) { return; } // the source retains nothing
        if (retained.LastOrDefault() is { } last) { await writer.WriteAsync(Envelope(key, last.Signal, last.Sequence), ct); }
    }

    private static SignalEnvelope Envelope(string key, Signal signal, long sequence) => new()
    {
        Source = key, Type = signal.GetType().Name, Publisher = signal.Publisher.ToString(), Sequence = sequence,
        Payload = ByteString.CopyFromUtf8(JsonSerializer.Serialize(signal, signal.GetType(), Json)),
        ServerTimeMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
    };
}
