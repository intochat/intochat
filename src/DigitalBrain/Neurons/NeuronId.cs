using System.Text.Json;
using System.Text.Json.Serialization;

namespace DigitalBrain;

/// <summary>
/// A neuron's name inside its brain. Identity as a value: it names a neuron, it grants nothing.
/// </summary>
/// <remarks>
/// Exists so that no code outside the brain builds or parses grain keys. The brain alone maps an
/// id to a grain (<see cref="IDigitalBrain.Get{T}"/>); everything else, signals and synapses
/// included, carries this value. A string converts implicitly, so a program writes
/// <c>brain.Get&lt;IButton&gt;("save")</c>.
/// </remarks>
[GenerateSerializer, Alias("neuron.id"), Immutable]
[JsonConverter(typeof(Json))]
public readonly record struct NeuronId
{
    /// <summary>The name, unique within its brain.</summary>
    [Id(0)] public string Name { get; }

    /// <exception cref="ArgumentException"><paramref name="name"/> is empty or contains a control character.</exception>
    public NeuronId(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (name.Any(char.IsControl))
        { throw new ArgumentException("A neuron name has no control characters.", nameof(name)); }
        Name = name;
    }

    public static implicit operator NeuronId(string name)
    {
        return new(name);
    }

    public override string ToString() => Name;

    // System.Text.Json builds a struct through its parameterless constructor and cannot set the get-only
    // Name, so without this a publisher reaches a script nameless. A name is read back through the
    // constructor; a missing name is the unstamped default.
    private sealed class Json : JsonConverter<NeuronId>
    {
        public override NeuronId Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            if (reader.TokenType != JsonTokenType.StartObject) { throw new JsonException($"A neuron id is an object with a {nameof(Name)}."); }
            string? name = null;
            while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
            {
                var property = reader.GetString();
                reader.Read();
                if (string.Equals(property, nameof(Name), StringComparison.OrdinalIgnoreCase)) { name = reader.GetString(); }
                else { reader.Skip(); }
            }
            return name is null ? default : new NeuronId(name);
        }

        public override void Write(Utf8JsonWriter writer, NeuronId value, JsonSerializerOptions options)
        {
            writer.WriteStartObject();
            writer.WriteString(options.PropertyNamingPolicy?.ConvertName(nameof(Name)) ?? nameof(Name), value.Name);
            writer.WriteEndObject();
        }
    }
}
