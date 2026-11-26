using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace IntoChat.OS.Architecture.Tests.Unit;

// The solution mirrors the disk. A project at src/A/B/<Name>/<Name>.csproj sits in the solution
// folder /A/B/ with the "IntoChat.OS." prefix dropped from each segment that carries it
// (src/IntoChat.OS.Aspire → /Aspire/, src/IntoChat.OS.Modules/AI → /Modules/AI/); a project
// directly under src has no folder. Module tests live under their module's tests/ directory and
// are grouped either with the module or in a /tests/ subfolder; ring tests sit beside their ring.
public sealed class RepositoryLayoutFacts
{
    private const string Prefix = "IntoChat.OS.";

    private static readonly Dictionary<string, string> RootNamespaces = new(StringComparer.Ordinal)
    {
        ["IntoChat.OS.Modules.Catalog"] = "IntoChat.OS.Modules.Apps",
        ["IntoChat.OS.Modules.Roslyn"] = "IntoChat.OS.Modules.Microsoft.Roslyn",
        ["IntoChat.OS.Modules.Shell"] = "IntoChat.OS.Modules.Flutter",
    };

    [Fact]
    public void NoScriptFilesExistInRepository()
    {
        var scriptExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".sh", ".py", ".ps1", ".pyc"
        };

        var allFiles = Directory.EnumerateFiles(Repository.Root, "*", SearchOption.AllDirectories)
            .Where(file =>
            {
                var parts = Path.GetRelativePath(Repository.Root, file).Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                return !parts.Any(p => p is ".git" or ".claude" or "bin" or "obj" or "artifacts" or "ephemeral");
            })
            .ToList();

        var offenders = new List<string>();

