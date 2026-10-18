namespace IntoChat.OS;

/// <summary>One type the kernel knows: a neuron contract, a signal or a connector.</summary>
/// <param name="Name">The full type name (a connector's id).</param>
/// <param name="Alias">The neuron's grain type as instances record it; a signal's or connector's stable alias.</param>
/// <param name="Kind">Which of the words this type is.</param>
/// <param name="Provider">The module id (or feature) that brought the type.</param>
/// <param name="Description">What the type is for.</param>
/// <param name="Shape">A neuron's method signatures, a signal's properties, a connector's neuron and signal names.</param>
/// <param name="Implements">The neuron contracts a neuron contract extends, by full name.</param>
[GenerateSerializer, Alias("kernel.type-entry")]
public sealed record TypeEntry(
    [property: Id(0)] string Name,
    [property: Id(1)] string Alias,
    [property: Id(2)] TypeKind Kind,
    [property: Id(3)] string Provider,
    [property: Id(4)] string Description,
    [property: Id(5)] string[] Shape,
    [property: Id(6)] string[] Implements);

/// <summary>Which of the words a <see cref="TypeEntry"/> is.</summary>
[GenerateSerializer, Alias("kernel.type-kind")]
public enum TypeKind { Neuron, Signal, Connector }
