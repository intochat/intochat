using DigitalBrain;
using DigitalBrain.Access;
using DigitalBrain.Capabilities;
using DigitalBrain.Capabilities.Storage;
using DigitalBrain.Runtime.Http;
using DigitalBrain.Runtime.Http.Storage;
using DigitalBrain.Runtime.Http.Surface;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace IntoChat.OS.Modules.Apps;

internal sealed record OverwriteAnswer(bool Approve);

// The hosted route on the host's module surface group, where the framework's session routes live (the session user is
// the resolved caller there, so the framework's membership check reads it as it reads a cookie session).
internal sealed class StorageContentSurface : IModuleHttpSurface
{
    public void Map(IEndpointRouteBuilder brainGroup) => StorageContentHosting.Map(brainGroup);
}

// Hosts the framework's storage content route (StorageContentEndpoints) for this brain's storages: a file's bytes by
// connection and id, and uploads by a ticket. A request is served for a person who is a member of the route's brain,
// acting in that brain: the storage is resolved through the broker under that person, so every read and write is the
// person's own (the workspace takes it without a question, a provider with confirmations asks). Next to it, two routes the
// framework leaves to its host: the workspace by path (what a studio page knows), and a person's answer to a parked
// overwrite (an upload over an existing file waits for one).
internal static class StorageContentHosting
{
    public static void AddServices(IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddModuleHttpSurface<StorageContentSurface>();
        services.AddDataProtection();
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<UploadPlanBook>();
        services.TryAddSingleton<ContentUploadLog>();
        services.TryAddSingleton(sp => new StorageContentGateway(
            (brain, key, ct) => Storage(sp, brain, key, ct),
            sp.GetRequiredService<UploadPlanBook>(),
            sp.GetRequiredService<ContentUploadLog>(),
            sp.GetRequiredService<IDataProtectionProvider>(),
            sp.GetRequiredService<TimeProvider>(),
            new PersonContentAuthorizer(sp)));
    }

    public static void Map(IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        var hosted = endpoints.MapGroup("");
        hosted.AddEndpointFilter(ActInTheRoutesBrain);
        StorageContentEndpoints.Map(hosted);
        BrainRoutes.Group(hosted, "/storage").MapPost("/{storage}/overwrites/{planId}", Answer);
        BrainRoutes.Group(hosted, "/workspace").MapGet("/item", Locate);
    }

    // The workspace file at a path, or where it would go: what a studio page needs to read it and to save it.
    private static async Task<IResult> Locate(HttpContext http, string brainId, string? path)
    {
        if (HttpCaller.Current(http) is not { Actor: Actor.Person }) { return Results.StatusCode(StatusCodes.Status403Forbidden); }
        string relative;
        try { relative = BrainWorkspaceFiles.Normalize(path ?? ""); }
        catch (ArgumentException) { return Results.BadRequest(new { error = "That path is not a workspace path." }); }
        if (relative.Length == 0) { return Results.BadRequest(new { error = "The path names no file." }); }

        var brain = new BrainId(brainId);
        var files = new BrainWorkspaceFiles(http.RequestServices.GetRequiredService<ICapabilityBroker>(), brain);
        var slash = relative.LastIndexOf('/');
        var (folderPath, name) = slash < 0 ? ("", relative) : (relative[..slash], relative[(slash + 1)..]);
        Item? folder;
        Item? file;
        try
        {
            folder = await files.Find(folderPath, http.RequestAborted).ConfigureAwait(false);
            file = await files.Find(relative, http.RequestAborted).ConfigureAwait(false);
        }
        catch (InvalidOperationException) { return Results.Text("This brain's workspace is not available.", statusCode: StatusCodes.Status503ServiceUnavailable); }
        if (folder is not { Kind: ItemKind.Folder }) { return Results.NotFound(new { error = "The folder of that path does not exist." }); }
        if (file is { Kind: ItemKind.Folder }) { return Results.BadRequest(new { error = "That path is a folder." }); }

        var key = BrainWorkspaceFiles.ConnectionKey(brain);
        var storage = "/brains/" + Uri.EscapeDataString(brainId) + "/storage/" + StorageContentEndpoints.Segment(key);
        return Results.Ok(new
        {
            path = BrainWorkspaceFiles.Stored(relative),
            name,
            folder = folder.Id.Value,
            id = file?.Id.Value,
            content = file is null ? null : StorageContentEndpoints.ContentPath(brainId, key, file.Id.Value),
            ticket = storage + "/items/" + StorageContentEndpoints.Escape(folder.Id.Value) + "/upload-ticket?name=" + Uri.EscapeDataString(name),
            overwrites = storage + "/overwrites/",
        });
    }

