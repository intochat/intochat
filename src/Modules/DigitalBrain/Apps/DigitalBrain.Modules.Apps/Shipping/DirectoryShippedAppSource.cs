namespace DigitalBrain.Apps;

// App folders read from a directory on disk: one subfolder per app, the same shape the
// embedded source reads from resources. Deployments point the host at a folder through
// DigitalBrain:Modules:apps:Shipped:{source}:Path and name its publisher with :Account.
public sealed class DirectoryShippedAppSource(string path, string account, bool writable = false) : IShippedAppSource
{
    public string Account => account;
    public bool Writable => writable;

    public IReadOnlyList<AppBundle> Load()
    {
        var root = new DirectoryInfo(path);
        if (!root.Exists) { throw new DirectoryNotFoundException($"Shipped app directory '{path}' for account '{account}' does not exist."); }
        return [.. root.EnumerateDirectories().OrderBy(folder => folder.Name, StringComparer.Ordinal)
            .Select(folder => ShippedAppFolder.ToBundle(folder.Name, ReadFolder(folder)))];
    }

    private static Dictionary<string, string> ReadFolder(DirectoryInfo folder)
    {
        var files = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var file in folder.EnumerateFiles("*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(folder.FullName, file.FullName).Replace('\\', '/');
            // Normalized exactly as the embedded source normalizes, so content hashes agree
            // across checkouts and operating systems.
            files[relative] = File.ReadAllText(file.FullName).Replace("\r\n", "\n", StringComparison.Ordinal);
        }
        return files;
    }
}
