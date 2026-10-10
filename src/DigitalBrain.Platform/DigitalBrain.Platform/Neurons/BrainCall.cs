using DigitalBrain.Framework;
using Grpc.Core;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace DigitalBrain.Platform.Neurons;

// A gRPC message carries its brain where an HTTP route carries it in the path: the same id rule,
// the same membership decision, then the brain stamped into the caller for BrainScope.CurrentId().
internal static class BrainCall
{
    public static async Task Enter(ServerCallContext context, string brainId)
    {
        if (!BrainScope.IsValidId(brainId)) { throw new RpcException(new Status(StatusCode.InvalidArgument, "brain_id is not a valid brain id.")); }
        var http = context.GetHttpContext();
        if (await BrainAccessFilter.Decide(http, brainId) is { } denied)
        {
            var code = denied is IStatusCodeHttpResult { StatusCode: StatusCodes.Status401Unauthorized } ? StatusCode.Unauthenticated : StatusCode.PermissionDenied;
            throw new RpcException(new Status(code, "This brain is not yours."));
        }
        CallerContextStamper.Stamp(CallerContextStamper.Require() with { BrainId = brainId });
    }

    // A caller's own mistakes come back with their reason; the host's failures stay in its logs.
    public static RpcException Translate(Exception error, ILogger? logger = null)
    {
        switch (error)
        {
            case ArgumentException e: return new RpcException(new Status(StatusCode.InvalidArgument, e.Message));
            case UnauthorizedAccessException e: return new RpcException(new Status(StatusCode.PermissionDenied, e.Message));
            case KeyNotFoundException e: return new RpcException(new Status(StatusCode.NotFound, e.Message));
            case TypeLoadException e: return new RpcException(new Status(StatusCode.InvalidArgument, e.Message));
            default:
                logger?.LogError(error, "Neuron channel call failed");
                return new RpcException(new Status(StatusCode.Internal, "The neuron channel failed; see the host's logs."));
        }
    }
}
