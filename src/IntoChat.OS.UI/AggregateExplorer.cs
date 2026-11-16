using System.Globalization;
using System.Reflection;
using System.Text;
using System.Text.Json;
using DigitalBrain;
using DigitalBrain.Contracts;
using DigitalBrain.Invocation;
using DigitalBrain.Neurons;
using DigitalBrain.Rules;
using DigitalBrain.UI;

namespace IntoChat.OS.UI;

/// <summary>
/// Supplies representative sample values for an Aggregate command parameter when explored by <see cref="AggregateExplorer{TState}"/>.
/// </summary>
[AttributeUsage(AttributeTargets.Parameter, AllowMultiple = false)]
public sealed class SampleAttribute(params object?[] values) : Attribute
{
    /// <summary>Sample values tried during BFS state-space exploration.</summary>
    public IReadOnlyList<object?> Values { get; } = values ?? [null];
}

/// <summary>
/// Marks a <see cref="bool"/> or <see cref="Rule"/> property or parameterless method on <c>TState</c> as a domain invariant
/// verified in every reachable state by <see cref="AggregateExplorer{TState}"/>.
/// </summary>
[AttributeUsage(AttributeTargets.Property | AttributeTargets.Method, Inherited = true)]
public sealed class InvariantAttribute(string? message = null) : Attribute
{
    /// <summary>Optional human-readable invariant description when marking a <see cref="bool"/> member.</summary>
    public string? Message { get; } = message;
}

/// <summary>
/// Excludes a public method on <c>TState</c> from automatic discovery in <see cref="AggregateExplorer{TState}"/>.
/// </summary>
[AttributeUsage(AttributeTargets.Method, Inherited = false)]
public sealed class IgnoreExploreAttribute : Attribute;

/// <summary>
/// Default parameter choice generator inspired by Accordant's <c>Accordant.Choose</c>, allowing tests to override
/// domain-specific input domains per parameter name or CLR type.
/// </summary>
public sealed class ChoiceSet
{
    private readonly Dictionary<string, IReadOnlyList<object?>> _byParameterName = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<Type, IReadOnlyList<object?>> _byType = [];

    /// <summary>Registers sample values for parameters named <paramref name="parameterName"/>.</summary>
    public ChoiceSet ForParameter<T>(string parameterName, params T[] values)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(parameterName);
        ArgumentNullException.ThrowIfNull(values);
        _byParameterName[parameterName] = [.. values.Cast<object?>()];
        return this;
    }

    /// <summary>Registers sample values for all parameters of type <typeparamref name="T"/>.</summary>
    public ChoiceSet ForType<T>(params T[] values)
    {
        ArgumentNullException.ThrowIfNull(values);
        _byType[typeof(T)] = [.. values.Cast<object?>()];
        return this;
    }

    /// <summary>Resolves candidate values for <paramref name="parameter"/>.</summary>
    public IReadOnlyList<object?> ValuesFor(ParameterInfo parameter)
    {
        ArgumentNullException.ThrowIfNull(parameter);

        if (parameter.GetCustomAttribute<SampleAttribute>() is { Values.Count: > 0 } sample)
        {
            return [.. sample.Values.Select(v => CoerceSample(v, parameter.ParameterType))];
        }

        if (parameter.Name is { Length: > 0 } name && _byParameterName.TryGetValue(name, out var byName) && byName.Count > 0)
        {
            return [.. byName.Select(v => CoerceSample(v, parameter.ParameterType))];
        }

        var underlying = Nullable.GetUnderlyingType(parameter.ParameterType) ?? parameter.ParameterType;
        if (_byType.TryGetValue(parameter.ParameterType, out var exactType) && exactType.Count > 0)
        {
            return exactType;
        }

        if (_byType.TryGetValue(underlying, out var underlyingValues) && underlyingValues.Count > 0)
        {
            return underlyingValues;
        }

        if (underlying == typeof(bool))
        {
            return [false, true];
        }

        if (underlying.IsEnum)
        {
            return [.. Enum.GetValues(underlying).Cast<object?>()];
        }

        if (underlying == typeof(string))
        {
            return parameter.HasDefaultValue && parameter.DefaultValue is string def && def.Length > 0
                ? [def, "alice"]
                : ["alice"];
        }

        if (underlying == typeof(int))
        {
            return [1, 0];
        }

        if (underlying == typeof(long))
        {
            return [1L, 0L];
        }

        if (underlying == typeof(decimal))
        {
            return [50m, 30m];
        }

        if (underlying == typeof(double))
        {
            return [50d, 10d];
        }

        if (parameter.HasDefaultValue)
        {
            return [parameter.DefaultValue];
        }

        return [parameter.ParameterType.IsValueType ? Activator.CreateInstance(parameter.ParameterType) : null];
    }

    private static object? CoerceSample(object? raw, Type targetType)
    {
        if (raw is null)
        {
            return null;
        }

        var underlying = Nullable.GetUnderlyingType(targetType) ?? targetType;
        if (underlying.IsInstanceOfType(raw))
        {
            return raw;
        }

        if (underlying == typeof(decimal))
        {
            return Convert.ToDecimal(raw, CultureInfo.InvariantCulture);
        }

        if (underlying.IsEnum)
        {
            return raw is string s ? Enum.Parse(underlying, s, ignoreCase: true) : Enum.ToObject(underlying, raw);
        }

        return Convert.ChangeType(raw, underlying, CultureInfo.InvariantCulture);
    }
}

