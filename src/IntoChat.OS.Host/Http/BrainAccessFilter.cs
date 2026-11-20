using DigitalBrain.Brains;
using DigitalBrain.Log;
using DigitalBrain.Runtime.Http.Surface;
using DigitalBrain.Runtime.Orleans.Capabilities;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using IntoChat.OS.Host.Logging;
using IntoChat.OS.Host.Brains;

namespace IntoChat.OS.Host.Http;

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
        if (HttpCaller.Current(http) is not { } caller) { return Results.Unauthorized(); }
        // Membership is a person's: a caller acting for nobody reaches no brain through a route.
        if (caller.For is not { } person) { return Results.StatusCode(StatusCodes.Status403Forbidden); }

        var access = http.RequestServices.GetRequiredService<IBrainAccess>();
        if (!await access.CanAccessAsync(person.Principal, person.Account, brainId, http.RequestAborted))
        { return Results.StatusCode(StatusCodes.Status403Forbidden); }
        await EnsureCreated(http, person.Account, brainId);
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
        var creating = seen.GetOrAdd(scope, _ => new Lazy<Task>(() => brains.Get<IBrain>(IBrain.Key(scope)).Create(new Owner(accountId))));
        // Best effort: the request itself is already allowed, and the next one retries.
        try { await creating.Value; }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            var logger = http.RequestServices.GetService<ILoggerFactory>()?.CreateLogger(typeof(BrainAccessFilter))
                ?? OsConsoleLog.For(typeof(BrainAccessFilter));
            logger.LogSwallowed(error, "best-effort brain EnsureCreated");
            seen.TryRemove(new(scope, creating));
        }
    }
}
