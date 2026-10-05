using System.Text.Json;
using DigitalBrain.Apps;

namespace DigitalBrain.Apps;

// App folders are embedded by the host under its configured resource prefix.
public sealed class EmbeddedShippedAppSource(System.Reflection.Assembly assembly, string resourcePrefix, string account) : IShippedAppSource
{
    public string Account => account;
    public bool Writable => false;
    public void Write(AppBundle bundle) => throw new NotSupportedException("Embedded shipped apps are read-only.");

    public IReadOnlyList<AppBundle> Load()
    {
        var folders = new SortedDictionary<string, Dictionary<string, string>>(StringComparer.Ordinal);
        foreach (var resource in assembly.GetManifestResourceNames())
        {
            var path = resource.Replace('\\', '/');
            if (!path.StartsWith(resourcePrefix, StringComparison.Ordinal)) { continue; }
            var relative = path[resourcePrefix.Length..];
            var slash = relative.IndexOf('/', StringComparison.Ordinal);
            if (slash < 0) { continue; }
            using var stream = assembly.GetManifestResourceStream(resource)!;
            using var reader = new StreamReader(stream);
            var folder = relative[..slash];
            if (!folders.TryGetValue(folder, out var files)) { folders[folder] = files = new(StringComparer.Ordinal); }
            files[relative[(slash + 1)..]] = reader.ReadToEnd().Replace("\r\n", "\n", StringComparison.Ordinal);
        }
        return [.. folders.Select(folder => ShippedAppFolder.ToBundle(folder.Key, folder.Value))];
    }
}

// A shipped app folder is the same shape wherever it comes from: app.json is the manifest,
// app.spec.md the spec, a legacy top-level app.cs becomes behaviors/app.cs, every other
// file travels in the bundle.
internal static class ShippedAppFolder
{
    private const string ManifestFile = "app.json";
    private const string LegacyScriptFile = "app.cs";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    internal static AppBundle ToBundle(string folder, IReadOnlyDictionary<string, string> files)
    {
        var manifestText = files.GetValueOrDefault(ManifestFile) ?? throw new InvalidDataException($"Shipped app {folder} has no {ManifestFile}.");
        var manifest = JsonSerializer.Deserialize<AppJson>(manifestText, Json) ?? throw new InvalidDataException($"{folder}/{ManifestFile} is empty.");
        var bundleFiles = files
            .Where(file => file.Key is not ManifestFile and not LegacyScriptFile and not AppFiles.SpecPath)
            .ToDictionary(file => file.Key, file => file.Value, StringComparer.Ordinal);
        if (files.TryGetValue(LegacyScriptFile, out var script)) { bundleFiles[AppFiles.BehaviorsPrefix + LegacyScriptFile] = script; }
        return new(manifest.Name, manifest.Version ?? "1.0.0",
            new AppManifest(
                manifest.Title,
                manifest.Description,
                [.. manifest.Operations.Select(operation => new AppOperation(operation.Name, operation.Description))],
                [.. manifest.Settings.Select(setting => new AppSetting(setting.Name, setting.Description, setting.Default))],
                [.. (manifest.Slots ?? []).Select(slot => new AppSlot(slot.Name, slot.Connector))],
                manifest.Icon),
            files.GetValueOrDefault(AppFiles.SpecPath, ""),
            bundleFiles);
    }

    internal static Dictionary<string, string> ToFiles(AppBundle bundle)
    {
        var manifest = new AppJson(bundle.Name, bundle.Manifest.Title, bundle.Manifest.Description,
            [.. bundle.Manifest.Operations.Select(operation => new OperationJson(operation.Name, operation.Description))],
            [.. bundle.Manifest.Settings.Select(setting => new SettingJson(setting.Name, setting.Description, setting.Default))],
            bundle.Version,
            bundle.Manifest.Slots.Length == 0 ? null : [.. bundle.Manifest.Slots.Select(slot => new SlotJson(slot.Name, slot.Connector))],
            bundle.Manifest.Icon);
        var files = new Dictionary<string, string>(bundle.Files, StringComparer.Ordinal)
        {
            [ManifestFile] = JsonSerializer.Serialize(manifest, Pretty) + "\n",
            [AppFiles.SpecPath] = bundle.Spec,
        };
        return files;
    }

    // A new app's folder: its title in PascalCase, the way src/IntoChat/Apps names folders.
    internal static string FolderName(AppBundle bundle)
    {
        var words = bundle.Manifest.Title.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Select(word => new string([.. word.Where(char.IsLetterOrDigit)]))
            .Where(word => word.Length > 0)
            .Select(word => char.ToUpperInvariant(word[0]) + word[1..]);
        var name = string.Concat(words);
        return name.Length > 0 ? name : bundle.Name;
    }

    private static readonly JsonSerializerOptions Pretty = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    private sealed record AppJson(string Name, string Title, string Description, IReadOnlyList<OperationJson> Operations, IReadOnlyList<SettingJson> Settings, string? Version = null, IReadOnlyList<SlotJson>? Slots = null, string? Icon = null);
    private sealed record OperationJson(string Name, string Description);
    private sealed record SettingJson(string Name, string Description, string Default);
    private sealed record SlotJson(string Name, string Connector);
}
