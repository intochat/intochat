using System.Collections.Concurrent;

namespace DigitalBrain.Components;

/// <summary>The role a component plays in the operating system.</summary>
public enum ComponentKind
{
    /// <summary>A module providing capabilities, neurons, or integrations.</summary>
    Module,
    /// <summary>A user-facing application or screen.</summary>
    App,
    /// <summary>A synapse-driven state unit.</summary>
    Unit,
    /// <summary>An autonomous agent component.</summary>
    Agent,
}

/// <summary>Display metadata for a component's user-facing screen.</summary>
public sealed record ComponentFace(
    string Title,
    string Description = "",
    string? Icon = null,
    string? Category = null,
    int? Dock = null,
    bool Maximized = false);

/// <summary>Declared operation on a component, derived from an <c>[On]</c> handler.</summary>
public sealed record OperationSpec(
    string Name,
    string Description = "",
    Type? Input = null,
    string? ParametersJson = null,
    string? SignalAlias = null);

/// <summary>
/// Declared metadata for a component: its id, kind, version, slots, optional screen face, declared operations, and contract/signal edges.
/// </summary>
public sealed record ComponentManifest(
    ComponentId Id,
    SlotNeed[] Slots,
    ComponentFace? Face = null,
    OperationSpec[]? Operations = null,
    ComponentKind Kind = ComponentKind.App,
    string Version = "1.0",
    Type[]? Provides = null,
    Type[]? Emits = null,
    Type[]? Reacts = null)
{
    /// <summary>Declared operations on this component.</summary>
    public OperationSpec[] Operations { get; init; } = Operations ?? [];

    /// <summary>Capability or contract types provided by this component.</summary>
    public Type[] Provides { get; init; } = Provides ?? [];

    /// <summary>Signal types emitted by this component.</summary>
    public Type[] Emits { get; init; } = Emits ?? [];

    /// <summary>Signal types this component reacts to.</summary>
    public Type[] Reacts { get; init; } = Reacts ?? [];

    /// <summary>Returns a copy with <paramref name="additional"/> merged by name/type, preserving order.</summary>
    internal ComponentManifest Merge(ComponentManifest additional)
    {
        var mergedSlots = MergeSlots(additional.Slots).Slots;
        var mergedFace = Face ?? additional.Face;
        var mergedOps = MergeOperations(additional.Operations);
        var mergedKind = Kind == ComponentKind.Module || additional.Kind == ComponentKind.Module
            ? ComponentKind.Module
            : (Kind == ComponentKind.App && additional.Kind != ComponentKind.App ? additional.Kind : Kind);
        var mergedVersion = (string.IsNullOrWhiteSpace(Version) || string.Equals(Version, "1.0", StringComparison.Ordinal))
            && !string.IsNullOrWhiteSpace(additional.Version)
            && !string.Equals(additional.Version, "1.0", StringComparison.Ordinal)
            ? additional.Version
            : Version;
        var mergedProvides = MergeTypes(Provides, additional.Provides);
        var mergedEmits = MergeTypes(Emits, additional.Emits);
        var mergedReacts = MergeTypes(Reacts, additional.Reacts);
        return this with
        {
            Slots = mergedSlots,
            Face = mergedFace,
            Operations = mergedOps,
            Kind = mergedKind,
            Version = mergedVersion,
            Provides = mergedProvides,
            Emits = mergedEmits,
            Reacts = mergedReacts,
        };
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

    private static Type[] MergeTypes(Type[] existing, Type[] additional)
    {
        if (additional.Length == 0) { return existing; }
        if (existing.Length == 0) { return additional; }
        var seen = new HashSet<Type>(existing);
        var merged = new List<Type>(existing);
        foreach (var type in additional)
        {
            if (seen.Add(type)) { merged.Add(type); }
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
    private static readonly ConcurrentDictionary<Type, Type[]> ReactsByNeuronClass = new();

    /// <summary>Registers or merges <paramref name="manifest"/> for its component id.</summary>
    public static void Register(ComponentManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentException.ThrowIfNullOrWhiteSpace(manifest.Id.Value, nameof(manifest));
        ByComponent.AddOrUpdate(manifest.Id, manifest, (_, existing) => existing.Merge(manifest));
    }

    /// <summary>Registers <paramref name="manifest"/> and records its slots and reactions for <paramref name="neuronClass"/>.</summary>
    public static void Register(Type neuronClass, ComponentManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(neuronClass);
        Register(manifest);
        ByNeuronClass[neuronClass] = manifest.Slots;
        if (manifest.Reacts.Length > 0)
        {
            ReactsByNeuronClass[neuronClass] = manifest.Reacts;
        }
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

    /// <summary>Returns the signal types directly registered for <paramref name="neuronClass"/>, or an empty array.</summary>
    public static Type[] ReactsOf(Type neuronClass)
    {
        ArgumentNullException.ThrowIfNull(neuronClass);
        return ReactsByNeuronClass.TryGetValue(neuronClass, out var reacts) ? reacts : [];
    }
}
