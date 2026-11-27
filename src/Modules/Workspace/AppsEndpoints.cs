using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using IntoChat.OS.Modules.Apps.AppRuns;
using DigitalBrain;
using DigitalBrain.Brains;
using DigitalBrain.Capabilities;
using DigitalBrain.Components;
using DigitalBrain.Kernel;
using DigitalBrain.Log;
using DigitalBrain.Neurons;
using DigitalBrain.Runtime.Orleans;
using DigitalBrain.Runtime.Orleans.Capabilities;
using DigitalBrain.Units;
using IntoChat.OS.Host.Kernel;
using IntoChat.OS.Modules.Flutter;
using IntoChat.OS.Modules.Flutter.Screens;
using IntoChat.OS.Runtime;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using DigitalBrain.Runtime.Http;
using DigitalBrain.Runtime.Http.Surface;
using IntoChat.OS.Host.Logging;
using HostAppRef = IntoChat.OS.Host.Primitives.AppRef;
using HostUnitAlias = IntoChat.OS.Host.Primitives.UnitAlias;

namespace IntoChat.OS.Modules.Apps;

// The whole apps surface, one screenful: create and author are synchronous (one model call);
// implement answers 202 and finishes in the background; sharing is export/import.
internal static class AppsEndpoints
{
    internal sealed record TextRequest(string? Text);
    internal sealed record SettingsRequest(Dictionary<string, string>? Settings, Dictionary<string, string>? Connections);
    internal sealed record RollbackRequest(string Version);
    internal sealed record RequestBody(string? Operation, string? Input);
    internal sealed record UpdateBody(string? Consent);
    internal sealed record SignalDispatchRequest(string Signal, string? PayloadJson);

    public static void Map(IEndpointRouteBuilder endpoints)
    {
        var apps = BrainRouteGroups.Brain(endpoints, "/apps").AddEndpointFilter(ModuleRouteGuard.Guard);
        MapPackages(apps);
        MapUnits(apps);
        MapRuns(apps);
        MapCatalog(apps);
        MapAuthoring(apps);
        MapScreens(apps);
        MapMarketplace(apps);
    }

    // Sandbox packages: the shipped samples and a compile check.
    private static void MapPackages(IEndpointRouteBuilder apps)
    {
        apps.MapGet("/packages", () => Results.Ok(ShippedPackages.All));

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
                return JsonResult(
                    new PackageCompileResponse(false, compiled.Errors, null, null),
                    WorkspaceAppsJsonContext.Default.PackageCompileResponse,
                    StatusCodes.Status422UnprocessableEntity);
            }

