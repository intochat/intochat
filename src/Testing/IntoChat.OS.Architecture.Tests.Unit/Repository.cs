using System.Xml.Linq;

namespace IntoChat.OS.Architecture.Tests.Unit;

// The one place a fact learns where the repository is. Intochat.OS.slnx is the map: every
// project, its directory and its solution folder come from there, so no fact carries a path
// literal that the next restructure silently invalidates (RepositoryPathLiteralFacts holds that line).
internal static class Repository
{
    internal const string SolutionFile = "Intochat.OS.slnx";

    internal sealed record Project(string Name, string Path, string Folder)
    {
        public string Directory => System.IO.Path.GetDirectoryName(Path)!;
    }

    internal static readonly string Root = FindRoot();

    internal static readonly XDocument Solution = XDocument.Load(System.IO.Path.Combine(Root, SolutionFile));

    // The folded-in framework (framework/, 06-00) shares the solution but not the os rules: its own
    // DigitalBrain.Architecture.Tests.Unit govern it, and FrameworkBoundaryFacts hold the folder boundary.
    internal const string FrameworkDirectory = "framework";

    internal static bool IsFrameworkEntry(XElement entry)
        => entry.Attribute("Path")!.Value.Replace('\\', '/').StartsWith(FrameworkDirectory + "/", StringComparison.Ordinal);

    internal static readonly string FrameworkRoot = System.IO.Directory.Exists(System.IO.Path.Combine(Root, FrameworkDirectory))
        ? System.IO.Path.Combine(Root, FrameworkDirectory)
        : System.IO.Path.GetFullPath(System.IO.Path.Combine(Root, "..", "digitalbrain"));

    // Compiled first-party apps keep their source under features/, with their own framework-only boundary.
    internal static readonly string FeatureRoot = System.IO.Path.Combine(Root, "features");

    internal static bool IsFeatureEntry(XElement entry)
        => entry.Attribute("Path")!.Value.Replace('\\', '/').StartsWith("features/", StringComparison.Ordinal);

    internal static readonly IReadOnlyDictionary<string, Project> Projects = Solution.Descendants("Project")
        .Where(entry => !IsFrameworkEntry(entry) && !IsFeatureEntry(entry))
        .Select(entry =>
        {
            var relative = entry.Attribute("Path")!.Value.Replace('\\', '/');
            var folder = entry.Parent!.Name.LocalName == "Folder" ? entry.Parent.Attribute("Name")!.Value : "/";
            return new Project(System.IO.Path.GetFileNameWithoutExtension(relative), System.IO.Path.GetFullPath(System.IO.Path.Combine(Root, relative)), folder);
        })
        .ToDictionary(project => project.Name, StringComparer.Ordinal);

    // Where every project lives: the common parent of all solution projects, learned from the
    // solution rather than named.
    internal static readonly string SourceRoot = CommonDirectory(Projects.Values.Select(project => project.Directory));

    internal static readonly string ModulesRoot = CommonDirectory(Projects.Values
        .Where(project => project.Name.StartsWith("IntoChat.OS.Modules.", StringComparison.Ordinal))
        .Select(project => project.Directory));

    internal static Project ProjectNamed(string name)
        => Projects.TryGetValue(name, out var project) ? project
            : throw new KeyNotFoundException($"{name} is not a project in {SolutionFile}.");

    // Files that belong to a solution project (app bundles, samples and the Flutter app sit
    // outside every project and have no project key).
    // A project directory may contain another project's directory (the host sits under IntoChat.OS). A file belongs to the
    // longest matching project, so the parent walk does not count it twice.
    internal static IEnumerable<string> ProjectFiles(string pattern, Func<string, bool>? predicate = null, Func<Project, bool>? projects = null)
        => Projects.Values.Where(project => projects is null || projects(project))
            .SelectMany(project => SourceFiles(project.Directory, pattern, predicate).Where(path => Owns(project, path)));

    private static bool Owns(Project project, string path)
    {
        var full = System.IO.Path.GetFullPath(path);
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var owner = Projects.Values
            .Where(candidate => full.StartsWith(candidate.Directory + System.IO.Path.DirectorySeparatorChar, comparison))
            .MaxBy(candidate => candidate.Directory.Length);
        return owner?.Name == project.Name;
    }

    internal static IEnumerable<string> SourceFiles(string directory, string pattern, Func<string, bool>? predicate = null)
        => Directory.EnumerateFiles(directory, pattern, SearchOption.AllDirectories)
            .Where(path => !path.Split(System.IO.Path.DirectorySeparatorChar).Any(part => part is "obj" or "bin"))
            .Where(path => predicate is null || predicate(path));

    internal static IEnumerable<Project> TestProjects()
        => Projects.Values.Where(project => project.Name.EndsWith(".Tests", StringComparison.Ordinal) || project.Name.Contains(".Tests.", StringComparison.Ordinal));

    internal static IEnumerable<string> References(string project)
        => XDocument.Load(project).Descendants("ProjectReference")
            .Select(reference => reference.Attribute("Include")?.Value)
            .OfType<string>()
            .Where(include => !include.Contains('@', StringComparison.Ordinal))
            .Select(include => System.IO.Path.GetFileNameWithoutExtension(include.Replace('\\', '/')));

    internal static IEnumerable<string> PackageReferences(string project)
        => XDocument.Load(project).Descendants("PackageReference")
            .Select(reference => reference.Attribute("Include")?.Value)
            .OfType<string>();

    internal static string Relative(string path) => System.IO.Path.GetRelativePath(Root, path).Replace('\\', '/');

    // "<ProjectName>/<path inside the project directory>": stable across directory moves, so a
    // baseline keyed this way survives a restructure as long as the project keeps its name.
    internal static string ProjectKey(string path)
    {
        var full = System.IO.Path.GetFullPath(path);
        var owner = Projects.Values
            .Where(project => full.StartsWith(project.Directory + System.IO.Path.DirectorySeparatorChar, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            .MaxBy(project => project.Directory.Length)
            ?? throw new InvalidOperationException($"{Relative(path)} belongs to no project in {SolutionFile}.");
        return owner.Name + "/" + System.IO.Path.GetRelativePath(owner.Directory, full).Replace('\\', '/');
    }

    internal static string FromProjectKey(string key)
    {
        var slash = key.IndexOf('/', StringComparison.Ordinal);
        var project = ProjectNamed(slash < 0 ? key : key[..slash]);
        return slash < 0 ? project.Directory : System.IO.Path.Combine(project.Directory, key[(slash + 1)..].Replace('/', System.IO.Path.DirectorySeparatorChar));
    }

    private static string FindRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(System.IO.Path.Combine(directory.FullName, SolutionFile)))
        {
            directory = directory.Parent;
        }
        return directory?.FullName ?? throw new DirectoryNotFoundException(SolutionFile + " was not found above " + AppContext.BaseDirectory);
    }

    private static string CommonDirectory(IEnumerable<string> directories)
    {
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        string? common = null;
        foreach (var directory in directories)
        {
            if (common is null) { common = directory; continue; }
            while (!directory.StartsWith(common + System.IO.Path.DirectorySeparatorChar, comparison) && !string.Equals(directory, common, comparison))
            {
                common = System.IO.Path.GetDirectoryName(common) ?? throw new InvalidOperationException("Projects share no directory.");
            }
        }
        return common ?? throw new InvalidOperationException("No projects.");
    }
}
