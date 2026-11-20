using System.Reflection;
using System.Text;
using System.Text.Json;
using DigitalBrain;
using DigitalBrain.Contracts;
using DigitalBrain.Invocation;
using DigitalBrain.Kernel;
using DigitalBrain.Log;
using DigitalBrain.Neurons;
using DigitalBrain.Rules;
using DigitalBrain.State;
using IntoChat.OS.Modules.UI;
using IntoChat.OS.UI;
using IntoChat.OS.Host.Logging;

namespace IntoChat.OS.Modules.AI.Agents.Context;

[GenerateSerializer, Alias("ui.introspector.tracked-ref")]
internal sealed record TrackedUiNeuronRef
{
    [Id(0)] public string NeuronKind { get; init; } = "";
    [Id(1)] public string Key { get; init; } = "";

    public TrackedUiNeuronRef() { }

    public TrackedUiNeuronRef(string neuronKind, string key)
    {
        NeuronKind = neuronKind;
        Key = key;
    }
}

[GenerateSerializer, Alias("ui.introspector.state")]
[ExportableState]
internal sealed record UiIntrospectorState
{
    [Id(0)] public long Revision { get; init; }
    [Id(1)] public TrackedUiNeuronRef[] TrackedNeurons { get; init; } = [];
    [Id(2)] public UiStateGraphSnapshot[] StateGraphs { get; init; } = [];
}

