namespace DigitalBrain.Framework;

[GenerateSerializer, Alias("registry.neuron-type")]
public sealed record NeuronType(
    [property: Id(0)] string Id,
    [property: Id(1)] string ModuleId,
    [property: Id(2)] string Name,
    [property: Id(3)] string Description,
    [property: Id(4)] IReadOnlyList<string> Methods,
    [property: Id(5)] string Contract = "",
    [property: Id(6)] IReadOnlyList<string>? Signals = null)
{
    // "DigitalBrain.Time.TimeModule" reads as module id "time".
    public static string ModuleIdOf(Type moduleType)
    {
        ArgumentNullException.ThrowIfNull(moduleType);
        var name = moduleType.Name;
        return (name.EndsWith("Module", StringComparison.Ordinal) ? name[..^"Module".Length] : name).ToLowerInvariant();
    }
}

[GenerateSerializer, Alias("registry.type-hit")]
public sealed record NeuronTypeHit([property: Id(0)] NeuronType Type, [property: Id(1)] double Score);
