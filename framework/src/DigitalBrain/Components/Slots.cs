using System.Reflection;
using DigitalBrain.Contracts;
using DigitalBrain.Units;

namespace DigitalBrain.Components;

/// <summary>Resolves a <see cref="BindingTarget"/> to a typed proxy or capability for a neuron activation.</summary>
public interface ISlotTargetResolver
{
    /// <summary>Returns a typed interface to <paramref name="target"/>.</summary>
    T Resolve<T>(BindingTarget target) where T : class;

    /// <summary>Returns a typed interface to <paramref name="target"/>, or <c>null</c> when the target neuron type is not registered in this runtime.</summary>
    T? TryResolve<T>(BindingTarget target) where T : class => Resolve<T>(target);
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

    /// <summary>Returns the active <see cref="Binding"/> for <paramref name="slot"/>, or <c>null</c> if unbound.</summary>
    public Binding? BindingOf(string slot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(slot);
        return _bindings.TryGetValue(slot, out var binding) ? binding : null;
    }

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

    private static BindingTarget.Local? TryDefaultTarget(BrainId brain, SlotNeed need)
    {
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
