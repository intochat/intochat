namespace IntoChat.OS;

// The legacy scheduling attributes of the clean-core plan (section 2.2). The contract attributes (NeuronContract,
// NeuronAlias, Field, DefaultNeuronType) come from DigitalBrain.Contracts; a type's effective alias is NeuronAliases.Of.

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
