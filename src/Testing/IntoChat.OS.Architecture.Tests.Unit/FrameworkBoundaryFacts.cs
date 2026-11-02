using System.Reflection;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace IntoChat.OS.Architecture.Tests.Unit;

// The framework is in the digitalbrain repository (or framework/ if checked out in-tree): os consumes it via NuGet
// packages and never by ProjectReference across repositories. framework/ names no path outside itself and nothing
// IntoChat, its tests reference nothing under src/, and the packable set is the DX-03 list. The DigitalBrain closure
// still reaches no os or host assembly (D-09).
public sealed partial class FrameworkBoundaryFacts
{
    // REQUIREMENTS DX-03, the digitalbrain packages.
    private static readonly string[] Dx03Packages =
    [
        "DigitalBrain",
        "DigitalBrain.Analyzers",
        "DigitalBrain.Apps.Packaging",
        "DigitalBrain.Capabilities.Identity",
        "DigitalBrain.Capabilities.Secrets",
        "DigitalBrain.Capabilities.Storage",
        "DigitalBrain.Graph",
        "DigitalBrain.Runtime.Http",
        "DigitalBrain.Runtime.Orleans",
        "DigitalBrain.Runtime.Orleans.Azure",
        "DigitalBrain.Templates",
        "DigitalBrain.Testing",
        "DigitalBrain.Testing.Silo",
        "DigitalBrain.UI",
    ];

    private static readonly string[] SkippedDirectories = ["bin", "obj", "artifacts", "node_modules", ".git", ".dart_tool", "build", ".vs", ".idea"];

    private static readonly StringComparison PathComparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    private static IEnumerable<string> FilesUnder(string start, Func<string, bool> name)
    {
        var pending = new Stack<string>([start]);
        while (pending.Count > 0)
        {
            var directory = pending.Pop();
            foreach (var child in Directory.EnumerateDirectories(directory))
            {
                if (!SkippedDirectories.Contains(Path.GetFileName(child), StringComparer.OrdinalIgnoreCase)) { pending.Push(child); }
            }
            foreach (var file in Directory.EnumerateFiles(directory).Where(file => name(Path.GetFileName(file)))) { yield return file; }
        }
    }

