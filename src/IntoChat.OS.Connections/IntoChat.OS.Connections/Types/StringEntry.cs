using IntoChat.OS.Framework;
using IntoChat.OS.Framework.Modules;

namespace IntoChat.OS.Connections;

// One key and value of a persisted string map. Persisted state holds these as an array: a dictionary has no
// stable shape for the serializer, and an entry array evolves like any other record array.
[GenerateSerializer, Alias("framework.string-entry")]
public sealed record StringEntry([property: Id(0)] string Key, [property: Id(1)] string Value)
{
    public static StringEntry[] From(IEnumerable<KeyValuePair<string, string>> map)
        => [.. map.Select(pair => new StringEntry(pair.Key, pair.Value))];

    // The array wins; a legacy dictionary read from older stored bytes is the fallback until the next write.
    public static Dictionary<string, string> ToMap(StringEntry[]? entries, IReadOnlyDictionary<string, string>? legacy)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        if (entries is { Length: > 0 }) { foreach (var entry in entries) { map[entry.Key] = entry.Value; } }
        else if (legacy is not null) { foreach (var (key, value) in legacy) { map[key] = value; } }
        return map;
    }
}
