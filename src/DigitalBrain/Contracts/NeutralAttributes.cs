namespace DigitalBrain;

// The neutral contract attributes of the clean-core plan (section 2.2). Each has the meaning of the
// Orleans attribute named beside it; the Orleans attributes stay in place until contracts migrate.

// Marks a record or class whose persisted and wire layout a runtime provider must generate. Was [GenerateSerializer].
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct, Inherited = false)]
public sealed class NeuronContractAttribute : Attribute;

// The rename-safe type name, with the same string and role as the Orleans [Alias]: it is the SignalId
// and the type name written into persisted polymorphic payloads.
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct | AttributeTargets.Interface | AttributeTargets.Enum, Inherited = false)]
public sealed class NeuronAliasAttribute(string alias) : Attribute
{
    public string Alias { get; } = alias;
}

// The stable member id inside its type's layer. Same number as the Orleans [Id].
[AttributeUsage(AttributeTargets.Property | AttributeTargets.Field, Inherited = false)]
public sealed class FieldAttribute(uint id) : Attribute
{
    public uint Id { get; } = id;
}

// The neuron type an interface resolves to by default. Was Orleans.Metadata.DefaultGrainType.
[AttributeUsage(AttributeTargets.Interface, Inherited = false)]
public sealed class DefaultNeuronTypeAttribute(string type) : Attribute
{
    /// <summary>The default neuron type name; the Orleans provider's generator reads this property.</summary>
    public string Type { get; } = type;
}

// Scheduling semantics declared by the contract and honoured by every runtime.

// Was [AlwaysInterleave].
[AttributeUsage(AttributeTargets.Method, Inherited = false)]
public sealed class InterleavesAttribute : Attribute;

// Was Orleans.Concurrency.ReadOnly.
[AttributeUsage(AttributeTargets.Method, Inherited = false)]
public sealed class ReadOnlyCallAttribute : Attribute;

// Was Orleans.Concurrency.OneWay.
[AttributeUsage(AttributeTargets.Method, Inherited = false)]
public sealed class OneWayCallAttribute : Attribute;

// Names a static predicate on the class that decides whether a call may interleave. Was [MayInterleave].
[AttributeUsage(AttributeTargets.Class, Inherited = false)]
public sealed class InterleavesWhenAttribute(string predicate) : Attribute
{
    public string Predicate { get; } = predicate;
}

// The call's response timeout as a TimeSpan string. Was [ResponseTimeout].
[AttributeUsage(AttributeTargets.Method, Inherited = false)]
public sealed class CallTimeoutAttribute(string timeout) : Attribute
{
    public string Timeout { get; } = timeout;
}
