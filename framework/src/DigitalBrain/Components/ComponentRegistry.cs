using System.Collections.Concurrent;

namespace DigitalBrain.Components;

/// <summary>
/// Process-wide mapping from a neuron's CLR class to the <see cref="ComponentId"/> that owns it.
/// </summary>
public static class ComponentRegistry
{
    private static readonly ConcurrentDictionary<Type, ComponentId> Registrations = new();

    /// <summary>Associates <paramref name="neuronClass"/> with <paramref name="component"/>.</summary>
    public static void Register(Type neuronClass, ComponentId component)
    {
        ArgumentNullException.ThrowIfNull(neuronClass);
        ArgumentException.ThrowIfNullOrWhiteSpace(component.Value, nameof(component));
        Registrations[neuronClass] = component;
    }

    /// <summary>Returns the owning component registered for <paramref name="neuronClass"/>, or <c>null</c> if none.</summary>
    public static ComponentId? Of(Type neuronClass)
    {
        ArgumentNullException.ThrowIfNull(neuronClass);
        return Registrations.TryGetValue(neuronClass, out var component) ? component : null;
    }
}
