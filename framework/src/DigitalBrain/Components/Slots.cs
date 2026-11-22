using System.Reflection;
using DigitalBrain.Contracts;
using DigitalBrain.Units;

namespace DigitalBrain.Components;

/// <summary>Resolves a <see cref="BindingTarget"/> to a typed proxy or capability for a neuron activation.</summary>
public interface ISlotTargetResolver
{
    /// <summary>The installing brain of the neuron activation.</summary>
    BrainId Brain => default;

    /// <summary>Returns a typed interface to <paramref name="target"/>.</summary>
    T Resolve<T>(BindingTarget target) where T : class;

    /// <summary>Returns a typed interface to <paramref name="target"/>, or <c>null</c> when the target neuron type is not registered in this runtime.</summary>
    T? TryResolve<T>(BindingTarget target) where T : class => Resolve<T>(target);

    /// <summary>Returns whether <paramref name="id"/> names a neuron of this brain whose registered class implements <typeparamref name="T"/>.</summary>
    bool Implements<T>(NeuronId id) where T : class => false;
}

/// <summary>
/// Resolves dynamic keys of a type-level bound slot collection (<see cref="BindingTarget.NeuronType"/>).
/// </summary>
public sealed class SlotCollection<T> where T : class
{
    private readonly BrainId _brain;
    private readonly string _neuronType;
    private readonly ISlotTargetResolver _resolver;

    internal SlotCollection(BrainId brain, string neuronType, ISlotTargetResolver resolver)
    {
        _brain = brain;
        _neuronType = neuronType;
        _resolver = resolver;
    }

    /// <summary>The bound neuron type alias, or empty string for a polymorphic contract collection.</summary>
    public string NeuronType => _neuronType;

    /// <summary>Resolves the neuron with <paramref name="key"/> in this collection.</summary>
    public T this[string key]
    {
        get
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(key);
            EnsureTypedCollection();
            return _resolver.Resolve<T>(new BindingTarget.Local(new NeuronId(_brain, _neuronType, key)));
        }
    }

    /// <summary>Resolves the neuron at <paramref name="id"/> in this collection.</summary>
    public T this[NeuronId id]
    {
        get
        {
            ValidateAddress(id);
            return _resolver.Resolve<T>(new BindingTarget.Local(id));
        }
    }

    /// <summary>Resolves the neuron with <paramref name="key"/>, or returns <c>null</c> when its neuron type is not registered in this runtime.</summary>
    public T? TryGet(string key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        if (string.IsNullOrEmpty(_neuronType))
        {
            return null;
        }
        return _resolver.TryResolve<T>(new BindingTarget.Local(new NeuronId(_brain, _neuronType, key)));
    }

    /// <summary>Resolves the neuron at <paramref name="id"/>, or returns <c>null</c> when its neuron type is not registered or does not match.</summary>
    public T? TryGet(NeuronId id)
    {
        if (id.Brain != _brain)
        {
            return null;
        }
        if (!string.IsNullOrEmpty(_neuronType) && !string.Equals(id.Type, _neuronType, StringComparison.Ordinal))
        {
            return null;
        }
        return _resolver.TryResolve<T>(new BindingTarget.Local(id));
    }

    /// <summary>Whether the neuron with <paramref name="key"/> exists in this brain and implements <typeparamref name="T"/>.</summary>
    public bool Exists(string key)
    {
        if (string.IsNullOrWhiteSpace(key) || string.IsNullOrEmpty(_neuronType))
        {
            return false;
        }
        return _resolver.Implements<T>(new NeuronId(_brain, _neuronType, key));
    }

    /// <summary>Whether <paramref name="id"/> belongs to this brain, matches this collection's neuron type (if constrained), and implements <typeparamref name="T"/>.</summary>
    public bool Exists(NeuronId id)
    {
        if (id.Brain != _brain)
        {
            return false;
        }
        if (!string.IsNullOrEmpty(_neuronType) && !string.Equals(id.Type, _neuronType, StringComparison.Ordinal))
        {
            return false;
        }
        return _resolver.Implements<T>(id);
    }

    private void EnsureTypedCollection()
    {
        if (string.IsNullOrEmpty(_neuronType))
        {
            throw new InvalidOperationException($"Collection slot for '{typeof(T).Name}' is polymorphic and must be addressed by {nameof(NeuronId)}, not by key alone.");
        }
    }

    private void ValidateAddress(NeuronId id)
    {
        if (id.Brain != _brain)
        {
            throw new ArgumentException($"Neuron {id} belongs to another brain than {_brain}.", nameof(id));
        }
        if (!string.IsNullOrEmpty(_neuronType) && !string.Equals(id.Type, _neuronType, StringComparison.Ordinal))
        {
            throw new ArgumentException($"Neuron {id} has type '{id.Type}', expected '{_neuronType}'.", nameof(id));
        }
    }
}

