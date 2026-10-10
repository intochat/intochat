using DigitalBrain.Platform.Neurons.Proto;
using Grpc.Core;

namespace DigitalBrain.Platform.Neurons;

// The gRPC edge of the two operations every caller has: neurons_call and brain.On<T>. The brain
// rides in the message, so each call decides access the way a /brains/{brainId} route does.
internal sealed class NeuronsService : Proto.Neurons.NeuronsBase
{
    public override Task<CallReply> Call(CallRequest request, ServerCallContext context)
        => throw new RpcException(new Status(StatusCode.Unimplemented, "Call"));

    public override Task Watch(WatchRequest request, IServerStreamWriter<SignalEnvelope> responseStream, ServerCallContext context)
        => throw new RpcException(new Status(StatusCode.Unimplemented, "Watch"));
}
