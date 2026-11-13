using DigitalBrain.Brains;
using DigitalBrain.Capabilities;
using DigitalBrain.Kernel;
using DigitalBrain.Neurons;
using DigitalBrain.Runtime.Orleans;
using DigitalBrain.Runtime.Orleans.Capabilities;
using DigitalBrain.Units;
using IntoChat.OS.Connections;
using IntoChat.OS.Framework;
using IntoChat.OS.Framework.Modules;
using IntoChat.OS.Runtime;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;

namespace IntoChat.OS.Modules.Apps;

// The whole apps surface, one screenful: create and author are synchronous (one model call);
// implement answers 202 and finishes in the background; sharing is export/import.
internal static class AppsEndpoints
{
    public sealed record TextRequest(string? Text);
    public sealed record SettingsRequest(Dictionary<string, string>? Settings, Dictionary<string, string>? Connections);
    public sealed record RollbackRequest(string Version);
    public sealed record RequestBody(string? Operation, string? Input);
    public sealed record UpdateBody(string? Consent);

    public static void Map(IEndpointRouteBuilder endpoints)
    {
        var apps = BrainRoutes.Group(endpoints, "/apps").AddEndpointFilter(ModuleRouteGuard.Guard);

        apps.MapGet("", (INeuronProxies brain, IBrainObserver kernel, [FromServices] MarketplaceView marketplace, CancellationToken ct) => Respond(async () =>
        {
            var listings = await marketplace.List(ct);
            var items = new List<object>();
            foreach (var (name, snapshot) in await InstalledApps.Read(brain, kernel, BrainScope.CurrentId(), ct))
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
                    shippedUpdate = false,
                });
            }
            return Results.Ok(items);
        }));

        apps.MapPost("", (TextRequest request, [FromServices] Creator creator, INeuronProxies brain, CancellationToken ct) => Respond(async () =>
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(request?.Text);
            return Results.Ok(View(await creator.Author(brain, BrainScope.CurrentId(), null, request.Text, ct)));
        }));

        apps.MapGet("/{name}", (string name, INeuronProxies brain, CancellationToken ct) => Respond(async () =>
        {
            var key = AppKey(name);
            var app = App(brain, name);
            var snapshot = await AppBuildStatus.Of(await app.Read().WaitAsync(ct), app, brain.Get<Flows.IFlows>(Flows.IFlows.Key(BrainScope.CurrentId()).ToString()), ct);
            return snapshot.Manifest.Title.Length == 0
                ? throw new KeyNotFoundException($"No app named {name} exists in this brain.")
                : Results.Ok(View(snapshot));
        }));

        apps.MapPost("/{name}/author", (string name, TextRequest request, [FromServices] Creator creator, INeuronProxies brain, CancellationToken ct) => Respond(async () =>
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(request?.Text);
            return Results.Ok(View(await creator.Author(brain, BrainScope.CurrentId(), name, request.Text, ct)));
        }));

        apps.MapPost("/{name}/implement", (string name, TextRequest request, [FromServices] Creator creator, INeuronProxies brain, CancellationToken ct) => Respond(async () =>
        {
            var snapshot = await creator.Implement(brain, AppKey(name), request?.Text, ct);
            return Results.Accepted($"/brains/{BrainScope.CurrentId()}/apps/{name}", View(snapshot));
        }));

        // A request is published for a synapse to react to; nothing waits for the app to answer.
        apps.MapPost("/{name}/request", (string name, RequestBody? request, INeuronProxies brain, CancellationToken ct) => Respond(async () =>
        {
            var operation = request?.Operation ?? "ask";
            var input = request?.Input ?? "{}";
            var scope = BrainScope.CurrentId();
            var id = await App(brain, name).Request(operation, input).WaitAsync(ct);
            await ForwardScreenRequest(brain, scope, name, operation, input, ct);
            return Results.Accepted($"/brains/{scope}/apps/{name}", new { requestId = id });
        }));

        apps.MapPut("/{name}/settings", (string name, SettingsRequest request, INeuronProxies brain) => Respond(async ()
            => Results.Ok(View(await App(brain, name).Configure(request?.Settings ?? [], request?.Connections ?? [])))));

        apps.MapPost("/{name}/rollback", (string name, RollbackRequest request, INeuronProxies brain) => Respond(async () =>
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(request?.Version);
            return Results.Ok(View(await App(brain, name).Rollback(request.Version)));
        }));

        apps.MapPost("/{name}/publish", (string name, INeuronProxies brain) => Respond(async ()
            => Results.Ok(View(await App(brain, name).Publish()))));

        // The one install path over HTTP: a person's own session, never an app's.
        apps.MapPost("/install", (InstallRequest request, [FromServices] AppInstaller installer, CancellationToken ct) => Install(request, installer, ct));

        apps.MapPost("/{name}/update", (string name, UpdateBody? body, INeuronProxies brain, [FromServices] AppInstaller installer, CancellationToken ct)
            => Update(name, body, brain, installer, ct));

        apps.MapGet("/{name}/icon", (string name, INeuronProxies brain, CancellationToken ct) => Respond(async () =>
        {
            var snapshot = await App(brain, name).Read().WaitAsync(ct);
            return Icon(snapshot.Manifest.Icon, snapshot.Files, name);
        }));

        apps.MapGet("/{name}/export", (string name, INeuronProxies brain, CancellationToken ct)
            => Respond(async () => Results.Ok(await App(brain, name).Export().WaitAsync(ct))));

        apps.MapPost("/import", (AppBundle bundle, INeuronProxies brain) => Respond(async () =>
        {
            ArgumentNullException.ThrowIfNull(bundle);
            ArgumentException.ThrowIfNullOrWhiteSpace(bundle.Name);
            return Results.Ok(View(await App(brain, bundle.Name).Import(bundle)));
        }));

        apps.MapDelete("/{name}", (string name, INeuronProxies brain) => Respond(async () =>
        {
            await App(brain, name).Delete();
            return Results.Ok(new { deleted = name });
        }));
    }

    // The one install path over HTTP: a person's own session, never an app's.
    internal static Task<IResult> Install(InstallRequest request, AppInstaller installer, CancellationToken ct) => Respond(async () =>
    {
        if (!IsPerson()) { return Results.Json(new { error = "Only you can install apps." }, statusCode: 403); }
        ArgumentNullException.ThrowIfNull(request?.Ref);
        var outcome = await installer.Install(request, ct);
        return outcome.Status switch
        {
            InstallStatus.Ok => Results.Ok(new { status = "ok", installedName = outcome.InstalledName }),
            InstallStatus.NeedsConsent => Results.Conflict(new { status = "needsConsent", requires = outcome.Requires, error = outcome.Message }),
            _ => Refused(outcome),
        };
    });

    internal static Task<IResult> Update(string name, UpdateBody? body, INeuronProxies brain, AppInstaller installer, CancellationToken ct) => Respond(async () =>
    {
        if (!IsPerson()) { return Results.Json(new { error = "Only you can update apps." }, statusCode: 403); }
        var outcome = await installer.Update(new(name, body?.Consent), ct);
        return outcome.Status switch
        {
            InstallStatus.Ok => Results.Ok(View(await App(brain, outcome.InstalledName!).Read().WaitAsync(ct))),
            InstallStatus.NeedsConsent => Results.Conflict(new { status = "needsConsent", added = outcome.Requires, requires = outcome.Latest, error = outcome.Message }),
            _ => Refused(outcome),
        };
    });

    // A person at the authenticated edge: an app's call through the app proxy is not one.
    internal static bool IsPerson()
        => RuntimeCallerContext.TryGet(out var caller) && caller.Kind == CallerKind.User && caller.StampedBy != TrustedEdge.AppProxy;

    internal static IResult Refused(InstallOutcome outcome) => outcome.Message == AppInstaller.OwnerOnly
        ? Results.Json(new { status = "refused", error = outcome.Message }, statusCode: 403)
        : Results.Conflict(new { status = "refused", error = outcome.Message });

    private static string AppKey(string name) => IApp.Key(BrainScope.CurrentId(), name);

    private static IApp App(INeuronProxies brain, string name) => brain.Get<IApp>(AppKey(name));

    private static async Task ForwardScreenRequest(INeuronProxies brain, string scope, string name, string operation, string input, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Contains('/')) { return; }
        System.Text.Json.JsonElement? args = null;
        if (!string.IsNullOrWhiteSpace(input))
        {
            try
            {
                using var doc = System.Text.Json.JsonDocument.Parse(input);
                args = doc.RootElement.Clone();
            }
            catch (System.Text.Json.JsonException)
            {
                args = System.Text.Json.JsonSerializer.SerializeToElement(new { input });
            }
        }
        try
        {
            var screenId = new DigitalBrain.NeuronId(new DigitalBrain.BrainId(scope), "app." + name, "default");
            var screen = brain.Get<DigitalBrain.IScreen>(screenId);
            _ = await screen.Read().WaitAsync(ct);
            var signal = operation.Contains('.', StringComparison.Ordinal) ? operation : $"{name}.{operation}";
            await screen.Request(signal, args).WaitAsync(ct);
        }
        catch (Exception error) when (error is InvalidOperationException or DigitalBrain.Invocation.BindingException or ArgumentException)
        {
        }
    }

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

    internal static object View(AppSnapshot snapshot, DigitalBrain.UI.View? presentedView = null, DigitalBrain.UI.View? presentedSide = null, long viewRevision = 0L) => new
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
        view = presentedView,
        side = presentedSide,
        viewRevision,
    };

    internal static async Task<IResult> Respond(Func<Task<IResult>> handler)
    {
        try { return await handler(); }
        catch (DigitalBrain.Rules.RuleViolationException error)
        {
            return Results.Conflict(new
            {
                operation = error.Rejected.Operation,
                rules = error.Rejected.Rules,
                messages = error.Rejected.Messages,
            });
        }
        catch (InvalidOperationException error) { return Results.Conflict(new { error = error.Message }); }
        catch (UnauthorizedAccessException error) { return Results.Json(new { error = error.Message }, statusCode: 403); }
        catch (KeyNotFoundException error) { return Results.NotFound(new { error = error.Message }); }
        catch (Exception error) when (error is ArgumentException or InvalidDataException)
        { return Results.UnprocessableEntity(new { error = error.Message }); }
    }
}
