using System.Collections.Concurrent;
using System.Reflection;
using System.Text.Json;
using DigitalBrain;
using DigitalBrain.Runtime.Orleans.Hosting;
using IntoChat.OS.Framework;
using IntoChat.OS.Modules.AI.Memory;
using IntoChat.OS.Modules.Apps.Hosting.InProcess;
using IntoChat.OS.Modules.Apps.Signals;
using IntoChat.OS.Modules.Flutter.Chat;
using IntoChat.OS.Modules.Flutter.Text;
using IntoChat.OS.Modules.Flutter.Views;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Orleans.Runtime;

namespace IntoChat.OS.Modules.Apps;

internal sealed partial class CompiledAppsHost(
    IServiceProvider services,
    InProcessAppHost host,
    DigitalBrain.Spi.IClusterListeners listeners,
    ILogger<CompiledAppsHost> logger) : IStartupTask
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { PropertyNameCaseInsensitive = true };

    private static readonly AppBundle DefaultInoBundle = new(
        "ino",
        "3.5.0",
        new AppManifest(
            Title: "Ino",
            Description: "Talk to your brain, use its capabilities, query databases, and create apps.",
            Operations:
            [
                new("open", "Show Ino."),
                new("send", "Route or answer a message."),
                new("stop", "Stop the running turn."),
                new("smart", "Turn smart mode on or off."),
                new("connect-db", "Connect a PostgreSQL database via connection string."),
                new("query-db", "Execute a read-only SQL query against the connected database."),
                new("new", "Start a new conversation."),
                new("new-conversation", "Alias for new."),
                new("voice", "Begin voice input."),
                new("make", "Apply a sentence to the focused app, or Scratch."),
            ],
            Settings:
            [
                new("Model", "Model catalog id; empty uses the default.", ""),
                new("RouteGate", "Confidence required for a direct route.", "0.8"),
                new("IntendGate", "Seconds to wait for Memory's brief before answering without it.", "8"),
            ],
            Slots: [],
            Icon: "icon.svg"),
        "",
        new Dictionary<string, string>(StringComparer.Ordinal));

    private static readonly AppBundle DefaultSettingsBundle = new(
        "settings",
        "2.1.0",
        new AppManifest(
            Title: "Settings",
            Description: "Your preferences, models, keys, connections and archived chats.",
            Operations:
            [
                new("open", "Show Settings on the tab the input names (general when empty or unknown)."),
                new("read", "Publish the current preferences to {app}/preferences."),
                new("refresh-connections", "Show the brain's connections and the host's connector offers again."),
                new("save-preferences", "Save DisplayName and Theme with the Baseline snapshot from preferences/baseline. Reject stale drafts and preserve invalid drafts."),
                new("reload-preferences", "Discard editable drafts and load the stored preferences."),
                new("reconcile-preferences", "Refresh clean drafts from stored preferences; retain unsaved edits and report conflicts."),
            ],
            Settings: [],
            Slots: []),
        "",
        new Dictionary<string, string>(StringComparer.Ordinal));

    private static readonly AppBundle DefaultDesktopBundle = new(
        "desktop",
        "0.4.0",
        new AppManifest(
            Title: "Desktop",
            Description: "Your apps in windows, with a dock.",
            Operations:
            [
                new("start", "Compose the desktop from the open windows and installed apps."),
                new("layout", "Choose the layout the next connect shows: desktop or classic."),
                new("open", "Open an app's window beside the others, or focus it (input: the app name). Enforces the window cap."),
                new("narrow", "The shell's width hint: true below 900 logical pixels, false otherwise."),
            ],
            Settings: [],
            Slots: [],
            Icon: "icon.svg"),
        "",
        new Dictionary<string, string>(StringComparer.Ordinal));

    private static (string Name, Type AppType, AppBundle Bundle) ResolveHosted(string appName, string typeName, AppBundle fallbackBundle)
    {
        var type = AppDomain.CurrentDomain.GetAssemblies()
            .Select(a => a.GetType(typeName, throwOnError: false))
            .FirstOrDefault(t => t is not null);
        if (type is not null && type.GetProperty("Bundle", BindingFlags.Public | BindingFlags.Static)?.GetValue(null) is AppBundle bundle)
        {
            return (appName, type, bundle);
        }
        return (appName, type ?? typeof(App), fallbackBundle);
    }

    private static readonly (string Name, Type AppType, AppBundle Bundle)[] Hosted =
    [
        ResolveHosted("ino", "IntoChat.OS.Apps.Ino.InoApp", DefaultInoBundle),
        ResolveHosted("settings", "IntoChat.OS.Apps.Settings.SettingsApp", DefaultSettingsBundle),
    ];

    private static readonly IReadOnlyDictionary<string, (string Name, Type AppType, AppBundle Bundle)> HostedByName =
        Hosted.ToDictionary(item => item.Name, StringComparer.Ordinal);

    private static (string Name, Type AppType, AppBundle Bundle) ResolveSpec(string appName) => appName switch
    {
        "ino" => ResolveHosted("ino", "IntoChat.OS.Apps.Ino.InoApp", HostedByName["ino"].Bundle),
        "settings" => ResolveHosted("settings", "IntoChat.OS.Apps.Settings.SettingsApp", HostedByName["settings"].Bundle),
        _ => HostedByName[appName],
    };

    private readonly ConcurrentDictionary<string, IFilesVfsService> _vfsByBrain = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, byte> _ensuredBrains = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _gates = new(StringComparer.Ordinal);

    private void TriggerEnsureAll(string brain)
    {
        if (!string.IsNullOrWhiteSpace(brain) && _ensuredBrains.TryAdd(brain, 0))
        {
            _ = Task.Run(() => EnsureAllAsync(brain), CancellationToken.None);
        }
    }

    public async Task Execute(CancellationToken cancellationToken)
    {
        _ = await listeners.Listen<Activated>(nameof(CompiledAppsHost), (publisher, _, _) =>
        {
            var brain = publisher.Brain.Value;
            _ensuredBrains.TryAdd(brain, 0);
            _ = Task.Run(() => EnsureAllAsync(brain), CancellationToken.None);
            return Task.CompletedTask;
        });

        _ = await listeners.Listen<IntoChat.OS.Modules.Flutter.Workspace.Signals.WorkspaceChanged>(nameof(CompiledAppsHost), (publisher, _, _) =>
        {
            var brain = publisher.Brain.Value;
            TriggerEnsureAll(brain);
            _ = Task.Run(() => EnsureDesktopViewAsync(brain), CancellationToken.None);
            return Task.CompletedTask;
        });

        _ = await listeners.Listen<AppRequested>(nameof(CompiledAppsHost), (publisher, signal, token) =>
        {
            TriggerEnsureAll(publisher.Brain.Value);
            if (!publisher.Key.StartsWith("apps/", StringComparison.Ordinal)) { return Task.CompletedTask; }
            var appName = publisher.Key["apps/".Length..];
            if (appName == "brain") { return Task.CompletedTask; }
            if (appName == "desktop")
            {
                _ = Task.Run(() => EnsureDesktopViewAsync(publisher.Brain.Value), CancellationToken.None);
            }
            _ = Task.Run(() => HandleAppRequestedAsync(publisher.Brain.Value, appName, signal, token), CancellationToken.None);
            return Task.CompletedTask;
        });

        _ = await listeners.Listen<ComposerSubmitted>(nameof(CompiledAppsHost), (publisher, signal, token) =>
        {
            TriggerEnsureAll(publisher.Brain.Value);
            if (publisher.Key != "apps/ino/composer") { return Task.CompletedTask; }
            _ = Task.Run(() => HandleComposerSubmittedAsync(publisher.Brain.Value, signal, token), CancellationToken.None);
            return Task.CompletedTask;
        });

        _ = await listeners.Listen<ViewAction>(nameof(CompiledAppsHost), (publisher, signal, token) =>
        {
            TriggerEnsureAll(publisher.Brain.Value);
            if (!publisher.Key.StartsWith("apps/", StringComparison.Ordinal)) { return Task.CompletedTask; }
            var rest = publisher.Key["apps/".Length..];
            var slash = rest.IndexOf('/', StringComparison.Ordinal);
            if (slash <= 0) { return Task.CompletedTask; }
            var appName = rest[..slash];
            if (appName == "brain") { return Task.CompletedTask; }
            if (appName == "desktop")
            {
                _ = Task.Run(() => HandleDesktopViewActionAsync(publisher.Brain.Value, signal, token), CancellationToken.None);
                return Task.CompletedTask;
            }
            if (!HostedByName.ContainsKey(appName))
            {
                _ = Task.Run(async () =>
                {
                    if (await HostCaller.ForBrain(services, publisher.Brain.Value) is { } caller)
                    {
                        CallerContextStamper.Stamp(caller);
                    }
                    var app = SiloNeurons.Get<IApp>(services, IApp.Key(publisher.Brain.Value, appName));
                    var parts = signal.Action.Split(':', 2, StringSplitOptions.TrimEntries);
                    await app.Request(parts[0], parts.Length > 1 ? parts[1] : string.Empty);
                }, CancellationToken.None);
                return Task.CompletedTask;
            }
            _ = Task.Run(() => HandleViewActionAsync(publisher.Brain.Value, appName, signal, token), CancellationToken.None);
            return Task.CompletedTask;
        });

        TriggerEnsureAll("owner");
        cancellationToken.ThrowIfCancellationRequested();
    }

    private async Task EnsureAllAsync(string brain)
    {
        foreach (var (name, _, _) in Hosted) { await EnsureAppAsync(brain, name); }
        await EnsureDesktopViewAsync(brain);
    }

    private async Task EnsureAppAsync(string brain, string appName)
    {
        RequestContext.Remove(CallerContextStamper.RequestContextKey);
        try
        {
            if (await HostCaller.ForBrain(services, brain) is { } caller && caller.BrainId == brain) { CallerContextStamper.Stamp(caller); }
            var appKey = IApp.Key(brain, appName);
            var gate = _gates.GetOrAdd(appKey, static _ => new SemaphoreSlim(1, 1));
            await gate.WaitAsync();
            try { await EnsureAssignedAsync(brain, appName, appKey, CancellationToken.None); }
            finally { gate.Release(); }
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            logger.LogWarning(error, "Compiled app {App} was not initialized in {Brain}.", appName, brain);
        }
    }

    private async Task<DigitalBrain.Apps.Hosting.AppTurns?> EnsureAssignedAsync(string brain, string appName, string appKey, CancellationToken ct)
    {
        if (HostedByName.ContainsKey(appName))
        {
            var spec = ResolveSpec(appName);
            var app = SiloNeurons.Get<IApp>(services, appKey);
            var snapshot = await app.Read();
            if (string.IsNullOrWhiteSpace(snapshot.Manifest.Title))
            {
                await app.Import(spec.Bundle, new AppOrigin(ShippedApps.Publisher, new AppSource.Shipped(), spec.Name, spec.Bundle.Version));
            }
            else if (snapshot.Manifest.Operations.Length < spec.Bundle.Manifest.Operations.Length)
            {
                await app.Replace(spec.Bundle, new AppOrigin(ShippedApps.Publisher, new AppSource.Shipped(), spec.Name, spec.Bundle.Version));
            }

            var turns = host.TryGetTurns(appKey);
            if (turns is null)
            {
                await host.AssignType(appKey, spec.AppType, new Dictionary<string, string>(StringComparer.Ordinal), ct);
                turns = host.TryGetTurns(appKey)!;
                WireInoServices(brain, turns.App);
            }
            await AttachWatchesAsync(brain, appKey, turns);
            return turns;
        }

        var existingTurns = host.TryGetTurns(appKey);
        if (existingTurns is not null)
        {
            await AttachWatchesAsync(brain, appKey, existingTurns);
            return existingTurns;
        }

        var unhostedSnapshot = await SiloNeurons.Get<IApp>(services, appKey).Read();
        if (unhostedSnapshot.Settings.TryGetValue(InProcessAppRuntime.CompiledTypeKey, out var typeName)
            && !string.IsNullOrWhiteSpace(typeName))
        {
            var type = Type.GetType(typeName, throwOnError: false)
                ?? AppDomain.CurrentDomain.GetAssemblies()
                    .Select(a => a.GetType(typeName, throwOnError: false))
                    .FirstOrDefault(t => t is not null);
            if (type is not null)
            {
                await host.AssignType(appKey, type, new Dictionary<string, string>(unhostedSnapshot.Settings, StringComparer.Ordinal), ct);
                var assignedTurns = host.TryGetTurns(appKey);
                if (assignedTurns is not null)
                {
                    WireInoServices(brain, assignedTurns.App);
                    await AttachWatchesAsync(brain, appKey, assignedTurns);
                }
                return assignedTurns;
            }
        }
        return null;
    }

    // Each assigned app's watch subscriptions, by app key, with the turns they feed: a reassigned app (new turns) gets new ones and
    // the old ones are disposed; a retired app's are disposed on its next ensure.
    private readonly ConcurrentDictionary<string, (DigitalBrain.Apps.Hosting.AppTurns Turns, IAsyncDisposable Watches)> _watches = new(StringComparer.Ordinal);

    internal async Task AttachWatchesAsync(string brain, string appKey, DigitalBrain.Apps.Hosting.AppTurns? turns)
    {
        if (_watches.TryGetValue(appKey, out var attached) && ReferenceEquals(attached.Turns, turns)) { return; }
        if (attached.Watches is not null && _watches.TryRemove(new(appKey, attached)))
        {
            try { await attached.Watches.DisposeAsync(); }
            catch (Exception error) when (error is not OperationCanceledException) { logger.LogWarning(error, "Watches of {App} did not stop cleanly.", appKey); }
        }
        if (turns is null || turns.App.DeclaredWatches().Count == 0) { return; }
        if (services.GetService<IIntoChatOSBrains>() is not { } brains)
        {
            logger.LogWarning("App {App} declares watches but this host has no brain client; its watches are not served.", appKey);
            return;
        }
        var watches = HostedAppWatches.Attach(brains.Open(brain), turns, new AppAddress(brain, appKey),
            (contract, id) => SiloNeurons.Get(services, contract, id),
            (error, watch) => logger.LogWarning(error, "Watch {Watch} of {App} failed.", watch, appKey),
            (watch, attempt, delay) => logger.LogInformation("Watch {Watch} of {App} restarts in {Delay} (attempt {Attempt}).", watch, appKey, delay, attempt));
        if (!_watches.TryAdd(appKey, (turns, watches))) { await watches.DisposeAsync(); }
    }

    // Test seam: ensure one app as the host does on activation and return its turns.
    internal async Task<DigitalBrain.Apps.Hosting.AppTurns?> EnsureForTestAsync(string brain, string appName, CancellationToken ct)
    {
        var turns = await EnsureAssignedAsync(brain, appName, IApp.Key(brain, appName), ct);
        return turns;
    }

    private void WireInoServices(string brain, App instance)
    {
        var type = instance.GetType();
        type.GetProperty("FilesVfs")?.SetValue(instance, _vfsByBrain.GetOrAdd(brain, b => WorkspaceFilesService.ForBrain(services, b)));
    }

    private async Task HandleAppRequestedAsync(string brain, string appName, AppRequested request, CancellationToken ct)
    {
        RequestContext.Remove(CallerContextStamper.RequestContextKey);
        try
        {
            var baseCaller = await HostCaller.ForBrain(services, brain);
            if (baseCaller is { } caller && caller.BrainId == brain) { CallerContextStamper.Stamp(caller); }
            var appKey = IApp.Key(brain, appName);
            var gate = _gates.GetOrAdd(appKey, static _ => new SemaphoreSlim(1, 1));
            string? openedDesktopApp = null;
            await gate.WaitAsync(ct);
            try
            {
                if (appName == "desktop")
                {
                    await EnsureDesktopViewAsync(brain);
                }
                var turns = await EnsureAssignedAsync(brain, appName, appKey, ct);
                if (turns is null) { return; }
                if (turns.Definition.FindOperation(request.Operation) is { } method)
                {
                    await turns.Request(request.Operation, ParseArgument(method, request.Input), ct);
                    var appNeuron = SiloNeurons.Get<IApp>(services, appKey);
                    var currentSnapshot = await appNeuron.Read();
                    using var appScope = baseCaller is not null ? CallerContextStamper.StampScope(HostCaller.AsApp(baseCaller, appKey)) : null;
                    await appNeuron.SaveRuntimeState(
                        JsonSerializer.Serialize(new { request.Operation, request.RequestId }),
                        currentSnapshot.RuntimeStateRevision);
                }
                if (appName == "desktop")
                {
                    if (request.Operation == "open" && !string.IsNullOrWhiteSpace(request.Input))
                    {
                        openedDesktopApp = request.Input.Trim().ToLowerInvariant();
                        await ApplyDesktopWorkspaceChangeAsync(brain, request.RequestId, s => OpenDesktopAppInState(brain, s, openedDesktopApp));
                    }
                    await EnsureDesktopViewAsync(brain);
                }

                var title = HostedByName.TryGetValue(appName, out var spec) ? spec.Bundle.Manifest.Title : appName;
                await SiloNeurons.Get<IApp>(services, appKey).Progress("idle", $"{title}: {request.Operation}");
                if (appName == "ino" && request.Operation == "send" && turns.App is App<InoAppState> inoRuntime && inoRuntime.State.Turns is { Length: > 0 } inoTurns)
                {
                    await ForwardStudioPromptIfRoutedAsync(brain, inoTurns[^1], ct);
                }
                if (appName == "ino" && request.Operation == "open")
                {
                    try { await SiloNeurons.Get<IText>(services, IApp.Key(brain, "desktop") + "/greeted").Set("true"); }
                    catch (Exception error) when (error is not OperationCanceledException)
                    { logger.LogDebug(error, "Could not mark desktop greeted in {Brain}.", brain); }
                }
            }
            finally { gate.Release(); }

            if (openedDesktopApp is not null)
            {
                await EnsureOpenAppViewAsync(brain, openedDesktopApp, ct);
            }
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            logger.LogWarning(error, "Compiled app {App} did not complete {Operation} in {Brain}.", appName, request.Operation, brain);
        }
    }

    private async Task HandleComposerSubmittedAsync(string brain, ComposerSubmitted signal, CancellationToken ct)
    {
        RequestContext.Remove(CallerContextStamper.RequestContextKey);
        try
        {
            if (await HostCaller.ForBrain(services, brain) is { } caller && caller.BrainId == brain) { CallerContextStamper.Stamp(caller); }
            var appKey = IApp.Key(brain, "ino");
            var composer = SiloNeurons.Get<IComposer>(services, appKey + "/composer");
            var composerState = await composer.Read();
            var submission = composerState.Submissions.FirstOrDefault(item => item.Id == signal.RequestId);
            if (submission is null || string.IsNullOrWhiteSpace(submission.Text)) { return; }

            await composer.Settle(signal.RequestId);
            var shortText = submission.Text.Length <= 42 ? submission.Text : submission.Text[..42] + "…";
            await SiloNeurons.Get<IApp>(services, appKey).Progress("working", $"Thinking · Processing '{shortText}'…");
            InoTurn? latestTurn = null;
            var gate = _gates.GetOrAdd(appKey, static _ => new SemaphoreSlim(1, 1));
            await gate.WaitAsync(ct);
            try
            {
                var turns = await EnsureAssignedAsync(brain, "ino", appKey, ct);
                if (turns is not null && turns.Definition.FindOperation("send") is { } sendMethod)
                {
                    await turns.Request("send", ParseArgument(sendMethod, submission.Text), ct);
                }
                await SiloNeurons.Get<IApp>(services, appKey).Progress("idle", "Ino: answered");
                if (turns?.App is App<InoAppState> ino && ino.State.Turns is { Length: > 0 } inoTurns) { latestTurn = inoTurns[^1]; }
            }
            finally { gate.Release(); }

            if (latestTurn is not null) { await ForwardStudioPromptIfRoutedAsync(brain, latestTurn, ct); }
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            logger.LogWarning(error, "Ino composer submission {RequestId} failed in {Brain}.", signal.RequestId, brain);
        }
    }

    private async Task ForwardStudioPromptIfRoutedAsync(string brain, InoTurn latestTurn, CancellationToken ct)
    {
        if (latestTurn.StartsWithStudio("markdraw"))
        {
            var markdrawOp = latestTurn.RouteHint["routed:markdraw.".Length..];
            if (string.IsNullOrWhiteSpace(markdrawOp))
            {
                markdrawOp = "patch-elements";
            }
            await SiloNeurons.Get<IApp>(services, IApp.Key(brain, "markdraw")).Request(markdrawOp, latestTurn.UserMessage);
            return;
        }

        string? studioApp = IntoChat.OS.Modules.ArtCraft.ArtCraftCatalog.All.Select(static a => a.Id).FirstOrDefault(latestTurn.StartsWithStudio);
        studioApp ??= latestTurn.RouteHint.StartsWith("routed:photocraft.", StringComparison.Ordinal)
            ? "photocraft"
            : latestTurn.StartsWithStudio("filmcraft")
                ? "filmcraft"
                : null;
        if (studioApp is null) { return; }
        if (HostedByName.ContainsKey(studioApp))
        {
            await HandleAppRequestedAsync(brain, studioApp, new AppRequested("edit", latestTurn.UserMessage, Guid.NewGuid()), ct);
        }
        else
        {
            await SiloNeurons.Get<IApp>(services, IApp.Key(brain, studioApp)).Request("edit", latestTurn.UserMessage);
        }
    }

    private Task HandleViewActionAsync(string brain, string appName, ViewAction signal, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(signal.Action)) { return Task.CompletedTask; }
        var parts = signal.Action.Split(':', 2, StringSplitOptions.TrimEntries);
        return HandleAppRequestedAsync(brain, appName, new AppRequested(parts[0], parts.Length > 1 ? parts[1] : string.Empty, signal.RequestId), ct);
    }

    internal static object? ParseArgument(MethodInfo method, string input)
    {
        var parameter = method.GetParameters().FirstOrDefault(p => p.ParameterType != typeof(CancellationToken));
        if (parameter is null) { return null; }
        var targetType = parameter.ParameterType;
        if (targetType == typeof(string)) { return string.IsNullOrEmpty(input) ? null : Uri.UnescapeDataString(input); }
        if (string.IsNullOrWhiteSpace(input))
        {
            var emptyCtor = targetType.GetConstructor(Type.EmptyTypes);
            if (emptyCtor is not null) { return emptyCtor.Invoke(null); }
            var defaultCtor = targetType.GetConstructors().FirstOrDefault(c => c.GetParameters().All(p => p.HasDefaultValue));
            return defaultCtor?.Invoke([.. defaultCtor.GetParameters().Select(p => p.DefaultValue)]);
        }

        var trimmed = input.Trim();
        if (trimmed.StartsWith('{') && trimmed.EndsWith('}')) { return JsonSerializer.Deserialize(trimmed, targetType, Json); }

        var segs = trimmed.Split(':').Select(Uri.UnescapeDataString).ToArray();
        var unescaped = Uri.UnescapeDataString(trimmed);
        var unquoted = unescaped.Length >= 2 && unescaped[0] == '"' && unescaped[^1] == '"' ? unescaped[1..^1] : unescaped;

        var ctor = targetType.GetConstructors().OrderByDescending(c => c.GetParameters().Length).FirstOrDefault();
        if (ctor is null) { return null; }

        var ctorParams = ctor.GetParameters();
        if (ctorParams.Length == 0) { return ctor.Invoke(null); }

        if (ctorParams.Length == 1 || ShouldBindEntireInputToFirstParameter(ctorParams, segs))
        {
            var singleArgs = new object?[ctorParams.Length];
            singleArgs[0] = ConvertSegment(unquoted, ctorParams[0]);
            for (var i = 1; i < ctorParams.Length; i++)
            {
                singleArgs[i] = ctorParams[i].HasDefaultValue ? ctorParams[i].DefaultValue : GetDefaultForType(ctorParams[i].ParameterType);
            }
            return ctor.Invoke(singleArgs);
        }

        var args = new object?[ctorParams.Length];
        for (var i = 0; i < ctorParams.Length; i++)
        {
            args[i] = i < segs.Length
                ? ConvertSegment(segs[i], ctorParams[i])
                : (ctorParams[i].HasDefaultValue ? ctorParams[i].DefaultValue : GetDefaultForType(ctorParams[i].ParameterType));
        }
        return ctor.Invoke(args);
    }

    private static bool ShouldBindEntireInputToFirstParameter(ParameterInfo[] ctorParams, string[] segs)
    {
        if (segs.Length <= 1)
        {
            return true;
        }

        if (!ctorParams.Skip(1).All(p => p.HasDefaultValue))
        {
            return false;
        }

        var firstParamName = ctorParams[0].Name ?? string.Empty;
        if (firstParamName.Equals("Text", StringComparison.OrdinalIgnoreCase)
            || firstParamName.Equals("Prompt", StringComparison.OrdinalIgnoreCase)
            || firstParamName.Equals("Query", StringComparison.OrdinalIgnoreCase)
            || firstParamName.Equals("Sql", StringComparison.OrdinalIgnoreCase)
            || firstParamName.Equals("Content", StringComparison.OrdinalIgnoreCase)
            || firstParamName.Equals("ConnectionString", StringComparison.OrdinalIgnoreCase)
            || firstParamName.Equals("Sentence", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return false;
    }

    private static object? ConvertSegment(string raw, ParameterInfo param)
    {
        var underlying = Nullable.GetUnderlyingType(param.ParameterType) ?? param.ParameterType;
        if (underlying == typeof(string))
        {
            return raw;
        }
        if (underlying == typeof(bool))
        {
            return bool.TryParse(raw, out var b)
                ? b
                : (param.HasDefaultValue ? param.DefaultValue : true);
        }
        if (underlying == typeof(int))
        {
            return int.TryParse(raw, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var n)
                ? n
                : (param.HasDefaultValue ? param.DefaultValue : 0);
        }
        if (underlying == typeof(long))
        {
            return long.TryParse(raw, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var l)
                ? l
                : (param.HasDefaultValue ? param.DefaultValue : 0L);
        }
        if (underlying == typeof(double))
        {
            return double.TryParse(raw, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var d)
                ? d
                : (param.HasDefaultValue ? param.DefaultValue : 0.0);
        }
        if (underlying.IsEnum && Enum.TryParse(underlying, raw, ignoreCase: true, out var enumVal))
        {
            return enumVal;
        }

        return param.HasDefaultValue ? param.DefaultValue : GetDefaultForType(param.ParameterType);
    }

    private static object? GetDefaultForType(Type type) =>
        type.IsValueType ? Activator.CreateInstance(type) : null;
}

file static class InoTurnRoutingExtensions
{
    public static bool StartsWithStudio(this InoTurn turn, string studio)
        => turn.RouteHint.StartsWith($"routed:{studio}.", StringComparison.Ordinal);
}

internal sealed class CompiledAppsLifecycle(IServiceProvider services) : ILifecycleParticipant<ISiloLifecycle>
{
    public void Participate(ISiloLifecycle lifecycle)
    {
        ArgumentNullException.ThrowIfNull(lifecycle);
        lifecycle.Subscribe<CompiledAppsLifecycle>(
            ServiceLifecycleStage.Active,
            cancellationToken => ActivatorUtilities.CreateInstance<CompiledAppsHost>(services).Execute(cancellationToken));
    }
}
