using System.Text.Json;
using System.Text.Json.Serialization;

namespace DigitalBrain.UI;

/// <summary>A typed instruction a view sends to a neuron: the signal to deliver and where.</summary>
/// <param name="Target">A neuron address relative to the view's neuron; <c>"."</c> is the neuron itself, <c>"dark-mode"</c> a child.</param>
/// <param name="Signal">The alias of the signal to deliver.</param>
/// <param name="Args">The signal payload as JSON (web casing), if any.</param>
/// <param name="Props">Set only by a parent screen routing the command to its child: the props its current view gives that child,
/// so a controlled control (props carry its value) can signal without storing. Anything else sending a command leaves it null.</param>
public sealed record Command(string Target, string Signal, JsonElement? Args = null, [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] JsonElement? Props = null)
{
    // Reads legacy emit objects ({owner, signal, args}) in stored Field.onSubmit fixtures as a command to "." with the same signal and args.
    [JsonConstructor]
    private Command(string? target, string signal, JsonElement? args, JsonElement? props, string? owner)
        : this(target ?? (owner is not null ? "." : null!), signal, args, props)
    {
    }

    [JsonInclude, JsonPropertyName("owner"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
#pragma warning disable CA1822
    private string? Owner => null;
#pragma warning restore CA1822

    /// <inheritdoc />
    public bool Equals(Command? other) => other is not null && Target == other.Target && Signal == other.Signal
        && Args?.GetRawText() == other.Args?.GetRawText() && Props?.GetRawText() == other.Props?.GetRawText();

    /// <inheritdoc />
    public override int GetHashCode() => HashCode.Combine(Target, Signal, Args?.GetRawText(), Props?.GetRawText());
}

/// <summary>Gives a control property one example value (JSON) for the showcase; repeat for several.</summary>
/// <param name="exampleJson">The example as JSON.</param>
[AttributeUsage(AttributeTargets.Parameter | AttributeTargets.Property, AllowMultiple = true)]
public sealed class ShowcaseAttribute(string exampleJson) : Attribute
{
    /// <summary>The example as JSON.</summary>
    public string ExampleJson { get; } = exampleJson;
}

/// <summary>Marks a control property as client-local state that is not part of the persisted view.</summary>
[AttributeUsage(AttributeTargets.Parameter | AttributeTargets.Property)]
public sealed class EphemeralAttribute : Attribute;

/// <summary>Names a fact (a past-tense signal) a control neuron publishes, for the generator and the showcase catalog; repeat for several.</summary>
/// <param name="signal">The signal record type.</param>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = true, Inherited = false)]
public sealed class PublishesAttribute(Type signal) : Attribute
{
    /// <summary>The signal record type.</summary>
    public Type Signal { get; } = signal;
}
