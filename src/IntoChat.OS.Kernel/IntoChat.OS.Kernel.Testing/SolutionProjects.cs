using System.Xml.Linq;

namespace IntoChat.OS.Kernel.Testing;

// Repository files are found through the solution, by project name, never by a path literal.
public static class SolutionProjects
{
    private const string SolutionFile = "Intochat.OS.slnx";

    public static readonly string Root = FindRoot();

    public static string DirectoryOf(string project)
    {
        var entry = XDocument.Load(Path.Combine(Root, SolutionFile)).Descendants("Project")
            .Select(element => element.Attribute("Path")!.Value.Replace('\\', '/'))
            .Single(path => Path.GetFileNameWithoutExtension(path) == project);
        return Path.GetFullPath(Path.Combine(Root, Path.GetDirectoryName(entry)!));
    }

    // The AppHost hands the OS the repository's features folder (AppHost.cs): {owner}/{App}/app.cs.
    public static string Features => Path.Combine(Root, "features");

    public static IReadOnlyList<IntoChat.OS.Apps.AppBundle> FeaturesOf(string owner)
        => [.. new IntoChat.OS.Apps.FeatureDirectory(Features).Load().Where(feature => feature.Owner == owner).Select(feature => feature.Bundle)];

    private static string FindRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, SolutionFile)))
        {
            directory = directory.Parent;
        }
        return directory?.FullName
            ?? throw new InvalidOperationException($"Repository root ({SolutionFile}) was not found above {AppContext.BaseDirectory}.");
    }
}
