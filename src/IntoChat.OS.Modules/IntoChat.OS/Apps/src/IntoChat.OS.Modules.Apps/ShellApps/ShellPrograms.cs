using System.Reflection;
using System.Text;

namespace IntoChat.OS.Modules.Apps;

// The shell apps' script sources, embedded from Shell/{Folder}. They stay programs the sandbox runs.
internal static class ShellPrograms
{
    internal static AppBundle Load(string folder)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(folder);
        var assembly = typeof(ShellPrograms).Assembly;
        var files = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var resource in assembly.GetManifestResourceNames())
        {
            var name = resource.Replace('\\', '/');
            const string marker = "shell/";
            var at = name.IndexOf(marker, StringComparison.Ordinal);
            if (at < 0) { continue; }
            var rest = name[(at + marker.Length)..];
            if (rest.StartsWith("Shell/", StringComparison.Ordinal)) { rest = rest["Shell/".Length..]; }
            var slash = rest.IndexOf('/');
            if (slash <= 0 || !rest[..slash].Equals(folder, StringComparison.Ordinal)) { continue; }
            using var stream = assembly.GetManifestResourceStream(resource)
                ?? throw new InvalidOperationException($"Embedded shell resource {resource} could not be opened.");
            using var reader = new StreamReader(stream, Encoding.UTF8);
            files[rest[(slash + 1)..]] = reader.ReadToEnd().Replace("\r\n", "\n", StringComparison.Ordinal);
        }
        if (!files.ContainsKey(AppFiles.DeclarationPath))
        {
            throw new InvalidOperationException(
                $"Shell app {folder} is not embedded. Resources: {string.Join(", ", assembly.GetManifestResourceNames())}.");
        }
        return FeatureFolder.ToBundle(folder, files);
    }
}
