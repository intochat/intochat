using DigitalBrain.Capabilities;
using DigitalBrain.Capabilities.Identity;
using DigitalBrain.Runtime.Http.Surface;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace DigitalBrain.Runtime.Http;

/// <summary>
/// The one place brain-scoped HTTP routes are rooted: <c>/brains/{brainId}</c>.
/// An invalid brain id is answered 400 in both groups.
/// </summary>
public static class BrainRouteGroups
{
    private const string MemberKey = "db.http.member";

    /// <summary>
    /// Marks a brain-scoped route group that enforces brain membership so an ephemeral guest session
    /// may reach its own brain without touching any other brain.
    /// </summary>
    public sealed class GuestBrainDoor
    {
        /// <summary>The shared metadata instance.</summary>
        public static readonly GuestBrainDoor Instance = new();
    }

    /// <summary>
    /// Explicitly closes a brain-scoped route group to guest sessions (e.g. device pairing, host OS, owner-only entries).
    /// </summary>
    public sealed class GuestClosed
    {
        /// <summary>The shared metadata instance.</summary>
        public static readonly GuestClosed Instance = new();
    }

    /// <summary>
    /// <c>/brains/{brainId}</c> plus <paramref name="suffix"/>, with brain id validation (400) and the membership check
    /// (<see cref="Decide"/>). Marks the group with <see cref="GuestBrainDoor.Instance"/>; add
    /// <see cref="GuestClosed.Instance"/> to close a brain-scoped group to guest sessions.
    /// When admitted through identity@1, the checked member is available via <see cref="MemberOf"/>.
    /// </summary>
    public static RouteGroupBuilder Brain(IEndpointRouteBuilder endpoints, string suffix = "")
    {
        var group = Session(endpoints, suffix);
        group.AddEndpointFilter(RequireMembership);
        group.WithMetadata(GuestBrainDoor.Instance);
        return group;
    }

    /// <summary>
    /// <c>/brains/{brainId}</c> plus <paramref name="suffix"/> with the brain id check only, for edges that authenticate their own
    /// caller (for example a sandbox session token).
    /// </summary>
    public static RouteGroupBuilder Session(IEndpointRouteBuilder endpoints, string suffix = "")
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        ArgumentNullException.ThrowIfNull(suffix);
        var group = endpoints.MapGroup("/brains/{brainId}" + suffix);
        group.AddEndpointFilter(async (context, next) =>
            BrainId.IsValid(context.HttpContext.Request.RouteValues["brainId"] as string) ? await next(context).ConfigureAwait(false) : Results.BadRequest());
        return group;
    }

    /// <summary>The membership <see cref="Brain"/> checked for this request, or null outside a brain group.</summary>
    public static Member? MemberOf(HttpContext http)
    {
        ArgumentNullException.ThrowIfNull(http);
        return http.Items.TryGetValue(MemberKey, out var member) ? member as Member : null;
    }

    /// <summary>
    /// Enforces brain membership for a request addressing <paramref name="brainId"/> (used by <see cref="Brain"/> as well as
    /// gRPC and body-carried-brain edges). Returns <c>null</c> when admitted, or a 401/403 <see cref="IResult"/> when denied.
    /// Uses <see cref="IBrainAccess"/> when registered, falling back to fail-closed identity@1 <see cref="IIdentity.Membership"/>.
    /// Invokes an optional <see cref="IBrainRouteInitializer"/> after admission.
    /// </summary>
    public static async ValueTask<IResult?> Decide(HttpContext http, string brainId)
    {
        ArgumentNullException.ThrowIfNull(http);

        string principalId;
        string accountId;
        if (HttpCaller.Current(http) is { } caller)
        {
            // Membership is a person's: a caller acting for nobody reaches no brain through a route.
            if (caller.For is not { } person) { return Results.StatusCode(StatusCodes.Status403Forbidden); }
            principalId = person.Principal;
            accountId = person.Account;
        }
        else if (SessionPrincipal.PrincipalId(http.User) is { } principal)
        {
            principalId = principal;
            accountId = http.User.FindFirst(SessionClaims.Account)?.Value ?? principal;
        }
        else
        {
            return Results.Unauthorized();
        }

        if (string.IsNullOrWhiteSpace(brainId)) { return Results.StatusCode(StatusCodes.Status403Forbidden); }

        if (http.RequestServices.GetService<IBrainAccess>() is { } access)
        {
            if (!await access.CanAccessAsync(principalId, accountId, brainId, http.RequestAborted).ConfigureAwait(false))
            {
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            }
        }
        else
        {
            var identity = http.RequestServices.GetKeyedService<IIdentity>(Capability.Identity.V1Id);
            if (identity is null) { return Results.StatusCode(StatusCodes.Status403Forbidden); }
            var member = await identity.Membership(brainId, principalId, http.RequestAborted).ConfigureAwait(false);
            if (member is null) { return Results.StatusCode(StatusCodes.Status403Forbidden); }
            http.Items[MemberKey] = member;
            if (!string.IsNullOrWhiteSpace(member.AccountId)) { accountId = member.AccountId; }
        }

        if (http.RequestServices.GetService<IBrainRouteInitializer>() is { } initializer)
        {
            await initializer.InitializeAsync(accountId, brainId, http.RequestAborted).ConfigureAwait(false);
        }

        return null;
    }

    private static async ValueTask<object?> RequireMembership(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var http = context.HttpContext;
        var brainId = http.Request.RouteValues["brainId"] as string;
        if (string.IsNullOrWhiteSpace(brainId)) { return Results.StatusCode(StatusCodes.Status403Forbidden); }
        return await Decide(http, brainId).ConfigureAwait(false) is { } denied ? denied : await next(context).ConfigureAwait(false);
    }
}
