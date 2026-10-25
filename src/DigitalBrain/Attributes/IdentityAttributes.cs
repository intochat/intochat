namespace DigitalBrain;

/// <summary>Marks a neuron class. The type name is frozen: it is part of the storage key.</summary>
[AttributeUsage(AttributeTargets.Class, Inherited = false)]
public sealed class NeuronAttribute(string type) : Attribute
{
    /// <summary>The frozen neuron type name.</summary>
    public string Type { get; } = type;

    /// <summary>The frozen persisted state name, or null for the default. Set it when a class moves so stored keys never move.</summary>
    public string? State { get; init; }
}

/// <summary>Marks a signal record. The alias is frozen: it is written into persisted payloads.</summary>
[AttributeUsage(AttributeTargets.Class, Inherited = false)]
public sealed class SignalAttribute(string alias) : Attribute
{
    /// <summary>The frozen signal alias.</summary>
    public string Alias { get; } = alias;
}

/// <summary>Marks a reaction: a method that runs when a signal arrives through a synapse. Signals arrive deduped and ordered per source.</summary>
[AttributeUsage(AttributeTargets.Method, Inherited = false)]
public sealed class OnAttribute : Attribute;

/// <summary>Marks an app class.</summary>
[AttributeUsage(AttributeTargets.Class, Inherited = false)]
public sealed class AppAttribute(string id) : Attribute
{
    /// <summary>The frozen app id.</summary>
    public string Id { get; } = id;
}

/// <summary>Marks a versioned capability contract, written <c>name@version</c>.</summary>
[AttributeUsage(AttributeTargets.Interface, Inherited = false)]
public sealed class CapabilityAttribute(string name, int version) : Attribute
{
    /// <summary>The capability name.</summary>
    public string Name { get; } = name;

    /// <summary>The contract version.</summary>
    public int Version { get; } = version;
}
