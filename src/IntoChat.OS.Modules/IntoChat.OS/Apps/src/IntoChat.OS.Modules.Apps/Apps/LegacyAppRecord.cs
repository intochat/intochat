using System.Text.Json;

namespace IntoChat.OS.Modules.Apps;

// What a record stored before 0.18 carries that the units shape does not. The typed AppState drops the retired fields on load, so the
// markers are read from the stored JSON itself. There is no migration: a record with any marker is refused and the Creator re-authors
// the app from its spec.
internal static class LegacyAppRecord
{
    internal const string Refusal = "refused a pre-0.18 record: rebuilt by the new Creator";

    private static readonly string[] RetiredFields = ["runtimeStateJson", "runtimeStateRevision", "synapses"];
    private const string RetiredPrefix = "migration";
    private const string PrebuiltSuffix = ".dll.base64";

    // The names of the old-shape markers found in the stored JSON of an app; empty for the units shape.
    internal static string[] Markers(JsonElement stored)
    {
        if (stored.ValueKind != JsonValueKind.Object) { return []; }
        List<string> found = [];
        foreach (var property in stored.EnumerateObject())
        {
            if (RetiredFields.Contains(property.Name, StringComparer.OrdinalIgnoreCase)
                || property.Name.StartsWith(RetiredPrefix, StringComparison.OrdinalIgnoreCase))
            { found.Add(property.Name); }
            else if (property.Name.Equals("fileEntries", StringComparison.OrdinalIgnoreCase) || property.Name.Equals("files", StringComparison.OrdinalIgnoreCase))
            { found.AddRange(Keys(property.Value).Where(IsProgramFile).Select(key => "file:" + key)); }
            else if (property.Name.Equals("settingEntries", StringComparison.OrdinalIgnoreCase) || property.Name.Equals("settings", StringComparison.OrdinalIgnoreCase))
            { found.AddRange(Keys(property.Value).Where(IsPrebuilt).Select(key => "setting:" + key)); }
        }
        return [.. found];
    }

    // Nothing in the units shape stores app.cs (the Builder is refused it; no Author writes it), so it counts as program here.
    internal static bool IsProgramFile(string path)
        => path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase) || path.EndsWith(PrebuiltSuffix, StringComparison.OrdinalIgnoreCase);

    // The kernel's own "__" settings: the compiled type stays, the old prebuilt-behaviors settings do not.
    internal static bool IsPrebuilt(string key)
        => key.EndsWith(PrebuiltSuffix, StringComparison.OrdinalIgnoreCase)
            || key.StartsWith("__", StringComparison.Ordinal) && key != AppTrust.CompiledTypeSetting;

    // The keys of a stored map: an array of {key, value} entries or a plain object.
    private static IEnumerable<string> Keys(JsonElement map)
    {
        if (map.ValueKind == JsonValueKind.Object) { return map.EnumerateObject().Select(property => property.Name); }
        if (map.ValueKind != JsonValueKind.Array) { return []; }
        return map.EnumerateArray()
            .Where(entry => entry.ValueKind == JsonValueKind.Object)
            .Select(entry => entry.EnumerateObject().FirstOrDefault(property => property.Name.Equals("key", StringComparison.OrdinalIgnoreCase)))
            .Where(key => key.Value.ValueKind == JsonValueKind.String)
            .Select(key => key.Value.GetString()!);
    }
}
