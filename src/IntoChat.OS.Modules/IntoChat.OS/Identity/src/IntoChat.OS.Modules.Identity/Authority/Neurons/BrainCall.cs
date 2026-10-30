using DigitalBrain;
using DigitalBrain.Capabilities;
using Grpc.Core;
using IntoChat.OS.Connections;
using IntoChat.OS.Framework;
using IntoChat.OS.Framework.Modules;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace IntoChat.OS.Authority.Neurons;

// A gRPC message carries its brain where an HTTP route carries it in the path: the same id rule,
// the same membership decision, then the brain stamped into the caller for BrainScope.CurrentId().
internal static class BrainCall
{
    public static async Task<CapabilityCaller> Enter(ServerCallContext context, string brainId)
    {
        if (!BrainScope.IsValidId(brainId)) { throw new RpcException(new Status(StatusCode.InvalidArgument, "brain_id is not a valid brain id.")); }
        var caller = CallerContextStamper.Require() with { BrainId = brainId };
        CallerContextStamper.Stamp(caller);
        var http = context.GetHttpContext();
        if (await BrainAccessFilter.Decide(http, brainId) is { } denied)
        {
            var code = denied is IStatusCodeHttpResult { StatusCode: StatusCodes.Status401Unauthorized } ? StatusCode.Unauthenticated : StatusCode.PermissionDenied;
            throw new RpcException(new Status(code, "This brain is not yours."));
        }
        // RequestContext changes in an async helper do not flow back to its caller.
        return caller;
    }

    // Error details ride in trailers until the status-details packages arrive with protocol v1 (W2 PR 5a):
    // the domain, a reason from the closed list, and per-reason metadata.
    internal const string ErrorDomain = "ui.intochat-os.io";
    internal const string DomainTrailer = "error-domain";
    internal const string ReasonTrailer = "error-reason";
    internal const string CurrentRevisionTrailer = "current-revision";

    // A caller's own mistakes come back with their reason; the host's failures stay in its logs.
    public static RpcException Translate(Exception error, ILogger? logger = null)
    {
        switch (error)
        {
            // Before ArgumentException and the default: both conflicts are the caller's to resolve, not host failures.
            case RevisionConflictException e:
                return Refused(StatusCode.Aborted, e.Message, "REVISION_CONFLICT", (CurrentRevisionTrailer, e.CurrentRevision.ToString(System.Globalization.CultureInfo.InvariantCulture)));
            case StaleActionException e: return Refused(StatusCode.FailedPrecondition, e.Message, "STALE_ACTION");
            case ArgumentException e: return new RpcException(new Status(StatusCode.InvalidArgument, e.Message));
            case UnauthorizedAccessException e: return new RpcException(new Status(StatusCode.PermissionDenied, e.Message));
            case KeyNotFoundException e: return new RpcException(new Status(StatusCode.NotFound, e.Message));
            case TypeLoadException e: return new RpcException(new Status(StatusCode.InvalidArgument, e.Message));
            default:
                logger?.LogError(error, "Neuron channel call failed");
                return new RpcException(new Status(StatusCode.Internal, "The neuron channel failed; see the host's logs."));
        }
    }

    private static RpcException Refused(StatusCode code, string message, string reason, params (string Key, string Value)[] details)
    {
        var trailers = new Metadata { { DomainTrailer, ErrorDomain }, { ReasonTrailer, reason } };
        foreach (var (key, value) in details) { trailers.Add(key, value); }
        return new RpcException(new Status(code, message), trailers, message);
    }
}
