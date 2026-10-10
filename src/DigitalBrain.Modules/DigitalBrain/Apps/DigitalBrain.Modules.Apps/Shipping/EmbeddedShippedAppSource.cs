using DigitalBrain.Apps;

namespace DigitalBrain.Apps;

// App folders are embedded by the host under its configured resource prefix.
public sealed class EmbeddedShippedAppSource(System.Reflection.Assembly assembly, string resourcePrefix, string publisher) : IShippedAppSource
{
    public string Publisher => publisher;

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

// A shipped app folder is the same shape wherever it comes from: app.cs declares the name, version
// and manifest with the spec as its leading comment; every other file travels in the bundle.
internal static class ShippedAppFolder
{
    internal static AppBundle ToBundle(string folder, IReadOnlyDictionary<string, string> files)
    {
        var source = files.GetValueOrDefault(AppFiles.DeclarationPath) ?? throw new InvalidDataException($"Shipped app {folder} has no {AppFiles.DeclarationPath}.");
        AppDeclarationContent declared;
        try { declared = AppDeclaration.Read(source); }
        catch (InvalidDataException error) { throw new InvalidDataException($"{folder}/{error.Message}", error); }
        var bundleFiles = files
            .Where(file => file.Key != AppFiles.DeclarationPath)
            .ToDictionary(file => file.Key, file => file.Value, StringComparer.Ordinal);
        return new(declared.Name, declared.Version, declared.Manifest, declared.Spec, bundleFiles);
    }

    internal static Dictionary<string, string> ToFiles(AppBundle bundle)
        => new(bundle.Files, StringComparer.Ordinal)
        {
            [AppFiles.DeclarationPath] = AppDeclaration.Write(new(bundle.Name, bundle.Version, bundle.Manifest, bundle.Spec)),
        };

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
}
