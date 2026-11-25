/*
# Desktop
Role: Shell
Shipped: true
Operations: start, layout, open, narrow, auto-arrange, pin, close, close-app, focus, tab, newtab, unpin, favourite, widget-open, widget-close, settings

The embedded folder scripts publish the Flutter desktop. These operations present Smart Desktop state, Whiteboard sticky-note app pinning, auto-arrange, and 3D BrainScene telemetry.
*/
using IntoChat.OS.Host.Apps;
using System.Reflection;
using DigitalBrain;
using DigitalBrain.Components;
using DigitalBrain.Kernel;
using DigitalBrain.Log;
using DigitalBrain.Neurons;
using DigitalBrain.Rules;
using DigitalBrain.UI;
using IntoChat.OS.Modules.Apps;
using IntoChat.OS.Modules.Apps.Signals;
using IntoChat.OS.Modules.Flutter;
using IntoChat.OS.Modules.Flutter.Viewport;
using IntoChat.OS.Modules.Flutter.Workspace;
using IntoChat.OS.Modules.Flutter.Workspace.Signals;
using IntoChat.OS.UI;
using IntoChat.OS.Modules.UI.Controls;
using Microsoft.Extensions.Logging;
using static IntoChat.OS.UI.Kit;

namespace IntoChat.OS.Apps.Desktop;

[Signal("desktop.start")]
public sealed record DesktopStart : Signal;

[Signal("desktop.layout")]
public sealed record DesktopLayoutChoice : Signal;

[Signal("desktop.open")]
public sealed record DesktopOpen(string? App = null, string? View = null) : Signal;

[Signal("desktop.close")]
public sealed record DesktopClose(string? Target = null) : Signal;

[Signal("desktop.close-app")]
public sealed record DesktopCloseApp(string? App = null) : Signal;

[Signal("desktop.focus")]
public sealed record DesktopFocus(string? WindowId = null) : Signal;

[Signal("desktop.tab")]
public sealed record DesktopTab(string? Target = null) : Signal;

[Signal("desktop.newtab")]
public sealed record DesktopNewTab(string? WindowId = null) : Signal;

[Signal("desktop.narrow")]
public sealed record DesktopNarrow(string? Narrow = null) : Signal;

[Signal("desktop.auto-arrange")]
public sealed record DesktopAutoArrange : Signal;

[Signal("desktop.pin")]
public sealed record DesktopPinApp(string? AppId = null, bool Pinned = true) : Signal;

[Signal("desktop.unpin")]
public sealed record DesktopUnpinApp(string? AppId = null) : Signal;

[Signal("desktop.favourite")]
public sealed record DesktopFavourite(string? WindowId = null) : Signal;

[Signal("desktop.widget-open")]
public sealed record DesktopWidgetOpen(string? Widget = null) : Signal;

[Signal("desktop.widget-close")]
public sealed record DesktopWidgetClose(string? Widget = null) : Signal;

[Signal("desktop.settings")]
public sealed record DesktopSettings : Signal;

[Neuron("app.desktop")]
[Screen("app.desktop", Title = "Desktop", Icon = Icons.LayoutGrid)]
public sealed partial class DesktopApp : AggregateScreen<DesktopAppState>
{
    private static readonly ILogger ConsoleLog = IntoChat.OS.Host.Logging.OsConsoleLog.For<DesktopApp>();
    private ILogger<DesktopApp>? InjectedLogger { get; }
    private Rejected? RecordedRejection { get; set; }

    public DesktopApp() : this(null) { }

    [Microsoft.Extensions.DependencyInjection.ActivatorUtilitiesConstructor]
    public DesktopApp(ILogger<DesktopApp>? logger = null) => InjectedLogger = logger;

    private ILogger Log => InjectedLogger ?? ConsoleLog;

    public const string AppName = "desktop";

    public static readonly HashSet<string> DesktopWindowVerbs = new(StringComparer.Ordinal)
    { "close", "close-app", "focus", "tab", "newtab", "pin", "unpin", "favourite", "widget-open", "widget-close", "settings" };

    public static AppBundle Bundle => AppBundle.FromScreen<DesktopApp>(
        "desktop",
        "0.4.0",
        description: "Your apps in windows, with a dock.",
        operations:
        [
            new("start", "Compose the desktop from the open windows and installed apps."),
            new("layout", "Choose the layout the next connect shows: desktop or classic."),
            new("open", "Open an app's window beside the others, or focus it (input: the app name). Enforces the window cap."),
            new("narrow", "The shell's width hint: true below 900 logical pixels, false otherwise."),
            new("auto-arrange", "Arrange the open windows."),
            new("focus", "Focus the window named by Input."),
            new("tab", "Activate the tab {window}/{index} named by Input."),
            new("newtab", "Open a new tab, in the window named by Input when given."),
            new("close", "Close the tab {window}/{index} named by Input."),
            new("close-app", "Close the app named by Input's windows."),
            new("pin", "Pin the app named by Input to the dock."),
            new("unpin", "Unpin the app named by Input from the dock."),
            new("favourite", "Toggle the favourite of the window named by Input's active tab."),
            new("widget-open", "Open the widget named by Input."),
            new("widget-close", "Close the widget named by Input."),
            new("settings", "Open the settings."),
        ]);

