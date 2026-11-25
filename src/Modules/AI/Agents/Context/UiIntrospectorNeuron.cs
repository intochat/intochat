using System.Reflection;
using System.Text;
using System.Text.Json;
using DigitalBrain;
using DigitalBrain.Invocation;
using DigitalBrain.Log;
using DigitalBrain.Neurons;
using DigitalBrain.Rules;
using DigitalBrain.State;
using DigitalBrain.UI;
using IntoChat.OS.Host.Logging;
using IntoChat.OS.Modules.Flutter.Screens;
using IntoChat.OS.UI;

namespace IntoChat.OS.Modules.AI.Agents.Context;

// Revision and StateGraphs only. A store written by the kind+key introspector also holds "trackedNeurons"; it is ignored on load
// and dropped by the next save.
[GenerateSerializer, Alias("ui.introspector.state")]
[ExportableState]
internal sealed record UiIntrospectorState
{
    [Id(0)] public long Revision { get; init; }
    [Id(2)] public UiStateGraphSnapshot[] StateGraphs { get; init; } = [];
}

// Reads screens only through the brain's screen directory, which draws each child in place under its child key path; a brain without
// one shows no screens.
[Neuron(IUiIntrospector.NeuronType, State = "ui-introspector")]
internal sealed class UiIntrospectorNeuron : Neuron<UiIntrospectorState>, IUiIntrospector
{
    private const int MaxStoredStateGraphs = 32;
    private const int MaxOpenScreens = 8;
    private const int MaxViewJsonChars = 4_000;
    private const int MaxSummaryNodes = 12;
    private const int MaxContextSummaryChars = 2_400;

    private NeuronId DirectoryId => new(Id.Brain, IScreenDirectory.TypeName, "main");

    public async Task<UiScreenSnapshot?> InspectScreen(string appId)
    {
        if (string.IsNullOrWhiteSpace(appId) || !Implements<IScreenDirectory>(DirectoryId))
        {
            return null;
        }

        try
        {
            return Snapshot(await Get<IScreenDirectory>(DirectoryId).Read(appId.Trim()).ConfigureAwait(true));
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            OsConsoleLog.For<UiIntrospectorNeuron>().LogSwallowed(error, "reading a screen through the screen directory");
            return null;
        }
    }

    public async Task<UiScreenSnapshot[]> InspectOpenScreens()
    {
        if (!Implements<IScreenDirectory>(DirectoryId))
        {
            return [];
        }

        ScreenSummary[] summaries;
        try
        {
            summaries = await Get<IScreenDirectory>(DirectoryId).List().ConfigureAwait(true);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            OsConsoleLog.For<UiIntrospectorNeuron>().LogSwallowed(error, "listing screens through the screen directory");
            return [];
        }

        var screens = new List<UiScreenSnapshot>();
        var open = summaries
            .Where(s => s.OpenInWorkspace)
            .OrderByDescending(s => s.Focused)
            .ThenBy(s => s.AppId, StringComparer.Ordinal)
            .Take(MaxOpenScreens);
        foreach (var summary in open)
        {
            if (await InspectScreen(summary.AppId).ConfigureAwait(true) is { } screen)
            {
                screens.Add(screen);
            }
        }

        return [.. screens];
    }

