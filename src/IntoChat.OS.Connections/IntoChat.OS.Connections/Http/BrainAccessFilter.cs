using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace IntoChat.OS.Framework;

// The HTTP edge of the brain membership rule. Routes that address a brain by path apply
// this filter so a principal cannot reach a brain it does not belong to, even though the route
// carries the brain id. Every request answers it: the composed IBrainAccess carries the host's
// posture, so openness is a composition decision, never inferred from the request's shape.
public static class BrainAccessFilter
{
    public static async ValueTask<object?> EnforceAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        ArgumentNullException.ThrowIfNull(context);
        var http = context.HttpContext;
        var brainId = http.Request.RouteValues["brainId"] as string;
        if (string.IsNullOrWhiteSpace(brainId)) { return await next(context); }

        return await Decide(http, brainId) is { } denied ? denied : await next(context);
    }

    // For a route that carries the brain in its body rather than its path.
    public static async ValueTask<IResult?> Decide(HttpContext http, string brainId)
    {
        ArgumentNullException.ThrowIfNull(http);
        if (!CallerContextStamper.TryGet(out var caller)) { return Results.Unauthorized(); }

        var access = http.RequestServices.GetRequiredService<IBrainAccess>();
        if (!await access.CanAccessAsync(caller.PrincipalId, caller.AccountId, brainId, http.RequestAborted))
        { return Results.StatusCode(StatusCodes.Status403Forbidden); }
        await EnsureCreated(http, caller.AccountId, brainId);
        return null;
    }

    // A brain in use is a created brain: a development owner never registers, and creating
    // provisions the OS defaults into it. Every request to the brain waits for the same creation,
    // so none sees a half-provisioned brain. Once per brain per host; the call is idempotent, so a
    // retry after a failure is harmless.
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<IIntoChatOSBrains, System.Collections.Concurrent.ConcurrentDictionary<string, Lazy<Task>>> Created = [];

    private static async Task EnsureCreated(HttpContext http, string accountId, string brainId)
    {
        if (http.RequestServices.GetService<IIntoChatOSBrains>() is not { } brains) { return; }
        var scope = BrainScope.Create(accountId, brainId).Id;
        var seen = Created.GetOrCreateValue(brains);
        var creating = seen.GetOrAdd(scope, _ => new Lazy<Task>(() => brains.Open(scope).Self.Create(new Owner(accountId))));
        // Best effort: the request itself is already allowed, and the next one retries.
        try { await creating.Value; }
        catch (Exception error) when (error is not OperationCanceledException) { seen.TryRemove(new(scope, creating)); }
    }
}
