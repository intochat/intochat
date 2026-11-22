using System.Collections.Concurrent;

namespace DigitalBrain.Components;

/// <summary>Controls how <c>AccessGate</c> enforces declared component slot bindings on same-brain calls.</summary>
public enum BindingEnforcementMode
{
    /// <summary>Do not check component slot bindings.</summary>
    Off,

    /// <summary>Allow undeclared calls and report a <see cref="MissingBindingEvent"/>.</summary>
    Audit,

    /// <summary>Report a <see cref="MissingBindingEvent"/> and refuse undeclared calls with <see cref="UnauthorizedAccessException"/>.</summary>
    Enforce,
}

/// <summary>Recorded when a component neuron invokes a same-brain target neuron without a declared binding.</summary>
/// <param name="Brain">The brain in which the call occurred.</param>
/// <param name="Component">The calling component.</param>
/// <param name="Target">The target neuron that was not bound.</param>
/// <param name="Operation">The contract and method invoked.</param>
public sealed record MissingBindingEvent(BrainId Brain, ComponentId Component, NeuronId Target, string Operation);

/// <summary>Host options for component slot binding enforcement in <c>AccessGate</c>.</summary>
public sealed class BindingGateOptions
{
    /// <summary>The enforcement mode. Defaults to <see cref="BindingEnforcementMode.Off"/>.</summary>
    public BindingEnforcementMode Mode { get; set; } = BindingEnforcementMode.Off;

    /// <summary>Optional callback invoked whenever an undeclared call is observed in Audit or Enforce mode.</summary>
    public Action<MissingBindingEvent>? OnMissingBinding { get; set; }

    /// <summary>Optional set of component IDs exempt from binding checks (e.g., privileged platform components).</summary>
    public IReadOnlySet<string>? PrivilegedComponents { get; set; }
}

/// <summary>Tracks resolved slot bindings per (brain, component) for fast admission checks.</summary>
public interface IBindingIndex
{
    /// <summary>Records the active resolved bindings for <paramref name="component"/> in <paramref name="brain"/>.</summary>
    void Record(BrainId brain, ComponentId component, IReadOnlyList<Binding> bindings);

    /// <summary>Clears recorded bindings for <paramref name="component"/> in <paramref name="brain"/>.</summary>
    void Invalidate(BrainId brain, ComponentId component);

    /// <summary>Returns true if <paramref name="target"/> is bound to a slot of <paramref name="component"/> in <paramref name="brain"/>.</summary>
    bool IsBound(BrainId brain, ComponentId component, NeuronId target);
}

/// <summary>Thread-safe in-memory <see cref="IBindingIndex"/>.</summary>
public sealed class InMemoryBindingIndex : IBindingIndex
{
    private readonly ConcurrentDictionary<(BrainId Brain, ComponentId Component), HashSet<NeuronId>> _bindings = new();

    /// <inheritdoc />
    public void Record(BrainId brain, ComponentId component, IReadOnlyList<Binding> bindings)
    {
        ArgumentNullException.ThrowIfNull(bindings);
        var targets = new HashSet<NeuronId>();
        foreach (var binding in bindings)
        {
            switch (binding.Target)
            {
                case BindingTarget.Local local:
                    targets.Add(local.Neuron);
                    break;
                case BindingTarget.Offered offered:
                    targets.Add(offered.Neuron);
                    break;
            }
        }
        _bindings[(brain, component)] = targets;
    }

    /// <inheritdoc />
    public void Invalidate(BrainId brain, ComponentId component)
        => _bindings.TryRemove((brain, component), out _);

    /// <inheritdoc />
    public bool IsBound(BrainId brain, ComponentId component, NeuronId target)
        => _bindings.TryGetValue((brain, component), out var targets) && targets.Contains(target);
}