    [Slot(Key = IDesktopRecords.Key, Required = false)] private partial IDesktopRecords? Records { get; }
    [Slot(Key = IInstalledApps.DefaultKey, Required = false)] private partial IInstalledApps? InstalledApps { get; }
    [Slot(Key = "apps/ino", Required = false)] private partial IApp? InoApp { get; }
    [Slot(Key = "app.ino", Required = false)] private partial IScreen? InoScreen { get; }
    [Slot(Required = false)] private partial IUserSettings? UserSettings { get; }
    [Slot(Required = false)] private partial IntoChat.OS.Modules.Apps.Debugger.IDebuggerReads? DebuggerReads { get; }
    [Slot(Required = false)] private partial ISynapseStudio? SynapseStudio { get; }
    [Slot(Key = "main", Required = false)] private partial IWorkspace? Workspace { get; }
    [Slot(Required = false)] private partial DigitalBrain.Components.SlotCollection<IApp> Apps { get; }
    [Slot] private partial DigitalBrain.Components.SlotCollection<IScreen> Screens { get; }
    [Slot(Required = false)] private partial DigitalBrain.Components.SlotCollection<IViewport> Viewports { get; }

    private static AppTile[] ResolveTiles(DesktopAppState state)
    {
        var list = new List<AppTile>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (state.Tiles is { Length: > 0 } liveTiles)
        {
            foreach (var tile in liveTiles)
            {
                if (tile is not null && !string.IsNullOrWhiteSpace(tile.Id) && !ShippedApps.Services.Contains(tile.Id) && seen.Add(tile.Id))
                {
                    list.Add(tile);
                }
            }
        }
        else
        {
            foreach (var manifest in ComponentManifests.All)
            {
                if (manifest.Face is null || !manifest.Id.Value.StartsWith("app.", StringComparison.Ordinal))
                {
                    continue;
                }

                var appId = manifest.Id.Value["app.".Length..];
                if (string.IsNullOrWhiteSpace(appId) || ShippedApps.Services.Contains(appId) || !seen.Add(appId))
                {
                    continue;
                }

                var face = manifest.Face;
                list.Add(new AppTile(
                    Id: appId,
                    Title: string.IsNullOrWhiteSpace(face.Title) ? HumanizeTitle(appId) : face.Title,
                    Icon: string.IsNullOrWhiteSpace(face.Icon) ? null : face.Icon,
                    Category: face.Category,
                    Dock: face.Dock,
                    Launch: face.Maximized ? AppLaunch.Maximized : AppLaunch.Windowed,
                    Kind: "compiled",
                    Description: string.IsNullOrWhiteSpace(face.Description) ? null : face.Description));
            }
        }

        foreach (var unitAlias in state.DeployedUnits ?? [])
        {
            if (string.IsNullOrWhiteSpace(unitAlias) || ShippedApps.Services.Contains(unitAlias) || !seen.Add(unitAlias))
            {
                continue;
            }

            list.Add(new AppTile(
                Id: unitAlias,
                Title: HumanizeTitle(unitAlias),
                Icon: "zap",
                Category: "Micro-App",
                Dock: null,
                Launch: AppLaunch.Windowed,
                Kind: "unit"));
        }

        return [.. list
            .OrderBy(static t => t.Dock ?? int.MaxValue)
            .ThenBy(static t => t.Title, StringComparer.OrdinalIgnoreCase)
            .ThenBy(static t => t.Id, StringComparer.OrdinalIgnoreCase)];
    }

    private static string HumanizeTitle(string appId)
        => string.IsNullOrWhiteSpace(appId) ? "App" : char.ToUpperInvariant(appId[0]) + appId[1..];

    private string? TryCurrentBrain()
    {
        try
        {
            var val = Id.Brain.Value;
            return string.IsNullOrEmpty(val) ? null : val;
        }
        catch (InvalidOperationException ex)
        {
            Log.LogSwallowed(ex, "Id.Brain is unassigned in standalone tests; keeping unattached state.");
            return null;
        }
    }