/// <summary>One reachable aggregate state node in an unfolded <see cref="StateGraph{TState}"/>.</summary>
public sealed record StateGraphState<TState>(
    string Id,
    int Index,
    int Depth,
    string Label,
    TState State,
    string Fingerprint) where TState : class;

/// <summary>One directed command transition (accepted forward/self edge or refused self-loop) in a <see cref="StateGraph{TState}"/>.</summary>
public sealed record StateGraphTransition(
    string FromId,
    string ToId,
    string Command,
    bool Accepted,
    IReadOnlyList<string> Signals,
    IReadOnlyList<string> BrokenRules,
    IReadOnlyList<string> Messages);

/// <summary>Invariant or UI render violation discovered during <see cref="AggregateExplorer{TState}"/> verification.</summary>
public sealed record ExplorerViolation(
    string StateId,
    string StateLabel,
    string Kind,
    string Name,
    string Message);

/// <summary>
/// Accordant-style unfolded state-transition graph over <typeparamref name="TState"/>, capturing reachable states,
/// accepted transitions, rule-rejected self-loops, invariant violations, and UI render/key uniqueness checks.
/// </summary>
public sealed class StateGraph<TState>(
    IReadOnlyList<StateGraphState<TState>> states,
    IReadOnlyList<StateGraphTransition> transitions,
    IReadOnlyList<ExplorerViolation> violations,
    bool truncated) where TState : class
{
    /// <summary>Unique reachable aggregate states in BFS discovery order (<c>s0</c> is <c>new TState()</c>).</summary>
    public IReadOnlyList<StateGraphState<TState>> States { get; } = states;

    /// <summary>All explored command transitions (both accepted edges and refused self-loops).</summary>
    public IReadOnlyList<StateGraphTransition> Transitions { get; } = transitions;

    /// <summary>Subset of <see cref="Transitions"/> that were accepted.</summary>
    public IReadOnlyList<StateGraphTransition> AcceptedTransitions { get; } = [.. transitions.Where(t => t.Accepted)];

    /// <summary>Subset of <see cref="Transitions"/> that were refused by domain rules (self-loops).</summary>
    public IReadOnlyList<StateGraphTransition> RejectedTransitions { get; } = [.. transitions.Where(t => !t.Accepted)];

    /// <summary>Any <c>[Invariant]</c> or UI render/duplicate-key violations discovered across reachable states.</summary>
    public IReadOnlyList<ExplorerViolation> Violations { get; } = violations;

    /// <summary>True if exploration stopped at <c>maxStates</c> while additional states remained queued.</summary>
    public bool Truncated { get; } = truncated;

    /// <summary>True when zero invariant or UI render violations were found.</summary>
    public bool IsHealthy => Violations.Count == 0;

    /// <summary>Throws an <see cref="InvalidOperationException"/> listing all <see cref="Violations"/> when unhealthy.</summary>
    public StateGraph<TState> EnsureHealthy()
    {
        if (Violations.Count == 0)
        {
            return this;
        }

        var lines = Violations.Select(v => $"[{v.StateId} ({v.StateLabel})] {v.Kind} '{v.Name}': {v.Message}");
        throw new InvalidOperationException(
            $"AggregateExplorer<{typeof(TState).Name}> found {Violations.Count} violation(s):{Environment.NewLine}{string.Join(Environment.NewLine, lines)}");
    }

    /// <summary>
    /// Converts this unfolded state graph into a <see cref="BrainSceneNode"/> so <c>Creator</c> or any app
    /// can render the Aggregate's state &amp; rule topology directly in the UI.
    /// </summary>
    public BrainSceneNode ToScene(string? selectedStateId = null, bool includeRejectedSelfLoops = true)
    {
        const int maxRowsPerSubColumn = 4;
        const double xStep = 230.0;
        const double yStep = 124.0;

        var nodes = new List<BrainGraphNode>(States.Count);
        var byDepth = States.GroupBy(s => s.Depth).OrderBy(g => g.Key).ToList();

        var currentBaseX = 0.0;
        foreach (var depthGroup in byDepth)
        {
            var siblings = depthGroup.ToList();
            var subColumnCount = Math.Max(1, (int)Math.Ceiling(siblings.Count / (double)maxRowsPerSubColumn));

            for (var idx = 0; idx < siblings.Count; idx++)
            {
                var state = siblings[idx];
                var subCol = idx / maxRowsPerSubColumn;
                var subRow = idx % maxRowsPerSubColumn;
                var rowsInThisSubCol = Math.Min(maxRowsPerSubColumn, siblings.Count - (subCol * maxRowsPerSubColumn));
                var centeredY = (subRow - ((rowsInThisSubCol - 1) / 2.0)) * yStep;
                var x = currentBaseX + (subCol * xStep);
                var hasViolation = Violations.Any(v => v.StateId == state.Id);
                var status = hasViolation ? "error" : (state.Index == 0 ? "done" : "idle");

                var rejectedCount = Transitions.Count(t => !t.Accepted && t.FromId == state.Id);
                var subtitle = rejectedCount > 0
                    ? $"depth {state.Depth} · {rejectedCount} guarded"
                    : $"depth {state.Depth}";

                nodes.Add(new BrainGraphNode(
                    Id: state.Id,
                    Label: state.Label,
                    Uri: $"state://{typeof(TState).Name.ToLowerInvariant()}/{state.Id}",
                    Kind: state.Index == 0 ? "initial" : "state",
                    X: x,
                    Y: centeredY,
                    Z: 0,
                    Status: status,
                    Subtitle: subtitle));
            }

            currentBaseX += subColumnCount * xStep;
        }

        var synapses = new List<BrainGraphSynapse>(Transitions.Count);
        foreach (var edge in Transitions)
        {
            if (!edge.Accepted && !includeRejectedSelfLoops)
            {
                continue;
            }

            var label = edge.Accepted
                ? (edge.Signals.Count == 0 ? edge.Command : $"{edge.Command} -> {string.Join(", ", edge.Signals)}")
                : $"{edge.Command} [rejected: {string.Join(", ", edge.BrokenRules)}]";

            synapses.Add(new BrainGraphSynapse(
                FromId: edge.FromId,
                ToId: edge.ToId,
                SignalLabel: label,
                Active: edge.Accepted));
        }

        return new BrainSceneNode(nodes, synapses, selectedStateId ?? States.FirstOrDefault()?.Id)
        {
            PauseRotation = true,
        };
    }

    /// <summary>Exports a Graphviz DOT state-transition diagram (matching Accordant's state graph visualization).</summary>
    public string ToDot()
    {
        var sb = new StringBuilder();
        sb.AppendLine($"digraph {typeof(TState).Name} {{");
        sb.AppendLine("  rankdir=LR;");
        foreach (var state in States)
        {
            var shape = state.Index == 0 ? "doublecircle" : "box";
            sb.AppendLine($"  \"{EscapeDot(state.Id)}\" [label=\"{EscapeDot(state.Label)}\", shape={shape}];");
        }

        foreach (var edge in Transitions)
        {
            if (edge.Accepted)
            {
                var sig = edge.Signals.Count > 0 ? $" / {string.Join(",", edge.Signals)}" : string.Empty;
                sb.AppendLine($"  \"{EscapeDot(edge.FromId)}\" -> \"{EscapeDot(edge.ToId)}\" [label=\"{EscapeDot(edge.Command + sig)}\", color=\"#1D8A5B\"];");
            }
            else
            {
                var rules = string.Join(",", edge.BrokenRules);
                sb.AppendLine($"  \"{EscapeDot(edge.FromId)}\" -> \"{EscapeDot(edge.ToId)}\" [label=\"{EscapeDot($"{edge.Command} [{rules}]")}\", style=dashed, color=\"#C83B3B\"];");
            }
        }

        sb.AppendLine("}");
        return sb.ToString();
    }

    private static string EscapeDot(string text)
        => text.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal);
}