        foreach (var file in allFiles)
        {
            var relativePath = Path.GetRelativePath(Repository.Root, file).Replace('\\', '/');

            // Allow systemone-adapter server.py, official installer scripts under eng/install/,
            // the reusable POSIX Azure CLI retry helper embedded by PulumiStackSynthesizer (scripts/az-retry.sh),
            // and Flutter's gitignored iOS build helper
            if (relativePath.EndsWith("Modules/AI/systemone-adapter/server.py", StringComparison.OrdinalIgnoreCase)
                || relativePath.StartsWith("eng/install/", StringComparison.OrdinalIgnoreCase)
                || relativePath.Equals("scripts/az-retry.sh", StringComparison.OrdinalIgnoreCase)
                || relativePath.Equals("ui/ios/Flutter/flutter_export_environment.sh", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var ext = Path.GetExtension(file);
            if (scriptExtensions.Contains(ext))
            {
                offenders.Add(relativePath);
                continue;
            }

            // Check scripts/ directory for any remaining files
            if (relativePath.StartsWith("scripts/", StringComparison.OrdinalIgnoreCase))
            {
                offenders.Add(relativePath);
            }
        }

        Assert.True(offenders.Count == 0,
            "Script files found in repository:\n" + string.Join("\n", offenders));
    }

    [Fact]
    public void EveryProjectHasOneSolutionEntryInItsPhysicalGroup()
    {
        SyncSolutionIfNeeded();

        var comparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        var entries = Repository.Solution.Descendants("Project").Where(entry => !Repository.IsFrameworkEntry(entry) && !Repository.IsFeatureEntry(entry)).ToArray();
        var paths = entries.Select(entry => entry.Attribute("Path")!.Value.Replace('\\', '/')).ToArray();
        Assert.Equal(paths.Length, paths.Distinct(comparer).Count());
        var disk = Repository.SourceFiles(Repository.SourceRoot, "*.csproj",
                path => !Repository.Relative(path).Split('/').Any(part => part is "framework" or "features" or "artifacts" or "templates"))
            .Select(Repository.Relative).Order().ToArray();
        Assert.True(disk.SequenceEqual(paths.Order(), comparer),
            "Solution entries differ from the projects on disk; fix with FIX_LAYOUT=1.\nOn disk only: "
            + string.Join(", ", disk.Except(paths, comparer)) + "\nIn the solution only: " + string.Join(", ", paths.Except(disk, comparer)));
        var sourceRoot = Repository.Relative(Repository.SourceRoot);
        var modulesRoot = Repository.Relative(Repository.ModulesRoot);
        var componentsRoot = Repository.Relative(Repository.ComponentsRoot);
        foreach (var entry in entries)
        {
            var path = entry.Attribute("Path")!.Value.Replace('\\', '/');
            var directory = Path.GetDirectoryName(path)!.Replace('\\', '/');
            var name = Path.GetFileNameWithoutExtension(path);
            var folderName = Path.GetFileName(directory);
            Assert.True(folderName == name
                    || name == Prefix + folderName
                    || name == "IntoChat.OS.Modules." + folderName
                    || (folderName == "Contracts" && name == "IntoChat.OS.Modules." + Path.GetFileName(Path.GetDirectoryName(directory)) + ".Contracts")
                    || (folderName == "Generators" && name == "IntoChat.OS.Modules." + Path.GetFileName(Path.GetDirectoryName(directory)) + ".Generators")
                    // A component: src/Components/<Name>/IntoChat.<Name>.csproj and <Name>/Contracts/IntoChat.<Name>.Contracts.csproj.
                    || (Path.GetDirectoryName(directory)!.Replace('\\', '/') == componentsRoot && name == "IntoChat." + folderName)
                    || (folderName == "Contracts" && Path.GetDirectoryName(Path.GetDirectoryName(directory))!.Replace('\\', '/') == componentsRoot
                        && name == "IntoChat." + Path.GetFileName(Path.GetDirectoryName(directory)) + ".Contracts"),
                $"{path} directory '{folderName}' is not the project name.");
            var group = directory[..directory.LastIndexOf('/')];
            var folder = entry.Parent!.Name.LocalName == "Folder" ? entry.Parent.Attribute("Name")!.Value : null;
            var expected = ExpectedFolders(group, sourceRoot);
            Assert.True(expected.Contains(folder, StringComparer.Ordinal),
                $"{path} sits in solution folder '{folder}', not where its directory puts it ({string.Join(" or ", expected.Select(f => f ?? "<root>"))}).");
            var isTest = name.EndsWith(".Tests", StringComparison.Ordinal) || name.Contains(".Tests.", StringComparison.Ordinal);
            if (isTest && directory.StartsWith(modulesRoot + "/", StringComparison.Ordinal))
            {
                Assert.True(Path.GetFileName(group) == "tests", $"Module test project {path} belongs under its module's tests/ directory.");
            }
            var project = XDocument.Load(Path.Combine(Repository.Root, path));
            foreach (var reference in project.Descendants("ProjectReference"))
            {
                var include = reference.Attribute("Include")!.Value;
                if (include.Contains('@', StringComparison.Ordinal))
                {
                    continue;
                }
                // The OS references its modules by glob; the catalog facts check that closure.
                if (include.Contains('*', StringComparison.Ordinal))
                {
                    continue;
                }
                Assert.True(File.Exists(Path.Combine(Repository.Root, directory, include.Replace('\\', '/'))), $"Broken project reference: {path} -> {include}");
            }
            foreach (var property in project.Descendants().Where(element => element.Name.LocalName is "RootNamespace" or "AssemblyName" or "PackageId"))
            {
                // A component's package ids follow their own rule (ComponentFacts.AComponentsPackageIdsAreIntoChatOSComponentsNames).
                if (property.Name.LocalName == "PackageId" && directory.StartsWith(componentsRoot + "/", StringComparison.Ordinal))
                {
                    continue;
                }
                var expectedValue = property.Name.LocalName == "RootNamespace" ? RootNamespaces.GetValueOrDefault(name, name) : name;
                Assert.Equal(expectedValue, property.Value);
            }
        }
    }

    // framework/{src,tests,templates}/<Name>/<Name>.csproj sits in the solution folder named by its group
    // (/framework/src/ ...); template payloads under content/ are not solution projects.
    [Fact]
    public void EveryFrameworkProjectHasOneSolutionEntryInItsFrameworkGroup()
    {
        var comparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        var entries = Repository.Solution.Descendants("Project").Where(Repository.IsFrameworkEntry).ToArray();
        if (!Directory.Exists(Path.Combine(Repository.Root, Repository.FrameworkDirectory)))
        {
            Assert.Empty(entries);
            return;
        }
        var paths = entries.Select(entry => entry.Attribute("Path")!.Value.Replace('\\', '/')).Order().ToArray();
        var disk = Repository.SourceFiles(Repository.FrameworkRoot, "*.csproj", path => !path.Split(Path.DirectorySeparatorChar).Contains("content"))
            .Select(Repository.Relative).Order().ToArray();
        Assert.True(disk.SequenceEqual(paths, comparer), "Framework solution entries differ from framework/ on disk; fix with FIX_LAYOUT=1.");
        foreach (var entry in entries)
        {
            var path = entry.Attribute("Path")!.Value.Replace('\\', '/');
            var group = path[..path.LastIndexOf('/', path.LastIndexOf('/') - 1)];
            Assert.Equal("/" + group + "/", entry.Parent!.Attribute("Name")?.Value);
        }
    }

    [Fact]
    public void EveryCompiledFeatureProjectHasOneSolutionEntryInItsFeatureGroup()
    {
        var comparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        var entries = Repository.Solution.Descendants("Project").Where(Repository.IsFeatureEntry).ToArray();
        var paths = entries.Select(entry => entry.Attribute("Path")!.Value.Replace('\\', '/')).Order().ToArray();
        var disk = Repository.SourceFiles(Repository.FeatureRoot, "*.csproj").Select(Repository.Relative).Order().ToArray();
        Assert.True(disk.SequenceEqual(paths, comparer), "Compiled feature solution entries differ from features/ on disk; fix with FIX_LAYOUT=1.");
        foreach (var entry in entries)
        {
            var path = entry.Attribute("Path")!.Value.Replace('\\', '/');
            var group = path[..path.LastIndexOf('/', path.LastIndexOf('/') - 1)];
            Assert.Equal("/" + group + "/", entry.Parent!.Attribute("Name")?.Value);
        }
    }

    private static void SyncSolutionIfNeeded()
    {
        if (Environment.GetEnvironmentVariable("FIX_LAYOUT") != "1")
        {
            return;
        }

        var root = Repository.Root;
        var slnx = Path.Combine(root, "Intochat.OS.slnx");
        var document = XDocument.Load(slnx);
        var solution = document.Root!;

        var disk = Directory.EnumerateFiles(Repository.SourceRoot, "*.csproj", SearchOption.AllDirectories)
            .Select(path => Path.GetRelativePath(root, path).Replace(Path.DirectorySeparatorChar, '/'))
            .Where(path => !path.Split('/').Any(part => part is "bin" or "obj" or "artifacts" or "templates" or "framework" or "features"))
            .Concat((Directory.Exists(Path.Combine(root, "features")) ? Directory.EnumerateFiles(Path.Combine(root, "features"), "*.csproj", SearchOption.AllDirectories) : [])
                .Select(path => Path.GetRelativePath(root, path).Replace(Path.DirectorySeparatorChar, '/'))
                .Where(path => !path.Split('/').Any(part => part is "bin" or "obj")))
            .Concat((Directory.Exists(Path.Combine(root, "framework")) ? Directory.EnumerateFiles(Path.Combine(root, "framework"), "*.csproj", SearchOption.AllDirectories) : [])
                .Select(path => Path.GetRelativePath(root, path).Replace(Path.DirectorySeparatorChar, '/'))
                .Where(path => !path.Split('/').Any(part => part is "bin" or "obj" or "content")))
            .Order(StringComparer.Ordinal)
            .ToArray();

        var current = solution.Descendants("Project")
            .ToDictionary(project => project.Attribute("Path")!.Value.Replace(Path.DirectorySeparatorChar, '/'), project => project.Parent!.Name.LocalName == "Folder" ? project.Parent.Attribute("Name")!.Value : null);

        var sourceRel = Repository.Relative(Repository.SourceRoot);
        var wanted = disk.ToDictionary(path => path, path =>
        {
            var allowed = AllowedFoldersForProject(path, sourceRel);
            return current.TryGetValue(path, out var existing) && allowed.Contains(existing) ? existing : allowed[^1];
        });

        var folders = solution.Elements("Folder").ToDictionary(folder => folder.Attribute("Name")!.Value, StringComparer.Ordinal);
        foreach (var project in solution.Descendants("Project").ToArray())
        {
            project.Remove();
        }

        foreach (var name in wanted.Values.Where(name => name is not null).Distinct().Order(StringComparer.Ordinal))
        {
            if (folders.ContainsKey(name!))
            {
                continue;
            }
            var created = new XElement("Folder", new XAttribute("Name", name!));
            folders[name!] = created;
            var before = solution.Elements("Folder").FirstOrDefault(folder => string.CompareOrdinal(folder.Attribute("Name")!.Value, name) > 0
                && folder.Attribute("Name")!.Value != "/Solution Items/");
            if (before is not null)
            {
                before.AddBeforeSelf(created);
            }
            else
            {
                solution.Add(created);
            }
        }

        foreach (var group in wanted.GroupBy(pair => pair.Value))
        {
            var elements = group.Select(pair => pair.Key).Order(StringComparer.OrdinalIgnoreCase).Select(path => new XElement("Project", new XAttribute("Path", path)));
            if (group.Key is null)
            {
                solution.Add(elements);
            }
            else
            {
                folders[group.Key].Add(elements);
            }
        }

        foreach (var folder in solution.Elements("Folder").Where(folder => !folder.HasElements && current.Values.Contains(folder.Attribute("Name")!.Value) && !wanted.Values.Contains(folder.Attribute("Name")!.Value)).ToArray())
        {
            folder.Remove();
        }

        var text = new StringBuilder();
        using (var writer = XmlWriter.Create(text, new XmlWriterSettings { OmitXmlDeclaration = true, Indent = true, IndentChars = "  ", NewLineChars = "\r\n", NewLineHandling = NewLineHandling.Replace }))
        {
            document.Save(writer);
        }
        var next = text.ToString().Replace("\"></Folder>", "\" />", StringComparison.Ordinal).TrimEnd() + "\r\n";
        File.WriteAllText(slnx, next, new UTF8Encoding(false));
    }

    private static string?[] AllowedFoldersForProject(string path, string sourceRel)
    {
        var group = path[..path.LastIndexOf('/', path.LastIndexOf('/') - 1)];
        if (group.StartsWith("framework/", StringComparison.Ordinal) || group.StartsWith("features/", StringComparison.Ordinal))
        {
            return ["/" + group + "/"];
        }
        if (group == sourceRel)
        {
            return [null];
        }
        var segments = (sourceRel == "." ? group : group[(sourceRel.Length + 1)..]).Split('/')
            .Select(s => s.StartsWith(Prefix, StringComparison.Ordinal) ? s[Prefix.Length..] : s).ToArray();
        var folder = "/" + string.Join('/', segments) + "/";
        return segments[^1] == "tests" ? [folder, "/" + string.Join('/', segments[..^1]) + "/", null] : [folder, null];
    }

    // null stands for "no folder": the project is a direct child of the solution.
    private static string?[] ExpectedFolders(string group, string sourceRoot)
    {
        if (group == sourceRoot)
        {
            return [null];
        }
        var segments = (sourceRoot == "." ? group : group[(sourceRoot.Length + 1)..]).Split('/')
            .Select(segment => segment.StartsWith(Prefix, StringComparison.Ordinal) ? segment[Prefix.Length..] : segment)
            .ToArray();
        var folder = "/" + string.Join('/', segments) + "/";
        // A module's Contracts/, implementation and tests/ directories (by module, R4-03) are grouped either with the module or
        // as their own subfolder. Slnd/slnx may also flatten all entries to root.
        return segments[^1] is "tests" or "Contracts" or "Generators" or ModuleFolderFacts.SourceFolder
            ? [null, folder, "/" + string.Join('/', segments[..^1]) + "/"]
            : [null, folder];
    }
}