    private static bool IsBuildFile(string name)
        => name.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".props", StringComparison.OrdinalIgnoreCase)
            || name.EndsWith(".targets", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".slnx", StringComparison.OrdinalIgnoreCase);

    private static bool InFramework(string path)
        => (Path.GetFullPath(path) + Path.DirectorySeparatorChar).StartsWith(Repository.FrameworkRoot + Path.DirectorySeparatorChar, PathComparison);

    private static string[] FrameworkBuildFiles()
    {
        var files = FilesUnder(Repository.FrameworkRoot, IsBuildFile).ToArray();
        Assert.True(files.Length > 20, "the framework scan found " + files.Length + " build files");
        return files;
    }

    // Every path a build file names: references, imports, compile/none/content items and solution entries.
    private static IEnumerable<string> NamedPaths(XDocument document)
    {
        foreach (var element in document.Descendants())
        {
            var value = element.Name.LocalName switch
            {
                "ProjectReference" or "Compile" or "None" or "Content" or "EmbeddedResource" => element.Attribute("Include")?.Value,
                "Import" when element.Attribute("Sdk") is null => element.Attribute("Project")?.Value,
                "Project" when element.Parent is not null => element.Attribute("Path")?.Value,
                _ => null,
            };
            if (value is not null) { yield return value; }
        }
    }

    // Literal paths a build file names, resolved against its directory; MSBuild expressions other than the two
    // directory properties are not resolvable here and are skipped.
    private static IEnumerable<(string Raw, string Resolved)> ResolvedPaths(string file)
    {
        var directory = Path.GetDirectoryName(file)! + Path.DirectorySeparatorChar;
        foreach (var raw in NamedPaths(XDocument.Load(file)).SelectMany(value => value.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)))
        {
            var value = raw.Replace("$(MSBuildThisFileDirectory)", directory, StringComparison.Ordinal)
                .Replace("$(MSBuildProjectDirectory)", directory, StringComparison.Ordinal);
            if (value.Contains("$(", StringComparison.Ordinal) || value.Contains("@(", StringComparison.Ordinal) || value.Contains("%(", StringComparison.Ordinal)) { continue; }
            var wildcard = value.IndexOfAny(['*', '?']);
            var literal = wildcard < 0 ? value : value[..wildcard];
            yield return (raw, Path.GetFullPath(Path.Combine(directory, literal.Replace('\\', '/'))));
        }
    }

    [Fact]
    public void FrameworkNamesNoPathOutsideFramework()
    {
        if (!Directory.Exists(Repository.FrameworkRoot)) { return; }
        var offenders = FrameworkBuildFiles()
            .SelectMany(file => ResolvedPaths(file).Where(path => !InFramework(path.Resolved)).Select(path => Repository.Relative(file) + ": " + path.Raw))
            .ToArray();
        Assert.True(offenders.Length == 0, "Build files under framework/ name only paths inside framework/:\n" + string.Join('\n', offenders));
    }

    [Fact]
    public void FrameworkReferencesNoIntoChat()
    {
        if (!Directory.Exists(Repository.FrameworkRoot)) { return; }
        var offenders = new List<string>();
        foreach (var project in FrameworkBuildFiles().Where(file => file.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase)))
        {
            var document = XDocument.Load(project);
            var names = document.Descendants("PackageReference").Select(reference => reference.Attribute("Include")?.Value)
                .Concat(document.Descendants("ProjectReference").Select(reference => reference.Attribute("Include")?.Value is { } include
                    ? Path.GetFileNameWithoutExtension(include.Replace('\\', '/')) : null))
                .OfType<string>();
            offenders.AddRange(names.Where(name => name.StartsWith("IntoChat", StringComparison.OrdinalIgnoreCase)).Select(name => Repository.Relative(project) + " -> " + name));
        }
        Assert.True(offenders.Count == 0, "The framework references nothing IntoChat:\n" + string.Join('\n', offenders));
    }

    [Fact]
    public void FrameworkTestsReferenceNoSrc()
    {
        if (!Directory.Exists(Repository.FrameworkRoot)) { return; }
        var source = Repository.SourceRoot + Path.DirectorySeparatorChar;
        var projects = new[] { "tests", "templates" }
            .SelectMany(folder => FilesUnder(Path.Combine(Repository.FrameworkRoot, folder), name => name.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase)))
            .ToArray();
        Assert.True(projects.Length > 10, "the framework test scan found " + projects.Length + " projects");
        var offenders = projects
            .SelectMany(project => ResolvedPaths(project).Where(path => (path.Resolved + Path.DirectorySeparatorChar).StartsWith(source, PathComparison))
                .Select(path => Repository.Relative(project) + ": " + path.Raw))
            .ToArray();
        Assert.True(offenders.Length == 0, "Framework tests and templates reference nothing under src/:\n" + string.Join('\n', offenders));
    }

    [Fact]
    public void ConsumersReferenceFrameworkByProject()
    {
        var files = FilesUnder(Repository.Root, IsBuildFile).Where(file => !InFramework(file)).ToArray();
        Assert.True(files.Length > 50, "the scan found " + files.Length + " build files");
        var offenders = files
            .SelectMany(file => XDocument.Load(file).Descendants("ProjectReference")
                .Where(element => element.Attribute("Include")?.Value is { } include
                    && Path.GetFileNameWithoutExtension(include.Replace('\\', '/')).StartsWith("DigitalBrain", StringComparison.Ordinal))
                .Select(element => Repository.Relative(file) + ": ProjectReference " + element.Attribute("Include")!.Value))
            .ToArray();
        Assert.True(offenders.Length == 0, "os consumes the framework as NuGet packages, never by ProjectReference:\n" + string.Join('\n', offenders));
    }

    [Fact]
    public void FrameworkPackagesAreExactlyTheDx03List()
    {
        if (!Directory.Exists(Repository.FrameworkRoot)) { return; }
        var packable = FrameworkBuildFiles()
            .Where(file => file.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase))
            .Select(XDocument.Load)
            .Where(project => project.Descendants("IsPackable").LastOrDefault()?.Value.Trim() == "true")
            .Select(project => project.Descendants("PackageId").LastOrDefault()?.Value.Trim())
            .OfType<string>()
            .Order(StringComparer.Ordinal)
            .ToArray();
        Assert.Equal(Dx03Packages.Order(StringComparer.Ordinal), packable);
    }

    // Kept from NuGetOnlyConsumptionFacts: no build file anywhere names a path outside the repository.
    [Fact]
    public void NoProjectPathLeavesTheRepository()
    {
        var root = Path.GetFullPath(Repository.Root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var offenders = FilesUnder(Repository.Root, IsBuildFile)
            .SelectMany(file => ResolvedPaths(file).Where(path => !(path.Resolved + Path.DirectorySeparatorChar).StartsWith(root, PathComparison))
                .Select(path => Repository.Relative(file) + ": " + path.Raw))
            .ToArray();
        Assert.True(offenders.Length == 0, "Build files may only name paths inside the repository:\n" + string.Join('\n', offenders));
    }

    // Kept from NuGetOnlyConsumptionFacts: restores go through the committed nuget.config (nuget.org only).
    [Fact]
    public void WorkflowsRestoreFromTheCommittedConfigOnly()
    {
        var offenders = Directory.EnumerateFiles(Path.Combine(Repository.Root, ".github", "workflows"), "*.yml")
            .Where(path => LocalFeed().IsMatch(File.ReadAllText(path)))
            .Select(Repository.Relative).ToArray();
        Assert.True(offenders.Length == 0, "Workflows restore through the committed nuget.config: " + string.Join(", ", offenders));
    }

    [GeneratedRegex("local" + "-feed|Restore" + "ConfigFile", RegexOptions.IgnoreCase)]
    private static partial Regex LocalFeed();

    // Kept from NuGetOnlyConsumptionFacts.
    [Fact]
    public void NoLockFiles()
    {
        var locks = FilesUnder(Repository.Root, name => name.Equals("packages.lock.json", StringComparison.OrdinalIgnoreCase)).Select(Repository.Relative).ToArray();
        Assert.True(locks.Length == 0, "No package lock files: " + string.Join(", ", locks));
    }

    // Kept from NuGetOnlyConsumptionFacts: the cloud profile reads its Key Vault from configuration; only tests may replace that source.
    [GeneratedRegex(@"new\s+CloudProfile\s*\(\s*[^)\s]|AddCloudTopology\s*\(\s*[^)\s]")]
    private static partial Regex KeyVaultOverride();

    [Fact]
    public void NoKeyVaultOverrideInOs()
    {
        var tests = Repository.TestProjects().Select(project => project.Name).ToHashSet(StringComparer.Ordinal);
        var offenders = Repository.ProjectFiles("*.cs", path => KeyVaultOverride().IsMatch(File.ReadAllText(path)), project => !tests.Contains(project.Name))
            .Select(Repository.ProjectKey).ToArray();
        Assert.True(offenders.Length == 0, "Production code never overrides the cloud Key Vault source: " + string.Join(", ", offenders));
    }

    // Kept from DigitalBrainReferenceClosureFacts (Phase 3 R4-07, D-09).
    [Fact]
    public void AuthoringTypesLiveInDigitalBrain()
    {
        var core = Assembly.Load("DigitalBrain");
        Assert.NotNull(core.GetType("DigitalBrain.Module", throwOnError: true));
        Assert.NotNull(core.GetType("DigitalBrain.ModuleManifest", throwOnError: true));
    }

    [Fact]
    public void DigitalBrainClosureExcludesEveryOsAssembly()
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var named = new HashSet<string>(StringComparer.Ordinal);
        var pending = new Stack<Assembly>([Assembly.Load("DigitalBrain")]);
        while (pending.Count > 0)
        {
            var assembly = pending.Pop();
            if (!seen.Add(assembly.GetName().Name!)) { continue; }
            named.UnionWith(assembly.GetReferencedAssemblies().Select(name => name.Name!));
            foreach (var reference in assembly.GetReferencedAssemblies().Where(name => name.Name!.StartsWith("DigitalBrain", StringComparison.Ordinal) || name.Name!.StartsWith("IntoChat", StringComparison.Ordinal)))
            { pending.Push(Assembly.Load(reference)); }
        }

        Assert.Contains("DigitalBrain", seen);
        var leaks = seen.Where(name => name.StartsWith("IntoChat", StringComparison.Ordinal)).Order(StringComparer.Ordinal).ToArray();
        Assert.True(leaks.Length == 0, "The DigitalBrain closure reaches: " + string.Join(", ", leaks));
        var hosts = named.Where(name => name.StartsWith("Orleans", StringComparison.Ordinal) || name.StartsWith("Microsoft.Orleans", StringComparison.Ordinal)
            || name.StartsWith("Microsoft.AspNetCore", StringComparison.Ordinal)).Order(StringComparer.Ordinal).ToArray();
        Assert.True(hosts.Length == 0, "The DigitalBrain closure references host assemblies: " + string.Join(", ", hosts));
    }

    [Fact]
    public void NoOsSdkProjectOrTemplatePackRemains()
    {
        Assert.DoesNotContain(Repository.Projects.Keys, name => name.StartsWith("IntoChat.OS." + "Sdk", StringComparison.Ordinal));
        Assert.False(Directory.Exists(Path.Combine(Repository.Root, "templates", "IntoChat.OS.Templates")), "the os template pack is replaced by DigitalBrain.Templates (intochat-module)");
    }
}
