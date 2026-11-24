using System.Xml.Linq;
using IntoChat.OS.Tests.Support;

namespace IntoChat.OS.Modules.Apps.Tests.Unit.Flows;

// Repository-relative project directories, read from Intochat.OS.slnx so a fact follows the tree when it moves.
internal static class SolutionPaths
{
    public static string Directory(string projectName)
    {
        var root = TestHome.RepoRoot();
        var slnx = XDocument.Load(Path.Combine(root, "Intochat.OS.slnx"));
        var path = slnx.Descendants("Project")
            .Select(static p => p.Attribute("Path")?.Value)
            .FirstOrDefault(p => p is not null && string.Equals(Path.GetFileNameWithoutExtension(p), projectName, StringComparison.OrdinalIgnoreCase))
            ?? throw new FileNotFoundException($"Project '{projectName}' was not found in Intochat.OS.slnx.");
        return Path.GetDirectoryName(path)!.Replace('\\', '/');
    }
}
