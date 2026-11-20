using System.Globalization;
using System.Reflection;
using System.Text;
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