            var resolution = await slots.ResolveAsync(BrainScope.CurrentId(), compiled.EnrichedPackage, cancellationToken: ct).ConfigureAwait(false);
            return JsonResult(
                new PackageCompileResponse(true, Array.Empty<string>(), compiled.EnrichedPackage, resolution),
                WorkspaceAppsJsonContext.Default.PackageCompileResponse);
        }));
    }

    // Units: the skills Ino can use, and unit archives out and in.
    private static void MapUnits(IEndpointRouteBuilder apps)
    {
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
            [FromServices] IUnitVerifier verifier,
            CancellationToken ct) => Respond(async () =>
        {
            if (!IsPerson(HttpCaller.Current(httpRequest.HttpContext)))
            {
                return ErrorJson(StatusCodes.Status403Forbidden, "Only you can import app packages.");
            }
            using var ms = new MemoryStream();
            await httpRequest.Body.CopyToAsync(ms, ct).ConfigureAwait(false);
            var aliasOverride = httpRequest.Query.TryGetValue("alias", out var aliasVals) ? aliasVals.FirstOrDefault() : null;
            var studio = brain.Get<ISynapseStudio>(ISynapseStudio.Key(BrainScope.CurrentId()).ToString());
            var result = await AppPackageArchive.ImportZipAsync(
                ms.ToArray(),
                verifier,
                studio,
                aliasOverride,
                ct: ct).ConfigureAwait(false);
            return result.Succeeded
                ? Results.Ok(result)
                : Results.UnprocessableEntity(result);
        }));
    }

    // Runs: a command or a signal sent to an app's program.
    private static void MapRuns(IEndpointRouteBuilder apps)
    {
        apps.MapPost("/{name}/command/{command}", async (
            string name,
            string command,
            HttpRequest httpRequest,
            INeuronProxies brain,
            CancellationToken ct) =>
        {
            using var reader = new StreamReader(httpRequest.Body);
            var payload = await reader.ReadToEndAsync(ct).ConfigureAwait(false);
            // Every command is a run of the app's synapse, so it shows on the runs index.
            var brainId = BrainScope.CurrentId();
            var brainScope = brainId is ['@', .. var sys] ? DigitalBrain.BrainId.System(sys) : new DigitalBrain.BrainId(brainId);
            var run = brain.Get<IAppRun>(new DigitalBrain.NeuronId(brainScope, "app.run", $"commands/{name}/{Guid.NewGuid():N}").FullKey());
            var result = await run.Start(AppTrigger.Command, name, command, string.IsNullOrWhiteSpace(payload) ? null : payload).WaitAsync(ct).ConfigureAwait(false);
            return result.Status == AppRunStatus.Failed
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
    }

    // The catalog itself: list, read, configure, roll back, icon, export, import and delete.
    private static void MapCatalog(IEndpointRouteBuilder apps)
    {
        apps.MapGet("", (INeuronProxies brain, IBrainObserver kernel, [FromServices] MarketplaceView marketplace, CancellationToken ct) => Respond(async () =>
        {
            var listings = await marketplace.List(ct);
            var items = new List<AppCatalogItemResponse>();
            foreach (var (name, snapshot) in await InstalledApps.Read(brain, kernel, BrainScope.CurrentId(), ct))
            {
                items.Add(new AppCatalogItemResponse(
                    Name: name,
                    Title: snapshot.Manifest.Title,
                    Description: snapshot.Manifest.Description,
                    Status: snapshot.Status.ToString(),
                    Version: snapshot.Version,
                    Operations: snapshot.Manifest.Operations.Select(operation => operation.Name).ToArray(),
                    Icon: snapshot.Manifest.Icon,
                    PublishedVersion: snapshot.PublishedVersion,
                    Origin: snapshot.Origin is { } origin
                        ? new AppCatalogOriginResponse(origin.Publisher, origin.Name, origin.Version)
                        : null,
                    Edited: snapshot.Edited,
                    UpdateAvailable: InstalledApps.UpdateAvailable(snapshot, listings),
                    ShippedUpdate: false));
            }
            return JsonResult(items, WorkspaceAppsJsonContext.Default.ListAppCatalogItemResponse);
        }));

        apps.MapGet("/{name}", (string name, INeuronProxies brain, CancellationToken ct) => Respond(async () =>
        {
            var key = AppKey(name);
            var app = App(brain, name);
            var snapshot = await AppBuildStatus.Of(await app.Read().WaitAsync(ct), app, brain.Get<Flows.IFlows>(Flows.IFlows.Key(BrainScope.CurrentId()).ToString()), ct);
            return snapshot.Manifest.Title.Length == 0
                ? throw new KeyNotFoundException($"No app named {name} exists in this brain.")
                : JsonResult(View(snapshot), WorkspaceAppsJsonContext.Default.AppViewResponse);
        }));

        apps.MapPut("/{name}/settings", (string name, SettingsRequest request, INeuronProxies brain) => Respond(async ()
            => JsonResult(View(await App(brain, name).Configure(request?.Settings ?? [], request?.Connections ?? [])), WorkspaceAppsJsonContext.Default.AppViewResponse)));

        apps.MapPost("/{name}/rollback", (string name, RollbackRequest request, INeuronProxies brain) => Respond(async () =>
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(request?.Version);
            return JsonResult(View(await App(brain, name).Rollback(request.Version)), WorkspaceAppsJsonContext.Default.AppViewResponse);
        }));

        apps.MapGet("/{name}/icon", (string name, INeuronProxies brain, CancellationToken ct) => Respond(async () =>
        {
            var snapshot = await App(brain, name).Read().WaitAsync(ct);
            return Icon(snapshot.Manifest.Icon, snapshot.Files, name);
        }));

        apps.MapGet("/{name}/export", (string name, INeuronProxies brain, CancellationToken ct)
            => Respond(async () => Results.Ok(await App(brain, name).Export().WaitAsync(ct))));

        // A raw bundle goes in through the installer too: a person, never over a root app, and with the consent (?consent=,
        // the digest of what the bundle asks for) when it asks for anything.
        apps.MapPost("/import", (AppBundle bundle, string? consent, INeuronProxies brain, [FromServices] AppInstaller installer, CancellationToken ct)
            => Import(bundle, consent, brain, installer, ct));

        // The one uninstall path. Removing an app that is not there stays a success, as it was.
        apps.MapDelete("/{name}", (string name, [FromServices] AppInstaller installer, CancellationToken ct) => Respond(async () =>
        {
            _ = await installer.Remove(name, ct);
            return JsonResult(new AppDeletedResponse(name), WorkspaceAppsJsonContext.Default.AppDeletedResponse);
        }));
    }

    // Authoring: create from a prompt, revise the definition, build.
    private static void MapAuthoring(IEndpointRouteBuilder apps)
    {
        apps.MapPost("", (TextRequest request, [FromServices] ICreator creator, INeuronProxies brain, CancellationToken ct) => Respond(async () =>
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(request?.Text);
            return JsonResult(View(await creator.Author(brain, BrainScope.CurrentId(), null, request.Text, ct)), WorkspaceAppsJsonContext.Default.AppViewResponse);
        }));

        apps.MapPost("/{name}/author", (string name, TextRequest request, [FromServices] ICreator creator, INeuronProxies brain, CancellationToken ct) => Respond(async () =>
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(request?.Text);
            return JsonResult(View(await creator.Author(brain, BrainScope.CurrentId(), name, request.Text, ct)), WorkspaceAppsJsonContext.Default.AppViewResponse);
        }));

        apps.MapPost("/{name}/implement", (string name, TextRequest request, [FromServices] ICreator creator, INeuronProxies brain, CancellationToken ct) => Respond(async () =>
        {
            var snapshot = await creator.Implement(brain, AppKey(name), request?.Text, ct);
            return new AcceptedJsonResult<AppViewResponse>(
                $"/brains/{BrainScope.CurrentId()}/apps/{name}",
                View(snapshot),
                WorkspaceAppsJsonContext.Default.AppViewResponse);
        }));
    }

    // The shell's transport: an app's live view and a request to it.
    private static void MapScreens(IEndpointRouteBuilder apps)
    {
        apps.MapGet("/{name}/view", (
            string name,
            INeuronProxies brain,
            [FromServices] IEnumerable<IHostedAppTypes> hosted,
            [FromServices] IEnumerable<IScreenTargetResolver> resolvers,
            [FromServices] IEnumerable<IHostedNeuronTypes> hostedNeurons,
            CancellationToken ct) => Respond(() => ReadViewAsync(
                name,
                brain,
                BrainScope.CurrentId(),
                ct,
                hosted.FirstOrDefault(),
                resolvers.FirstOrDefault(),
                hostedNeurons.FirstOrDefault())));

        // A request is published for a synapse to react to; nothing waits for the app to answer.
        apps.MapPost("/{name}/request", (
            string name,
            RequestBody? request,
            INeuronProxies brain,
            [FromServices] IEnumerable<IHostedAppTypes> hosted,
            [FromServices] IEnumerable<IScreenTargetResolver> resolvers,
            [FromServices] IEnumerable<IHostedNeuronTypes> hostedNeurons,
            CancellationToken ct) => Respond(async () =>
        {
            var operation = request?.Operation ?? "ask";
            var input = request?.Input ?? "{}";
            var scope = BrainScope.CurrentId();
            var hostedApps = hosted.FirstOrDefault();
            var resolver = resolvers.FirstOrDefault();
            var neurons = hostedNeurons.FirstOrDefault();
            try
            {
                var id = await App(brain, name).Request(operation, input).WaitAsync(ct);
                _ = await ForwardScreenRequest(brain, scope, name, operation, input, hostedApps, resolver, neurons, ct);
                return new AcceptedJsonResult<AppRequestAcceptedResponse>(
                    $"/brains/{scope}/apps/{name}",
                    new AppRequestAcceptedResponse(id.ToString()),
                    WorkspaceAppsJsonContext.Default.AppRequestAcceptedResponse);
            }
            catch (KeyNotFoundException)
            {
                if (await ForwardScreenRequest(brain, scope, name, operation, input, hostedApps, resolver, neurons, ct))
                {
                    return new AcceptedJsonResult<AppRequestAcceptedResponse>(
                        $"/brains/{scope}/apps/{name}",
                        new AppRequestAcceptedResponse(Guid.NewGuid().ToString("N")),
                        WorkspaceAppsJsonContext.Default.AppRequestAcceptedResponse);
                }

                throw;
            }
        }));
    }

    // Marketplace: publish, install and update.
    private static void MapMarketplace(IEndpointRouteBuilder apps)
    {
        apps.MapPost("/{name}/publish", (string name, INeuronProxies brain) => Respond(async ()
            => JsonResult(View(await App(brain, name).Publish()), WorkspaceAppsJsonContext.Default.AppViewResponse)));

        // The one install path over HTTP: a person's own session, never an app's.
        apps.MapPost("/install", (AppInstallRequest request, [FromServices] AppInstaller installer, CancellationToken ct) => Install(request, installer, ct));

        apps.MapPost("/{name}/update", (string name, UpdateBody? body, INeuronProxies brain, [FromServices] AppInstaller installer, CancellationToken ct)
            => Update(name, body, brain, installer, ct));
    }

    internal static Task<IResult> Import(AppBundle bundle, string? consent, INeuronProxies brain, AppInstaller installer, CancellationToken ct) => Respond(async () =>
    {
        var outcome = await installer.Import(bundle, consent, ct);
        return outcome.Status switch
        {
            AppInstallStatus.Ok => JsonResult(View(await App(brain, outcome.InstalledName!).Read().WaitAsync(ct)), WorkspaceAppsJsonContext.Default.AppViewResponse),
            // The digest to send back as ?consent= once the person has seen what the bundle asks for.
            AppInstallStatus.NeedsConsent => JsonResult(
                new AppImportConsentResponse("needsConsent", outcome.Requires, AppConsent.Of(outcome.Requires), outcome.Message),
                WorkspaceAppsJsonContext.Default.AppImportConsentResponse,
                StatusCodes.Status409Conflict),
            _ => Refused(outcome),
        };
    });

    // The one install path over HTTP: a person's own session, never an app's.
    internal static Task<IResult> Install(AppInstallRequest request, AppInstaller installer, CancellationToken ct) => Respond(async () =>
    {
        if (!IsPerson(installer.Caller)) { return ErrorJson(StatusCodes.Status403Forbidden, "Only you can install apps."); }
        ArgumentNullException.ThrowIfNull(request?.Ref);
        var outcome = await installer.Install(request, ct);
        return outcome.Status switch
        {
            AppInstallStatus.Ok => JsonResult(
                new AppInstallOkResponse("ok", outcome.InstalledName),
                WorkspaceAppsJsonContext.Default.AppInstallOkResponse),
            AppInstallStatus.NeedsConsent => JsonResult(
                new AppInstallConsentResponse("needsConsent", outcome.Requires, outcome.Message),
                WorkspaceAppsJsonContext.Default.AppInstallConsentResponse,
                StatusCodes.Status409Conflict),
            _ => Refused(outcome),
        };
    });

    internal static Task<IResult> Update(string name, UpdateBody? body, INeuronProxies brain, AppInstaller installer, CancellationToken ct) => Respond(async () =>
    {
        if (!IsPerson(installer.Caller)) { return ErrorJson(StatusCodes.Status403Forbidden, "Only you can update apps."); }
        var outcome = await installer.Update(new(name, body?.Consent), ct);
        return outcome.Status switch
        {
            AppInstallStatus.Ok => JsonResult(View(await App(brain, outcome.InstalledName!).Read().WaitAsync(ct)), WorkspaceAppsJsonContext.Default.AppViewResponse),
            AppInstallStatus.NeedsConsent => JsonResult(
                new AppUpdateConsentResponse("needsConsent", outcome.Requires, outcome.Latest, outcome.Message),
                WorkspaceAppsJsonContext.Default.AppUpdateConsentResponse,
                StatusCodes.Status409Conflict),
            _ => Refused(outcome),
        };
    });

    // A person at the authenticated edge: an app's call through the app proxy is stamped as the app, so it is not one.
    internal static bool IsPerson(DigitalBrain.Access.Caller? caller) => caller?.Actor is DigitalBrain.Access.Actor.Person;

    internal static IResult Refused(AppInstallOutcome outcome) => outcome.Message == AppInstaller.OwnerOnly
        ? JsonResult(new AppInstallRefusedResponse("refused", outcome.Message), WorkspaceAppsJsonContext.Default.AppInstallRefusedResponse, StatusCodes.Status403Forbidden)
        : JsonResult(new AppInstallRefusedResponse("refused", outcome.Message), WorkspaceAppsJsonContext.Default.AppInstallRefusedResponse, StatusCodes.Status409Conflict);

    private static string AppKey(string name) => HostAppRef.Of(new BrainId(BrainScope.CurrentId()), name).Key;

    private static IApp App(INeuronProxies brain, string name) => brain.Get<IApp>(AppKey(name));

    internal static JsonElement? BuildScreenRequestArgs(string name, string operation, string? input)
    {
        if (string.IsNullOrWhiteSpace(input)) { return null; }
        var scalar = input;
        try
        {
            using var doc = JsonDocument.Parse(input);
            if (doc.RootElement.ValueKind == JsonValueKind.Object)
            {
                return doc.RootElement.Clone();
            }
            if (doc.RootElement.ValueKind == JsonValueKind.Null)
            {
                return null;
            }
            if (doc.RootElement.ValueKind == JsonValueKind.String)
            {
                scalar = doc.RootElement.GetString() ?? string.Empty;
            }
        }
        catch (JsonException error)
        {
            OsConsoleLog.For(typeof(AppsEndpoints)).LogSwallowed(error, "Screen request input is not JSON; forwarding raw scalar.");
        }

        var op = operation.Contains('.', StringComparison.Ordinal)
            ? operation[(operation.IndexOf('.', StringComparison.Ordinal) + 1)..]
            : operation;
        var field = (name.ToLowerInvariant(), op.ToLowerInvariant()) switch
        {
            ("settings", "open" or "navigate") => "section",
            _ => "input",
        };
        return field == "input"
            ? JsonSerializer.SerializeToElement(
                new Dictionary<string, string> { ["input"] = scalar },
                WorkspaceAppsJsonContext.Default.DictionaryStringString)
            : JsonSerializer.SerializeToElement(
                new Dictionary<string, string> { ["input"] = scalar, [field] = scalar },
                WorkspaceAppsJsonContext.Default.DictionaryStringString);
    }

    internal static async Task<IResult> ReadViewAsync(
        string name,
        INeuronProxies brain,
        string scope,
        CancellationToken ct,
        IHostedAppTypes? hosted = null,
        IScreenTargetResolver? resolver = null,
        IHostedNeuronTypes? hostedNeurons = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        var trimmed = name.Trim();
        var brainId = new BrainId(scope);
        var log = OsConsoleLog.For(typeof(AppsEndpoints));

        var resolved = await ScreenViewResolver.ResolveAsync(brainId, trimmed, resolver, hosted, ct, hostedNeurons).ConfigureAwait(false);
        if (resolved is { Kind: ScreenResolutionKind.NotLoaded })
        {
            throw new KeyNotFoundException($"No live view is published for '{name}' in brain '{scope}'.");
        }

        if (resolved is { Kind: ScreenResolutionKind.Unit })
        {
            var unitView = await ScreenViewResolver.ReadUnitViewAsync(brain, brainId, trimmed, log, ct).ConfigureAwait(false);
            if (unitView is not null)
            {
                return Results.Json(ChildKeys.Hide(unitView), ShellUiJsonContext.Default.PublishedView);
            }
        }
        else if (resolved is { Kind: ScreenResolutionKind.Screen, ScreenId: { } screenId })
        {
            if (await ScreenViewResolver.TryReadScreenAsync(brain, screenId, log, "Screen view could not be read.", ct, hostedNeurons).ConfigureAwait(false) is { } published)
            {
                return Results.Json(ChildKeys.Screen(published), ShellUiJsonContext.Default.PublishedView);
            }
        }
        else if (resolved is { Kind: ScreenResolutionKind.View, ScreenId: { } viewId })
        {
            try
            {
                var directView = await brain.Get<IntoChat.OS.Modules.Flutter.Views.IView>(viewId).Read().WaitAsync(ct).ConfigureAwait(false);
                if (directView is not null)
                {
                    return Results.Json(ChildKeys.Hide(directView), ShellUiJsonContext.Default.PublishedView);
                }
            }
            catch (Exception error) when (error is InvalidOperationException or DigitalBrain.Invocation.BindingException or ArgumentException)
            {
                log.LogSwallowed(error, "IView lookup was not available for live view lookup.");
            }
        }

        if ((resolver is null || resolved is not { Kind: ScreenResolutionKind.Screen, IsAgentApp: false })
            && HostUnitAlias.TryParse(trimmed, out _))
        {
            if (await ScreenViewResolver.TryReadUnitReaderAsync(brain, brainId, trimmed, log, "Unit view fallback could not be read.", ct).ConfigureAwait(false) is { } unitFallback)
            {
                return Results.Json(ChildKeys.Hide(unitFallback), ShellUiJsonContext.Default.PublishedView);
            }
        }

        if (HostAppRef.TryOf(brainId, trimmed, out var appRef))
        {
            try
            {
                var fallbackView = await brain.Get<IntoChat.OS.Modules.Flutter.Views.IView>(appRef.ViewKey).Read().WaitAsync(ct).ConfigureAwait(false);
                if (fallbackView is not null)
                {
                    return Results.Json(ChildKeys.Hide(fallbackView), ShellUiJsonContext.Default.PublishedView);
                }
            }
            catch (Exception error) when (error is InvalidOperationException or DigitalBrain.Invocation.BindingException or ArgumentException)
            {
                log.LogSwallowed(error, "IView lookup was not available for live view lookup.");
            }
        }

        throw new KeyNotFoundException($"No live view is published for '{name}' in brain '{scope}'.");
    }

    internal static async Task<bool> ForwardScreenRequest(
        INeuronProxies brain,
        string scope,
        string name,
        string operation,
        string input,
        IHostedAppTypes? hosted,
        IScreenTargetResolver? resolver,
        IHostedNeuronTypes? hostedNeurons,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Contains('/')) { return false; }
        var args = BuildScreenRequestArgs(name, operation, input);
        try
        {
            var resolved = await ScreenViewResolver.ResolveAsync(new BrainId(scope), name.Trim(), resolver, hosted, ct, hostedNeurons).ConfigureAwait(false);
            if (resolved is not { Kind: ScreenResolutionKind.Screen, ScreenId: { } screenId })
            {
                return false;
            }
            var screen = brain.Get<IScreen>(screenId);
            _ = await screen.Read().WaitAsync(ct).ConfigureAwait(false);
            var signal = operation.Contains('.', StringComparison.Ordinal) ? operation : $"{name}.{operation}";
            await screen.Request(signal, args).WaitAsync(ct).ConfigureAwait(false);
            return true;
        }
        catch (Exception error) when (error is InvalidOperationException or DigitalBrain.Invocation.BindingException or ArgumentException or JsonException)
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
        { return ErrorJson(StatusCodes.Status404NotFound, $"{app} has no icon."); }
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

    private sealed class AcceptedJsonResult<T>(string location, T value, JsonTypeInfo<T> typeInfo)
        : IResult, IStatusCodeHttpResult, IValueHttpResult
    {
        public int? StatusCode => StatusCodes.Status202Accepted;
        public object? Value => value;

        public Task ExecuteAsync(HttpContext http)
        {
            http.Response.Headers.Location = location;
            return Results.Json(value, typeInfo, statusCode: StatusCodes.Status202Accepted).ExecuteAsync(http);
        }
    }

    private static IResult JsonResult<T>(T value, JsonTypeInfo<T> typeInfo, int statusCode = StatusCodes.Status200OK)
        => Results.Json(value, typeInfo, statusCode: statusCode);

    private static IResult ErrorJson(int statusCode, string error)
        => Results.Json(new WorkspaceErrorResponse(error), WorkspaceAppsJsonContext.Default.WorkspaceErrorResponse, statusCode: statusCode);

    internal static AppViewResponse View(AppSnapshot snapshot, DigitalBrain.UI.View? presentedView = null, DigitalBrain.UI.View? presentedSide = null, long viewRevision = 0L) => new(
        Name: snapshot.Name,
        Manifest: snapshot.Manifest,
        Spec: snapshot.Spec,
        Files: snapshot.Files,
        Settings: snapshot.Settings,
        Connections: snapshot.Connections,
        Version: snapshot.Version,
        Status: snapshot.Status.ToString(),
        Activity: snapshot.Activity,
        Versions: snapshot.Versions,
        PublishedVersion: snapshot.PublishedVersion,
        Origin: snapshot.Origin,
        Edited: snapshot.Edited,
        View: presentedView,
        Side: presentedSide,
        ViewRevision: viewRevision);

    internal static async Task<IResult> Respond(Func<Task<IResult>> handler)
    {
        try { return await handler(); }
        catch (DigitalBrain.Rules.RuleViolationException error)
        {
            return JsonResult(
                new RuleViolationResponse(error.Rejected.Operation, error.Rejected.Rules, error.Rejected.Messages),
                WorkspaceAppsJsonContext.Default.RuleViolationResponse,
                StatusCodes.Status409Conflict);
        }
        catch (InvalidOperationException error) { return ErrorJson(StatusCodes.Status409Conflict, error.Message); }
        catch (UnauthorizedAccessException error) { return ErrorJson(StatusCodes.Status403Forbidden, error.Message); }
        catch (KeyNotFoundException error) { return ErrorJson(StatusCodes.Status404NotFound, error.Message); }
        catch (Exception error) when (error is ArgumentException or InvalidDataException)
        { return ErrorJson(StatusCodes.Status422UnprocessableEntity, error.Message); }
    }
}
