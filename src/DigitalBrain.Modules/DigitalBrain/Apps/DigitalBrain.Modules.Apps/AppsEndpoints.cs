using System.Text.Json;
using DigitalBrain;
using DigitalBrain.Framework;
using DigitalBrain.Framework.Enforcement;
using DigitalBrain.Framework.Http;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;

namespace DigitalBrain.Apps;

// The whole apps surface, one screenful: create and author are synchronous (one model call);
// implement and verify answer 202 and finish in the background; sharing is export/import.
internal static class AppsEndpoints
{
    public sealed record TextRequest(string? Text);
    public sealed record AskRequest(string? Operation, string? Input);
    public sealed record SettingsRequest(Dictionary<string, string>? Settings, Dictionary<string, string>? Connections);
    public sealed record RollbackRequest(string Version);

    public static void Map(IEndpointRouteBuilder endpoints)
    {
        var apps = BrainRoutes.Group(endpoints, "/apps").AddEndpointFilter(ModuleRouteGuard.Guard);

        apps.MapGet("", (IDigitalBrain brain, CancellationToken ct) => Respond(async () =>
        {
            var scope = BrainScope.CurrentId();
            var names = await brain.Get<IAppIndex>(scope).List().WaitAsync(ct);
            var listings = await brain.Get<IMarketplace>(IMarketplace.Key).List().WaitAsync(ct);
            var items = new List<object>();
            foreach (var name in names)
            {
                var snapshot = await App(brain, name).Read().WaitAsync(ct);
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
                    // The same rule as AppInstaller.Entry: an unedited copy whose publisher listed a newer version.
                    updateAvailable = snapshot.Origin is { } source && !snapshot.Edited
                        && listings.FirstOrDefault(listing => listing.Publisher == source.Publisher && listing.Name == source.Name) is { } listed
                        && AppVersions.Compare(listed.Version, source.Version) > 0,
                });
            }
            return Results.Ok(items);
        }));

        apps.MapPost("", (TextRequest request, [FromServices] Creator creator, IDigitalBrain brain, CancellationToken ct) => Respond(async () =>
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(request?.Text);
            return Results.Ok(View(await creator.Author(brain, BrainScope.CurrentId(), null, request.Text, ct)));
        }));

        apps.MapGet("/{name}", (string name, IDigitalBrain brain, CancellationToken ct)
            => Respond(async () => Results.Ok(View(await App(brain, name).Read().WaitAsync(ct)))));

        apps.MapPost("/{name}/author", (string name, TextRequest request, [FromServices] Creator creator, IDigitalBrain brain, CancellationToken ct) => Respond(async () =>
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(request?.Text);
            return Results.Ok(View(await creator.Author(brain, BrainScope.CurrentId(), name, request.Text, ct)));
        }));

        apps.MapPost("/{name}/implement", (string name, TextRequest request, [FromServices] Creator creator, IDigitalBrain brain) => Respond(async () =>
        {
            var snapshot = await creator.Implement(brain, Key(name), request?.Text);
            return Results.Accepted($"/brains/{BrainScope.CurrentId()}/apps/{name}", View(snapshot));
        }));

        apps.MapPost("/{name}/verify", (string name, HttpContext context, IDigitalBrain brain) => Respond(async () =>
        {
            var snapshot = await App(brain, name).BeginVerify();
            AppVerifyJob.Enqueue(context.RequestServices, Key(name));
            return Results.Accepted($"/brains/{BrainScope.CurrentId()}/apps/{name}", View(snapshot));
        }));

        apps.MapPut("/{name}/settings", (string name, SettingsRequest request, IDigitalBrain brain) => Respond(async ()
            => Results.Ok(View(await App(brain, name).Configure(request?.Settings ?? [], request?.Connections ?? [])))));

        apps.MapPost("/{name}/rollback", (string name, RollbackRequest request, IDigitalBrain brain) => Respond(async () =>
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(request?.Version);
            return Results.Ok(View(await App(brain, name).Rollback(request.Version)));
        }));

        apps.MapPost("/{name}/publish", (string name, IDigitalBrain brain) => Respond(async ()
            => Results.Ok(View(await App(brain, name).Publish()))));

        apps.MapPost("/{name}/update", (string name, [FromServices] AppInstaller installer, CancellationToken ct) => Respond(async ()
            => Results.Ok(View(await installer.Update(name, ct)))));

        apps.MapGet("/{name}/icon", (string name, IDigitalBrain brain, CancellationToken ct) => Respond(async () =>
        {
            var snapshot = await App(brain, name).Read().WaitAsync(ct);
            return Icon(snapshot.Manifest.Icon, snapshot.Files, name);
        }));

        apps.MapPost("/{name}/ask", (string name, AskRequest? request, IDigitalBrain brain, CancellationToken ct)
            => Respond(() => Ask(App(brain, name), request?.Operation ?? "ask", request?.Input ?? "{}", ct)));

        apps.MapGet("/{name}/export", (string name, IDigitalBrain brain, CancellationToken ct)
            => Respond(async () => Results.Ok(await App(brain, name).Export().WaitAsync(ct))));

        apps.MapPost("/import", (AppBundle bundle, IDigitalBrain brain) => Respond(async () =>
        {
            ArgumentNullException.ThrowIfNull(bundle);
            ArgumentException.ThrowIfNullOrWhiteSpace(bundle.Name);
            return Results.Ok(View(await App(brain, bundle.Name).Import(bundle)));
        }));

        apps.MapDelete("/{name}", (string name, IDigitalBrain brain) => Respond(async () =>
        {
            await App(brain, name).Delete();
            return Results.Ok(new { deleted = name });
        }));
    }

    private static string Key(string name) => IApp.Key(BrainScope.CurrentId(), name);

    private static IApp App(IDigitalBrain brain, string name) => brain.Get<IApp>(Key(name));

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
        verdicts = snapshot.Verdicts,
        publishedVersion = snapshot.PublishedVersion,
        origin = snapshot.Origin,
        edited = snapshot.Edited,
    };

    private static async Task<IResult> Ask(IApp app, string operation, string input, CancellationToken ct)
    {
        var invocation = await app.Invoke(new(Guid.NewGuid(), operation, input.Length == 0 ? "{}" : input)).WaitAsync(ct);
        var started = TimeProvider.System.GetTimestamp();
        while (invocation.Status == InvocationStatus.Pending && TimeProvider.System.GetElapsedTime(started) < TimeSpan.FromSeconds(30))
        {
            await Task.Delay(100, ct);
            invocation = await app.ReadInvocation(invocation.Id).WaitAsync(ct);
        }
        if (invocation.Status == InvocationStatus.Pending)
        { return Results.Json(new { error = "The app has not answered yet.", invocationId = invocation.Id }, statusCode: 408); }
        if (invocation.Status != InvocationStatus.Completed || invocation.Output is null)
        { return Results.UnprocessableEntity(new { error = invocation.Error ?? "The app returned no output.", invocationId = invocation.Id }); }
        try
        {
            using var output = JsonDocument.Parse(invocation.Output);
            return Results.Ok(output.RootElement.Clone());
        }
        catch (JsonException)
        {
            return Results.Text(invocation.Output);
        }
    }

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
