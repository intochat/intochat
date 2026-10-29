using IntoChat.OS.Runtime.Storage.Confinement;

namespace IntoChat.OS.Modules.Apps;

public sealed record SideloadScan(IReadOnlyList<ShippedFeature> Features, IReadOnlyList<FeatureFailure> Failures);

// <Home>/apps is flat: one folder per app, publisher always sideload. A missing directory is an empty
// sideload. FeatureDirectory is not used here because a folder of app.cs files would publish the
// directory name (apps) as the owner.
public sealed class SideloadDirectory(string path)
{
    public string Path => path;

    public SideloadScan Scan()
    {
        if (!Directory.Exists(path)) { return new([], []); }
        List<ShippedFeature> features = [];
        List<FeatureFailure> failures = [];
        foreach (var folder in new DirectoryInfo(path).EnumerateDirectories().OrderBy(item => item.Name, StringComparer.Ordinal))
        {
            if (folder.Name.StartsWith('_') || folder.Name.StartsWith('.')) { continue; }
            if (!File.Exists(System.IO.Path.Combine(folder.FullName, AppFiles.DeclarationPath))) { continue; }
            try
            {
                var files = Read(folder);
                var bundle = FeatureFolder.ToBundle(folder.Name, files);
                if (Blocked(files) is { } blocked)
                {
                    failures.Add(new(FeatureCatalog.SideloadOwner, bundle.Name, bundle.Version, blocked));
                    continue;
                }
                features.Add(new(FeatureCatalog.SideloadOwner, bundle, folder.Name));
            }
            catch (Exception error) when (error is ConfinementDenied or InvalidDataException or IOException or UnauthorizedAccessException)
            { failures.Add(new(FeatureCatalog.SideloadOwner, folder.Name, "", Reason(error))); }
        }
        return new(features, failures);
    }

    public async Task<string> Adopt(GitSideload git, string url, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(git);
        var staging = await git.CloneInto(path, url, cancellationToken).ConfigureAwait(false);
        try
        {
            var folder = new DirectoryInfo(staging);
            var files = Read(folder);
            var bundle = FeatureFolder.ToBundle(url, files);
            if (Blocked(files) is { } blocked) { throw new InvalidOperationException(blocked); }
            Write(bundle);
            return bundle.Name;
        }
        finally
        {
            if (Directory.Exists(staging)) { Directory.Delete(staging, recursive: true); }
        }
    }

    public void Write(AppBundle bundle)
    {
        ArgumentNullException.ThrowIfNull(bundle);
        if (bundle.Name is "." or ".." || bundle.Name.IndexOfAny(['/', '\\', ':']) >= 0 || bundle.Name.IndexOfAny(System.IO.Path.GetInvalidFileNameChars()) >= 0)
        { throw new ArgumentException($"'{bundle.Name}' is not an app folder name.", nameof(bundle)); }
        Directory.CreateDirectory(path);
        var root = PathConfinement.Normalize(path);
        PathConfinement.RequireNoLinks(root);
        var folder = System.IO.Path.Combine(root, bundle.Name);
        Directory.CreateDirectory(folder);
        var files = FeatureFolder.ToFiles(bundle);
        var inside = PathConfinement.Normalize(folder) + System.IO.Path.DirectorySeparatorChar;
        foreach (var relative in files.Keys)
        {
            var target = System.IO.Path.GetFullPath(System.IO.Path.Combine(folder, relative.Replace('/', System.IO.Path.DirectorySeparatorChar)));
            if (System.IO.Path.IsPathRooted(relative) || !target.StartsWith(inside, StringComparison.OrdinalIgnoreCase))
            { throw new ArgumentException($"{bundle.Name} file '{relative}' is outside its folder.", nameof(bundle)); }
        }
        foreach (var stale in new DirectoryInfo(folder).EnumerateFiles("*", SearchOption.AllDirectories))
        {
            var relative = System.IO.Path.GetRelativePath(folder, stale.FullName).Replace('\\', '/');
            if (!files.ContainsKey(relative)) { stale.Delete(); }
        }
        foreach (var (relative, content) in files)
        {
            var target = System.IO.Path.Combine(folder, relative.Replace('/', System.IO.Path.DirectorySeparatorChar));
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(target)!);
            File.WriteAllText(target, content);
        }
    }

    internal static void RequireInside(string root, string candidate)
    {
        var full = PathConfinement.Normalize(candidate);
        var baseRoot = PathConfinement.Normalize(root);
        PathConfinement.RequireNoLinks(full);
        _ = PathConfinement.Resolve([baseRoot], full);
    }

    private static Dictionary<string, string> Read(DirectoryInfo folder)
    {
        var root = PathConfinement.Normalize(folder.FullName);
        PathConfinement.RequireNoLinks(root);
        var files = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var entry in folder.EnumerateFileSystemInfos("*", SearchOption.AllDirectories))
        {
            var full = PathConfinement.Normalize(entry.FullName);
            PathConfinement.RequireNoLinks(full);
            _ = PathConfinement.Resolve([root], full);
            if (entry is not FileInfo file) { continue; }
            var relative = System.IO.Path.GetRelativePath(folder.FullName, file.FullName).Replace('\\', '/');
            if (relative.StartsWith("bin/", StringComparison.Ordinal) || relative.StartsWith("obj/", StringComparison.Ordinal) || relative.StartsWith(".git/", StringComparison.Ordinal))
            { continue; }
            files[relative] = File.ReadAllText(file.FullName).Replace("\r\n", "\n", StringComparison.Ordinal);
        }
        return files;
    }

    private static string? Blocked(IReadOnlyDictionary<string, string> files)
    {
        var found = DevAppEndpoints.EmptyCatches(files);
        if (found.Length == 0) { return null; }
        var first = found[0];
        return $"{first.Id} {first.Message} ({first.File}:{first.Line})";
    }

    private static string Reason(Exception error) => error is ConfinementDenied denied ? denied.Reason.ToString() : error.Message;
}