    public async Task<UiStateGraphSnapshot> ExploreStateGraph(string stateOrScreenTypeName, int maxStates = 32)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stateOrScreenTypeName);
        var clampedMax = Math.Clamp(maxStates, 1, 128);
        var resolvedType = ResolveTargetType(stateOrScreenTypeName.Trim());
        await Check(
            "ExploreStateGraph",
            Decision.Rule(
                "resolvable_state_or_screen_type",
                resolvedType,
                static t => t is null,
                $"Could not resolve Aggregate state or AggregateScreen type '{stateOrScreenTypeName}'."));

        var snapshot = UnfoldStateGraph(resolvedType!, clampedMax);
        await UpsertStateGraphAsync(snapshot).ConfigureAwait(true);
        return snapshot;
    }

    public async Task PublishStateGraph(UiStateGraphSnapshot graph)
    {
        ArgumentNullException.ThrowIfNull(graph);
        ArgumentException.ThrowIfNullOrWhiteSpace(graph.Name);
        await UpsertStateGraphAsync(graph).ConfigureAwait(true);
    }

    public Task<UiStateGraphSnapshot[]> ListStateGraphs()
        => Task.FromResult<UiStateGraphSnapshot[]>([.. State.StateGraphs]);

    public async Task<UiIntrospectionReport> SyncContext(string contextKey = IUiIntrospector.DefaultContextKey)
    {
        var normalizedContextKey = string.IsNullOrWhiteSpace(contextKey)
            ? IUiIntrospector.DefaultContextKey
            : StripBrainPrefix(contextKey.Trim());
        var screens = await InspectOpenScreens().ConfigureAwait(true);
        var graphs = State.StateGraphs;
        var summary = BuildContextSummary(screens, graphs);

        var contextNeuron = Get<IAgentContext>(new NeuronId(Id.Brain, "agent-context", normalizedContextKey));
        await contextNeuron.Upsert(new ContextEntry("ui.introspector", summary, [typeof(IUiIntrospector).FullName!])).ConfigureAwait(true);

        var next = State with { Revision = State.Revision + 1 };
        await Save(next, new UiIntrospectorSynced(Id.FullKey(), next.Revision, screens.Length, graphs.Length)).ConfigureAwait(true);

        return new UiIntrospectionReport
        {
            Revision = next.Revision,
            Screens = screens,
            StateGraphs = [.. graphs],
            ContextSummary = summary,
        };
    }

    private async Task UpsertStateGraphAsync(UiStateGraphSnapshot snapshot)
    {
        var updated = State.StateGraphs
            .Where(g => !string.Equals(g.Name, snapshot.Name, StringComparison.OrdinalIgnoreCase))
            .Append(snapshot)
            .TakeLast(MaxStoredStateGraphs)
            .ToArray();

        var next = State with { Revision = State.Revision + 1, StateGraphs = updated };
        await Save(next, new UiIntrospectorSynced(Id.FullKey(), next.Revision, 0, next.StateGraphs.Length)).ConfigureAwait(true);
    }

    private static UiScreenSnapshot? Snapshot(ScreenSnapshot? screen)
    {
        if (screen is null)
        {
            return null;
        }

        var nodes = screen.Controls.Select(c => new UiScreenNode(c.Key, c.Kind, c.Label, c.Value)).ToArray();
        var viewJson = screen.View is null ? "" : JsonSerializer.Serialize(screen.View, RuntimeProtocol.OptionsFor(typeof(View)));
        return new UiScreenSnapshot
        {
            AppId = screen.AppId,
            Title = screen.Title,
            Kind = screen.Kind,
            Generation = screen.Generation,
            Nodes = nodes,
            ViewJson = viewJson.Length <= MaxViewJsonChars ? viewJson : viewJson[..MaxViewJsonChars] + "...",
            Summary = Summarize(nodes),
        };
    }

    private static string Summarize(UiScreenNode[] nodes)
    {
        if (nodes.Length == 0)
        {
            return "no keyed nodes";
        }

        var parts = nodes.Take(MaxSummaryNodes).Select(n => n.Value is null
            ? $"{n.Key}={n.Kind} \"{n.Label}\""
            : $"{n.Key}={n.Kind} \"{n.Label}\" value=\"{(n.Value.Length <= 32 ? n.Value : n.Value[..29] + "...")}\"");
        var more = nodes.Length > MaxSummaryNodes ? $"; +{nodes.Length - MaxSummaryNodes} more" : "";
        return string.Join("; ", parts) + more;
    }

    private string StripBrainPrefix(string key)
    {
        var prefix = Id.Brain.Value + "/";
        return key.StartsWith(prefix, StringComparison.Ordinal) ? key[prefix.Length..] : key;
    }

    private static Type? ResolveTargetType(string name)
    {
        foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            if (assembly.IsDynamic)
            {
                continue;
            }

            var direct = assembly.GetType(name, throwOnError: false, ignoreCase: false);
            if (direct is not null)
            {
                return direct;
            }
        }

        foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            if (assembly.IsDynamic)
            {
                continue;
            }

            Type[] candidates;
            try
            {
                candidates = assembly.GetTypes();
            }
            catch (ReflectionTypeLoadException ex)
            {
                candidates = ex.Types.OfType<Type>().ToArray();
            }

            var match = candidates.FirstOrDefault(t =>
                string.Equals(t.FullName, name, StringComparison.OrdinalIgnoreCase)
                || string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase));
            if (match is not null)
            {
                return match;
            }
        }

        return null;
    }

    private static UiStateGraphSnapshot UnfoldStateGraph(Type targetType, int maxStates)
    {
        var screenStateType = FindAggregateScreenStateType(targetType);
        if (screenStateType is not null)
        {
            if (targetType.GetConstructor(Type.EmptyTypes) is not null)
            {
                var screenInstance = Activator.CreateInstance(targetType)!;
                var explorerType = typeof(AggregateExplorer<>).MakeGenericType(screenStateType);
                var exploreScreen = explorerType
                    .GetMethods(BindingFlags.Public | BindingFlags.Static)
                    .Single(m => m.Name == nameof(AggregateExplorer<object>.ExploreScreen) && !m.IsGenericMethodDefinition);
                var graphObj = exploreScreen.Invoke(null, [screenInstance, null, maxStates, null])!;
                return ExtractGraphSnapshot(targetType.Name, targetType.FullName ?? targetType.Name, screenStateType, graphObj);
            }

            targetType = screenStateType;
        }

        if (!targetType.IsClass || targetType.IsAbstract || targetType.GetConstructor(Type.EmptyTypes) is null)
        {
            throw new ArgumentException($"Type '{targetType.FullName}' must be a concrete reference type with a public parameterless constructor to unfold its StateGraph.");
        }

        var stateExplorer = typeof(AggregateExplorer<>).MakeGenericType(targetType);
        var exploreMethod = stateExplorer
            .GetMethods(BindingFlags.Public | BindingFlags.Static)
            .Single(m => m.Name == nameof(AggregateExplorer<object>.Explore));
        var stateGraph = exploreMethod.Invoke(null, [null, null, maxStates, null])!;
        return ExtractGraphSnapshot(targetType.Name, targetType.FullName ?? targetType.Name, targetType, stateGraph);
    }

    private static Type? FindAggregateScreenStateType(Type type)
    {
        for (var cur = type; cur is not null; cur = cur.BaseType)
        {
            if (cur.IsGenericType && cur.GetGenericTypeDefinition() == typeof(AggregateScreenBase<>))
            {
                return cur.GetGenericArguments()[0];
            }
        }
        return null;
    }

    private static UiStateGraphSnapshot ExtractGraphSnapshot(string name, string fullTypeName, Type stateType, object graphObj)
    {
        var graphType = typeof(StateGraph<>).MakeGenericType(stateType);
        var isHealthy = (bool)graphType.GetProperty(nameof(StateGraph<object>.IsHealthy))!.GetValue(graphObj)!;
        var truncated = (bool)graphType.GetProperty(nameof(StateGraph<object>.Truncated))!.GetValue(graphObj)!;
        var dot = (string)graphType.GetMethod(nameof(StateGraph<object>.ToDot))!.Invoke(graphObj, null)!;

        var rawStates = (System.Collections.IEnumerable)graphType.GetProperty(nameof(StateGraph<object>.States))!.GetValue(graphObj)!;
        var stateNodeType = typeof(StateGraphState<>).MakeGenericType(stateType);
        var idProp = stateNodeType.GetProperty("Id")!;
        var depthProp = stateNodeType.GetProperty("Depth")!;
        var labelProp = stateNodeType.GetProperty("Label")!;

        var nodes = new List<UiStateGraphNode>();
        foreach (var item in rawStates)
        {
            nodes.Add(new UiStateGraphNode(
                (string)idProp.GetValue(item)!,
                (int)depthProp.GetValue(item)!,
                (string)labelProp.GetValue(item)!));
        }

        var transitions = ((IReadOnlyList<StateGraphTransition>)graphType.GetProperty(nameof(StateGraph<object>.Transitions))!.GetValue(graphObj)!)
            .Select(t => new UiStateGraphEdge
            {
                FromId = t.FromId,
                ToId = t.ToId,
                Command = t.Command,
                Accepted = t.Accepted,
                Signals = [.. t.Signals],
                BrokenRules = [.. t.BrokenRules],
                Messages = [.. t.Messages],
            })
            .ToArray();

        var violations = ((IReadOnlyList<ExplorerViolation>)graphType.GetProperty(nameof(StateGraph<object>.Violations))!.GetValue(graphObj)!)
            .Select(v => $"[{v.StateId}] {v.Kind} '{v.Name}': {v.Message}")
            .ToArray();

        return new UiStateGraphSnapshot
        {
            Name = name,
            TypeName = fullTypeName,
            IsHealthy = isHealthy,
            Truncated = truncated,
            States = [.. nodes],
            Transitions = transitions,
            Violations = violations,
            Dot = dot,
        };
    }

    private static string BuildContextSummary(UiScreenSnapshot[] screens, UiStateGraphSnapshot[] graphs)
    {
        var sb = new StringBuilder();
        sb.AppendLine("[Live UI Introspection]");
        sb.Append("Open screens (").Append(screens.Length).AppendLine("):");
        foreach (var screen in screens)
        {
            sb.Append("- ").Append(screen.AppId).Append(" \"").Append(screen.Title).Append("\" (").Append(screen.Kind)
                .Append(", generation ").Append(screen.Generation).Append("): ").AppendLine(screen.Summary);
        }

        if (graphs.Length > 0)
        {
            sb.AppendLine("Accordant StateGraphs:");
            foreach (var g in graphs.Take(8))
            {
                var accepted = g.Transitions.Count(t => t.Accepted);
                var guarded = g.Transitions.Length - accepted;
                sb.Append("- ").Append(g.Name)
                    .Append(": ").Append(g.States.Length).Append(" state(s), ")
                    .Append(accepted).Append(" accepted / ").Append(guarded).Append(" guarded transition(s), ")
                    .AppendLine(g.IsHealthy ? "healthy" : $"{g.Violations.Length} violation(s)");
            }
        }

        var full = sb.ToString().TrimEnd();
        return full.Length <= MaxContextSummaryChars ? full : full[..(MaxContextSummaryChars - 3)] + "...";
    }
}
