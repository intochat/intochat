using System.Collections.Concurrent;
using System.Reflection;
using System.Text.Json;
using DigitalBrain;
using DigitalBrain.Runtime.Apps;
using DigitalBrain.Runtime.Orleans.Hosting;
using IntoChat.OS.Framework;
using IntoChat.OS.Modules.AI.Decision;
using IntoChat.OS.Modules.AI.Memory;
using IntoChat.OS.Modules.AI.Tasks;
using IntoChat.OS.Modules.Apps.Signals;
using IntoChat.OS.Modules.Flutter;
using IntoChat.OS.Modules.Flutter.Chat;
using IntoChat.OS.Modules.Flutter.Text;
using IntoChat.OS.Modules.Flutter.Views;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Orleans.Runtime;
using Ui = IntoChat.OS.Modules.Flutter.Views.Ui;

namespace IntoChat.OS.Modules.Apps;

internal sealed class CompiledAppsHost(
    IServiceProvider services,
    InProcessAppHost host,
    DigitalBrain.Spi.IClusterListeners listeners,
    ILogger<CompiledAppsHost> logger) : IStartupTask
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { PropertyNameCaseInsensitive = true };

    private static readonly (string Name, Type AppType, AppBundle Bundle)[] Hosted =
    [
        ("ino", typeof(InoApp), InoApp.Bundle),
        ("settings", typeof(SettingsApp), SettingsApp.Bundle),
    ];

    private static readonly IReadOnlyDictionary<string, (string Name, Type AppType, AppBundle Bundle)> HostedByName =
        Hosted.ToDictionary(item => item.Name, StringComparer.Ordinal);

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

        _ = await listeners.Listen<AppRequested>(nameof(CompiledAppsHost), (publisher, signal, token) =>
        {
            TriggerEnsureAll(publisher.Brain.Value);
            if (!publisher.Key.StartsWith("apps/", StringComparison.Ordinal)) { return Task.CompletedTask; }
            var appName = publisher.Key["apps/".Length..];
            if (appName == "desktop")
            {
                _ = Task.Run(() => EnsureDesktopViewAsync(publisher.Brain.Value), CancellationToken.None);
                return Task.CompletedTask;
            }
            if (!HostedByName.ContainsKey(appName)) { return Task.CompletedTask; }
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
            if (!HostedByName.ContainsKey(appName))
            {
                _ = Task.Run(async () =>
                {
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
        if (brain == "owner") { await EnsureDesktopViewAsync(brain); }
    }

    private async Task EnsureDesktopViewAsync(string brain)
    {
        RequestContext.Remove(CallerContextStamper.RequestContextKey);
        try
        {
            if (await HostCaller.ForBrain(services, brain) is { } caller && caller.BrainId == brain) { CallerContextStamper.Stamp(caller); }
            var view = SiloNeurons.Get<IView>(services, $"{brain}/apps/desktop/view");
            var current = await view.Read();
            if (current?.Root is not null) { return; }

            var initialWindows = new (string WinId, string AppId, string Title)[]
            {
                ("w-1", "files", "Files"),
                ("w-2", "photocraft", "PhotoCraft"),
                ("w-3", "ino", "Ino"),
            };

            foreach (var (winId, appId, _) in initialWindows)
            {
                var viewport = SiloNeurons.Get<IntoChat.OS.Modules.Flutter.Viewport.IViewport>(services, $"{brain}/apps/desktop/window/{winId}");
                var currentVp = await viewport.Read();
                var targetRef = new IntoChat.OS.Modules.Flutter.Workspace.WorkspaceViewRef(appId, $"{brain}/apps/{appId}", "full", "");
                if (currentVp.Target != targetRef) { await viewport.Set(targetRef, currentVp.Revision); }
            }

            UiNode BuildWindowFrame(string winId, string appId, string title)
            {
                UiNode[] chromeButtons =
                [
                    Ui.Button($"focus-{winId}", title, $"focus:{winId}", style: UiStyle.Ghost),
                    Ui.IconButton($"newtab-{winId}", "plus", "New tab", $"newtab:{winId}"),
                    Ui.IconButton($"star-{winId}", "star", "Add to favourites", $"favourite:{winId}"),
                    .. appId == "ino" ? Array.Empty<UiNode>() : [Ui.IconButton($"pin-{winId}", "pin", "Pin", $"pin:{appId}")],
                    Ui.IconButton($"close-{winId}", "x", "Close tab", $"close:{winId}/0"),
                ];
                return Ui.Column($"frame-{winId}",
                [
                    Ui.Row($"chrome-{winId}", chromeButtons, gap: 4, extents: [0, .. chromeButtons.Skip(1).Select(_ => 32.0)]),
                    Ui.Tabs($"tabs-{winId}", [Ui.Tab($"tab-{winId}-0", title, $"tab:{winId}/0", true)]),
                    Ui.Reference($"content-{winId}", new(UIVocabulary.ViewportType, $"{brain}/apps/desktop/window/{winId}")),
                ], gap: 0, extents: [40, 38, 0]);
            }

            var windows = Ui.Column("windows", [Ui.Row("windows-0", [.. initialWindows.Select(w => BuildWindowFrame(w.WinId, w.AppId, w.Title))])], extents: [0]);
            var dock = Ui.Row("dock",
            [
                Ui.Row("apps",
                [
                    Ui.Button("dock-ino", "Ino", "open:ino", style: UiStyle.Primary, icon: "ino"),
                    Ui.Button("dock-files", "Files", "open:files", style: UiStyle.Ghost),
                    Ui.Button("dock-photocraft", "PhotoCraft", "open:photocraft", style: UiStyle.Ghost),
                    Ui.Button("dock-filmcraft", "FilmCraft", "open:filmcraft", style: UiStyle.Ghost),
                    Ui.Button("dock-creator", "Creator", "open:creator", style: UiStyle.Ghost),
                    Ui.Button("dock-gallery", "Gallery", "open:gallery", style: UiStyle.Ghost),
                    Ui.Button("dock-post", "Post", "open:post", style: UiStyle.Ghost),
                    Ui.Button("dock-settings", "Settings", "open:settings", style: UiStyle.Ghost),
                    Ui.IconButton("newtab", "plus", "New tab", "newtab"),
                ], gap: 4),
                Ui.Row("right",
                [
                    Ui.Text("usage", "Usage"),
                    Ui.Button("avatar", "You", "open:circle"),
                    Ui.Button("more-settings", "Settings", "settings"),
                ], gap: 4),
            ], extents: [0, 360]);

            await view.Publish(new UiView(1L, 1L, Ui.Column("desktop", [windows, dock], extents: [0, 68])));
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            logger.LogWarning(error, "Initial desktop view seed for {Brain} did not complete.", brain);
        }
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

    private async Task<DigitalBrain.Apps.Hosting.AppTurns> EnsureAssignedAsync(string brain, string appName, string appKey, CancellationToken ct)
    {
        var spec = HostedByName[appName];
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
        return turns;
    }

    private void WireInoServices(string brain, App instance)
    {
        if (instance is InoApp ino)
        {
            ino.DecisionMaker = services.GetService(typeof(IDecisionMaker)) as IDecisionMaker;
            ino.FilesVfs = _vfsByBrain.GetOrAdd(brain, b => WorkspaceFilesService.ForBrain(services, b));
            ino.KnowledgeStore = services.GetService(typeof(IKnowledgeStore)) as IKnowledgeStore;
            ino.TaskEngine = services.GetService(typeof(ITaskEngine)) as ITaskEngine;
        }
    }

    private async Task HandleAppRequestedAsync(string brain, string appName, AppRequested request, CancellationToken ct)
    {
        RequestContext.Remove(CallerContextStamper.RequestContextKey);
        try
        {
            if (await HostCaller.ForBrain(services, brain) is { } caller && caller.BrainId == brain) { CallerContextStamper.Stamp(caller); }
            var spec = HostedByName[appName];
            var appKey = IApp.Key(brain, appName);
            var gate = _gates.GetOrAdd(appKey, static _ => new SemaphoreSlim(1, 1));
            await gate.WaitAsync(ct);
            try
            {
                var turns = await EnsureAssignedAsync(brain, appName, appKey, ct);
                if (turns.Definition.FindOperation(request.Operation) is { } method)
                {
                    await turns.Request(request.Operation, ParseArgument(method, request.Input), ct);
                }

                await SiloNeurons.Get<IApp>(services, appKey).Progress("idle", $"{spec.Bundle.Manifest.Title}: {request.Operation}");
                if (appName == "ino" && request.Operation == "send" && turns.App is InoApp inoRuntime && inoRuntime.State.Turns is { Length: > 0 } inoTurns)
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
            InoTurn? latestTurn = null;
            var gate = _gates.GetOrAdd(appKey, static _ => new SemaphoreSlim(1, 1));
            await gate.WaitAsync(ct);
            try
            {
                var turns = await EnsureAssignedAsync(brain, "ino", appKey, ct);
                await turns.Request("send", new InoSend(submission.Text), ct);
                await SiloNeurons.Get<IApp>(services, appKey).Progress("idle", "Ino: answered");
                if (turns.App is InoApp ino && ino.State.Turns is { Length: > 0 } inoTurns) { latestTurn = inoTurns[^1]; }
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
        string? studioApp = latestTurn.RouteHint.StartsWith("routed:photocraft.", StringComparison.Ordinal)
            ? "photocraft"
            : latestTurn.RouteHint.StartsWith("routed:filmcraft.", StringComparison.Ordinal)
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

    private static object? ParseArgument(MethodInfo method, string input)
    {
        var parameter = method.GetParameters().FirstOrDefault(p => p.ParameterType != typeof(CancellationToken));
        if (parameter is null) { return null; }
        var targetType = parameter.ParameterType;
        if (targetType == typeof(string)) { return string.IsNullOrEmpty(input) ? null : Uri.UnescapeDataString(input); }
        if (string.IsNullOrWhiteSpace(input)) { return null; }

        var trimmed = input.Trim();
        if (trimmed.StartsWith('{') && trimmed.EndsWith('}')) { return JsonSerializer.Deserialize(trimmed, targetType, Json); }

        var segs = trimmed.Split(':').Select(Uri.UnescapeDataString).ToArray();
        var unescaped = Uri.UnescapeDataString(trimmed);
        var unquoted = unescaped.Length >= 2 && unescaped[0] == '"' && unescaped[^1] == '"' ? unescaped[1..^1] : unescaped;

        if (targetType == typeof(InoOpen)) { return new InoOpen(unquoted); }
        if (targetType == typeof(InoSend)) { return new InoSend(unquoted); }
        if (targetType == typeof(InoConnectDb)) { return new InoConnectDb(unquoted); }
        if (targetType == typeof(InoQueryDb)) { return new InoQueryDb(unquoted); }
        if (targetType == typeof(InoApproveCard))
        {
            var cardId = segs.ElementAtOrDefault(0) ?? unquoted;
            var approved = segs.Length < 2 || !bool.TryParse(segs[1], out var parsedApproved) || parsedApproved;
            return new InoApproveCard(cardId, approved);
        }
        if (targetType == typeof(InoMake)) { return new InoMake(unquoted); }
        if (targetType == typeof(SettingsOpen)) { return new SettingsOpen(unquoted); }
        if (targetType == typeof(SettingsRead)) { return new SettingsRead(); }
        if (targetType == typeof(SettingsNavigate)) { return new SettingsNavigate(segs.ElementAtOrDefault(0), segs.ElementAtOrDefault(1), segs.ElementAtOrDefault(2)); }
        if (targetType == typeof(SettingsSavePreferences))
        {
            return new SettingsSavePreferences(
                segs.ElementAtOrDefault(0), segs.ElementAtOrDefault(1), segs.ElementAtOrDefault(2), segs.ElementAtOrDefault(3), segs.ElementAtOrDefault(4),
                segs.ElementAtOrDefault(5), segs.ElementAtOrDefault(6), segs.ElementAtOrDefault(7), segs.ElementAtOrDefault(8), segs.ElementAtOrDefault(9));
        }
        if (targetType == typeof(SettingsGoBack)) { return new SettingsGoBack(); }
        if (targetType == typeof(SettingsRefreshConnections)) { return new SettingsRefreshConnections(unquoted); }
        if (targetType == typeof(SettingsSideloadList)) { return new SettingsSideloadList(); }
        if (targetType == typeof(SettingsSideloadInstall)) { return new SettingsSideloadInstall(unquoted); }
        if (targetType == typeof(SettingsSideloadAddGit)) { return new SettingsSideloadAddGit(unquoted); }
        return null;
    }
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