/// <summary>
/// Accordant-inspired pure in-memory BFS state-space explorer and invariant/UI verifier over any
/// <typeparamref name="TState"/> Aggregate.
/// </summary>
public static class AggregateExplorer<TState> where TState : class, new()
{
    private static ChoiceSet ResolveChoices(ChoiceSet? choices) => choices is not null ? choices : new ChoiceSet();

    /// <summary>
    /// Explores the state space reachable from <c>new TState()</c> up to <paramref name="maxStates"/>,
    /// checking <see cref="InvariantAttribute"/> members and verifying <paramref name="render"/> throws no exceptions
    /// and emits no duplicate interactive keys in any reachable state.
    /// </summary>
    public static StateGraph<TState> Explore(
        Func<TState, Node>? render = null,
        ChoiceSet? choices = null,
        int maxStates = 64,
        Func<TState, string>? stateLabel = null)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxStates, 1);

        var choiceSet = ResolveChoices(choices);
        var commandMethods = DiscoverCommandMethods();
        var invariants = DiscoverInvariants();

        var states = new List<StateGraphState<TState>>();
        var stateIdsByFingerprint = new Dictionary<string, string>(StringComparer.Ordinal);
        var transitions = new List<StateGraphTransition>();
        var seenTransitions = new HashSet<string>(StringComparer.Ordinal);
        var violations = new List<ExplorerViolation>();
        var queue = new Queue<StateGraphState<TState>>();

        var initial = new TState();
        var initialFp = Fingerprint(initial);
        var s0 = new StateGraphState<TState>("s0", 0, 0, FormatLabel(initial, stateLabel), initial, initialFp);
        states.Add(s0);
        stateIdsByFingerprint[initialFp] = s0.Id;
        queue.Enqueue(s0);

        var truncated = false;

        while (queue.Count > 0)
        {
            var current = queue.Dequeue();

            // 1. Check domain [Invariant] properties and methods on current state.
            CheckInvariants(current, invariants, violations);

            // 2. Render UI (if provided), verify zero exceptions & zero duplicate keys, and harvest concrete UI commands.
            var renderedCommands = VerifyRenderAndExtractCommands(current, render, violations);

            // 3. Build candidate invocations: UI-emitted commands first (exact parameters shown on screen), then ChoiceSet combinations.
            var invocations = BuildCandidateInvocations(commandMethods, renderedCommands, choiceSet);

            foreach (var invocation in invocations)
            {
                object? raw;
                try
                {
                    raw = invocation.Method.Invoke(current.State, invocation.Args);
                }
                catch (TargetInvocationException tie) when (tie.InnerException is not null)
                {
                    violations.Add(new ExplorerViolation(
                        current.Id,
                        current.Label,
                        "CommandException",
                        invocation.Label,
                        $"{tie.InnerException.GetType().Name}: {tie.InnerException.Message}"));
                    continue;
                }

                if (raw is Decision<TState> { IsRefused: true } refused)
                {
                    var transitionKey = $"{current.Id}|{invocation.Label}|refused|{string.Join(',', refused.Rejection.Rules)}";
                    if (seenTransitions.Add(transitionKey))
                    {
                        transitions.Add(new StateGraphTransition(
                            FromId: current.Id,
                            ToId: current.Id,
                            Command: invocation.Label,
                            Accepted: false,
                            Signals: [],
                            BrokenRules: refused.Rejection.Rules,
                            Messages: refused.Rejection.Messages));
                    }

                    continue;
                }

                TState nextState;
                IReadOnlyList<Signal> signals;
                if (raw is Decision<TState> { IsAccepted: true } accepted)
                {
                    nextState = accepted.Next;
                    signals = accepted.Events;
                }
                else if (raw is TState pureNext)
                {
                    nextState = pureNext;
                    signals = [];
                }
                else
                {
                    continue;
                }

                var nextFp = Fingerprint(nextState);
                if (!stateIdsByFingerprint.TryGetValue(nextFp, out var targetId))
                {
                    if (states.Count >= maxStates)
                    {
                        truncated = true;
                        continue;
                    }

                    targetId = $"s{states.Count}";
                    var created = new StateGraphState<TState>(
                        targetId,
                        states.Count,
                        current.Depth + 1,
                        FormatLabel(nextState, stateLabel),
                        nextState,
                        nextFp);
                    states.Add(created);
                    stateIdsByFingerprint[nextFp] = targetId;
                    queue.Enqueue(created);
                }

                var signalNames = signals.Select(SignalName).ToArray();
                var acceptedKey = $"{current.Id}|{invocation.Label}|accepted|{targetId}|{string.Join(',', signalNames)}";
                if (seenTransitions.Add(acceptedKey))
                {
                    transitions.Add(new StateGraphTransition(
                        FromId: current.Id,
                        ToId: targetId,
                        Command: invocation.Label,
                        Accepted: true,
                        Signals: signalNames,
                        BrokenRules: [],
                        Messages: []));
                }
            }
        }

        return new StateGraph<TState>(states, transitions, violations, truncated);
    }

    /// <summary>Explores an <see cref="AggregateScreen{TState}"/> directly using its <see cref="AggregateScreenBase{TState}.PreviewNode(TState)"/> projection.</summary>
    public static StateGraph<TState> ExploreScreen(
        AggregateScreenBase<TState> screen,
        ChoiceSet? choices = null,
        int maxStates = 64,
        Func<TState, string>? stateLabel = null)
    {
        ArgumentNullException.ThrowIfNull(screen);
        return Explore(screen.PreviewNode, choices, maxStates, stateLabel);
    }

    private sealed record CandidateInvocation(MethodInfo Method, object?[] Args, string Label);

    private static MethodInfo[] DiscoverCommandMethods()
        => [.. typeof(TState)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .Where(m => !m.IsSpecialName
                && m.DeclaringType != typeof(object)
                && !m.Name.Equals("Clone", StringComparison.Ordinal)
                && !m.Name.StartsWith('<')
                && m.GetCustomAttribute<IgnoreExploreAttribute>() is null
                && (m.ReturnType == typeof(TState) || m.ReturnType == typeof(Decision<TState>)))
            .OrderBy(m => m.MetadataToken)];

    private static MemberInfo[] DiscoverInvariants()
        => [.. typeof(TState)
            .GetMembers(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
            .Where(m => m.GetCustomAttribute<InvariantAttribute>(inherit: true) is not null)
            .OrderBy(m => m.MetadataToken)];

    private static void CheckInvariants(
        StateGraphState<TState> current,
        MemberInfo[] invariants,
        List<ExplorerViolation> violations)
    {
        foreach (var member in invariants)
        {
            var attr = member.GetCustomAttribute<InvariantAttribute>(inherit: true)!;
            object? value;
            try
            {
                value = member switch
                {
                    PropertyInfo prop => prop.GetValue(current.State),
                    MethodInfo method when method.GetParameters().Length == 0 => method.Invoke(current.State, null),
                    _ => null,
                };
            }
            catch (TargetInvocationException tie) when (tie.InnerException is not null)
            {
                violations.Add(new ExplorerViolation(current.Id, current.Label, "Invariant", member.Name, tie.InnerException.Message));
                continue;
            }

            switch (value)
            {
                case bool ok when !ok:
                    violations.Add(new ExplorerViolation(
                        current.Id,
                        current.Label,
                        "Invariant",
                        member.Name,
                        attr.Message ?? $"Invariant '{member.Name}' evaluated to false."));
                    break;

                case Rule { Broken: true } brokenRule:
                    violations.Add(new ExplorerViolation(
                        current.Id,
                        current.Label,
                        "Invariant",
                        brokenRule.Name,
                        brokenRule.Message));
                    break;
            }
        }
    }

    private static List<UiCommand> VerifyRenderAndExtractCommands(
        StateGraphState<TState> current,
        Func<TState, Node>? render,
        List<ExplorerViolation> violations)
    {
        var extracted = new List<UiCommand>();
        if (render is null)
        {
            return extracted;
        }

        Node root;
        try
        {
            using var scope = CommandBinding.PushOwner("explorer");
            root = render(current.State);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            violations.Add(new ExplorerViolation(
                current.Id,
                current.Label,
                "RenderException",
                "Render",
                $"{ex.GetType().Name}: {ex.Message}"));
            return extracted;
        }

        JsonElement wire;
        try
        {
            var view = new View("Explorer", NodeOwnerNormalizer.Normalize(root, "explorer"));
            wire = JsonSerializer.SerializeToElement(view, RuntimeProtocol.OptionsFor(typeof(View)));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            violations.Add(new ExplorerViolation(
                current.Id,
                current.Label,
                "WireSerialization",
                "View",
                $"{ex.GetType().Name}: {ex.Message}"));
            return extracted;
        }

        var seenKeys = new HashSet<string>(StringComparer.Ordinal);
        InspectWireTree(wire, current, seenKeys, extracted, violations);
        return extracted;
    }

    private static void InspectWireTree(
        JsonElement element,
        StateGraphState<TState> current,
        HashSet<string> seenKeys,
        List<UiCommand> extracted,
        List<ExplorerViolation> violations)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            if (element.TryGetProperty("key", out var keyProp)
                && keyProp.ValueKind == JsonValueKind.String
                && keyProp.GetString() is { Length: > 0 } key
                && IsAddressableNode(element))
            {
                if (!seenKeys.Add(key))
                {
                    violations.Add(new ExplorerViolation(
                        current.Id,
                        current.Label,
                        "DuplicateKey",
                        key,
                        $"Interactive node key '{key}' appears more than once in Render({current.Label})."));
                }
            }

            TryExtractUiCommand(element, "emit", extracted);
            TryExtractUiCommand(element, "onSubmit", extracted);

            foreach (var prop in element.EnumerateObject())
            {
                if (prop.Name is "emit" or "onSubmit" or "args")
                {
                    continue;
                }

                InspectWireTree(prop.Value, current, seenKeys, extracted, violations);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                InspectWireTree(item, current, seenKeys, extracted, violations);
            }
        }
    }

    private static bool IsAddressableNode(JsonElement obj)
        => (obj.TryGetProperty("kind", out var k) && k.ValueKind == JsonValueKind.String)
            || (obj.TryGetProperty("emit", out var e) && e.ValueKind == JsonValueKind.Object)
            || (obj.TryGetProperty("intent", out var i) && i.ValueKind == JsonValueKind.Object)
            || (obj.TryGetProperty("resolve", out var r) && r.ValueKind == JsonValueKind.Object)
            || (obj.TryGetProperty("onSubmit", out var s) && s.ValueKind == JsonValueKind.Object);

    private static void TryExtractUiCommand(JsonElement node, string propName, List<UiCommand> target)
    {
        if (!node.TryGetProperty(propName, out var emitObj) || emitObj.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        if (!emitObj.TryGetProperty("signal", out var sig) || sig.GetString() != UiCommand.SignalAlias)
        {
            return;
        }

        if (!emitObj.TryGetProperty("args", out var argsObj) || argsObj.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        var cmd = argsObj.Deserialize<UiCommand>(RuntimeProtocol.OptionsFor(typeof(UiCommand)));
        if (cmd is not null && !string.IsNullOrWhiteSpace(cmd.Method) && string.Equals(cmd.Target, "state", StringComparison.OrdinalIgnoreCase))
        {
            target.Add(cmd);
        }
    }

    private static List<CandidateInvocation> BuildCandidateInvocations(
        MethodInfo[] methods,
        List<UiCommand> renderedCommands,
        ChoiceSet choices)
    {
        var list = new List<CandidateInvocation>();
        var seenLabels = new HashSet<string>(StringComparer.Ordinal);

        // 1. Commands discovered directly on rendered UI buttons/switches/tabs (including disabled buttons, to prove server rules refuse them!).
        foreach (var cmd in renderedCommands)
        {
            var method = methods.FirstOrDefault(m => string.Equals(m.Name, cmd.Method, StringComparison.Ordinal));
            if (method is null)
            {
                continue;
            }

            var effectiveCmd = cmd.ValueIndex is not null && cmd.Value is null
                ? cmd with { Value = "sample" }
                : cmd;
            var args = AggregateScreenBase<TState>.BindArguments(method, effectiveCmd, CancellationToken.None);
            var label = FormatInvocation(method.Name, args);
            if (seenLabels.Add(label))
            {
                list.Add(new CandidateInvocation(method, args, label));
            }
        }

        // 2. Method-level parameter combinations from [Sample] / ChoiceSet.
        foreach (var method in methods)
        {
            var parameters = method.GetParameters();
            if (parameters.Length == 0)
            {
                var label = FormatInvocation(method.Name, []);
                if (seenLabels.Add(label))
                {
                    list.Add(new CandidateInvocation(method, [], label));
                }

                continue;
            }

            var perParam = parameters
                .Select(IReadOnlyList<object?> (p) => p.ParameterType == typeof(CancellationToken)
                    ? [CancellationToken.None]
                    : choices.ValuesFor(p))
                .ToArray();

            foreach (var combo in CartesianProduct(perParam))
            {
                var label = FormatInvocation(method.Name, combo);
                if (seenLabels.Add(label))
                {
                    list.Add(new CandidateInvocation(method, combo, label));
                }
            }
        }

        return list;
    }

    private static IEnumerable<object?[]> CartesianProduct(IReadOnlyList<object?>[] sequences)
    {
        if (sequences.Length == 0)
        {
            yield return [];
            yield break;
        }

        var indices = new int[sequences.Length];
        while (true)
        {
            var current = new object?[sequences.Length];
            for (var i = 0; i < sequences.Length; i++)
            {
                current[i] = sequences[i][indices[i]];
            }

            yield return current;

            var pos = sequences.Length - 1;
            while (pos >= 0 && ++indices[pos] >= sequences[pos].Count)
            {
                indices[pos] = 0;
                pos--;
            }

            if (pos < 0)
            {
                yield break;
            }
        }
    }

    private static string Fingerprint(TState state)
        => JsonSerializer.Serialize(state, RuntimeProtocol.OptionsFor(typeof(TState)));

    private static string FormatLabel(TState state, Func<TState, string>? customLabel)
    {
        if (customLabel is not null)
        {
            return customLabel(state);
        }

        var text = state.ToString() ?? typeof(TState).Name;
        var prefix = typeof(TState).Name + " ";
        if (text.StartsWith(prefix, StringComparison.Ordinal))
        {
            text = text[prefix.Length..];
        }

        return text.Length > 64 ? text[..61] + "…" : text;
    }

    private static string FormatInvocation(string methodName, object?[] args)
    {
        if (args.Length == 0)
        {
            return $"{methodName}()";
        }

        var formatted = args
            .Where(a => a is not CancellationToken)
            .Select(FormatArgLiteral);
        return $"{methodName}({string.Join(", ", formatted)})";
    }

    private static string FormatArgLiteral(object? arg) => arg switch
    {
        null => "null",
        string s => $"\"{s}\"",
        bool b => b ? "true" : "false",
        decimal m => m.ToString("0.##", CultureInfo.InvariantCulture),
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
        _ => arg.ToString() ?? "?",
    };

    private static string SignalName(Signal signal)
        => signal.GetType().GetCustomAttribute<SignalAttribute>(inherit: false)?.Alias
            ?? signal.GetType().Name;
}
