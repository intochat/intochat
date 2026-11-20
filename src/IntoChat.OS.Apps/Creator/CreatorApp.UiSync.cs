/*
# Creator
Role: Default
Shipped: true
Operations: open, select, trace, replay, scenario, share, run, approve, reject, ui.command

Topology, Accordant state-space, scenario & 1-event-at-a-time time-travel debugger for the DigitalBrain OS.
Left: catalog units, installed apps, active build/engineer flows, and studio launchers.
Center: live BrainScene topology (with causal replay pulses and Flame camera HUD), embedded Live Studio preview,
Accordant StateGraphView, and the ISlider + ICommandBar time-travel transport bar.
Right: 5-tab Modules.UI Inspector (State via IPropertyGrid, Source via ICodeEditor, Signals via ITable, State Graph, Scenarios).
*/
using System.Globalization;
using DigitalBrain;
using DigitalBrain.Confirmation;
using DigitalBrain.Flows;
using DigitalBrain.Neurons;
using DigitalBrain.Rules;
using DigitalBrain.UI;
using IntoChat.OS.Modules.Apps;
using IntoChat.OS.Modules.Apps.Approvals;
using IntoChat.OS.Modules.Apps.Debugger;
using IntoChat.OS.Modules.Apps.Flows;
using IntoChat.OS.Modules.Apps.Trace;
using IntoChat.OS.Modules.UI.CodeEditor;
using IntoChat.OS.Modules.UI.CommandBar;
using IntoChat.OS.Modules.UI.CommandBar.Signals;
using IntoChat.OS.Modules.UI.PropertyGrid;
using IntoChat.OS.Modules.UI.Slider;
using IntoChat.OS.Modules.UI.Slider.Signals;
using IntoChat.OS.Modules.UI.Table;
using IntoChat.OS.Modules.UI.Table.Signals;
using IntoChat.OS.Modules.UI.Tabs;
using IntoChat.OS.Modules.UI.Tabs.Signals;
using IntoChat.OS.UI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using AppRef = IntoChat.OS.Host.Primitives.AppRef;
using IntoChat.OS.Host.Primitives;

namespace IntoChat.OS.Apps.Creator;

