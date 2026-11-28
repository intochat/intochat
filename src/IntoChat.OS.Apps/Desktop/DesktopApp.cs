/*
# Desktop
Role: Shell
Shipped: true
Operations: start, layout, open, narrow, auto-arrange, pin, close, close-app, focus, tab, newtab, unpin, favourite, widget-open, widget-close, settings, new-specialist, import-skill

The embedded folder scripts publish the Flutter desktop. These operations present Smart Desktop state, Whiteboard sticky-note app pinning, auto-arrange, and 3D BrainScene telemetry.
*/
using IntoChat.OS.Host.Apps;
using AppRef = IntoChat.OS.Host.Primitives.AppRef;
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
using IntoChat.OS.Modules.UI.Controls;
using IntoChat.OS.UI;
using Microsoft.Extensions.Logging;

namespace IntoChat.OS.Apps.Desktop;

[Signal("desktop.start")] public sealed record DesktopStart : Signal;
[Signal("desktop.layout")] public sealed record DesktopLayoutChoice : Signal;
[Signal("desktop.open")] public sealed record DesktopOpen(string? App = null, string? View = null) : Signal;
[Signal("desktop.close")] public sealed record DesktopClose(string? Target = null) : Signal;
[Signal("desktop.close-app")] public sealed record DesktopCloseApp(string? App = null) : Signal;
[Signal("desktop.focus")] public sealed record DesktopFocus(string? WindowId = null) : Signal;
[Signal("desktop.tab")] public sealed record DesktopTab(string? Target = null) : Signal;
[Signal("desktop.newtab")] public sealed record DesktopNewTab(string? WindowId = null) : Signal;
[Signal("desktop.narrow")] public sealed record DesktopNarrow(string? Narrow = null) : Signal;
[Signal("desktop.auto-arrange")] public sealed record DesktopAutoArrange : Signal;
[Signal("desktop.pin")] public sealed record DesktopPinApp(string? AppId = null, bool Pinned = true) : Signal;
[Signal("desktop.unpin")] public sealed record DesktopUnpinApp(string? AppId = null) : Signal;
[Signal("desktop.favourite")] public sealed record DesktopFavourite(string? WindowId = null) : Signal;
[Signal("desktop.widget-open")] public sealed record DesktopWidgetOpen(string? Widget = null) : Signal;
[Signal("desktop.widget-close")] public sealed record DesktopWidgetClose(string? Widget = null) : Signal;
[Signal("desktop.settings")] public sealed record DesktopSettings : Signal;

[Signal("desktop.new-specialist")]
public sealed record DesktopCreateSpecialist(
    string? Title = null,
    string? Handle = null,
    string? Instructions = null,
    string? Description = null,
    string[]? AllowedContracts = null,
    string[]? ToolSources = null,
    string? MemoryScope = null,
    string[]? Starters = null,
    Dictionary<string, string>? Settings = null,
    Dictionary<string, string>? Connections = null) : Signal;

[Signal("desktop.import-skill")]
public sealed record DesktopImportSkill(
    string? FileName = null,
    string? Content = null,
    string? Handle = null,
    string[]? AllowedContracts = null,
    string[]? ToolSources = null,
    string? MemoryScope = null) : Signal;

