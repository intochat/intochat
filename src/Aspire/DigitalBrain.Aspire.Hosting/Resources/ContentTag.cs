using System.Security.Cryptography;
using System.Text;

namespace DigitalBrain.Aspire.Hosting;

// A derived image's tag IS its identity: same sources, same image, no per-session rebuilds or
// tag litter. Sessions reference the artifact; only changed sources mint a new tag.
public static class ContentTag
{
    private static readonly HashSet<string> ExcludedDirectories =
        new(["bin", "obj", "build", ".dart_tool", ".idea", ".git", "windows"], StringComparer.OrdinalIgnoreCase);

    public static string Of(string root, params string[] extraInputs)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var file in Files(new DirectoryInfo(root)).OrderBy(file => file.relative, StringComparer.Ordinal))
        {
            hash.AppendData(Encoding.UTF8.GetBytes(file.relative));
            hash.AppendData(File.ReadAllBytes(file.path));
        }
        foreach (var input in extraInputs) { hash.AppendData(Encoding.UTF8.GetBytes(input)); }
        return Convert.ToHexStringLower(hash.GetHashAndReset())[..16];
    }

    private static IEnumerable<(string path, string relative)> Files(DirectoryInfo root)
    {
        var pending = new Stack<DirectoryInfo>([root]);
        while (pending.Count > 0)
        {
            var directory = pending.Pop();
            foreach (var child in directory.EnumerateDirectories())
            {
                if (!ExcludedDirectories.Contains(child.Name)) { pending.Push(child); }
            }
            foreach (var file in directory.EnumerateFiles())
            {
                if (file.Extension.Equals(".iml", StringComparison.OrdinalIgnoreCase)) { continue; }
                yield return (file.FullName, Path.GetRelativePath(root.FullName, file.FullName).Replace('\\', '/'));
            }
        }
    }
}
