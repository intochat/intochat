using DigitalBrain.Brains;
using DigitalBrain.Capabilities;
using DigitalBrain.Kernel;
using DigitalBrain.Log;
using DigitalBrain.Neurons;
using DigitalBrain.Runtime.Orleans;
using DigitalBrain.Runtime.Orleans.Capabilities;
using DigitalBrain.Units;
using IntoChat.OS.Connections;
using IntoChat.OS.Framework;
using IntoChat.OS.Framework.Logging;
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
    private static readonly System.Text.Json.JsonSerializerOptions ViewJsonOptions = new(System.Text.Json.JsonSerializerDefaults.Web)
    {
        MaxDepth = 128,
    };

    public sealed record TextRequest(string? Text);
    public sealed record SettingsRequest(Dictionary<string, string>? Settings, Dictionary<string, string>? Connections);
    public sealed record RollbackRequest(string Version);
    public sealed record RequestBody(string? Operation, string? Input);
    public sealed record UpdateBody(string? Consent);
    public sealed record SignalDispatchRequest(string Signal, string? PayloadJson);

    public static void Map(IEndpointRouteBuilder endpoints)
    {
        var apps = BrainRoutes.Group(endpoints, "/apps").AddEndpointFilter(ModuleRouteGuard.Guard);

        apps.MapGet("/packages", () => Results.Ok(ShippedPackages.All));

        apps.MapGet("/skills", (INeuronProxies brain, CancellationToken ct) => Respond(async () =>
        {
            var studio = brain.Get<ISynapseStudio>(ISynapseStudio.Key(BrainScope.CurrentId()).ToString());
            var skills = await SkillDiscovery.DiscoverAsync(studio, ct: ct).ConfigureAwait(false);
            return Results.Ok(skills);
        }));

        apps.MapGet("/units/{alias}/export-zip", (string alias, INeuronProxies brain, CancellationToken ct) => Respond(async () =>
        {
            var studio = brain.Get<ISynapseStudio>(ISynapseStudio.Key(BrainScope.CurrentId()).ToString());
            var bytes = await AppPackageArchive.ExportZipAsync(alias, studio, ct).ConfigureAwait(false);
            return Results.File(bytes, "application/zip", $"{alias}.zip");
        }));

        apps.MapPost("/units/import-zip", (
            HttpRequest httpRequest,
            INeuronProxies brain,
            IServiceProvider services,
            CancellationToken ct) => Respond(async () =>
        {
            if (!IsPerson()) { return Results.Json(new { error = "Only you can import app packages." }, statusCode: 403); }
            using var ms = new MemoryStream();
            await httpRequest.Body.CopyToAsync(ms, ct).ConfigureAwait(false);
            var aliasOverride = httpRequest.Query.TryGetValue("alias", out var aliasVals) ? aliasVals.FirstOrDefault() : null;
            var studio = brain.Get<ISynapseStudio>(ISynapseStudio.Key(BrainScope.CurrentId()).ToString());
            var result = await AppPackageArchive.ImportZipAsync(
                ms.ToArray(),
                studio,
                aliasOverride,
                services: services,
                ct: ct).ConfigureAwait(false);
            return result.Succeeded
                ? Results.Ok(result)
                : Results.UnprocessableEntity(result);
        }));

        apps.MapPost("/packages/compile", (
            AppPackage package,
            [FromServices] AppPackageCompiler compiler,
            [FromServices] SlotResolver slots,
            CancellationToken ct) => Respond(async () =>
        {
            ArgumentNullException.ThrowIfNull(package);
            var compiled = compiler.Compile(package);
            if (!compiled.Succeeded || compiled.EnrichedPackage is null)
            {
                return Results.UnprocessableEntity(new
                {
                    succeeded = false,
                    errors = compiled.Errors,
                    package = (AppPackage?)null,
                    slots = (SlotResolutionResult?)null,
                });
            }

            var resolution = await slots.ResolveAsync(BrainScope.CurrentId(), compiled.EnrichedPackage, cancellationToken: ct).ConfigureAwait(false);
            return Results.Ok(new
            {
                succeeded = true,
                errors = Array.Empty<string>(),
                package = compiled.EnrichedPackage,
                slots = resolution,
            });
        }));

        apps.MapPost("/{name}/command/{command}", async (
            string name,
            string command,
            HttpRequest httpRequest,
            [FromServices] ISandboxRuntimeBridge bridge,
            CancellationToken ct) =>
        {
            using var reader = new StreamReader(httpRequest.Body);
            var payload = await reader.ReadToEndAsync(ct).ConfigureAwait(false);
            var result = await bridge.DispatchCommandAsync(
                BrainScope.CurrentId(),
                name,
                command,
                string.IsNullOrWhiteSpace(payload) ? null : payload,
                ct).ConfigureAwait(false);
            return result.Error is { Length: > 0 }
                ? Results.Conflict(result)
                : Results.Ok(result);
        });

        apps.MapPost("/{name}/signal", (
            string name,
            SignalDispatchRequest request,
            [FromServices] ISandboxRuntimeBridge bridge,
            CancellationToken ct) => Respond(async () =>
        {
            ArgumentNullException.ThrowIfNull(request);
            ArgumentException.ThrowIfNullOrWhiteSpace(request.Signal);
            var result = await bridge.DispatchSignalAsync(
                BrainScope.CurrentId(),
                name,
                request.Signal,
                string.IsNullOrWhiteSpace(request.PayloadJson) ? "{}" : request.PayloadJson,
                ct).ConfigureAwait(false);
            return result.Error is { Length: > 0 }
                ? Results.Conflict(result)
                : Results.Ok(result);
        }));

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

        apps.MapGet("/{name}/view", (string name, INeuronProxies brain, CancellationToken ct) => Respond(async () =>
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(name);
            var scope = BrainScope.CurrentId();
            var published = await ReadLiveAppOrUnitViewAsync(brain, scope, name.Trim(), ct).ConfigureAwait(false);
            return published is null
                ? throw new KeyNotFoundException($"No live view is published for '{name}' in brain '{scope}'.")
                : Results.Json(published, ViewJsonOptions);
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
            try
            {
                var id = await App(brain, name).Request(operation, input).WaitAsync(ct);
                _ = await ForwardScreenRequest(brain, scope, name, operation, input, ct);
                return Results.Accepted($"/brains/{scope}/apps/{name}", new { requestId = id });
            }
            catch (KeyNotFoundException)
            {
                if (await ForwardScreenRequest(brain, scope, name, operation, input, ct))
                {
                    return Results.Accepted($"/brains/{scope}/apps/{name}", new { requestId = Guid.NewGuid().ToString("N") });
                }

                throw;
            }
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

    private static string AppKey(string name) => IntoChat.OS.Primitives.AppRef.Of(new DigitalBrain.BrainId(BrainScope.CurrentId()), name).Key;

    private static IApp App(INeuronProxies brain, string name) => brain.Get<IApp>(AppKey(name));

    internal static System.Text.Json.JsonElement? BuildScreenRequestArgs(string name, string operation, string? input)
    {
        if (string.IsNullOrWhiteSpace(input)) { return null; }
        var scalar = input;
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(input);
            if (doc.RootElement.ValueKind == System.Text.Json.JsonValueKind.Object)
            {
                return doc.RootElement.Clone();
            }
            if (doc.RootElement.ValueKind == System.Text.Json.JsonValueKind.Null)
            {
                return null;
            }
            if (doc.RootElement.ValueKind == System.Text.Json.JsonValueKind.String)
            {
                scalar = doc.RootElement.GetString() ?? string.Empty;
            }
        }
        catch (System.Text.Json.JsonException error)
        {
            OsConsoleLog.For(typeof(AppsEndpoints)).LogSwallowed(error, "Screen request input is not JSON; forwarding raw scalar.");
        }

        var op = operation.Contains('.', StringComparison.Ordinal)
            ? operation[(operation.IndexOf('.', StringComparison.Ordinal) + 1)..]
            : operation;
        var field = (name.ToLowerInvariant(), op.ToLowerInvariant()) switch
        {
            ("desktop", "narrow") => "narrow",
            ("desktop", "open" or "close-app") => "app",
            ("desktop", "close" or "tab") => "target",
            ("desktop", "focus" or "newtab" or "favourite") => "windowId",
            ("desktop", "pin" or "unpin") => "appId",
            ("desktop", "widget-open" or "widget-close") => "widget",
            ("settings", "open" or "navigate") => "section",
            _ => "input",
        };
        return field == "input"
            ? System.Text.Json.JsonSerializer.SerializeToElement(new Dictionary<string, string> { ["input"] = scalar })
            : System.Text.Json.JsonSerializer.SerializeToElement(new Dictionary<string, string> { ["input"] = scalar, [field] = scalar });
    }

    private static async Task<PublishedView?> ReadLiveAppOrUnitViewAsync(
        INeuronProxies brain,
        string scope,
        string name,
        CancellationToken ct)
    {
        var brainId = new DigitalBrain.BrainId(scope);
        if (IntoChat.OS.Primitives.UnitAlias.TryParse(name, out var unit))
        {
            try
            {
                var unitView = await brain.Get<IViewReader>(unit.StateNeuron(brainId)).Read(name).WaitAsync(ct).ConfigureAwait(false);
                if (unitView is not null)
                {
                    return unitView;
                }
            }
            catch (Exception error) when (error is InvalidOperationException or DigitalBrain.Invocation.BindingException or ArgumentException)
            {
                OsConsoleLog.For(typeof(AppsEndpoints)).LogSwallowed(error, "Unit view reader was not available for live view lookup.");
            }
        }

        foreach (var candidateType in new[] { "app." + name, name })
        {
            try
            {
                var screenId = candidateType.StartsWith("app.", StringComparison.Ordinal) && IntoChat.OS.Primitives.UnitAlias.TryParse(name, out var screenUnit)
                    ? screenUnit.ScreenNeuron(brainId)
                    : new DigitalBrain.NeuronId(brainId, candidateType, "default");
                var screenView = await brain.Get<DigitalBrain.IScreen>(screenId).Read().WaitAsync(ct).ConfigureAwait(false);
                if (screenView is not null)
                {
                    return screenView;
                }
            }
            catch (Exception error) when (error is InvalidOperationException or DigitalBrain.Invocation.BindingException or ArgumentException)
            {
                OsConsoleLog.For(typeof(AppsEndpoints)).LogSwallowed(error, "Screen view reader was not available for live view lookup.");
            }
        }

        try
        {
            var published = await brain.Get<IntoChat.OS.Modules.Flutter.Views.IView>(IntoChat.OS.Primitives.AppRef.Of(brainId, name).ViewKey).Read().WaitAsync(ct).ConfigureAwait(false);
            if (published is not null)
            {
                return published;
            }
        }
        catch (Exception error) when (error is InvalidOperationException or DigitalBrain.Invocation.BindingException or ArgumentException)
        {
            OsConsoleLog.For(typeof(AppsEndpoints)).LogSwallowed(error, "IView lookup was not available for live view lookup.");
        }

        return null;
    }

    private static async Task<bool> ForwardScreenRequest(INeuronProxies brain, string scope, string name, string operation, string input, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Contains('/')) { return false; }
        var args = BuildScreenRequestArgs(name, operation, input);
        try
        {
            var screenId = new DigitalBrain.NeuronId(new DigitalBrain.BrainId(scope), "app." + name, "default");
            var screen = brain.Get<DigitalBrain.IScreen>(screenId);
            _ = await screen.Read().WaitAsync(ct);
            var signal = operation.Contains('.', StringComparison.Ordinal) ? operation : $"{name}.{operation}";
            await screen.Request(signal, args).WaitAsync(ct);
            return true;
        }
        catch (Exception error) when (error is InvalidOperationException or DigitalBrain.Invocation.BindingException or ArgumentException or System.Text.Json.JsonException)
        {
            OsConsoleLog.For(typeof(AppsEndpoints)).LogSwallowed(error, "Screen request forwarding failed.");
            return false;
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
