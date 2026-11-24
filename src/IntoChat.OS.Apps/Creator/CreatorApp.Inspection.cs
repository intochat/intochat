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
    private async Task<Inspection> FlowRunAsync(string runId)
    {
        var flow = _flows.FirstOrDefault(f => string.Equals(f.FlowId, runId, StringComparison.Ordinal));
        if (flow is null)
        {
            return Inspection.None;
        }

        var subjectAlias = Blank(flow.Subject);
        var files = subjectAlias is not null
            ? await FilesAsync(subjectAlias)
            : new Dictionary<string, string>(StringComparer.Ordinal);
        var deployed = subjectAlias is not null ? FindDeployedUnit(subjectAlias) : null;
        if (files.Count == 0 && deployed is not null && !string.Equals(deployed.SourceKey, subjectAlias, StringComparison.Ordinal))
        {
            files = await FilesAsync(deployed.SourceKey);
        }

        var matchedSynapse = _synapses.FirstOrDefault(s =>
            string.Equals(s.Name, subjectAlias ?? flow.Type, StringComparison.Ordinal)
            || string.Equals(s.Id, $"flow:{flow.Type}", StringComparison.Ordinal));
        var passRate = flow.Status switch
        {
            FlowStatus.Completed => 1.0,
            FlowStatus.Failed or FlowStatus.Cancelled => 0.0,
            _ => Math.Clamp(flow.Fraction, 0.0, 1.0),
        };

        return new Inspection(Selection.Node, flow.FlowId)
        {
            Alias = subjectAlias ?? flow.Type,
            Files = files,
            Synapse = matchedSynapse,
            DeployedUnit = deployed,
            ScenarioPassRate = passRate,
        };
    }

    private async Task<Inspection> NodeAsync(string id)
    {
        var matchedSynapse = FindSynapseDefinition(id);
        var alias = UnitAlias(id) ?? matchedSynapse?.Name;
        if (alias is null)
        {
            var isApp = AppRef.TryParse(id, out var compiledApp);
            var appGraph = isApp ? await TryExploreGraphAsync($"app.{compiledApp.Name}") : null;
            return new Inspection(Selection.Node, id)
            {
                Notice = isApp ? "A compiled app." : "A kernel neuron.",
                IntrospectedGraph = appGraph,
                ScenarioPassRate = appGraph is null ? null : (appGraph.IsHealthy ? 1.0 : 0.0),
            };
        }

        var isSynapseSelection = id.StartsWith(Selection.SynapsePrefix, StringComparison.Ordinal);
        var isWorkflowOrFlowOnly = matchedSynapse?.Form is IntoChat.OS.Modules.Apps.Synapses.SynapseForm.Workflow
            or IntoChat.OS.Modules.Apps.Synapses.SynapseForm.Flow;

        string? state = null, view = null, notice = null;
        if (!id.StartsWith("draft:", StringComparison.Ordinal) && !isWorkflowOrFlowOnly)
        {
            if (DebuggerReads is { } reads)
            {
                try
                {
                    state = await reads.State(alias);
                    view = await reads.View(alias);
                }
                catch (Exception error) when (error is not OperationCanceledException)
                {
                    Log.LogDebug(error, "State read is optional.");
                    if (!isSynapseSelection)
                    {
                        notice = $"state unavailable: {error.Message}";
                    }
                }
            }
            else if (!isSynapseSelection)
            {
                notice = "state unavailable: debugger reads unavailable.";
            }
        }

        var deployed = FindDeployedUnit(alias);
        var files = await FilesAsync(alias);
        if (files.Count == 0 && deployed is not null && !string.Equals(deployed.SourceKey, alias, StringComparison.Ordinal))
        {
            files = await FilesAsync(deployed.SourceKey);
        }

        string? failureSummary = null;
        try
        {
            if (SynapseStudio is { } studio)
            {
                var failures = await studio.Failures(alias);
                failureSummary = failures.Text;
            }
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            Log.LogDebug(error, "Failure summary read is optional.");
        }

        var introspected = files.Count == 0 ? await TryExploreGraphAsync(alias) : null;
        var passRate = CreatorStateSpaceInspector.ComputeTranscriptPassRate(State.Transcript)
            ?? ComputeUnitPassRate(deployed, matchedSynapse, failureSummary, introspected, id.StartsWith("draft:", StringComparison.Ordinal));

        return new Inspection(Selection.Node, id)
        {
            Alias = alias,
            State = state,
            View = view,
            Files = files,
            Notice = notice,
            Synapse = matchedSynapse,
            DeployedUnit = deployed,
            IntrospectedGraph = introspected,
            ScenarioPassRate = passRate,
            FailureSummary = failureSummary,
        };
    }

    private async Task<IntoChat.OS.Modules.AI.Agents.Context.UiStateGraphSnapshot?> TryExploreGraphAsync(string targetName)
    {
        try
        {
            if (UiIntrospector is { } introspector)
            {
                var snapshot = await introspector.ExploreStateGraph(targetName, maxStates: 24);
                return snapshot is { States.Length: > 0 } ? snapshot : null;
            }
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            Log.LogDebug(error, "Optional IUiIntrospector state graph exploration skipped for {Target}.", targetName);
        }

        return null;
    }

    private static double? ComputeUnitPassRate(
        DigitalBrain.Units.DeployedSource? deployed,
        IntoChat.OS.Modules.Apps.Synapses.SynapseDefinition? synapse,
        string? failureSummary,
        IntoChat.OS.Modules.AI.Agents.Context.UiStateGraphSnapshot? introspected,
        bool isDraft)
    {
        if (isDraft) { return 0.5; }
        if (deployed is not null)
        {
            if (deployed.Status == DigitalBrain.Units.DeployStatus.Failed) { return 0.0; }
            if (deployed.Status == DigitalBrain.Units.DeployStatus.Staged) { return 0.75; }
            if (!string.IsNullOrWhiteSpace(failureSummary)
                && !failureSummary.Contains("no recorded failures", StringComparison.OrdinalIgnoreCase))
            {
                return 0.5;
            }

            return 1.0;
        }

        if (synapse is not null)
        {
            if (string.Equals(synapse.LastRun, "Failed", StringComparison.OrdinalIgnoreCase))
            {
                return synapse.Runs > 1 ? 0.5 : 0.0;
            }

            if (string.Equals(synapse.LastRun, "Succeeded", StringComparison.OrdinalIgnoreCase))
            {
                return 1.0;
            }

            return synapse.Runs > 0 ? 0.75 : 1.0;
        }

        if (introspected is not null)
        {
            return introspected.IsHealthy ? 1.0 : 0.0;
        }

        return null;
    }

    private IntoChat.OS.Modules.Apps.Synapses.SynapseDefinition? FindSynapseDefinition(string id)
    {
        if (id.StartsWith(Selection.SynapsePrefix, StringComparison.Ordinal))
        {
            var synId = id[Selection.SynapsePrefix.Length..];
            return _synapses.FirstOrDefault(s =>
                string.Equals(s.Id, synId, StringComparison.Ordinal)
                || string.Equals(s.Name, synId, StringComparison.Ordinal));
        }

        var alias = BrainGraph.AliasOf(id);
        return alias is null
            ? null
            : _synapses.FirstOrDefault(s => string.Equals(s.Name, alias, StringComparison.Ordinal));
    }

    private DigitalBrain.Units.DeployedSource? FindDeployedUnit(string alias)
        => _deployedUnits.FirstOrDefault(u =>
            string.Equals(u.SourceKey, alias, StringComparison.Ordinal)
            || u.Aliases.Contains(alias, StringComparer.Ordinal));

    private async Task<Inspection> EdgeAsync(string key)
    {
        if (BrainGraph.ParseEdge(key) is not { } edge)
        {
            return new Inspection(Selection.Edge, key) { Notice = "Not an edge of the graph." };
        }

        var alias = edge.Alias.Split('#')[0];
        if (DebuggerReads is not { } reads)
        {
            return new Inspection(Selection.Edge, key) { Alias = alias, Notice = "journal unavailable: debugger reads unavailable." };
        }

        try
        {
            var page = await reads.Signals(alias, edge.From == "*" ? null : edge.From, EdgeSignals);
            var edgeHops = TraceFold.Fold(page.Entries);
            return new Inspection(Selection.Edge, key)
            {
                Alias = alias,
                Signals = page.Entries,
                Hops = edgeHops,
                Notice = page.Entries.Length == 0 ? $"none in the last {IDebuggerReads.Window} signals" : null,
            };
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            Log.LogDebug(error, "Journal read is optional.");
            return new Inspection(Selection.Edge, key) { Alias = alias, Notice = $"journal unavailable: {error.Message}" };
        }
    }

    private async Task<Inspection> PulseAsync(string traceId)
    {
        if (TraceReader is not { } traceReader)
        {
            return new Inspection(Selection.Pulse, traceId) { Notice = "trace reader unavailable." };
        }

        var entries = new List<TraceEntry>();
        try
        {
            long since = 0;
            while (entries.Count < MaxHops)
            {
                var page = await traceReader.ReadTrace(traceId, since, DigitalBrain.Kernel.SignalPage.MaxLimit);
                entries.AddRange(page.Entries);
                if (page.Entries.Length < DigitalBrain.Kernel.SignalPage.MaxLimit) { break; }
                since = page.Entries[^1].Sequence;
            }
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            Log.LogDebug(error, "Trace reader is optional.");
            return new Inspection(Selection.Pulse, traceId) { Notice = $"trace reader unavailable: {error.Message}" };
        }

        var hops = TraceFold.Fold([.. entries.Take(MaxHops)]);
        return new Inspection(Selection.Pulse, traceId)
        {
            Hops = hops,
            Notice = hops.Length == 0 ? $"no journal entries carry trace {traceId}" : null,
        };
    }

    private async Task<Dictionary<string, string>> FilesAsync(string alias)
    {
        var files = new Dictionary<string, string>(StringComparer.Ordinal);
        if (SynapseStudio is { } studio)
        {
            try
            {
                foreach (var (path, text) in await studio.SourceFiles(alias)) { files[path] = text; }
                foreach (var (path, text) in await studio.Sidecars(alias)) { files[path] = text; }
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                Log.LogDebug(error, "Unit files are optional.");
            }
        }

        if (files.Count == 0 && !string.IsNullOrWhiteSpace(TryBrainIdValue()) && AppRef.TryOf(Id.Brain, alias, out var appRef))
        {
            try
            {
                var exported = await Apps[appRef.Key].Export();
                if (exported?.Files is { Count: > 0 })
                {
                    foreach (var (path, text) in exported.Files)
                    {
                        files[path] = text;
                    }
                }
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                Log.LogDebug(error, "Export fallback for installed app bundle failed.");
            }
        }

        return files;
    }

    private bool Known(Selection selection) => selection.Kind switch
    {
        Selection.Pulse => true,
        Selection.Edge => _topology.Edges.Any(e => BrainGraph.EdgeKey(e.SourceId, e.TargetId, BrainGraph.EdgeLabel(e.SignalType, e.Kind)) == selection.Id),
        _ when selection.Id.StartsWith("draft:", StringComparison.Ordinal) => _flows.Any(f => f.Type == BuildInner.Flow && f.Subject == selection.Id["draft:".Length..]),
        _ when selection.Id.StartsWith(Selection.SynapsePrefix, StringComparison.Ordinal) =>
            FindSynapseDefinition(selection.Id) is not null
            || (UnitAlias(selection.Id) is { } synAlias && FindDeployedUnit(synAlias) is not null),
        _ => _topology.Nodes.Any(n => n.Id == selection.Id)
            || (UnitAlias(selection.Id) is { } unitAlias
                && (FindDeployedUnit(unitAlias) is not null
                    || _synapses.Any(s => string.Equals(s.Name, unitAlias, StringComparison.Ordinal)))),
    };

    private string? SelectedAlias() => State.Selected is { Kind: Selection.Node } node ? UnitAlias(node.Id) : null;

    public static string? UnitAlias(string id)
    {
        if (string.IsNullOrWhiteSpace(id)) { return null; }
        if (id.StartsWith("draft:", StringComparison.Ordinal)) { return id["draft:".Length..]; }
        if (id.StartsWith(Selection.SynapsePrefix, StringComparison.Ordinal))
        {
            var body = id[Selection.SynapsePrefix.Length..];
            var colon = body.IndexOf(':', StringComparison.Ordinal);
            if (colon > 0 && colon + 1 < body.Length)
            {
                var formPrefix = body[..colon];
                if (formPrefix is "reaction" or "app" or "flow" or "workflow" or "agent" or "codeblock")
                {
                    return body[(colon + 1)..];
                }
            }

            return body.Length > 0 ? body : null;
        }

        return BrainGraph.AliasOf(id);
    }

}
