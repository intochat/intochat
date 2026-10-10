using DigitalBrain.Framework;
using Grpc.Core;
using Microsoft.AspNetCore.Http;

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

    public static RpcException Translate(Exception error) => error switch
    {
        ArgumentException e => new RpcException(new Status(StatusCode.InvalidArgument, e.Message)),
        UnauthorizedAccessException e => new RpcException(new Status(StatusCode.PermissionDenied, e.Message)),
        KeyNotFoundException e => new RpcException(new Status(StatusCode.NotFound, e.Message)),
        TypeLoadException e => new RpcException(new Status(StatusCode.InvalidArgument, e.Message)),
        _ => new RpcException(new Status(StatusCode.Internal, error.Message)),
    };
}