public sealed partial class CreatorApp
{
    private async Task SyncUiNeuronsAsync(bool skipTabsConfigure = false)
    {
        try
        {
            var hops = ActiveTimelineHops;
            var steps = ReplayScript.Steps(hops);
            var stepCount = steps.Length;
            var cursor = State.ReplayCursor is { } c && stepCount > 0 ? Math.Clamp(c, 0, stepCount - 1) : (long?)null;

            if (!skipTabsConfigure && TryGetOptional<ITabs>(CreatorView.InspectorTabsKey) is { } tabs)
            {
                var currentTabs = await tabs.Read();
                if (currentTabs.Version == 0 || !string.Equals(currentTabs.SelectedId, State.InspectorTab, StringComparison.Ordinal))
                {
                    var tabsState = await tabs.Configure(
                    [
                        new UiTabItem("state", "State"),
                        new UiTabItem("source", "Source"),
                        new UiTabItem("signals", "Signals"),
                        new UiTabItem("state-graph", "State Graph"),
                        new UiTabItem("scenarios", "Scenarios"),
                    ], State.InspectorTab);
                    _lastSyncedTabsVersion = Math.Max(_lastSyncedTabsVersion, tabsState.Version);
                }
            }

            if (TryGetOptional<ISlider>(CreatorView.ReplaySliderKey) is { } slider)
            {
                var min = stepCount >= 2 ? 1d : 0d;
                var max = stepCount >= 2 ? stepCount : 1d;
                var val = stepCount == 0 ? 0d : (cursor is { } cur ? cur + 1d : stepCount);
                var sliderState = await slider.Configure(
                    ReplayScript.FormatCursorReadout(cursor, stepCount),
                    min: min,
                    max: max,
                    step: 1,
                    value: val,
                    isEnabled: stepCount > 0);
                _lastSyncedSliderVersion = sliderState.Version;
            }

            if (TryGetOptional<ICommandBar>(CreatorView.ReplayCommandBarKey) is { } transport)
            {
                var disabled = stepCount == 0;
                await transport.Configure(
                [
                    new CommandBarItem("first", "First", "chevrons-left", Disabled: disabled),
                    new CommandBarItem("prev", "Step Back", "chevron-left", Disabled: disabled),
                    new CommandBarItem("toggle", cursor is not null ? "Live" : "Replay", cursor is not null ? "play" : "pause", Disabled: disabled && cursor is null),
                    new CommandBarItem("next", "Step Forward", "chevron-right", Disabled: disabled),
                    new CommandBarItem("last", "Last", "chevrons-right", Disabled: disabled),
                ]);
            }

            if (TryGetOptional<IPropertyGrid>(CreatorView.InspectorStateKey) is { } propGrid)
            {
                var stateJson = ReplayScript.StateJsonAt(steps, cursor, _inspection.State);
                var title = cursor is { } idx && stepCount > 0
                    ? string.Create(CultureInfo.InvariantCulture, $"State · Event {idx + 1} / {stepCount}")
                    : (_inspection.Alias is { Length: > 0 } alias ? $"Live State · {alias}" : "Neuron State");
                await propGrid.LoadJson(title, stateJson, readOnly: true);
            }

            if (TryGetOptional<ICodeEditor>(CreatorView.InspectorSourceKey) is { } editor)
            {
                var ordered = CreatorInspector.OrderedFiles(_inspection.Files);
                var activeFile = CreatorInspector.ResolveActiveFile(State.SelectedSourceFile, ordered);
                if (activeFile is not null && _inspection.Files.TryGetValue(activeFile, out var content))
                {
                    await editor.Configure(activeFile, CreatorInspector.InferLanguage(activeFile), content, readOnly: true, wordWrap: false);
                }
                else
                {
                    await editor.Configure("NoUnitSelected.cs", "csharp", "// Select an installed unit to inspect its C# source.", readOnly: true, wordWrap: false);
                }
            }

            if (TryGetOptional<ITable>(CreatorView.InspectorSignalsKey) is { } signalsTable)
            {
                if (_inspection.Kind == Selection.Edge && _inspection.Signals.Count > 0)
                {
                    await signalsTable.Render(
                        $"Signals · {_inspection.Alias ?? _inspection.Id}",
                        [
                            new TableColumn("seq", "#"),
                            new TableColumn("at", "At"),
                            new TableColumn("publisher", "Publisher"),
                            new TableColumn("trace", "Trace"),
                        ],
                        [.. _inspection.Signals.Select(s => (IReadOnlyList<string>)
                        [
                            s.Sequence.ToString(CultureInfo.InvariantCulture),
                            s.At.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture),
                            s.Publisher,
                            s.TraceId ?? string.Empty,
                        ])]);
                }
                else if (hops.Count > 0)
                {
                    var firstAt = hops.Min(h => h.At);
                    var currentSeq = cursor is { } idx ? steps[idx].Sequence : (long?)null;
                    await signalsTable.Render(
                        State.TraceId is { Length: > 0 } tid ? $"Trace {tid}" : "Causal Signal Stream",
                        [
                            new TableColumn("seq", "#"),
                            new TableColumn("dt", "Δt"),
                            new TableColumn("publisher", "Publisher"),
                            new TableColumn("signal", "Signal"),
                            new TableColumn("cause", "Cause"),
                        ],
                        [.. hops.Select(h => (IReadOnlyList<string>)
                        [
                            (h.Sequence == currentSeq ? "▶ " : "") + h.Sequence.ToString(CultureInfo.InvariantCulture),
                            string.Create(CultureInfo.InvariantCulture, $"+{(h.At - firstAt).TotalMilliseconds:0}ms"),
                            TraceFold.Indent(h.Depth) + h.Publisher,
                            h.Alias,
                            h.Cause ?? string.Empty,
                        ])]);
                }
                else
                {
                    await signalsTable.Render(
                        "Causal Signal Stream",
                        [new TableColumn("seq", "#"), new TableColumn("signal", "Signal"), new TableColumn("publisher", "Publisher")],
                        []);
                }
            }
        }
#pragma warning disable CA1031
        catch (Exception error) when (error is not OperationCanceledException)
#pragma warning restore CA1031
        {
            Log.LogDebug(error, "Creator UI neuron sync skipped in minimal test host.");
        }
    }

}
