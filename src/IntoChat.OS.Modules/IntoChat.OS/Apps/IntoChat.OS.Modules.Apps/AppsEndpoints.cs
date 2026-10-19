using IntoChat.OS.Framework;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;

namespace IntoChat.OS.Apps;

// The whole apps surface, one screenful: create and author are synchronous (one model call);
// implement answers 202 and finishes in the background; sharing is export/import.
internal static class AppsEndpoints
{
    public sealed record TextRequest(string? Text);
    public sealed record SettingsRequest(Dictionary<string, string>? Settings, Dictionary<string, string>? Connections);
    public sealed record RollbackRequest(string Version);
    public sealed record RequestBody(string? Operation, string? Input);
    public sealed record SynapseBody(string? Sentence, string[]? Focus, Guid? OperationId);

    public static void Map(IEndpointRouteBuilder endpoints)
    {
        var apps = BrainRoutes.Group(endpoints, "/apps").AddEndpointFilter(ModuleRouteGuard.Guard);

        apps.MapGet("", (IIntoChatOS brain, [FromServices] MarketplaceView marketplace, CancellationToken ct) => Respond(async () =>
        {
            var listings = await marketplace.List(ct);
            var items = new List<object>();
            foreach (var (name, snapshot) in await InstalledApps.Read(brain, BrainScope.CurrentId(), ct))
            {
                items.Add(new
                {
                    name,
                    title = snapshot.Manifest.Title,
                    description = snapshot.Manifest.Description,
                    status = snapshot.Status.ToString(),
                    version = snapshot.Version,
                    operations = snapshot.Manifest.Operations.Select(operation => operation.Name).ToArray(),
                    icon = snapshot.Manifest.Icon,
                    publishedVersion = snapshot.PublishedVersion,
                    origin = snapshot.Origin is { } origin
                        ? new { publisher = origin.Publisher, name = origin.Name, version = origin.Version }
                        : null,
                    edited = snapshot.Edited,
                    updateAvailable = InstalledApps.UpdateAvailable(snapshot, listings),
                });
            }
            return Results.Ok(items);
        }));

        apps.MapPost("", (TextRequest request, [FromServices] Creator creator, IIntoChatOS brain, CancellationToken ct) => Respond(async () =>
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(request?.Text);
            return Results.Ok(View(await creator.Author(brain, BrainScope.CurrentId(), null, request.Text, ct)));
        }));

        apps.MapGet("/{name}", (string name, IIntoChatOS brain, CancellationToken ct) => Respond(async () =>
        {
            var snapshot = await App(brain, name).Read().WaitAsync(ct);
            return snapshot.Manifest.Title.Length == 0
                ? throw new KeyNotFoundException($"No app named {name} exists in this brain.")
                : Results.Ok(View(snapshot));
        }));

