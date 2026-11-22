using System.Collections.Concurrent;

namespace DigitalBrain.Components;

/// <summary>Display metadata for a component's user-facing screen.</summary>
public sealed record ComponentFace(
    string Title,
    string Description = "",
    string? Icon = null,
    string? Category = null);

/// <summary>Declared operation on a component, derived from an <c>[On]</c> handler.</summary>
public sealed record OperationSpec(
    string Name,
    string Description = "",
    Type? Input = null);

/// <summary>
/// Declared metadata for a component: its id, the slots its neurons declare, optional screen face, and declared operations.
/// </summary>
public sealed record ComponentManifest(
    ComponentId Id,
    SlotNeed[] Slots,
    ComponentFace? Face = null,
    OperationSpec[]? Operations = null)
{
    /// <summary>Declared operations on this component.</summary>
    public OperationSpec[] Operations { get; init; } = Operations ?? [];

    /// <summary>Returns a copy with <paramref name="additional"/> merged by name, preserving order.</summary>
    internal ComponentManifest Merge(ComponentManifest additional)
    {
        var mergedSlots = MergeSlots(additional.Slots).Slots;
        var mergedFace = Face ?? additional.Face;
        var mergedOps = MergeOperations(additional.Operations);
        return this with { Slots = mergedSlots, Face = mergedFace, Operations = mergedOps };
    }

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

    private OperationSpec[] MergeOperations(OperationSpec[] additional)
    {
        if (additional.Length == 0) { return Operations; }
        if (Operations.Length == 0) { return additional; }
        var seen = new HashSet<string>(Operations.Select(static o => o.Name), StringComparer.Ordinal);
        var merged = new List<OperationSpec>(Operations);
        foreach (var op in additional)
        {
            if (seen.Add(op.Name)) { merged.Add(op); }
        }
        return [.. merged];
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
        ByComponent.AddOrUpdate(manifest.Id, manifest, (_, existing) => existing.Merge(manifest));
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

    /// <summary>Returns the manifest registered for the component owning <paramref name="neuronClass"/>, or <c>null</c> if none.</summary>
    public static ComponentManifest? Of(Type neuronClass)
    {
        ArgumentNullException.ThrowIfNull(neuronClass);
        return ComponentRegistry.Of(neuronClass) is { } component ? Of(component) : null;
    }

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
