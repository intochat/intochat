using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Text.Json;
using DigitalBrain;
using DigitalBrain.Contracts;
using DigitalBrain.Invocation;
using DigitalBrain.Neurons;
using DigitalBrain.Rules;
using DigitalBrain.UI;
using IntoChat.OS.UI.Dispatch;

namespace IntoChat.OS.UI;

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

    /// <summary>Explores an <see cref="AggregateScreen{TState}"/> by type using its <see cref="AggregateScreenBase{TState}.PreviewNode(TState)"/> projection.</summary>
    public static StateGraph<TState> ExploreScreen<TScreen>(
        ChoiceSet? choices = null,
        int maxStates = 64,
        Func<TState, string>? stateLabel = null)
        where TScreen : AggregateScreenBase<TState>, new()
        => Explore(Activator.CreateInstance<TScreen>().PreviewNode, choices, maxStates, stateLabel);

    /// <summary>Projects a single <typeparamref name="TState"/> snapshot through <typeparamref name="TScreen"/>'s node renderer.</summary>
    public static Node PreviewNode<TScreen>(TState state)
        where TScreen : AggregateScreenBase<TState>, new()
        => Activator.CreateInstance<TScreen>().PreviewNode(state);

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
            Trace.TraceWarning("AggregateExplorer render failed for screen '{0}': {1}", current.Id, ex);
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
            Trace.TraceWarning("AggregateExplorer wire serialization failed for screen '{0}': {1}", current.Id, ex);
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

            TryExtractUiCommand(element, "command", extracted);
            TryExtractUiCommand(element, "close", extracted);
            TryExtractUiCommand(element, "onSubmit", extracted);

            foreach (var prop in element.EnumerateObject())
            {
                if (prop.Name is "command" or "close" or "onSubmit" or "args")
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
            || (obj.TryGetProperty("command", out var c) && c.ValueKind == JsonValueKind.Object)
            || (obj.TryGetProperty("close", out var cl) && cl.ValueKind == JsonValueKind.Object)
            || (obj.TryGetProperty("intent", out var i) && i.ValueKind == JsonValueKind.Object)
            || (obj.TryGetProperty("resolve", out var r) && r.ValueKind == JsonValueKind.Object)
            || (obj.TryGetProperty("onSubmit", out var s) && s.ValueKind == JsonValueKind.Object);

    private static void TryExtractUiCommand(JsonElement node, string propName, List<UiCommand> target)
    {
        if (!node.TryGetProperty(propName, out var commandObj) || commandObj.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        if (!commandObj.TryGetProperty("signal", out var sig) || sig.GetString() != UiCommand.SignalAlias)
        {
            return;
        }

        if (!commandObj.TryGetProperty("args", out var argsObj) || argsObj.ValueKind != JsonValueKind.Object)
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