[Neuron("app.desktop")]
[Screen("app.desktop", Title = "Desktop", Icon = Icons.LayoutGrid)]
public sealed partial class DesktopApp : AggregateScreen<DesktopAppState>, IDesktopShell
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
            new("new-specialist", "Create a new custom specialist bot from Apps and open it in Unified Messenger."),
            new("import-skill", "Import a SKILL.md specialist bot from Apps and open it in Unified Messenger."),
        ]);

    [Slot(Key = IDesktopRecords.Key, Required = false)] private partial IDesktopRecords? Records { get; }
    [Slot(Key = IInstalledApps.DefaultKey, Required = false)] private partial IInstalledApps? InstalledApps { get; }
    [Slot(Key = "apps/ino", Required = false)] private partial IApp? InoApp { get; }
    [Slot(Key = "app.ino", Required = false)] private partial IScreen? InoScreen { get; }
    [Slot(Required = false)] private partial IUserSettings? UserSettings { get; }
    [Slot(Required = false)] private partial IntoChat.OS.Modules.Apps.Debugger.IDebuggerReads? DebuggerReads { get; }
    [Slot(Required = false)] private partial ISynapseStudio? SynapseStudio { get; }
    [Slot(Key = "main", Required = false)] private partial IWorkspace? Workspace { get; }
    [Slot(Required = false)] private partial SlotCollection<IApp> Apps { get; }
    [Slot] private partial SlotCollection<IScreen> Screens { get; }
    [Slot(Required = false)] private partial SlotCollection<IViewport> Viewports { get; }

    private string? TryCurrentBrain()
    {
        try { var val = Id.Brain.Value; return string.IsNullOrEmpty(val) ? null : val; }
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
        await TryInvokeOptionalSlotAsync(() => Follow<IWorkspace, WorkspaceChanged>("main"), "Workspace follow is optional when running in standalone mode.");
        await TryInvokeOptionalSlotAsync(() => Follow<ISynapseStudio, SynapseStudioChanged>("ino/synapses"), "SynapseStudio follow is optional when running in standalone mode.");
        await TryInvokeOptionalSlotAsync(() => Context.Wires.EnsureBrainWide([typeof(DigitalBrain.Confirmation.ConfirmationResolved), typeof(AppChanged)]), "Brain-wide signal subscriptions are optional when running in standalone mode.");

        // Every window's tab strip is a child keyed by the window id (w-1 .. w-MaxWindows plus any stored window ids).
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

    internal static bool HasTabStrip(string? windowId) => windowId is { Length: > 0 } && !windowId.Contains('/', StringComparison.Ordinal);

    [On]
    public Task OnSelectionChanged(DigitalBrain.UI.Commands.SelectionChanged changed)
    {
        ArgumentNullException.ThrowIfNull(changed);
        return SignalChild is { } key && key.StartsWith("tabs-", StringComparison.Ordinal)
            && changed.Id.StartsWith(key["tabs-".Length..] + "/", StringComparison.Ordinal)
            ? Tab(new DesktopTab(changed.Id))
            : Task.CompletedTask;
    }

    [On] public Task HandleWorkspaceChanged(WorkspaceChanged _) => SyncSmartDesktopAsync(State.LayoutMode, State.IsNarrow);
    [On] public Task HandleSynapseStudioChanged(SynapseStudioChanged? changed) => changed is null || string.IsNullOrEmpty(changed.Alias) ? Task.CompletedTask : SyncSmartDesktopAsync(State.LayoutMode, State.IsNarrow);
    [On] public Task HandleAppChanged(AppChanged? changed) => changed is null || string.IsNullOrEmpty(changed.Name) ? Task.CompletedTask : SyncSmartDesktopAsync(State.LayoutMode, State.IsNarrow);
    [On] public Task HandleConfirmationResolved(DigitalBrain.Confirmation.ConfirmationResolved? resolved) => resolved is null || string.IsNullOrEmpty(resolved.PlanId) ? Task.CompletedTask : SyncSmartDesktopAsync(State.LayoutMode, State.IsNarrow);

    [On]
    public async Task Start(DesktopStart request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var layout = "smart-desktop";
        if (Records is { } recordsSlot)
        {
            await TryInvokeOptionalSlotAsync(async () =>
            {
                var records = await recordsSlot.Read();
                if (records.Layout is { Length: > 0 }) { layout = DesktopAppState.NormalizeLayoutMode(records.Layout); }
                if (!records.Greeted)
                {
                    var greeted = InoApp is { } inoApp
                        && await TryInvokeOptionalSlotAsync(() => inoApp.Request("open", "greet"), "Falling back to app.ino screen for desktop greet.");
                    if (!greeted && InoScreen is { } inoScreen)
                    {
                        await TryInvokeOptionalSlotAsync(
                            () => inoScreen.Request("open", DigitalBrain.Invocation.RuntimeProtocol.SerializeToElement("greet", typeof(string))),
                            "Ino was not there to greet; the desktop starts without it.");
                    }

                    await recordsSlot.Write(records with { Greeted = true });
                }
            }, "DesktopRecords is optional when DesktopApp runs in standalone tests.");
        }

        await SyncSmartDesktopAsync(layout, State.IsNarrow);
    }

    [On]
    public async Task WidgetOpen(DesktopWidgetOpen request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (Decision.Check("widget-open", DesktopAppState.ValidWidgetRule(request.Widget)) is { } refused) { await RecordRejectionAsync(refused.Rejection); return; }

        var id = DesktopAppState.NormalizeWidgetId(request.Widget)!;
        if (Records is { } recordsSlot)
        {
            await TryInvokeOptionalSlotAsync(async () =>
            {
                var records = await recordsSlot.Read();
                if (!records.Widgets.Contains(id, StringComparer.Ordinal)) { await recordsSlot.Write(records with { Widgets = [.. records.Widgets, id] }); }
            }, "DesktopRecords is optional when DesktopApp runs in standalone tests.");
        }

        await SyncSmartDesktopAsync(State.LayoutMode, State.IsNarrow);
    }

    [On]
    public async Task WidgetClose(DesktopWidgetClose request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (Decision.Check("widget-close", DesktopAppState.ValidWidgetRule(request.Widget)) is { } invalid) { await RecordRejectionAsync(invalid.Rejection); return; }

        var id = DesktopAppState.NormalizeWidgetId(request.Widget)!;
        var rejected = false;
        var handledByRecords = Records is { } recordsSlot
            && await TryInvokeOptionalSlotAsync(async () =>
            {
                var records = await recordsSlot.Read();
                if (Decision.Check("widget-close", DesktopAppState.WidgetOpenInRule(records.Widgets, id)) is { } missing)
                {
                    await RecordRejectionAsync(missing.Rejection);
                    rejected = true;
                    return;
                }

                await recordsSlot.Write(records with { Widgets = [.. records.Widgets.Where(known => known != id)] });
            }, "DesktopRecords is optional when DesktopApp runs in standalone tests.");

        if (rejected) { return; }
        if (!handledByRecords && Decision.Check("widget-close", State.WidgetOpenRule(id)) is { } fallbackMissing) { await RecordRejectionAsync(fallbackMissing.Rejection); return; }

        await SyncSmartDesktopAsync(State.LayoutMode, State.IsNarrow);
    }

    [On]
    public async Task Layout(DesktopLayoutChoice request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var nextLayout = State.ToggleLayoutMode().LayoutMode;
        if (Records is { } recordsSlot)
        {
            await TryInvokeOptionalSlotAsync(async () => await recordsSlot.Write(await recordsSlot.Read() with { Layout = nextLayout }), "DesktopRecords is optional when DesktopApp runs in standalone tests.");
        }

        await SyncSmartDesktopAsync(nextLayout, State.IsNarrow);
    }

    private sealed record DesktopOpenViewArgs(string Input, string View, string Section);

    [On]
    public async Task Open(DesktopOpen request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.App is null) { await SyncSmartDesktopAsync(State.LayoutMode, State.IsNarrow); return; }
        if (Decision.Check("open", DesktopAppState.OpenableAppRule(request.App)) is { } refused) { await RecordRejectionAsync(refused.Rejection); return; }

        var app = request.App.Trim().ToLowerInvariant();
        if (await TryRouteSpecialistToInoAsync(app)) { return; }

        if (request.View is { } view)
        {
            var trimmedView = view.Trim();
            var forwarded = await TryInvokeOptionalSlotAsync(() => Apps[$"apps/{app}"].Request("open", trimmedView), $"Falling back to app.{app} screen for open view.");
            if (!forwarded)
            {
                await TryInvokeOptionalSlotAsync(
                    () => Screens["app." + app].Request("open", DigitalBrain.Invocation.RuntimeProtocol.SerializeToElement(new DesktopOpenViewArgs(trimmedView, trimmedView, trimmedView), typeof(DesktopOpenViewArgs))),
                    "The app was not there to receive its open view.");
            }
        }

        var brain = TryCurrentBrain();
        await ApplyDesktopWorkspaceChangeAsync(
            s => DesktopAppState.OpenWorkspaceApp(s, app, brain: brain),
            ws => BrainId.IsValid(brain)
                && AppRef.TryOf(new BrainId(brain), app, out var appRef)
                && ws.Windows.Any(w => w.Tabs is { Length: > 0 } && w.Tabs.Any(t => string.Equals(t.App, appRef.Key, StringComparison.OrdinalIgnoreCase)))
                    ? null
                    : Decision.Check("open", DesktopAppState.WindowLimitInRule(ws.Windows))?.Rejection);
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
        if (!decision.IsAccepted) { await RecordRejectionAsync(decision.Rejection); return; }

        RecordedRejection = null;
        await Show(decision.Next);
        if (TryCurrentBrain() is not { Length: > 0 } brain) { return; }
        var isPin = request.Pinned;
        await ApplyDesktopWorkspaceChangeAsync(s =>
        {
            if (s.Pins.Any(p => string.Equals(p.Id, appId, StringComparison.OrdinalIgnoreCase)) == isPin) { return (null, []); }
            var appKey = AppRef.Of(new BrainId(brain), appId).Key;
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
        return Decision.Check("unpin", DesktopAppState.UnpinnableAppRule(request.AppId)) is { } refused
            ? RecordRejectionAsync(refused.Rejection)
            : Pin(new DesktopPinApp(request.AppId, Pinned: false));
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
        return Decision.Check("close-app", DesktopAppState.OpenableAppRule(request.App)) is { } refused
            ? RecordRejectionAsync(refused.Rejection)
            : ApplyDesktopWorkspaceChangeAsync(s => DesktopAppState.CloseWorkspaceApp(s, request.App!, TryCurrentBrain()));
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
                    return Decision.Check("newtab", DesktopAppState.WindowExistsInRule(openWins, request.WindowId), DesktopAppState.TabLimitInRule(openWins, request.WindowId))?.Rejection;
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
    public Task<DesktopShellProjection> ReadProjection() => Task.FromResult(Project(State, TryCurrentBrain()));
    public static DesktopShellProjection Project(DesktopAppState state, string? brain = null) => DesktopProjectionBuilder.Build(state, brain);

    private Task RecordRejectionAsync(Rejected rejection)
    {
        RecordedRejection = rejection;
        return PublishEphemeral(rejection);
    }

    protected override Node Render(DesktopAppState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        return DesktopViewRenderer.Render(DesktopProjectionBuilder.Build(state, TryCurrentBrain()), Child<ITabStripControl, TabStripProps>);
    }
}