    protected override async Task OnActivate(CancellationToken cancellationToken)
    {
        AppActivationScope.Notify(this);
        await base.OnActivate(cancellationToken);
        try { await Follow<IWorkspace, WorkspaceChanged>("main"); }
        catch (Exception ex) { Log.LogSwallowed(ex, "Workspace follow is optional when running in standalone mode."); }
        try { await Follow<ISynapseStudio, SynapseStudioChanged>("ino/synapses"); }
        catch (Exception ex) when (ex is not OperationCanceledException) { Log.LogSwallowed(ex, "SynapseStudio follow is optional when running in standalone mode."); }
        try { await Context.Wires.EnsureBrainWide([typeof(DigitalBrain.Confirmation.ConfirmationResolved), typeof(AppChanged)]); }
        catch (Exception ex) when (ex is not OperationCanceledException) { Log.LogSwallowed(ex, "Brain-wide signal subscriptions are optional when running in standalone mode."); }
        // Every window's tab strip is a child keyed by the window id. Window ids are w-1 .. w-MaxWindows (the lowest free number,
        // at most MaxWindows windows), so wiring those, and any other id the stored windows hold, covers every strip drawn.
        var windowIds = Enumerable.Range(1, DesktopAppState.MaxWindows).Select(static n => $"w-{n}")
            .Concat((State.Windows ?? []).Select(static w => w.Id).Where(HasTabStrip)).Distinct(StringComparer.Ordinal);
        foreach (var windowId in windowIds)
        {
            await FollowChild<ITabStripControl, DigitalBrain.UI.Commands.SelectionChanged>(WindowTabsKey(windowId));
        }
        await SyncSmartDesktopAsync(State.LayoutMode, State.IsNarrow);
    }

    /// <summary>The child key of window <paramref name="windowId"/>'s tab strip.</summary>
    public static string WindowTabsKey(string windowId) => $"tabs-{windowId}";

    // A child key is one path segment: a stored window id that is not (none is written today) draws its frame without a tab strip.
    private static bool HasTabStrip(string? windowId) => windowId is { Length: > 0 } && !windowId.Contains('/', StringComparison.Ordinal);

    // A window's tab strip owns nothing: a pick names the tab "{window}/{index}" and goes the way of the tab verb.
    [On]
    public Task OnSelectionChanged(DigitalBrain.UI.Commands.SelectionChanged changed)
    {
        ArgumentNullException.ThrowIfNull(changed);
        return SignalChild is { } key && key.StartsWith("tabs-", StringComparison.Ordinal)
            && changed.Id.StartsWith(key["tabs-".Length..] + "/", StringComparison.Ordinal)
            ? Tab(new DesktopTab(changed.Id))
            : Task.CompletedTask;
    }

    [On]
    public Task HandleWorkspaceChanged(WorkspaceChanged _) => SyncSmartDesktopAsync(State.LayoutMode, State.IsNarrow);

    [On]
    public Task HandleSynapseStudioChanged(SynapseStudioChanged? changed)
        => changed is null || string.IsNullOrEmpty(changed.Alias)
            ? Task.CompletedTask
            : SyncSmartDesktopAsync(State.LayoutMode, State.IsNarrow);

    [On]
    public Task HandleAppChanged(AppChanged? changed)
        => changed is null || string.IsNullOrEmpty(changed.Name)
            ? Task.CompletedTask
            : SyncSmartDesktopAsync(State.LayoutMode, State.IsNarrow);

    [On]
    public Task HandleConfirmationResolved(DigitalBrain.Confirmation.ConfirmationResolved? resolved)
        => resolved is null || string.IsNullOrEmpty(resolved.PlanId)
            ? Task.CompletedTask
            : SyncSmartDesktopAsync(State.LayoutMode, State.IsNarrow);

    [On]
    public async Task Start(DesktopStart request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var layout = "smart-desktop";
        if (Records is { } recordsSlot)
        {
            try
            {
                var records = await recordsSlot.Read();
                if (records.Layout is { Length: > 0 }) { layout = DesktopAppState.NormalizeLayoutMode(records.Layout); }
                if (!records.Greeted)
                {
                    try
                    {
                        var greeted = false;
                        if (InoApp is { } inoApp)
                        {
                            try
                            {
                                await inoApp.Request("open", "greet");
                                greeted = true;
                            }
                            catch (Exception fallbackEx) when (fallbackEx is not OperationCanceledException)
                            {
                                Log.LogSwallowed(fallbackEx, "Falling back to app.ino screen for desktop greet.");
                            }
                        }
                        if (!greeted && InoScreen is { } inoScreen)
                        {
                            await inoScreen.Request("open", DigitalBrain.Invocation.RuntimeProtocol.SerializeToElement("greet", typeof(string)));
                        }
                    }
                    catch (Exception error) when (error is not OperationCanceledException) { Log.LogSwallowed(error, "Ino was not there to greet; the desktop starts without it."); }
                    await recordsSlot.Write(records with { Greeted = true });
                }
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                Log.LogSwallowed(error, "DesktopRecords is optional when DesktopApp runs in standalone tests.");
            }
        }
        await SyncSmartDesktopAsync(layout, State.IsNarrow);
    }