    // A person's answer to a parked overwrite of a file of this brain; an app, an agent or another brain's person cannot give one.
    private static IResult Answer(HttpContext http, string brainId, string storage, string planId, OverwriteAnswer answer)
    {
        var caller = HttpCaller.Current(http);
        if (caller is not { Actor: Actor.Person } || !StorageContentEndpoints.TryKey(storage, out var key)) { return Results.StatusCode(StatusCodes.Status403Forbidden); }
        var book = http.RequestServices.GetRequiredService<UploadPlanBook>();
        if (book.Find(planId) is not { } parked || parked.Brain != brainId || parked.Storage != key || parked.Outcome is not null)
        {
            return Results.NotFound(new { error = "No overwrite of that file is waiting." });
        }
        book.MarkStarted(planId);
        if (!answer.Approve)
        {
            book.Deny(planId);
            return Results.Ok(new { approved = false });
        }
        return book.Approve(planId, caller) ? Results.Ok(new { approved = true }) : Results.StatusCode(StatusCodes.Status403Forbidden);
    }

    // The session's person acting in the route's brain, as a gRPC call is stamped (BrainCall); the brain group's membership
    // check runs inside this, so nothing is served to a person who is not a member. The result runs inside it too: a
    // download's bytes are read while the response is written.
    private static async ValueTask<object?> ActInTheRoutesBrain(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var http = context.HttpContext;
        if (http.Request.RouteValues["brainId"] is not string brainId || !BrainId.IsValid(brainId)
            || HttpCaller.Current(http) is not { Actor: Actor.Person person }
            || http.RequestServices.GetService<EdgeAuthority>() is not { } edge)
        {
            return await next(context).ConfigureAwait(false);
        }
        using var entered = edge.Enter(edge.Person(person.Principal, person.Account, new BrainId(brainId)));
        var result = await next(context).ConfigureAwait(false);
        if (result is not IResult response) { return result; }
        await response.ExecuteAsync(http).ConfigureAwait(false);
        return Results.Empty;
    }

    // One connection of the brain by its key, through the broker under the caller of the request; never matched by its text.
    private static async Task<IStorage> Storage(IServiceProvider services, BrainId brain, string key, CancellationToken ct)
    {
        var connections = await services.GetRequiredService<ICapabilityBroker>().ResolveAll<IStorage>(brain, ct).ConfigureAwait(false);
        return connections.FirstOrDefault(connection => string.Equals(connection.ConnectionKey, key, StringComparison.Ordinal))?.Capability
            ?? throw new NotFound(new ItemId(key));
    }

    // Admits a person acting in the brain to a connection of that same brain; anyone else, or another brain's connection, is refused.
    private sealed class PersonContentAuthorizer(IServiceProvider services) : IStorageContentAuthorizer
    {
        public Task Authorise(BrainId brain, string storageKey, ItemId item, CancellationToken ct)
        {
            var caller = services.GetService<ICallerAmbient>()?.Current;
            if (caller is not { Actor: Actor.Person } || caller.Brain != brain
                || !storageKey.StartsWith(brain.Value + "/", StringComparison.Ordinal))
            {
                throw new Denied(item);
            }
            return Task.CompletedTask;
        }
    }
}