[Neuron(IUiIntrospector.NeuronType, State = "ui-introspector")]
internal sealed class UiIntrospectorNeuron(IBrainObserver? observer = null)
    : Neuron<UiIntrospectorState>, IUiIntrospector
{
    private const int MaxTrackedNeurons = 128;
    private const int MaxStoredStateGraphs = 32;
    private const int MaxStateJsonChars = 2_000;
    private const int MaxContextSummaryChars = 2_400;

    private static readonly MethodInfo ContextGetMethod = typeof(INeuronContext)
        .GetMethods(BindingFlags.Public | BindingFlags.Instance)
        .Single(m => m.Name == nameof(INeuronContext.Get) && m.IsGenericMethodDefinition && m.GetParameters().Length == 1);

    public Task<UiContractSchema[]> Contracts(string? kindFilter = null)
    {
        var schemas = DiscoverUiContracts(kindFilter).Select(c => c.Schema).ToArray();
        return Task.FromResult(schemas);
    }

    public async Task<UiNeuronSnapshot?> InspectNeuron(string neuronKind, string key)
    {
        if (string.IsNullOrWhiteSpace(neuronKind) || string.IsNullOrWhiteSpace(key))
        {
            return null;
        }

        var normalizedKind = neuronKind.Trim();
        var normalizedKey = StripBrainPrefix(key.Trim());
        var discovered = DiscoverUiContracts(normalizedKind)
            .FirstOrDefault(c => string.Equals(c.Schema.NeuronKind, normalizedKind, StringComparison.OrdinalIgnoreCase));
        if (discovered is null)
        {
            return null;
        }

        return await ReadUiNeuronAsync(discovered, normalizedKey).ConfigureAwait(false);
    }

    public async Task<UiNeuronSnapshot[]> InspectLiveNeurons(string? kindFilter = null)
    {
        var contractsByKind = DiscoverUiContracts(kindFilter)
            .ToDictionary(c => c.Schema.NeuronKind, StringComparer.OrdinalIgnoreCase);
        if (contractsByKind.Count == 0)
        {
            return [];
        }

        var candidates = new List<(DiscoveredUiContract Contract, string Key)>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        if (observer is not null)
        {
            await foreach (var instance in observer.Instances(null, CancellationToken.None).ConfigureAwait(false))
            {
                if (!contractsByKind.TryGetValue(instance.Type, out var contract))
                {
                    continue;
                }

                var relativeKey = ExtractBrainScopedKey(instance.Key, instance.Type);
                if (relativeKey is null)
                {
                    continue;
                }

                var dedupeKey = $"{contract.Schema.NeuronKind}:{relativeKey}";
                if (seen.Add(dedupeKey))
                {
                    candidates.Add((contract, relativeKey));
                }
            }
        }

        foreach (var tracked in State.TrackedNeurons)
        {
            if (!contractsByKind.TryGetValue(tracked.NeuronKind, out var contract))
            {
                continue;
            }

            var dedupeKey = $"{contract.Schema.NeuronKind}:{tracked.Key}";
            if (seen.Add(dedupeKey))
            {
                candidates.Add((contract, tracked.Key));
            }
        }

        var snapshots = new List<UiNeuronSnapshot>(candidates.Count);
        foreach (var (contract, relativeKey) in candidates.OrderBy(c => c.Contract.Schema.NeuronKind, StringComparer.Ordinal).ThenBy(c => c.Key, StringComparer.Ordinal))
        {
            var snap = await ReadUiNeuronAsync(contract, relativeKey).ConfigureAwait(false);
            if (snap is not null)
            {
                snapshots.Add(snap);
            }
        }

        return [.. snapshots];
    }

    public async Task TrackNeuron(string neuronKind, string key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(neuronKind);
        ArgumentException.ThrowIfNullOrWhiteSpace(key);

        var normalizedKind = neuronKind.Trim();
        var normalizedKey = StripBrainPrefix(key.Trim());
        var matchingContracts = DiscoverUiContracts(normalizedKind);
        await Check(
            "TrackNeuron",
            Decision.Rule(
                "known_ui_kind",
                (Contracts: matchingContracts, Kind: normalizedKind),
                static arg => !arg.Contracts.Any(c => string.Equals(c.Schema.NeuronKind, arg.Kind, StringComparison.OrdinalIgnoreCase)),
                $"'{normalizedKind}' is not a registered Modules.UI neuron kind."));

        if (State.TrackedNeurons.Any(t => string.Equals(t.NeuronKind, normalizedKind, StringComparison.OrdinalIgnoreCase)
            && string.Equals(t.Key, normalizedKey, StringComparison.Ordinal)))
        {
            return;
        }

        var tracked = State.TrackedNeurons
            .Append(new TrackedUiNeuronRef(normalizedKind, normalizedKey))
            .TakeLast(MaxTrackedNeurons)
            .ToArray();
        var next = State with { Revision = State.Revision + 1, TrackedNeurons = tracked };
        await Save(next, new UiIntrospectorSynced(Id.FullKey(), next.Revision, next.TrackedNeurons.Length, next.StateGraphs.Length)).ConfigureAwait(false);
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
        await UpsertStateGraphAsync(snapshot).ConfigureAwait(false);
        return snapshot;
    }

    public async Task PublishStateGraph(UiStateGraphSnapshot graph)
    {
        ArgumentNullException.ThrowIfNull(graph);
        ArgumentException.ThrowIfNullOrWhiteSpace(graph.Name);
        await UpsertStateGraphAsync(graph).ConfigureAwait(false);
    }

    public Task<UiStateGraphSnapshot[]> ListStateGraphs()
        => Task.FromResult<UiStateGraphSnapshot[]>([.. State.StateGraphs]);

    public async Task<UiIntrospectionReport> SyncContext(string contextKey = IUiIntrospector.DefaultContextKey)
    {
        var normalizedContextKey = string.IsNullOrWhiteSpace(contextKey)
            ? IUiIntrospector.DefaultContextKey
            : StripBrainPrefix(contextKey.Trim());
        var contracts = DiscoverUiContracts(null).Select(c => c.Schema).ToArray();
        var liveNeurons = await InspectLiveNeurons(null).ConfigureAwait(false);
        var graphs = State.StateGraphs;
        var summary = BuildContextSummary(contracts, liveNeurons, graphs);

        var contextNeuron = Get<IAgentContext>(new NeuronId(Id.Brain, "agent-context", normalizedContextKey));
        await contextNeuron.Upsert(new ContextEntry("ui.introspector", summary, [typeof(IUiIntrospector).FullName!])).ConfigureAwait(false);

        var next = State with { Revision = State.Revision + 1 };
        await Save(next, new UiIntrospectorSynced(Id.FullKey(), next.Revision, liveNeurons.Length, graphs.Length)).ConfigureAwait(false);

        return new UiIntrospectionReport
        {
            Revision = next.Revision,
            Contracts = contracts,
            LiveNeurons = liveNeurons,
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
        await Save(next, new UiIntrospectorSynced(Id.FullKey(), next.Revision, next.TrackedNeurons.Length, next.StateGraphs.Length)).ConfigureAwait(false);
    }

    private async Task<UiNeuronSnapshot?> ReadUiNeuronAsync(DiscoveredUiContract discovered, string relativeKey)
    {
        var targetId = new NeuronId(Id.Brain, discovered.Schema.NeuronKind, relativeKey);
        string? rawJson = null;

        if (observer is not null)
        {
            try
            {
                var element = await observer.State(targetId, CancellationToken.None).ConfigureAwait(false);
                if (element is { } jsonElement)
                {
                    rawJson = jsonElement.GetRawText();
                }
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                OsConsoleLog.For<UiIntrospectorNeuron>().LogSwallowed(error, "reading UI neuron state from brain observer");
            }
        }

        if (rawJson is null && discovered.ReadMethod is not null)
        {
            try
            {
                var proxy = ContextGetMethod.MakeGenericMethod(discovered.InterfaceType).Invoke(Context, [targetId])!;
                var task = (Task)discovered.ReadMethod.Invoke(proxy, null)!;
                var stateObj = await TaskBoxing.ReturnResultAsync(task, discovered.ReadMethod.ReturnType).ConfigureAwait(false);
                if (stateObj is not null)
                {
                    rawJson = JsonSerializer.Serialize(stateObj, JsonSerializerOptions.Web);
                }
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                OsConsoleLog.For<UiIntrospectorNeuron>().LogSwallowed(error, "reading UI neuron state through contract Read()");
                return null;
            }
        }

        if (rawJson is null)
        {
            return null;
        }

        var boundedJson = rawJson.Length <= MaxStateJsonChars ? rawJson : rawJson[..MaxStateJsonChars] + "...";
        return new UiNeuronSnapshot
        {
            NeuronKey = targetId.FullKey(),
            NeuronKind = discovered.Schema.NeuronKind,
            Key = relativeKey,
            ContractType = discovered.Schema.ContractType,
            StateJson = boundedJson,
            Summary = SummarizeStateJson(discovered.Schema.NeuronKind, rawJson),
        };
    }

    private string? ExtractBrainScopedKey(string instanceKey, string neuronKind)
    {
        if (NeuronId.TryParse(instanceKey, out var parsed))
        {
            return parsed.Brain == Id.Brain ? parsed.Key : null;
        }

        var brainPrefix = Id.Brain.Value + "/";
        if (!instanceKey.StartsWith(brainPrefix, StringComparison.Ordinal))
        {
            return null;
        }

        var remainder = instanceKey[brainPrefix.Length..];
        var typePrefix = neuronKind + "/";
        return remainder.StartsWith(typePrefix, StringComparison.Ordinal) ? remainder[typePrefix.Length..] : remainder;
    }

    private string StripBrainPrefix(string key)
    {
        var prefix = Id.Brain.Value + "/";
        return key.StartsWith(prefix, StringComparison.Ordinal) ? key[prefix.Length..] : key;
    }

    private sealed record DiscoveredUiContract(Type InterfaceType, MethodInfo? ReadMethod, UiContractSchema Schema);

    private static List<DiscoveredUiContract> DiscoverUiContracts(string? kindFilter)
    {
        var assembly = typeof(UiModule).Assembly;
        var exported = assembly.GetExportedTypes();
        var signalsByNamespace = exported
            .Where(t => typeof(Signal).IsAssignableFrom(t) && !t.IsAbstract)
            .Select(t => (Namespace: t.Namespace ?? "", Alias: t.GetCustomAttribute<SignalAttribute>(false)?.Alias ?? t.Name))
            .ToArray();

        var result = new List<DiscoveredUiContract>();
        foreach (var type in exported.Where(t => t.IsInterface))
        {
            var alias = type.GetCustomAttribute<NeuronAliasAttribute>(false)?.Alias;
            if (string.IsNullOrWhiteSpace(alias) || !alias.StartsWith("ui.", StringComparison.Ordinal))
            {
                continue;
            }

            if (!string.IsNullOrWhiteSpace(kindFilter)
                && !alias.Contains(kindFilter.Trim(), StringComparison.OrdinalIgnoreCase)
                && !type.Name.Contains(kindFilter.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var methods = type.GetMethods(BindingFlags.Public | BindingFlags.Instance)
                .OrderBy(m => m.Name, StringComparer.Ordinal)
                .Select(m => new UiContractMethod(
                    m.Name,
                    m.GetParameters()
                        .Where(p => p.ParameterType != typeof(CancellationToken))
                        .Select(p => $"{FormatTypeName(p.ParameterType)} {p.Name}")
                        .ToArray(),
                    FormatTypeName(m.ReturnType),
                    m.IsDefined(typeof(ReadOnlyCallAttribute), false)))
                .ToArray();

            var readMethod = type.GetMethod("Read", BindingFlags.Public | BindingFlags.Instance, Type.EmptyTypes);
            var stateType = readMethod is not null && readMethod.ReturnType.IsGenericType
                ? readMethod.ReturnType.GetGenericArguments()[0].Name
                : null;

            var ns = type.Namespace ?? "";
            var signals = signalsByNamespace
                .Where(s => s.Namespace == ns || s.Namespace.StartsWith(ns + ".", StringComparison.Ordinal))
                .Select(s => s.Alias)
                .Distinct(StringComparer.Ordinal)
                .OrderBy(a => a, StringComparer.Ordinal)
                .ToArray();

            result.Add(new DiscoveredUiContract(
                type,
                readMethod,
                new UiContractSchema
                {
                    NeuronKind = alias,
                    ContractType = type.FullName ?? type.Name,
                    StateType = stateType,
                    Methods = methods,
                    Signals = signals,
                }));
        }

        return [.. result.OrderBy(r => r.Schema.NeuronKind, StringComparer.Ordinal)];
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

    private static string SummarizeStateJson(string kind, string rawJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(rawJson);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
            {
                return kind;
            }

            var parts = new List<string>();
            foreach (var prop in doc.RootElement.EnumerateObject())
            {
                if (parts.Count >= 4)
                {
                    break;
                }

                switch (prop.Value.ValueKind)
                {
                    case JsonValueKind.String:
                        var s = prop.Value.GetString();
                        if (!string.IsNullOrWhiteSpace(s))
                        {
                            parts.Add($"{prop.Name}=\"{(s.Length <= 32 ? s : s[..29] + "...")}\"");
                        }
                        break;
                    case JsonValueKind.Number:
                    case JsonValueKind.True:
                    case JsonValueKind.False:
                        parts.Add($"{prop.Name}={prop.Value.GetRawText()}");
                        break;
                    case JsonValueKind.Array:
                        parts.Add($"{prop.Name}[{prop.Value.GetArrayLength()}]");
                        break;
                }
            }

            return parts.Count == 0 ? kind : string.Join(", ", parts);
        }
        catch (JsonException)
        {
            return kind;
        }
    }

    private static string BuildContextSummary(
        UiContractSchema[] contracts,
        UiNeuronSnapshot[] liveNeurons,
        UiStateGraphSnapshot[] graphs)
    {
        var sb = new StringBuilder();
        sb.AppendLine("[Live UI Introspection]");
        sb.Append("Modules.UI contracts (").Append(contracts.Length).Append("): ");
        sb.AppendLine(string.Join(", ", contracts.Select(c => c.NeuronKind)));

        if (liveNeurons.Length > 0)
        {
            sb.AppendLine("Active UI neurons:");
            foreach (var n in liveNeurons.Take(16))
            {
                sb.Append("- ").Append(n.NeuronKey).Append(" (").Append(n.NeuronKind).Append("): ").AppendLine(n.Summary);
            }
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

    private static string FormatTypeName(Type type)
    {
        if (!type.IsGenericType)
        {
            return type.Name;
        }

        var name = type.Name;
        var tick = name.IndexOf('`', StringComparison.Ordinal);
        if (tick >= 0)
        {
            name = name[..tick];
        }

        return $"{name}<{string.Join(", ", type.GetGenericArguments().Select(FormatTypeName))}>";
    }
}