    [On]
    public async Task WidgetOpen(DesktopWidgetOpen request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (Decision.Check("widget-open", DesktopAppState.ValidWidgetRule(request.Widget)) is { } refused)
        {
            await RecordRejectionAsync(refused.Rejection);
            return;
        }

        var id = DesktopAppState.NormalizeWidgetId(request.Widget)!;
        if (Records is { } recordsSlot)
        {
            try
            {
                var records = await recordsSlot.Read();
                if (!records.Widgets.Contains(id, StringComparer.Ordinal))
                {
                    await recordsSlot.Write(records with { Widgets = [.. records.Widgets, id] });
                }
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                Log.LogSwallowed(error, "DesktopRecords is optional when DesktopApp runs in standalone tests.");
            }
        }
        await SyncSmartDesktopAsync(State.LayoutMode, State.IsNarrow);
    }

    [On]
    public async Task WidgetClose(DesktopWidgetClose request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (Decision.Check("widget-close", DesktopAppState.ValidWidgetRule(request.Widget)) is { } invalid)
        {
            await RecordRejectionAsync(invalid.Rejection);
            return;
        }

        var id = DesktopAppState.NormalizeWidgetId(request.Widget)!;
        if (Records is { } recordsSlot)
        {
            try
            {
                var records = await recordsSlot.Read();
                if (Decision.Check("widget-close", DesktopAppState.WidgetOpenInRule(records.Widgets, id)) is { } missing)
                {
                    await RecordRejectionAsync(missing.Rejection);
                    return;
                }

                await recordsSlot.Write(records with { Widgets = [.. records.Widgets.Where(known => known != id)] });
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                Log.LogSwallowed(error, "DesktopRecords is optional when DesktopApp runs in standalone tests.");
                if (Decision.Check("widget-close", State.WidgetOpenRule(id)) is { } fallbackMissing)
                {
                    await RecordRejectionAsync(fallbackMissing.Rejection);
                    return;
                }
            }
        }
        else if (Decision.Check("widget-close", State.WidgetOpenRule(id)) is { } fallbackMissing)
        {
            await RecordRejectionAsync(fallbackMissing.Rejection);
            return;
        }
        await SyncSmartDesktopAsync(State.LayoutMode, State.IsNarrow);
    }

    [On]
    public async Task Layout(DesktopLayoutChoice request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var nextLayout = State.ToggleLayoutMode().LayoutMode;
        if (Records is { } recordsSlot)
        {
            try
            {
                await recordsSlot.Write(await recordsSlot.Read() with { Layout = nextLayout });
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                Log.LogSwallowed(error, "DesktopRecords is optional when DesktopApp runs in standalone tests.");
            }
        }
        await SyncSmartDesktopAsync(nextLayout, State.IsNarrow);
    }

    private sealed record DesktopOpenViewArgs(string Input, string View, string Section);

    [On]
    public async Task Open(DesktopOpen request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.App is null)
        {
            await SyncSmartDesktopAsync(State.LayoutMode, State.IsNarrow);
            return;
        }

        if (Decision.Check("open", DesktopAppState.OpenableAppRule(request.App)) is { } refused)
        {
            await RecordRejectionAsync(refused.Rejection);
            return;
        }

        var app = request.App.Trim().ToLowerInvariant();
        if (request.View is { } view)
        {
            try
            {
                try { await Apps[$"apps/{app}"].Request("open", view.Trim()); }
                catch (Exception fallbackEx) when (fallbackEx is not OperationCanceledException)
                {
                    Log.LogSwallowed(fallbackEx, $"Falling back to app.{app} screen for open view.");
                    await Screens["app." + app].Request("open", DigitalBrain.Invocation.RuntimeProtocol.SerializeToElement(new DesktopOpenViewArgs(view.Trim(), view.Trim(), view.Trim()), typeof(DesktopOpenViewArgs)));
                }
            }
            catch (Exception error) when (error is not OperationCanceledException) { Log.LogSwallowed(error, "The app was not there to receive its open view."); }
        }

