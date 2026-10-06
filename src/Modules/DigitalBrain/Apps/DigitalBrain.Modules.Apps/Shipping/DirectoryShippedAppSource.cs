namespace DigitalBrain.Apps;

// App folders read from a directory on disk: one subfolder per app, the same shape the
// embedded source reads from resources. Deployments point the host at a folder through
// DigitalBrain:Modules:apps:Shipped:{source}:Path and name its publisher with :Account.
public sealed class DirectoryShippedAppSource(string path, string account, bool writable = false) : IShippedAppSource
{
    public string Account => account;

    public IReadOnlyList<AppBundle> Load()
    {
        var root = new DirectoryInfo(path);
        if (!root.Exists) { throw new DirectoryNotFoundException($"Shipped app directory '{path}' for account '{account}' does not exist."); }
        return [.. root.EnumerateDirectories().OrderBy(folder => folder.Name, StringComparer.Ordinal)
            .Select(folder => ShippedAppFolder.ToBundle(folder.Name, ReadFolder(folder)))];
    }

    public bool Writable => writable;
    public bool VerifiedByBuild => false;

    // Publishing writes the published version back as files, so the repository diff is the change.
    public void Write(AppBundle bundle)
    {
        if (!writable) { throw new InvalidOperationException($"Shipped app directory '{path}' is read-only."); }
        var root = new DirectoryInfo(path);
        root.Create();
        var folder = root.EnumerateDirectories().FirstOrDefault(candidate =>
            File.Exists(Path.Combine(candidate.FullName, "app.json"))
            && ShippedAppFolder.ToBundle(candidate.Name, ReadFolder(candidate)).Name == bundle.Name)
            ?? root.CreateSubdirectory(ShippedAppFolder.FolderName(bundle));
        var files = ShippedAppFolder.ToFiles(bundle);
        foreach (var stale in folder.EnumerateFiles("*", SearchOption.AllDirectories)
            .Where(file => !IsBuildOutput(folder, file)
                && !files.ContainsKey(Path.GetRelativePath(folder.FullName, file.FullName).Replace('\\', '/'))))
        { stale.Delete(); }
        foreach (var (relative, content) in files)
        {
            var target = Path.Combine(folder.FullName, relative.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.WriteAllText(target, content);
        }
    }

    private static bool IsBuildOutput(DirectoryInfo folder, FileInfo file)
        => Path.GetRelativePath(folder.FullName, file.FullName).Replace('\\', '/') is var relative
            && (relative.StartsWith("bin/", StringComparison.Ordinal) || relative.StartsWith("obj/", StringComparison.Ordinal));

    private static Dictionary<string, string> ReadFolder(DirectoryInfo folder)
    {
        var files = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var file in folder.EnumerateFiles("*", SearchOption.AllDirectories).Where(file => !IsBuildOutput(folder, file)))
        {
            var relative = Path.GetRelativePath(folder.FullName, file.FullName).Replace('\\', '/');
            // Normalized exactly as the embedded source normalizes, so content hashes agree
            // across checkouts and operating systems.
            files[relative] = File.ReadAllText(file.FullName).Replace("\r\n", "\n", StringComparison.Ordinal);
        }
        return files;
    }
}
