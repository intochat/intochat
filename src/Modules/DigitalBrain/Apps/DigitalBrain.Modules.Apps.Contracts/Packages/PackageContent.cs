namespace DigitalBrain.Apps;

// Files carry everything an app is made of, keyed by path: its app.spec.md spec, its
// tests.cs, its behaviors/*.cs scripts, prompts and the configuration its runtime reads.
// There is exactly one representation; "app.cs" is an ordinary entry like any other.
[GenerateSerializer, Alias("apps.package-content")]
public sealed record PackageContent
{
    [System.Text.Json.Serialization.JsonConstructor]
    public PackageContent(PackageManifest manifest, Dictionary<string, string>? files = null)
    {
        Manifest = manifest;
        Files = files;
    }

    // Sugar for the single-script shape: the source becomes Files["app.cs"].
    public PackageContent(PackageManifest manifest, string source, Dictionary<string, string>? files = null)
        : this(manifest, Merge(source, files))
    {
    }

    [Id(0)] public PackageManifest Manifest { get; init; }
    // Id 1 (the legacy single-script Source) is retired; never reuse it.
    [Id(2)] public Dictionary<string, string>? Files { get; init; }

    public const string SpecPath = "app.spec.md";
    public const string TestsPath = "tests.cs";
    public const string SourcePath = "app.cs";
    public const string BehaviorsPrefix = "behaviors/";

    public string? File(string path) => Files is not null && Files.TryGetValue(path, out var text) ? text : null;

    // Everything a csharp app runs: the app.cs entry plus one script per behaviors/*.cs file.
    public IReadOnlyDictionary<string, string> Programs()
    {
        var programs = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var (path, text) in Files ?? new Dictionary<string, string>())
        {
            if (path == SourcePath
                || (path.StartsWith(BehaviorsPrefix, StringComparison.Ordinal) && path.EndsWith(".cs", StringComparison.Ordinal)))
            { programs[path] = text; }
        }
        return programs;
    }

    private static Dictionary<string, string>? Merge(string source, Dictionary<string, string>? files)
    {
        if (string.IsNullOrWhiteSpace(source)) { return files; }
        var merged = new Dictionary<string, string>(files ?? [], StringComparer.Ordinal) { [SourcePath] = source };
        return merged;
    }
}