        apps.MapPost("/{name}/author", (string name, TextRequest request, [FromServices] Creator creator, IIntoChatOS brain, CancellationToken ct) => Respond(async () =>
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(request?.Text);
            return Results.Ok(View(await creator.Author(brain, BrainScope.CurrentId(), name, request.Text, ct)));
        }));

        apps.MapPost("/{name}/implement", (string name, TextRequest request, [FromServices] Creator creator, IIntoChatOS brain, CancellationToken ct) => Respond(async () =>
        {
            var snapshot = await creator.Implement(brain, Key(name), request?.Text, ct);
            return Results.Accepted($"/brains/{BrainScope.CurrentId()}/apps/{name}", View(snapshot));
        }));

        // A request is published for a synapse to react to; nothing waits for the app to answer.
        apps.MapPost("/{name}/request", (string name, RequestBody? request, IIntoChatOS brain, CancellationToken ct) => Respond(async () =>
        {
            var id = await App(brain, name).Request(request?.Operation ?? "ask", request?.Input ?? "{}").WaitAsync(ct);
            return Results.Accepted($"/brains/{BrainScope.CurrentId()}/apps/{name}", new { requestId = id });
        }));

        // The fast path: one sentence becomes one synapse of the app. The request is recorded and the app answers
        // at once; the synapse, or why it was rejected, lands in the app's synapses.
        apps.MapPost("/{name}/synapse", (string name, SynapseBody? body, IIntoChatOS brain, CancellationToken ct) => Respond(async () =>
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(body?.Sentence);
            var snapshot = await App(brain, name).WriteSynapse(body.Sentence, body.Focus ?? [], body.OperationId ?? Guid.NewGuid()).WaitAsync(ct);
            return Results.Accepted($"/brains/{BrainScope.CurrentId()}/apps/{name}", View(snapshot));
        }));

        apps.MapPut("/{name}/settings", (string name, SettingsRequest request, IIntoChatOS brain) => Respond(async ()
            => Results.Ok(View(await App(brain, name).Configure(request?.Settings ?? [], request?.Connections ?? [])))));

        apps.MapPost("/{name}/rollback", (string name, RollbackRequest request, IIntoChatOS brain) => Respond(async () =>
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(request?.Version);
            return Results.Ok(View(await App(brain, name).Rollback(request.Version)));
        }));

        apps.MapPost("/{name}/publish", (string name, IIntoChatOS brain) => Respond(async ()
            => Results.Ok(View(await App(brain, name).Publish()))));

        // Development only: writes an edited copy of a feature back into its owner's features folder.
        apps.MapPost("/{name}/save-to-source", (string name, IIntoChatOS brain) => Respond(async ()
            => Results.Ok(View(await App(brain, name).SaveToSource()))));

        apps.MapPost("/{name}/update", (string name, [FromServices] AppInstaller installer, CancellationToken ct) => Respond(async ()
            => Results.Ok(View(await installer.Update(name, ct)))));

        apps.MapGet("/{name}/icon", (string name, IIntoChatOS brain, CancellationToken ct) => Respond(async () =>
        {
            var snapshot = await App(brain, name).Read().WaitAsync(ct);
            return Icon(snapshot.Manifest.Icon, snapshot.Files, name);
        }));

        apps.MapGet("/{name}/export", (string name, IIntoChatOS brain, CancellationToken ct)
            => Respond(async () => Results.Ok(await App(brain, name).Export().WaitAsync(ct))));

        apps.MapPost("/import", (AppBundle bundle, IIntoChatOS brain) => Respond(async () =>
        {
            ArgumentNullException.ThrowIfNull(bundle);
            ArgumentException.ThrowIfNullOrWhiteSpace(bundle.Name);
            return Results.Ok(View(await App(brain, bundle.Name).Import(bundle)));
        }));

        apps.MapDelete("/{name}", (string name, IIntoChatOS brain) => Respond(async () =>
        {
            await App(brain, name).Delete();
            return Results.Ok(new { deleted = name });
        }));
    }

    private static string Key(string name) => IApp.Key(BrainScope.CurrentId(), name);

    private static IApp App(IIntoChatOS brain, string name) => brain.Get<IApp>(Key(name));

    // The wire view: the snapshot plus status as a string the shell can switch on.
    // An icon is a bundle file the manifest names; the shell renders it by its content type.
    internal static IResult Icon(string? path, IReadOnlyDictionary<string, string> files, string app)
    {
        if (path is null || !files.TryGetValue(path, out var content))
        { return Results.NotFound(new { error = $"{app} has no icon." }); }
        var type = Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".svg" => "image/svg+xml",
            ".png" => "image/png",
            ".jpg" or ".jpeg" => "image/jpeg",
            ".webp" => "image/webp",
            _ => "text/plain",
        };
        return new InertFile(Results.Text(content, type));
    }

    // A publisher's file served from the API origin: an SVG can carry script, so the answer runs
    // nothing and is never sniffed into something that would.
    private sealed class InertFile(IResult inner) : IResult
    {
        public Task ExecuteAsync(HttpContext http)
        {
            http.Response.Headers.ContentSecurityPolicy = "default-src 'none'; style-src 'unsafe-inline'; sandbox";
            http.Response.Headers.XContentTypeOptions = "nosniff";
            return inner.ExecuteAsync(http);
        }
    }

    internal static object View(AppSnapshot snapshot) => new
    {
        name = snapshot.Name,
        manifest = snapshot.Manifest,
        spec = snapshot.Spec,
        files = snapshot.Files,
        settings = snapshot.Settings,
        connections = snapshot.Connections,
        version = snapshot.Version,
        status = snapshot.Status.ToString(),
        activity = snapshot.Activity,
        versions = snapshot.Versions,
        publishedVersion = snapshot.PublishedVersion,
        origin = snapshot.Origin,
        edited = snapshot.Edited,
        synapses = (snapshot.Synapses ?? []).Select(synapse => new
        {
            operationId = synapse.OperationId,
            sentence = synapse.Sentence,
            focus = synapse.Focus,
            status = synapse.Status.ToString(),
            requestedAt = synapse.RequestedAt,
            errors = synapse.Errors,
            path = synapse.Path,
            hash = synapse.Hash,
            elapsedMs = synapse.ElapsedMs,
            attempts = synapse.Attempts,
            @base = synapse.Base,
        }).ToArray(),
    };

    internal static async Task<IResult> Respond(Func<Task<IResult>> handler)
    {
        try { return await handler(); }
        catch (InvalidOperationException error) { return Results.Conflict(new { error = error.Message }); }
        catch (UnauthorizedAccessException error) { return Results.Json(new { error = error.Message }, statusCode: 403); }
        catch (KeyNotFoundException error) { return Results.NotFound(new { error = error.Message }); }
        catch (Exception error) when (error is ArgumentException or InvalidDataException)
        { return Results.UnprocessableEntity(new { error = error.Message }); }
    }
}
