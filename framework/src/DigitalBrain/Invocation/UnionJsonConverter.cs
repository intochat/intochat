using System.Collections.Concurrent;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace DigitalBrain.Invocation;

// A C# union is written as its case's object with a "$case" member naming the case type; plain System.Text.Json writes the
// case's members only, so the case is lost on the way back.
internal sealed class UnionJsonConverterFactory : JsonConverterFactory
{
    public override bool CanConvert(Type typeToConvert) =>
        typeof(IUnion).IsAssignableFrom(typeToConvert) && typeToConvert.IsDefined(typeof(UnionAttribute), inherit: false);

    public override JsonConverter CreateConverter(Type typeToConvert, JsonSerializerOptions options) =>
        (JsonConverter)Activator.CreateInstance(typeof(UnionJsonConverter<>).MakeGenericType(typeToConvert))!;
}

internal sealed class UnionJsonConverter<T> : JsonConverter<T> where T : IUnion
{
    internal const string Tag = "$case";

    private static readonly ConcurrentDictionary<string, ConstructorInfo> Cases = new(
        typeof(T).GetConstructors()
            .Where(static c => c.GetParameters().Length == 1)
            .ToDictionary(static c => c.GetParameters()[0].ParameterType.Name));

    public override T Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var node = JsonNode.Parse(ref reader)?.AsObject() ?? throw new JsonException($"{typeof(T).Name}: null is not a case.");
        var name = node[Tag]?.GetValue<string>() ?? throw new JsonException($"{typeof(T).Name}: missing {Tag}.");
        if (!Cases.TryGetValue(name, out var constructor)) { throw new JsonException($"{typeof(T).Name}: unknown case {name}."); }
        node.Remove(Tag);
        var value = node.Deserialize(constructor.GetParameters()[0].ParameterType, options);
        return (T)constructor.Invoke([value]);
    }

    public override void Write(Utf8JsonWriter writer, T value, JsonSerializerOptions options)
    {
        var inner = value.Value ?? throw new JsonException($"{typeof(T).Name}: a default union has no case.");
        var members = JsonSerializer.SerializeToNode(inner, inner.GetType(), options)!.AsObject();
        var tagged = new JsonObject { [Tag] = CaseName(inner.GetType()) };
        foreach (var (key, member) in members.ToList())
        {
            members.Remove(key);
            tagged[key] = member;
        }

        tagged.WriteTo(writer, options);
    }

    // Stored unions need concrete cases: a runtime subtype of a case parameter (an Outcome's Refusal) could not be read back.
    private static string CaseName(Type type) =>
        Cases.TryGetValue(type.Name, out var constructor) && constructor.GetParameters()[0].ParameterType == type
            ? type.Name
            : throw new JsonException($"{typeof(T).Name}: {type.Name} is not one of its cases; only exact case types can be stored.");
}
