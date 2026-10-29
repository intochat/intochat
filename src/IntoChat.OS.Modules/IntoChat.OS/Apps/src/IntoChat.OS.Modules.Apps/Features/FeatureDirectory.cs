namespace IntoChat.OS.Modules.Apps;

// A feature as it sits in a features folder: {owner}/{AppFolder}/app.cs plus its other files.
public sealed record ShippedFeature(string Owner, AppBundle Bundle, string Folder);

// The features folder on disk: one subfolder per owner account (os, intochat), one folder per app
// under it. A development host opens it writable, so Save to source writes an edited app back into
// the repository and the diff is the change.
public sealed class FeatureDirectory(string path, bool writable = false)
{
    public string Path => path;

    public bool Writable => writable;

    public IReadOnlyList<ShippedFeature> Load()
    {
        var root = new DirectoryInfo(path);
        if (!root.Exists) { throw new DirectoryNotFoundException($"Features folder '{path}' does not exist."); }
        var children = root.EnumerateDirectories().OrderBy(folder => folder.Name, StringComparer.Ordinal).ToArray();
        // features/os holds one folder per app (app.cs directly inside). features holds one folder per owner.
        if (children.Any(HasDeclaration))
        { return [.. children.Where(HasDeclaration).Select(folder => Feature(root.Name, folder))]; }
        return [.. children.SelectMany(owner => owner.EnumerateDirectories().OrderBy(folder => folder.Name, StringComparer.Ordinal)
            .Select(folder => Feature(owner.Name, folder)))];
    }

    private static bool HasDeclaration(DirectoryInfo folder)
        => File.Exists(System.IO.Path.Combine(folder.FullName, AppFiles.DeclarationPath));

    private static ShippedFeature Feature(string owner, DirectoryInfo folder)
        => new(owner, FeatureFolder.ToBundle($"{owner}/{folder.Name}", ReadFolder(folder)), $"{owner}/{folder.Name}");

    public void Write(string owner, AppBundle bundle)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(owner);
        ArgumentNullException.ThrowIfNull(bundle);
        if (!writable) { throw new InvalidOperationException($"Features folder '{path}' is read-only."); }
        // The owner and every file path come from a brain's state: neither may leave the features folder.
        if (owner is "." or ".." || owner.IndexOfAny(['/', '\\', ':']) >= 0 || owner.IndexOfAny(System.IO.Path.GetInvalidFileNameChars()) >= 0)
        { throw new ArgumentException($"'{owner}' is not a features owner folder.", nameof(owner)); }
        var root = new DirectoryInfo(System.IO.Path.Combine(path, owner));
        root.Create();
        var folder = root.EnumerateDirectories().FirstOrDefault(candidate =>
            File.Exists(System.IO.Path.Combine(candidate.FullName, AppFiles.DeclarationPath))
            && FeatureFolder.ToBundle(candidate.Name, ReadFolder(candidate)).Name == bundle.Name)
            ?? root.CreateSubdirectory(FeatureFolder.FolderName(bundle));
        var files = FeatureFolder.ToFiles(bundle);
        var inside = System.IO.Path.TrimEndingDirectorySeparator(folder.FullName) + System.IO.Path.DirectorySeparatorChar;
        foreach (var relative in files.Keys)
        {
            var target = System.IO.Path.GetFullPath(System.IO.Path.Combine(folder.FullName, relative.Replace('/', System.IO.Path.DirectorySeparatorChar)));
            if (System.IO.Path.IsPathRooted(relative) || !target.StartsWith(inside, StringComparison.OrdinalIgnoreCase))
            { throw new ArgumentException($"{bundle.Name} file '{relative}' is outside its feature folder.", nameof(bundle)); }

        }
        foreach (var stale in folder.EnumerateFiles("*", SearchOption.AllDirectories)
            .Where(file => !IsBuildOutput(folder, file) && !files.ContainsKey(Relative(folder, file))))
        { stale.Delete(); }
        foreach (var (relative, content) in files)
        {
            var target = System.IO.Path.Combine(folder.FullName, relative.Replace('/', System.IO.Path.DirectorySeparatorChar));
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(target)!);
            File.WriteAllText(target, content);
        }
    }

    private static string Relative(DirectoryInfo folder, FileInfo file)
        => System.IO.Path.GetRelativePath(folder.FullName, file.FullName).Replace('\\', '/');

    private static bool IsBuildOutput(DirectoryInfo folder, FileInfo file)
        => Relative(folder, file) is var relative
            && (relative.StartsWith("bin/", StringComparison.Ordinal) || relative.StartsWith("obj/", StringComparison.Ordinal));

    private static Dictionary<string, string> ReadFolder(DirectoryInfo folder)
    {
        var files = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var file in folder.EnumerateFiles("*", SearchOption.AllDirectories).Where(file => !IsBuildOutput(folder, file)))
        {
            // Line endings normalized, so content hashes agree across checkouts and operating systems.
            files[Relative(folder, file)] = File.ReadAllText(file.FullName).Replace("\r\n", "\n", StringComparison.Ordinal);
        }
        return files;
    }
}

// A feature folder is the same shape wherever it comes from: app.cs declares the name, version
// and manifest with the spec as its leading comment; every other file travels in the bundle.
internal static class FeatureFolder
{
    internal static AppBundle ToBundle(string folder, IReadOnlyDictionary<string, string> files)
    {
        var source = files.GetValueOrDefault(AppFiles.DeclarationPath) ?? throw new InvalidDataException($"Feature {folder} has no {AppFiles.DeclarationPath}.");
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

    // A new app's folder: its title in PascalCase, the way features/{owner} names folders.
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
