using DigitalBrain;
using DigitalBrain.Kernel;
using DigitalBrain.Log;
using DigitalBrain.Neurons;
using DigitalBrain.Rules;
using DigitalBrain.UI;
using AppRef = IntoChat.OS.Host.Primitives.AppRef;
using IntoChat.OS.Modules.Apps;
using IntoChat.OS.Modules.Flutter;
using IntoChat.OS.Modules.Flutter.Viewport;
using IntoChat.OS.Modules.Flutter.Workspace;
using IntoChat.OS.UI;

namespace IntoChat.OS.Apps.Desktop;

public sealed partial class DesktopApp
{
    internal async Task<T> TryReadOptionalSlotAsync<T>(Func<Task<T>> read, T fallback, string context)
    {
        try { return await read(); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log.LogSwallowed(ex, context);
            return fallback;
        }
    }

    internal async Task<bool> TryInvokeOptionalSlotAsync(Func<Task> action, string context)
    {
        try { await action(); return true; }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log.LogSwallowed(ex, context);
            return false;
        }
    }

    private async Task SyncSmartDesktopAsync(string layoutMode, bool narrow)
    {
        var normalizedLayout = DesktopAppState.NormalizeLayoutMode(layoutMode);

        var (preset, barLayout) = UserSettings is { } settings
            ? await TryReadOptionalSlotAsync(async () => { var s = await settings.Read(); return (s.StylePreset, s.BarLayout); }, (State.StylePreset, State.BarLayout), "UserSettings is optional.")
            : (State.StylePreset, State.BarLayout);

        var activeNeurons = DebuggerReads is { } debuggerReads
            ? await TryReadOptionalSlotAsync(async () => { var t = await debuggerReads.Topology(); return t.Nodes.Count > 0 ? t.Nodes.Count : 10; }, 10, "DebuggerReads is optional.")
            : 10;

        var tiles = InstalledApps is { } installedApps
            ? await TryReadOptionalSlotAsync<AppTile[]?>(async () => await installedApps.List(), State.Tiles, "InstalledApps is optional when DesktopApp runs in standalone tests.")
            : State.Tiles;

        var (deployedUnits, deployedUnitCount) = SynapseStudio is { } synapseStudio
            ? await TryReadOptionalSlotAsync(async () =>
            {
                var units = await synapseStudio.List();
                var aliases = units
                    .Select(u => u.Aliases.FirstOrDefault() ?? u.SourceKey)
                    .Where(alias => !string.IsNullOrWhiteSpace(alias))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(alias => alias, StringComparer.OrdinalIgnoreCase)
                    .ToArray();
                return (aliases, units.Length);
            }, (State.DeployedUnits ?? [], 0), "SynapseStudio is optional.")
            : (State.DeployedUnits ?? [], 0);

        if (deployedUnitCount > 0 && activeNeurons == 10) { activeNeurons += deployedUnitCount; }

        var (windows, pins, favs, focusedViewId) = Workspace is { } workspace
            ? await TryReadOptionalSlotAsync(async () =>
            {
                var ws = await workspace.Read();
                var wsWindows = ws.Windows.Take(DesktopAppState.MaxWindows).ToArray();
                var wsPins = ws.Pins
                    .Where(static p => !string.Equals(p.Id, "desktop", StringComparison.OrdinalIgnoreCase))
                    .Take(DesktopAppState.MaxPins)
                    .ToArray();
                var wsFavs = ws.Favourites.Take(DesktopAppState.MaxFavourites).ToArray();
                var wsFocused = wsWindows.Length == 0
                    ? null
                    : wsWindows.Any(w => (w.Tabs ?? []).Any(t => string.Equals(t.Id, ws.FocusedViewId, StringComparison.Ordinal)))
                        ? ws.FocusedViewId
                        : wsWindows[^1].ActiveTabId;
                return (wsWindows, wsPins, wsFavs, wsFocused);
            }, (State.Windows ?? [], State.Pins ?? [], State.Favourites ?? [], State.FocusedViewId), "Workspace is optional.")
            : (State.Windows ?? [], State.Pins ?? [], State.Favourites ?? [], State.FocusedViewId);

        await SyncWindowViewportsAsync(windows);

        var widgetsText = Records is { } recordsSlot
            ? await TryReadOptionalSlotAsync(async () => string.Join(" · ", (await recordsSlot.Read()).Widgets), "", "Records is optional.")
            : "";

        var pinnedApps = pins.Length > 0
            ? pins.Select(p => p.Id)
                .Where(id => !string.IsNullOrWhiteSpace(id) && id is not ("ino" or "desktop"))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(DesktopAppState.MaxPins)
                .ToArray()
            : (State.PinnedApps ?? ["ino"]);

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
            await TryInvokeOptionalSlotAsync(async () =>
            {
                for (var attempt = 0; attempt < 2; attempt++)
                {
                    var wsState = await workspace.Read();
                    workspaceAvailable = true;
                    if (validateWorkspace?.Invoke(wsState) is { } wsRejection) { rejected = wsRejection; break; }

                    var (update, closed) = change(wsState);
                    closedWindowIds = closed;
                    if (update is null) { break; }
                    try { await workspace.Apply(update, $"desktop:{Guid.NewGuid():N}:{attempt}", wsState.Revision); break; }
                    catch (RevisionConflictException ex) when (attempt == 0)
                    {
                        Log.LogSwallowed(ex, "Workspace revision conflict on initial attempt; retrying.");
                    }
                }
            }, "Workspace is optional.");

            if (!workspaceAvailable && validateStandalone is not null) { rejected = validateStandalone(); }
        }
        else if (validateStandalone is not null) { rejected = validateStandalone(); }

        if (rejected is not null) { await RecordRejectionAsync(rejected); return; }

        await ClearViewportsAsync(closedWindowIds);
        await SyncSmartDesktopAsync(State.LayoutMode, State.IsNarrow);
    }

    private async Task SyncWindowViewportsAsync(IReadOnlyList<WorkspaceWindow> windows)
    {
        if (TryCurrentBrain() is not { Length: > 0 } brain) { return; }
        foreach (var win in windows)
        {
            if (win is null || string.IsNullOrWhiteSpace(win.Id) || win.Tabs is not { Length: > 0 }) { continue; }
            var activeTab = win.Tabs.FirstOrDefault(t => string.Equals(t.Id, win.ActiveTabId, StringComparison.Ordinal)) ?? win.Tabs[0];
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

    private Task SetViewportTargetAsync(string brain, string winId, WorkspaceViewRef? target, string failureContext)
    {
        if (string.IsNullOrWhiteSpace(winId)) { return Task.CompletedTask; }
        return TryInvokeOptionalSlotAsync(async () =>
        {
            var viewport = Viewports[AppRef.Of(new BrainId(brain), AppName).ChildNeuron(UIVocabulary.ViewportType, $"window/{winId}")];
            for (var attempt = 0; attempt < 2; attempt++)
            {
                var currentVp = await viewport.Read();
                if (currentVp.Target == target) { return; }
                try { await viewport.Set(target, currentVp.Revision); return; }
                catch (RevisionConflictException ex) when (attempt == 0)
                {
                    Log.LogSwallowed(ex, "Viewport revision conflict on initial attempt; retrying.");
                }
            }
        }, failureContext);
    }
}
