using DigitalBrain.Brains;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace IntoChat.OS.Host.Http;

public static class OsBrainRoutes
{
    // Marks a brain-scoped route group that already enforces BrainAccessFilter so an ephemeral try-* guest
    // may reach its own RAM brain without touching any other brain.
    public sealed class GuestBrainDoor
    {
        public static readonly GuestBrainDoor Instance = new();
    }

    // Explicitly closes a brain-scoped route group to guest sessions (e.g. device pairing, host OS, owner-only entries).
    public sealed class GuestClosed
    {
        public static readonly GuestClosed Instance = new();
    }

    // The one place brain-scoped HTTP routes are rooted: /brains/{brainId} with route-id
    // validation and the membership filter applied once.
    public static RouteGroupBuilder Group(IEndpointRouteBuilder endpoints, string suffix = "")
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        var group = endpoints.MapGroup("/brains/{brainId}" + suffix);
        group.AddEndpointFilter(async (context, next) =>
            await ValidateId(context.HttpContext) is { } invalid ? invalid : await next(context));
        group.AddEndpointFilter(BrainAccessFilter.EnforceAsync);
        group.WithMetadata(GuestBrainDoor.Instance);
        return group;
    }

    // For edges that authenticate their own caller, such as a sandbox session's token: the same
    // /brains/{brainId} root and id validation, without the membership filter.
    public static RouteGroupBuilder SessionGroup(IEndpointRouteBuilder endpoints, string suffix = "")
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        var group = endpoints.MapGroup("/brains/{brainId}" + suffix);
        group.AddEndpointFilter(async (context, next) =>
            await ValidateId(context.HttpContext) is { } invalid ? invalid : await next(context));
        return group;
    }

    internal static ValueTask<IResult?> ValidateId(HttpContext http)
        => ValueTask.FromResult<IResult?>(
            BrainScope.IsValidId(http.Request.RouteValues["brainId"] as string) ? null : Results.BadRequest());
}