/// <summary>
/// Resolves a neuron's declared <c>[Slot]</c> properties against the bindings loaded for this activation.
/// </summary>
public sealed class Slots
{
    private readonly ComponentId _component;
    private readonly ISlotTargetResolver _resolver;
    private readonly Dictionary<string, Binding> _bindings;

    /// <summary>Creates a slot resolver for one activation.</summary>
    public Slots(ComponentId component, IReadOnlyList<Binding> bindings, ISlotTargetResolver resolver)
    {
        ArgumentNullException.ThrowIfNull(bindings);
        ArgumentNullException.ThrowIfNull(resolver);
        _component = component;
        _resolver = resolver;
        _bindings = new Dictionary<string, Binding>(StringComparer.Ordinal);
        foreach (var binding in bindings)
        {
            _bindings[binding.Slot] = binding;
        }
    }

    /// <summary>Returns the resolved proxy or capability for <paramref name="slot"/>.</summary>
    /// <exception cref="SlotUnboundException">No binding exists for <paramref name="slot"/>.</exception>
    public T Get<T>(string slot) where T : class
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(slot);
        if (!_bindings.TryGetValue(slot, out var binding))
        {
            throw new SlotUnboundException(_component, slot);
        }
        return _resolver.Resolve<T>(binding.Target);
    }

    /// <summary>
    /// Returns the resolved proxy or capability for <paramref name="slot"/>, or <c>null</c> when the slot is unbound
    /// or its target neuron type is not registered in this runtime.
    /// </summary>
    public T? TryGet<T>(string slot) where T : class
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(slot);
        if (!_bindings.TryGetValue(slot, out var binding))
        {
            return null;
        }
        return _resolver.TryResolve<T>(binding.Target);
    }

    /// <summary>Returns the resolved <see cref="SlotCollection{T}"/> for <paramref name="slot"/>.</summary>
    /// <exception cref="SlotUnboundException">No binding exists for <paramref name="slot"/>.</exception>
    public SlotCollection<T> GetCollection<T>(string slot) where T : class
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(slot);
        if (!_bindings.TryGetValue(slot, out var binding))
        {
            throw new SlotUnboundException(_component, slot);
        }
        if (binding.Target is not BindingTarget.NeuronType neuronType)
        {
            throw new InvalidOperationException($"Slot '{slot}' on component '{_component}' is bound to '{binding.Target.GetType().Name}', not a collection target.");
        }
        return new SlotCollection<T>(_resolver.Brain, neuronType.TypeName, _resolver);
    }

    /// <summary>Returns the active <see cref="Binding"/> for <paramref name="slot"/>, or <c>null</c> if unbound.</summary>
    public Binding? BindingOf(string slot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(slot);
        return _bindings.TryGetValue(slot, out var binding) ? binding : null;
    }

    /// <summary>Updates the active in-memory binding for <paramref name="slot"/> in the current activation.</summary>
    public void Rebind(string slot, BindingTarget target)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(slot);
        ArgumentNullException.ThrowIfNull(target);
        _bindings[slot] = new Binding(slot, target);
    }

    /// <summary>Returns a typed interface to an offered neuron on another brain.</summary>
    public T Offered<T>(BrainId owner, NeuronId neuron) where T : class
        => _resolver.Resolve<T>(new BindingTarget.Offered(owner, neuron));

    /// <summary>
    /// Resolves activation bindings for <paramref name="needs"/> against <paramref name="installed"/> bindings and contract defaults.
    /// Throws <see cref="ActivationRefusedException"/> with <see cref="ActivationRefusedException.SlotUnbound"/> when any required slot remains unbound.
    /// </summary>
    public static IReadOnlyList<Binding> Resolve(
        BrainId brain,
        ComponentId component,
        IReadOnlyList<SlotNeed> needs,
        IReadOnlyList<Binding>? installed)
    {
        ArgumentNullException.ThrowIfNull(needs);
        var explicitBySlot = new Dictionary<string, Binding>(StringComparer.Ordinal);
        if (installed is not null)
        {
            foreach (var binding in installed)
            {
                explicitBySlot[binding.Slot] = binding;
            }
        }

        var resolved = new List<Binding>(needs.Count + explicitBySlot.Count);
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var need in needs)
        {
            if (explicitBySlot.TryGetValue(need.Name, out var bound))
            {
                if (seen.Add(need.Name)) { resolved.Add(bound); }
                continue;
            }

            if (TryDefaultTarget(brain, need) is { } defaultTarget)
            {
                if (seen.Add(need.Name)) { resolved.Add(new Binding(need.Name, defaultTarget)); }
                continue;
            }

            if (need.Required)
            {
                throw new ActivationRefusedException(
                    ActivationRefusedException.SlotUnbound,
                    $"Required slot '{need.Name}' on component '{component}' has no binding.");
            }
        }

        foreach (var binding in explicitBySlot.Values)
        {
            if (seen.Add(binding.Slot)) { resolved.Add(binding); }
        }

        return resolved;
    }

    private static BindingTarget? TryDefaultTarget(BrainId brain, SlotNeed need)
    {
        if (need.IsCollection)
        {
            if (need.Contract.GetCustomAttribute<DefaultNeuronTypeAttribute>(inherit: false) is { } defaultType
                && !string.IsNullOrWhiteSpace(defaultType.Type))
            {
                return new BindingTarget.NeuronType(defaultType.Type);
            }
            if (!need.Required)
            {
                return new BindingTarget.NeuronType(string.Empty);
            }
            if (need.Contract.GetCustomAttribute<NeuronAliasAttribute>(inherit: false) is { } alias
                && !string.IsNullOrWhiteSpace(alias.Alias))
            {
                return new BindingTarget.NeuronType(alias.Alias);
            }
            return null;
        }

        var neuronType = NeuronTypeOf(need.Contract);
        if (string.IsNullOrWhiteSpace(neuronType)) { return null; }

        var key = !string.IsNullOrWhiteSpace(need.Key)
            ? need.Key
            : DefaultKeyOf(need.Contract);
        if (string.IsNullOrWhiteSpace(key)) { return null; }

        return new BindingTarget.Local(new NeuronId(brain, neuronType, key));
    }

    private static string? NeuronTypeOf(Type contract)
    {
        if (contract.GetCustomAttribute<DefaultNeuronTypeAttribute>(inherit: false) is { } defaultType
            && !string.IsNullOrWhiteSpace(defaultType.Type))
        {
            return defaultType.Type;
        }
        if (contract.GetCustomAttribute<NeuronAliasAttribute>(inherit: false) is { } alias
            && !string.IsNullOrWhiteSpace(alias.Alias))
        {
            return alias.Alias;
        }
        return null;
    }

    private static string? DefaultKeyOf(Type contract)
    {
        if (ReadDefaultKey(contract) is { } direct) { return direct; }
        foreach (var inherited in contract.GetInterfaces())
        {
            if (ReadDefaultKey(inherited) is { } fromBase) { return fromBase; }
        }
        return null;
    }

    private static string? ReadDefaultKey(Type type)
    {
        const BindingFlags flags = BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly;
        if (type.GetField("DefaultKey", flags) is { } field && field.FieldType == typeof(string))
        {
            return field.GetValue(null) as string;
        }
        if (type.GetProperty("DefaultKey", flags) is { } prop && prop.PropertyType == typeof(string) && prop.GetMethod is not null)
        {
            return prop.GetValue(null) as string;
        }
        return null;
    }
}
