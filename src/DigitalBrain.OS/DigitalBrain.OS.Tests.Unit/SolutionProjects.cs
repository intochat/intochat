using System.Xml.Linq;

namespace DigitalBrain.OS.Tests.Unit;

// Repository files are found through the solution, by project name, never by a path literal.
internal static class SolutionProjects
{
    private const string SolutionFile = "DigitalBrain.slnx";

    internal static readonly string Root = FindRoot();

    internal static string DirectoryOf(string project)
    {
        var entry = XDocument.Load(Path.Combine(Root, SolutionFile)).Descendants("Project")
            .Select(element => element.Attribute("Path")!.Value.Replace('\\', '/'))
            .Single(path => Path.GetFileNameWithoutExtension(path) == project);
        return Path.GetFullPath(Path.Combine(Root, Path.GetDirectoryName(entry)!));
    }

    // The AppHost hands the OS the repository's features folder (AppHost.cs): {owner}/{App}/app.cs.
    internal static string Features => Path.Combine(Root, "features");

    internal static IReadOnlyList<DigitalBrain.Apps.AppBundle> FeaturesOf(string owner)
        => [.. new DigitalBrain.Apps.FeatureDirectory(Features).Load().Where(feature => feature.Owner == owner).Select(feature => feature.Bundle)];

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
