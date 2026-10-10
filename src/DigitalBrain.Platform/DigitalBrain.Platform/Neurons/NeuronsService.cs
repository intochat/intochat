using DigitalBrain.Framework.Surface;
using DigitalBrain.Platform.Neurons.Proto;
using CallRequest = DigitalBrain.Platform.Neurons.Proto.CallRequest;
using Google.Protobuf;
using Grpc.Core;

namespace DigitalBrain.Platform.Neurons;

// The gRPC edge of the two operations every caller has: neurons_call and brain.On<T>. The brain
// rides in the message, so each call decides access the way a /brains/{brainId} route does.
internal sealed class NeuronsService(NeuronSurface surface) : Proto.Neurons.NeuronsBase
{
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

    public override Task Watch(WatchRequest request, IServerStreamWriter<SignalEnvelope> responseStream, ServerCallContext context)
        => throw new RpcException(new Status(StatusCode.Unimplemented, "Watch"));
}