        var brain = TryCurrentBrain();
        await ApplyDesktopWorkspaceChangeAsync(
            s => DesktopAppState.OpenWorkspaceApp(s, app, brain: brain),
            ws =>
            {
                if (BrainId.IsValid(brain)
                    && IntoChat.OS.Host.Primitives.AppRef.TryOf(new BrainId(brain), app, out var appRef)
                    && ws.Windows.Any(w => w.Tabs is { Length: > 0 } && w.Tabs.Any(t => string.Equals(t.App, appRef.Key, StringComparison.OrdinalIgnoreCase))))
                {
                    return null;
                }

                return Decision.Check("open", DesktopAppState.WindowLimitInRule(ws.Windows))?.Rejection;
            });
    }

    [On]
    public Task Narrow(DesktopNarrow request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return SyncSmartDesktopAsync(State.LayoutMode, string.Equals(request.Narrow, "true", StringComparison.OrdinalIgnoreCase));
    }

    [On]
    public Task AutoArrange(DesktopAutoArrange request)
    {
        ArgumentNullException.ThrowIfNull(request);
        RecordedRejection = null;
        return Show(State.AutoArrange());
    }

    [On]
    public async Task Pin(DesktopPinApp request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var appId = string.IsNullOrWhiteSpace(request.AppId) ? "photocraft" : request.AppId.Trim().ToLowerInvariant();
        var decision = State.PinOrUnpin(appId, request.Pinned);
        if (!decision.IsAccepted)
        {
            await RecordRejectionAsync(decision.Rejection);
            return;
        }

        RecordedRejection = null;
        await Show(decision.Next);
        if (TryCurrentBrain() is not { Length: > 0 } brain) { return; }
        var isPin = request.Pinned;
        await ApplyDesktopWorkspaceChangeAsync(s =>
        {
            if (s.Pins.Any(p => string.Equals(p.Id, appId, StringComparison.OrdinalIgnoreCase)) == isPin) { return (null, []); }
            var appKey = IntoChat.OS.Host.Primitives.AppRef.Of(new BrainId(brain), appId).Key;
            WorkspaceViewRef[] nextPins = isPin
                ? [.. s.Pins, new WorkspaceViewRef(appId, appKey, "full", string.Empty)]
                : [.. s.Pins.Where(p => !string.Equals(p.Id, appId, StringComparison.OrdinalIgnoreCase))];
            return (DesktopAppState.BuildUpdate(s, s.Windows, pins: nextPins), []);
        });
    }

    [On]
    public Task Unpin(DesktopUnpinApp request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (Decision.Check("unpin", DesktopAppState.UnpinnableAppRule(request.AppId)) is { } refused)
        {
            return RecordRejectionAsync(refused.Rejection);
        }

        return Pin(new DesktopPinApp(request.AppId, Pinned: false));
    }

    [On]
    public Task Close(DesktopClose request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return ApplyDesktopWorkspaceChangeAsync(
            s => DesktopAppState.CloseWorkspaceTarget(s, request.Target!),
            ws => Decision.Check("close", DesktopAppState.CloseTargetExistsInRule(ws.Windows, request.Target))?.Rejection,
            () => Decision.Check("close", State.CloseTargetExistsRule(request.Target))?.Rejection);
    }

    [On]
    public Task CloseApp(DesktopCloseApp request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (Decision.Check("close-app", DesktopAppState.OpenableAppRule(request.App)) is { } refused)
        {
            return RecordRejectionAsync(refused.Rejection);
        }

        var brain = TryCurrentBrain();
        return ApplyDesktopWorkspaceChangeAsync(s => DesktopAppState.CloseWorkspaceApp(s, request.App!, brain));
    }

    [On]
    public Task Focus(DesktopFocus request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return ApplyDesktopWorkspaceChangeAsync(
            s => s.Windows.FirstOrDefault(win => win.Id == request.WindowId) is { } w && s.FocusedViewId != w.ActiveTabId
                ? (DesktopAppState.BuildUpdate(s, s.Windows, w.ActiveTabId), [])
                : (null, []),
            ws => Decision.Check("focus", DesktopAppState.WindowExistsInRule(ws.Windows, request.WindowId))?.Rejection,
            () => Decision.Check("focus", State.WindowExistsRule(request.WindowId))?.Rejection);
    }

    [On]
    public Task Tab(DesktopTab request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return ApplyDesktopWorkspaceChangeAsync(
            s => DesktopAppState.SelectWorkspaceTab(s, request.Target!),
            ws => Decision.Check("tab", DesktopAppState.TabTargetExistsInRule(ws.Windows, request.Target))?.Rejection,
            () => Decision.Check("tab", State.TabTargetExistsRule(request.Target))?.Rejection);
    }

    [On]
    public Task NewTab(DesktopNewTab request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var brain = TryCurrentBrain();
        return ApplyDesktopWorkspaceChangeAsync(
            s => DesktopAppState.OpenWorkspaceApp(s, "ino", request.WindowId, asNewTab: true, brain: brain),
            ws =>
            {
                var openWins = ws.Windows.Where(static w => w.Tabs is { Length: > 0 }).ToArray();
                if (request.WindowId is { Length: > 0 })
                {
                    return Decision.Check(
                        "newtab",
                        DesktopAppState.WindowExistsInRule(openWins, request.WindowId),
                        DesktopAppState.TabLimitInRule(openWins, request.WindowId))?.Rejection;
                }

                var fallbackWin = openWins.FirstOrDefault(w => w.Tabs.Any(t => t.Id == ws.FocusedViewId)) ?? openWins.LastOrDefault();
                return fallbackWin is not null
                    ? Decision.Check("newtab", DesktopAppState.TabLimitInRule(openWins, fallbackWin.Id))?.Rejection
                    : Decision.Check("newtab", DesktopAppState.WindowLimitInRule(openWins))?.Rejection;
            });
    }

    [On]
    public Task Favourite(DesktopFavourite request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var brain = TryCurrentBrain();
        return ApplyDesktopWorkspaceChangeAsync(
            s => DesktopAppState.ToggleWorkspaceFavourite(s, request.WindowId!, brain),
            ws => Decision.Check("favourite", DesktopAppState.WindowExistsInRule(ws.Windows, request.WindowId))?.Rejection,
            () => Decision.Check("favourite", State.WindowExistsRule(request.WindowId))?.Rejection);
    }

    [On]
    public Task Settings(DesktopSettings request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return Open(new DesktopOpen("settings"));
    }

    public new DesktopAppState State => base.State;
    public new Rejected? LastRejection => RecordedRejection ?? base.LastRejection;
    public View Render() => PreviewView(State);
    public Exception? RecordedError => null;

    private Task RecordRejectionAsync(Rejected rejection)
    {
        RecordedRejection = rejection;
        return PublishEphemeral(rejection);
    }

    protected override Node Render(DesktopAppState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        var brain = TryCurrentBrain();
        var activeWindows = (state.Windows ?? []).Where(w => w.Tabs is { Length: > 0 }).ToArray();
        var tiles = ResolveTiles(state);
        var tilesById = new Dictionary<string, AppTile>(StringComparer.OrdinalIgnoreCase);
        foreach (var tile in tiles)
        {
            tilesById.TryAdd(tile.Id, tile);
        }

        string TitleFor(string id)
            => tilesById.TryGetValue(id, out var known) && !string.IsNullOrWhiteSpace(known.Title)
                ? known.Title
                : HumanizeTitle(id);

        AppTileNode BuildTile(string key, AppTile tile)
        {
            var command = string.Equals(tile.Id, "settings", StringComparison.OrdinalIgnoreCase)
                ? Sends(new DesktopSettings())
                : Sends(new DesktopOpen(tile.Id));
            return Kit.AppTile(
                key: key,
                appId: tile.Id,
                title: tile.Title,
                icon: tile.Icon,
                category: tile.Category,
                launch: tile.Launch,
                dock: tile.Dock,
                appKind: tile.Kind,
                description: tile.Description,
                command: command,
                automationName: tile.Title);
        }

        Node BuildWindowFrame(WorkspaceWindow window)
        {
            var winId = window.Id;
            var activeIndex = Math.Max(0, Array.FindIndex(window.Tabs, t => t.Id == window.ActiveTabId));
            var activeTab = window.Tabs[activeIndex];
            var appId = brain is { Length: > 0 } b ? IntoChat.OS.Host.Primitives.AppRef.NameOf(b, activeTab.App) ?? activeTab.Id : activeTab.Id;
            var title = TitleFor(appId);
            var isPinned = (state.Pins ?? []).Any(p => string.Equals(p.Id, appId, StringComparison.OrdinalIgnoreCase));
            var isFav = (state.Favourites ?? []).Any(f => string.Equals(f.App, activeTab.App, StringComparison.OrdinalIgnoreCase));

            Node[] chromeButtons =
            [
                Button(title).Key($"focus-{winId}").Sends(new DesktopFocus(winId)),
                Button("New tab", Icons.Plus).Key($"newtab-{winId}").Small().Sends(new DesktopNewTab(winId)),
                Button(isFav ? "Remove from favourites" : "Add to favourites", "star").Key($"star-{winId}").Small().Sends(new DesktopFavourite(winId)),
                .. appId == "ino"
                    ? Array.Empty<Node>()
                    : [isPinned
                        ? Button("Unpin", "pin-off").Key($"unpin-{winId}").Small().Sends(new DesktopUnpinApp(appId)).ToNode()
                        : Button("Pin", "pin").Key($"pin-{winId}").Small().Sends(new DesktopPinApp(appId, true)).ToNode()],
                Button("Close tab", "x").Key($"close-{winId}").Small().Sends(new DesktopClose($"{winId}/{activeIndex}")),
            ];

            TabStripItem[] tabItems = [.. window.Tabs.Select((tab, idx) => new TabStripItem($"{winId}/{idx}", TitleFor(tab.Id)))];

            return VStack(0, [
                new Row(chromeButtons) { Gap = 4, Extents = [0, .. chromeButtons.Skip(1).Select(_ => 32.0)] },
                HasTabStrip(winId)
                    ? Child<ITabStripControl, TabStripProps>(WindowTabsKey(winId), new TabStripProps(tabItems, AutomationName: $"{title} tabs", SelectedId: $"{winId}/{activeIndex}"))
                    : new Spacer(0),
                Kit.Embed(brain is { Length: > 0 } ? IntoChat.OS.Host.Primitives.AppRef.Of(new BrainId(brain), AppName).ChildKey($"window/{winId}") : $"apps/desktop/window/{winId}", "viewport"),
            ]).Extents(40, 38, 0);
        }

        Node windows = activeWindows.Length == 0
            ? VStack([])
            : VStack([.. activeWindows.Chunk(3).Select(row => (Node)HStack([.. row.Select(BuildWindowFrame)]))])
                .Extents([.. activeWindows.Chunk(3).Select(_ => 0.0)]);

        var dockNodes = new List<Node>();
        var dockSeen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var tile in tiles)
        {
            if (tile.Dock is not null && dockSeen.Add(tile.Id))
            {
                dockNodes.Add(BuildTile($"dock-{tile.Id}", tile));
            }
        }

        foreach (var pin in state.Pins ?? [])
        {
            var pinId = pin.Id?.Trim();
            if (string.IsNullOrWhiteSpace(pinId) || ShippedApps.Services.Contains(pinId) || !dockSeen.Add(pinId))
            {
                continue;
            }

            var tile = tilesById.TryGetValue(pinId, out var known)
                ? known
                : new AppTile(pinId, TitleFor(pinId), null, null, null, AppLaunch.Windowed, "runtime");
            dockNodes.Add(BuildTile($"dock-pin-{pinId}", tile));
        }

        foreach (var tile in tiles)
        {
            if (string.Equals(tile.Kind, "unit", StringComparison.OrdinalIgnoreCase) && dockSeen.Add(tile.Id))
            {
                dockNodes.Add(BuildTile($"dock-unit-{tile.Id}", tile));
            }
        }

        dockNodes.Add(Button("New tab", Icons.Plus).Key("newtab").Small().Sends(new DesktopNewTab()));

        var catalogNodes = new Node[tiles.Length];
        for (var i = 0; i < tiles.Length; i++)
        {
            catalogNodes[i] = BuildTile($"catalog-{tiles[i].Id}", tiles[i]);
        }

        var dock = HStack([
            HStack(4, [.. dockNodes]),
            HStack(4)[
                StatusPill(StatusKind.Done, "Usage"),
                Text(state.WidgetsText),
                Button("You").Key("avatar").Sends(new DesktopOpen("friends")),
                Button("Settings").Key("more-settings").Sends(new DesktopSettings())
            ]
        ]).Extents(0, 360);

        var catalog = new Surface(catalogNodes, "app-catalog", Padding: 0);

        return VStack([windows, dock, catalog]).Extents(0, 68, 0);
    }

    private async Task SyncSmartDesktopAsync(string layoutMode, bool narrow)
    {
        var normalizedLayout = DesktopAppState.NormalizeLayoutMode(layoutMode);
        var preset = State.StylePreset;
        var barLayout = State.BarLayout;
        if (UserSettings is { } settings)
        {
            try
            {
                var current = await settings.Read();
                preset = current.StylePreset;
                barLayout = current.BarLayout;
            }
            catch (Exception ex) { Log.LogSwallowed(ex, "UserSettings is optional."); }
        }

        var activeNeurons = 10;
        if (DebuggerReads is { } debuggerReads)
        {
            try
            {
                var topology = await debuggerReads.Topology();
                if (topology.Nodes.Count > 0) { activeNeurons = topology.Nodes.Count; }
            }
            catch (Exception error) when (error is not OperationCanceledException) { Log.LogSwallowed(error, "DebuggerReads is optional."); }
        }

        var tiles = State.Tiles;
        if (InstalledApps is { } installedApps)
        {
            try
            {
                tiles = await installedApps.List();
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                Log.LogSwallowed(error, "InstalledApps is optional when DesktopApp runs in standalone tests.");
            }
        }

        string[] deployedUnits = State.DeployedUnits ?? [];
        if (SynapseStudio is { } synapseStudio)
        {
            try
            {
                var units = await synapseStudio.List();
                deployedUnits = units
                    .Select(u => u.Aliases.FirstOrDefault() ?? u.SourceKey)
                    .Where(alias => !string.IsNullOrWhiteSpace(alias))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(alias => alias, StringComparer.OrdinalIgnoreCase)
                    .ToArray();
                if (units.Length > 0 && activeNeurons == 10) { activeNeurons += units.Length; }
            }
            catch (Exception error) when (error is not OperationCanceledException) { Log.LogSwallowed(error, "SynapseStudio is optional."); }
        }

        var windows = State.Windows ?? [];
        var pins = State.Pins ?? [];
        var favs = State.Favourites ?? [];
        var focusedViewId = State.FocusedViewId;
        if (Workspace is { } workspace)
        {
            try
            {
                var ws = await workspace.Read();
                windows = ws.Windows.Take(DesktopAppState.MaxWindows).ToArray();
                pins = ws.Pins
                    .Where(static p => !string.Equals(p.Id, "desktop", StringComparison.OrdinalIgnoreCase))
                    .Take(DesktopAppState.MaxPins)
                    .ToArray();
                favs = ws.Favourites.Take(DesktopAppState.MaxFavourites).ToArray();
                focusedViewId = windows.Length == 0
                    ? null
                    : windows.Any(w => (w.Tabs ?? []).Any(t => string.Equals(t.Id, ws.FocusedViewId, StringComparison.Ordinal)))
                        ? ws.FocusedViewId
                        : windows[^1].ActiveTabId;
            }
            catch (Exception error) when (error is not OperationCanceledException) { Log.LogSwallowed(error, "Workspace is optional."); }
        }

        await SyncWindowViewportsAsync(windows);

        var widgetsText = "";
        if (Records is { } recordsSlot)
        {
            try
            {
                var records = await recordsSlot.Read();
                widgetsText = string.Join(" · ", records.Widgets);
            }
            catch (Exception error) when (error is not OperationCanceledException) { Log.LogSwallowed(error, "Records is optional."); }
        }

        var pinnedApps = pins.Length > 0
            ? pins.Select(p => p.Id)
                .Where(id => !string.IsNullOrWhiteSpace(id) && id is not ("ino" or "desktop"))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(DesktopAppState.MaxPins)
                .ToArray()
            : (State.PinnedApps ?? ["ino", "creator"]);

        RecordedRejection = null;
        await Show(State with
        {
            LayoutMode = normalizedLayout,
            IsNarrow = narrow,
            StylePreset = preset,
            BarLayout = barLayout,
            ActiveNeurons = activeNeurons,
            Status = narrow ? "Smart Desktop (Compact)" : $"Smart Desktop ({normalizedLayout})",
            Windows = windows,
            FocusedViewId = focusedViewId,
            Pins = pins,
            Favourites = favs,
            PinnedApps = pinnedApps,
            WidgetsText = widgetsText,
            DeployedUnits = deployedUnits,
            Tiles = tiles,
        });
    }

    private async Task ApplyDesktopWorkspaceChangeAsync(
        Func<WorkspaceState, (WorkspaceUpdate? Update, IReadOnlyList<string> ClosedWindowIds)> change,
        Func<WorkspaceState, Rejected?>? validateWorkspace = null,
        Func<Rejected?>? validateStandalone = null)
    {
        IReadOnlyList<string> closedWindowIds = [];
        Rejected? rejected = null;
        var workspaceAvailable = false;
        if (Workspace is { } workspace)
        {
            try
            {
                for (var attempt = 0; attempt < 2; attempt++)
                {
                    var wsState = await workspace.Read();
                    workspaceAvailable = true;
                    if (validateWorkspace?.Invoke(wsState) is { } wsRejection)
                    {
                        rejected = wsRejection;
                        break;
                    }

                    var (update, closed) = change(wsState);
                    closedWindowIds = closed;
                    if (update is null) { break; }
                    try
                    {
                        await workspace.Apply(update, $"desktop:{Guid.NewGuid():N}:{attempt}", wsState.Revision);
                        break;
                    }
                    catch (RevisionConflictException ex) when (attempt == 0)
                    {
                        Log.LogSwallowed(ex, "Workspace revision conflict on initial attempt; retrying.");
                    }
                }
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                Log.LogSwallowed(error, "Workspace is optional.");
                if (!workspaceAvailable && validateStandalone is not null)
                {
                    rejected = validateStandalone();
                }
            }
        }
        else if (validateStandalone is not null)
        {
            rejected = validateStandalone();
        }

        if (rejected is not null)
        {
            await RecordRejectionAsync(rejected);
            return;
        }

        await ClearViewportsAsync(closedWindowIds);
        await SyncSmartDesktopAsync(State.LayoutMode, State.IsNarrow);
    }

    private async Task SyncWindowViewportsAsync(IReadOnlyList<WorkspaceWindow> windows)
    {
        if (TryCurrentBrain() is not { Length: > 0 } brain) { return; }
        foreach (var win in windows)
        {
            if (win is null || string.IsNullOrWhiteSpace(win.Id) || win.Tabs is not { Length: > 0 }) { continue; }
            var activeTab = win.Tabs.FirstOrDefault(t => string.Equals(t.Id, win.ActiveTabId, StringComparison.Ordinal))
                ?? win.Tabs[0];
            await SetViewportTargetAsync(brain, win.Id, activeTab, "Failed to sync viewport for open window.");
        }
    }

    private async Task ClearViewportsAsync(IReadOnlyList<string> closedWindowIds)
    {
        if (TryCurrentBrain() is not { Length: > 0 } brain) { return; }
        foreach (var winId in closedWindowIds)
        {
            await SetViewportTargetAsync(brain, winId, target: null, "Failed to clear viewport for closed window.");
        }
    }

    private async Task SetViewportTargetAsync(string brain, string winId, WorkspaceViewRef? target, string failureContext)
    {
        if (string.IsNullOrWhiteSpace(winId)) { return; }
        try
        {
            var viewport = Viewports[
                IntoChat.OS.Host.Primitives.AppRef.Of(new BrainId(brain), AppName).ChildNeuron(UIVocabulary.ViewportType, $"window/{winId}")];
            for (var attempt = 0; attempt < 2; attempt++)
            {
                var currentVp = await viewport.Read();
                if (currentVp.Target == target) { return; }
                try
                {
                    await viewport.Set(target, currentVp.Revision);
                    return;
                }
                catch (RevisionConflictException ex) when (attempt == 0)
                {
                    Log.LogSwallowed(ex, "Viewport revision conflict on initial attempt; retrying.");
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log.LogSwallowed(ex, failureContext);
        }
    }
}
