using System.Collections.Concurrent;

namespace DigitalBrain.Components;

/// <summary>
/// Declared metadata for a component: its id and the slots its neurons declare.
/// </summary>
public sealed record ComponentManifest(
    ComponentId Id,
    SlotNeed[] Slots)
{
    /// <summary>Returns a copy with <paramref name="additional"/> slots merged by name, preserving order.</summary>
    internal ComponentManifest MergeSlots(SlotNeed[] additional)
    {
        if (additional.Length == 0) { return this; }
        var seen = new HashSet<string>(Slots.Select(static s => s.Name), StringComparer.Ordinal);
        var merged = new List<SlotNeed>(Slots);
        foreach (var slot in additional)
        {
            if (seen.Add(slot.Name)) { merged.Add(slot); }
        }
        return this with { Slots = [.. merged] };
    }
}

/// <summary>
/// Process-wide registry of component manifests populated by generated <c>[ModuleInitializer]</c> methods.
/// </summary>
public static class ComponentManifests
{
    private static readonly ConcurrentDictionary<ComponentId, ComponentManifest> ByComponent = new();
    private static readonly ConcurrentDictionary<Type, SlotNeed[]> ByNeuronClass = new();

    /// <summary>Registers or merges <paramref name="manifest"/> for its component id.</summary>
    public static void Register(ComponentManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentException.ThrowIfNullOrWhiteSpace(manifest.Id.Value, nameof(manifest));
        ByComponent.AddOrUpdate(manifest.Id, manifest, (_, existing) => existing.MergeSlots(manifest.Slots));
    }

    /// <summary>Registers <paramref name="manifest"/> and records its slots for <paramref name="neuronClass"/>.</summary>
    public static void Register(Type neuronClass, ComponentManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(neuronClass);
        Register(manifest);
        ByNeuronClass[neuronClass] = manifest.Slots;
    }

    /// <summary>Returns the manifest registered for <paramref name="id"/>, or <c>null</c> if none.</summary>
    public static ComponentManifest? Of(ComponentId id)
        => ByComponent.TryGetValue(id, out var manifest) ? manifest : null;

    /// <summary>Returns all registered component manifests.</summary>
    public static IReadOnlyCollection<ComponentManifest> All => [.. ByComponent.Values];

    /// <summary>Returns the declared slots for <paramref name="neuronClass"/>, or an empty array when it declares none.</summary>
    public static SlotNeed[] SlotsOf(Type neuronClass)
    {
        ArgumentNullException.ThrowIfNull(neuronClass);
        if (ByNeuronClass.TryGetValue(neuronClass, out var direct)) { return direct; }
        if (ComponentRegistry.Of(neuronClass) is { } component && ByComponent.TryGetValue(component, out var manifest))
        {
            return manifest.Slots;
        }
        return [];
    }

    /// <summary>Returns <see langword="true"/> when <paramref name="neuronClass"/> declares at least one slot.</summary>
    public static bool HasSlots(Type neuronClass) => SlotsOf(neuronClass).Length > 0;
}
