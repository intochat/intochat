/*
# Desktop
Role: Shell
Shipped: true
Operations: start, layout, open, narrow, auto-arrange, pin, close, close-app, focus, tab, newtab, unpin, favourite, widget-open, widget-close, settings, ui.command

The embedded folder scripts publish the Flutter desktop. These operations present Smart Desktop state, Whiteboard sticky-note app pinning, auto-arrange, and 3D BrainScene telemetry.
*/
using System.Reflection;
using DigitalBrain;
using DigitalBrain.Kernel;
using DigitalBrain.Log;
using DigitalBrain.Neurons;
using DigitalBrain.Rules;
using DigitalBrain.UI;
using IntoChat.OS.Modules.Apps;
using IntoChat.OS.Modules.ArtCraft;
using IntoChat.OS.Modules.Flutter;
using IntoChat.OS.Modules.Flutter.Viewport;
using IntoChat.OS.Modules.Flutter.Workspace;
using IntoChat.OS.Modules.Flutter.Workspace.Signals;
using IntoChat.OS.UI;
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
public sealed class DesktopApp : AggregateScreen<DesktopAppState>
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

    public static AppBundle Bundle { get; } = new(
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
                new(UiCommand.SignalAlias, "Dispatch a typed UI Kit aggregate command."),
            ],
            Settings: [],
            Slots: [],
            Icon: "icon.svg"),
        "",
        new Dictionary<string, string>(StringComparer.Ordinal))
    {
        CompiledType = typeof(DesktopApp).AssemblyQualifiedName,
    };

    private IDesktopRecords Records => Get<IDesktopRecords>(IDesktopRecords.Key);

    public static readonly IReadOnlyList<string> DefaultDockApps =
        ["ino", "photocraft", "filmcraft", "markdraw", "creator", "files", "settings"];

    public static readonly IReadOnlyList<(string Id, string Title, string Uri, string Icon, string Category, string RuntimeMode)> NativeCatalog =
    [
        ("ino", "Ino Assistant", "neuron://local/apps/ino", "sparkles", "Intelligence", "orleans-grain"),
        ("files", "Files", "neuron://local/apps/files", "folder", "Workspace", "orleans-grain"),
        ("tasks", "Tasks", "neuron://local/apps/tasks", "tasks", "Workspace", "orleans-grain"),
        ("calculator", "Calculator", "neuron://local/apps/calculator", "calculator", "Workspace", "orleans-grain"),
        ("data", "Data Studio", "neuron://local/apps/data", "bar-chart-2", "Studio", "orleans-grain"),
        ("markdraw", "Markdraw", "neuron://local/apps/markdraw", "pen-tool", "Studio", "orleans-grain"),
        ("creator", "Creator 3D Loop", "neuron://local/apps/creator", "cpu", "Studio", "orleans-grain"),
        ("engineer", "IntoChat Engineer", "neuron://local/apps/engineer", "workflow", "Studio", "orleans-grain"),
        ("aspire", ".NET Aspire", "neuron://local/apps/aspire", "activity", "Developer", "orleans-grain"),
        ("orleans", "Orleans Dashboard", "neuron://local/apps/orleans", "cpu", "Developer", "orleans-grain"),
        ("gallery", "UI Gallery", "neuron://local/apps/gallery", "layout-grid", "Studio", "orleans-grain"),
        ("friends", "Friends", "neuron://local/apps/friends", "users", "Social", "orleans-grain"),
        ("usage", "Usage", "neuron://local/apps/usage", "wallet", "System", "orleans-grain"),
        ("settings", "Settings", "neuron://local/apps/settings", "settings", "System", "orleans-grain"),
    ];

    public static IReadOnlyList<(string Id, string Title, string Uri, string Icon, string Category, string RuntimeMode)> Catalog { get; } =
        NativeCatalog.Concat(
            ArtCraftCatalog.All.Select(app => (app.Id, app.Title, $"neuron://local/apps/{app.Id}", app.Icon, app.Category, "wasm-ui+mcp+cli"))
        ).ToArray();

    public IReadOnlyList<(string Id, string Title, string Uri, string Icon, string Category, string RuntimeMode)> EffectiveCatalog()
    {
        var brain = TryCurrentBrain();
        var items = Catalog
            .Select(entry => brain is { Length: > 0 } b
                ? entry with { Uri = entry.Uri.Replace("neuron://local/", $"neuron://{b}/", StringComparison.Ordinal) }
                : entry)
            .ToList();
        var knownIds = items.Select(i => i.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var unitAlias in State.DeployedUnits ?? [])
        {
            if (string.IsNullOrWhiteSpace(unitAlias) || !knownIds.Add(unitAlias))
            {
                continue;
            }
            var unitUri = brain is { Length: > 0 } b
                ? $"neuron://{b}/units/{unitAlias}"
                : $"neuron:///units/{unitAlias}";
            items.Add((unitAlias, FormatAppTitle(unitAlias), unitUri, "zap", "Micro-App", "synapse-unit"));
        }

        return items;
    }

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

    internal static string FormatAppTitle(string appId)
    {
        if (string.IsNullOrWhiteSpace(appId)) { return "App"; }
        var known = Catalog.FirstOrDefault(c => string.Equals(c.Id, appId, StringComparison.OrdinalIgnoreCase));
        if (!string.IsNullOrWhiteSpace(known.Title))
        {
            return appId.Equals("ino", StringComparison.OrdinalIgnoreCase) ? "Ino"
                : appId.Equals("creator", StringComparison.OrdinalIgnoreCase) ? "Creator"
                : known.Title;
        }
        return char.ToUpperInvariant(appId[0]) + appId[1..];
    }

    protected override async Task OnActivate(CancellationToken cancellationToken)
    {
        AppActivationScope.Notify(this);
        await base.OnActivate(cancellationToken);
        try { await Follow<IWorkspace, WorkspaceChanged>("main"); }
        catch (Exception ex) { Log.LogSwallowed(ex, "Workspace follow is optional when running in standalone mode."); }
        try { await Follow<ISynapseStudio, SynapseStudioChanged>("ino/synapses"); }
        catch (Exception ex) when (ex is not OperationCanceledException) { Log.LogSwallowed(ex, "SynapseStudio follow is optional when running in standalone mode."); }
        try { await Context.Wires.EnsureBrainWide([typeof(DigitalBrain.Confirmation.ConfirmationResolved)]); }
        catch (Exception ex) when (ex is not OperationCanceledException) { Log.LogSwallowed(ex, "ConfirmationResolved follow is optional when running in standalone mode."); }
        await SyncSmartDesktopAsync(State.LayoutMode, State.IsNarrow);
    }

    [On]
    public Task HandleWorkspaceChanged(WorkspaceChanged _) => SyncSmartDesktopAsync(State.LayoutMode, State.IsNarrow);

    [On]
    public Task HandleSynapseStudioChanged(SynapseStudioChanged? changed)
        => changed is null || string.IsNullOrEmpty(changed.Alias)
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
        try
        {
            var records = await Records.Read();
            if (records.Layout is { Length: > 0 }) { layout = DesktopAppState.NormalizeLayoutMode(records.Layout); }
            if (!records.Greeted)
            {
                try
                {
                    try { await Get<IApp>("apps/ino").Request("open", "greet"); }
                    catch (Exception fallbackEx) when (fallbackEx is not OperationCanceledException)
                    {
                        Log.LogSwallowed(fallbackEx, "Falling back to app.ino screen for desktop greet.");
                        await Get<IScreen>("app.ino").Request("open", DigitalBrain.Invocation.RuntimeProtocol.SerializeToElement("greet", typeof(string)));
                    }
                }
                catch (Exception error) when (error is not OperationCanceledException) { Log.LogSwallowed(error, "Ino was not there to greet; the desktop starts without it."); }
                await Records.Write(records with { Greeted = true });
            }
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            Log.LogSwallowed(error, "DesktopRecords is optional when DesktopApp runs in standalone tests.");
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
        try
        {
            var records = await Records.Read();
            if (!records.Widgets.Contains(id, StringComparer.Ordinal))
            {
                await Records.Write(records with { Widgets = [.. records.Widgets, id] });
            }
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            Log.LogSwallowed(error, "DesktopRecords is optional when DesktopApp runs in standalone tests.");
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
        try
        {
            var records = await Records.Read();
            if (Decision.Check("widget-close", DesktopAppState.WidgetOpenInRule(records.Widgets, id)) is { } missing)
            {
                await RecordRejectionAsync(missing.Rejection);
                return;
            }

            await Records.Write(records with { Widgets = [.. records.Widgets.Where(known => known != id)] });
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
        await SyncSmartDesktopAsync(State.LayoutMode, State.IsNarrow);
    }

    [On]
    public async Task Layout(DesktopLayoutChoice request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var nextLayout = State.ToggleLayoutMode().LayoutMode;
        try
        {
            await Records.Write(await Records.Read() with { Layout = nextLayout });
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            Log.LogSwallowed(error, "DesktopRecords is optional when DesktopApp runs in standalone tests.");
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
                try { await Get<IApp>($"apps/{app}").Request("open", view.Trim()); }
                catch (Exception fallbackEx) when (fallbackEx is not OperationCanceledException)
                {
                    Log.LogSwallowed(fallbackEx, $"Falling back to app.{app} screen for open view.");
                    await Get<IScreen>("app." + app).Request("open", DigitalBrain.Invocation.RuntimeProtocol.SerializeToElement(new DesktopOpenViewArgs(view.Trim(), view.Trim(), view.Trim()), typeof(DesktopOpenViewArgs)));
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

    private static new Emit Emits<TSignal>(TSignal signal) where TSignal : Signal
    {
        ArgumentNullException.ThrowIfNull(signal);
        var alias = typeof(TSignal).GetCustomAttribute<SignalAttribute>(false)?.Alias ?? typeof(TSignal).Name;
        return new Emit("app.desktop", alias, DigitalBrain.Invocation.RuntimeProtocol.SerializeToElement(signal, typeof(TSignal)));
    }

    protected override Node Render(DesktopAppState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        var brain = TryCurrentBrain();
        var activeWindows = (state.Windows ?? []).Where(w => w.Tabs is { Length: > 0 }).ToArray();

        Node BuildWindowFrame(WorkspaceWindow window)
        {
            var winId = window.Id;
            var activeIndex = Math.Max(0, Array.FindIndex(window.Tabs, t => t.Id == window.ActiveTabId));
            var activeTab = window.Tabs[activeIndex];
            var appId = brain is { Length: > 0 } b ? IntoChat.OS.Host.Primitives.AppRef.NameOf(b, activeTab.App) ?? activeTab.Id : activeTab.Id;
            var title = char.ToUpperInvariant(appId[0]) + appId[1..];
            var isPinned = (state.Pins ?? []).Any(p => string.Equals(p.Id, appId, StringComparison.OrdinalIgnoreCase));
            var isFav = (state.Favourites ?? []).Any(f => string.Equals(f.App, activeTab.App, StringComparison.OrdinalIgnoreCase));

            Node[] chromeButtons =
            [
                Button(title).Key($"focus-{winId}").Emits(Emits(new DesktopFocus(winId))),
                Button("New tab", Icons.Plus).Key($"newtab-{winId}").Small().Emits(Emits(new DesktopNewTab(winId))),
                Button(isFav ? "Remove from favourites" : "Add to favourites", "star").Key($"star-{winId}").Small().Emits(Emits(new DesktopFavourite(winId))),
                .. appId == "ino"
                    ? Array.Empty<Node>()
                    : [isPinned
                        ? Button("Unpin", "pin-off").Key($"unpin-{winId}").Small().Emits(Emits(new DesktopUnpinApp(appId))).ToNode()
                        : Button("Pin", "pin").Key($"pin-{winId}").Small().Emits(Emits(new DesktopPinApp(appId, true))).ToNode()],
                Button("Close tab", "x").Key($"close-{winId}").Small().Emits(Emits(new DesktopClose($"{winId}/{activeIndex}"))),
            ];

            var tabItems = window.Tabs.Select((tab, idx) => Kit.Tab(
                $"{winId}/{idx}",
                char.ToUpperInvariant(tab.Id[0]) + tab.Id[1..])
                .Key($"tab-{winId}-{idx}")
                .Emits(Emits(new DesktopTab($"{winId}/{idx}")))).ToArray();

            return VStack(0, [
                new Row(chromeButtons) { Gap = 4, Extents = [0, .. chromeButtons.Skip(1).Select(_ => 32.0)] },
                Tabs($"{winId}/{activeIndex}", tabItems),
                Kit.Slot(brain is { Length: > 0 } ? IntoChat.OS.Host.Primitives.AppRef.Of(new BrainId(brain), AppName).ChildKey($"window/{winId}") : $"apps/desktop/window/{winId}", "viewport"),
            ]).Extents(40, 38, 0);
        }

        Node windows = activeWindows.Length == 0
            ? VStack([])
            : VStack([.. activeWindows.Chunk(3).Select(row => (Node)HStack([.. row.Select(BuildWindowFrame)]))])
                .Extents([.. activeWindows.Chunk(3).Select(_ => 0.0)]);

        var deployedUnits = state.DeployedUnits ?? [];
        var pinnedIds = (state.Pins ?? []).Select(p => p.Id).Where(id => !string.IsNullOrWhiteSpace(id)).ToArray();
        var hasDynamicDockItems = deployedUnits.Length > 0 || pinnedIds.Length > 0;

        Node[] dockLeftButtons;
        if (!hasDynamicDockItems)
        {
            dockLeftButtons =
            [
                PrimaryButton("Ino", "ino").Key("dock-ino").Emits(Emits(new DesktopOpen("ino"))),
                Button("Files").Key("dock-files").Emits(Emits(new DesktopOpen("files"))),
                Button("Settings").Key("dock-settings").Emits(Emits(new DesktopSettings())),
                Button("New tab", Icons.Plus).Key("newtab").Small().Emits(Emits(new DesktopNewTab())),
            ];
        }
        else
        {
            var buttons = new List<Node>
            {
                PrimaryButton("Ino", "ino").Key("dock-ino").Emits(Emits(new DesktopOpen("ino"))),
                Button("PhotoCraft", "artcraft:photocraft").Key("dock-photocraft").Emits(Emits(new DesktopOpen("photocraft"))),
                Button("FilmCraft", "artcraft:filmcraft").Key("dock-filmcraft").Emits(Emits(new DesktopOpen("filmcraft"))),
                Button("Markdraw", "pen-tool").Key("dock-markdraw").Emits(Emits(new DesktopOpen("markdraw"))),
                Button("Creator", "cpu").Key("dock-creator").Emits(Emits(new DesktopOpen("creator"))),
                Button("Files").Key("dock-files").Emits(Emits(new DesktopOpen("files"))),
                Button("Settings").Key("dock-settings").Emits(Emits(new DesktopSettings())),
            };

            var builtInSet = new HashSet<string>(DefaultDockApps, StringComparer.OrdinalIgnoreCase);
            foreach (var pinId in pinnedIds)
            {
                if (builtInSet.Add(pinId))
                {
                    buttons.Add(Button(FormatAppTitle(pinId)).Key($"dock-pin-{pinId}").Emits(Emits(new DesktopOpen(pinId))));
                }
            }

            foreach (var unitAlias in deployedUnits)
            {
                if (builtInSet.Add(unitAlias))
                {
                    buttons.Add(Button(FormatAppTitle(unitAlias), "zap").Key($"dock-unit-{unitAlias}").Emits(Emits(new DesktopOpen(unitAlias))));
                }
            }

            buttons.Add(Button("New tab", Icons.Plus).Key("newtab").Small().Emits(Emits(new DesktopNewTab())));
            dockLeftButtons = [.. buttons];
        }

        var dock = HStack([
            HStack(4, dockLeftButtons),
            HStack(4)[
                StatusPill(StatusKind.Done, "Usage"),
                Text(state.WidgetsText),
                Button("You").Key("avatar").Emits(Emits(new DesktopOpen("friends"))),
                Button("Settings").Key("more-settings").Emits(Emits(new DesktopSettings()))
            ]
        ]).Extents(0, 360);

        return VStack([windows, dock]).Extents(0, 68);
    }

    private async Task SyncSmartDesktopAsync(string layoutMode, bool narrow)
    {
        var normalizedLayout = DesktopAppState.NormalizeLayoutMode(layoutMode);
        var preset = State.StylePreset;
        var barLayout = State.BarLayout;
        if (TryGetOptional<IUserSettings>("settings") is { } settings)
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
        try
        {
            var topology = await Get<IntoChat.OS.Modules.Apps.Debugger.IDebuggerReads>("creator/reads").Topology();
            if (topology.Nodes.Count > 0) { activeNeurons = topology.Nodes.Count; }
        }
        catch (Exception error) when (error is not OperationCanceledException) { Log.LogSwallowed(error, "DebuggerReads is optional."); }

        string[] deployedUnits = State.DeployedUnits ?? [];
        try
        {
            var units = await Get<ISynapseStudio>("ino/synapses").List();
            deployedUnits = units
                .Select(u => u.Aliases.FirstOrDefault() ?? u.SourceKey)
                .Where(alias => !string.IsNullOrWhiteSpace(alias))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(alias => alias, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            if (units.Length > 0 && activeNeurons == 10) { activeNeurons += units.Length; }
        }
        catch (Exception error) when (error is not OperationCanceledException) { Log.LogSwallowed(error, "SynapseStudio is optional."); }

        var windows = State.Windows ?? [];
        var pins = State.Pins ?? [];
        var favs = State.Favourites ?? [];
        var focusedViewId = State.FocusedViewId;
        try
        {
            var ws = await Get<IWorkspace>("main").Read();
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

        await SyncWindowViewportsAsync(windows);

        var widgetsText = "";
        try
        {
            var records = await Records.Read();
            widgetsText = string.Join(" · ", records.Widgets);
        }
        catch (Exception error) when (error is not OperationCanceledException) { Log.LogSwallowed(error, "Records is optional."); }

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
        try
        {
            var workspace = Get<IWorkspace>("main");
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
            var viewport = Get<IViewport>(
                IntoChat.OS.Host.Primitives.AppRef.Of(new BrainId(brain), AppName).ChildNeuron(UIVocabulary.ViewportType, $"window/{winId}"));
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

    private T? TryGetOptional<T>(string key) where T : class
    {
        try { return Get<T>(key); }
        catch (InvalidOperationException error)
        {
            Log.LogSwallowed(error, $"{typeof(T).Name} is optional when DesktopApp runs in baseline tests.");
            return null;
        }
    }
}
