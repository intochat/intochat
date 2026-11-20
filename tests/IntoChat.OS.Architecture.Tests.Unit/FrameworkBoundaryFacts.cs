using System.Reflection;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using DigitalBrain.Modules;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace IntoChat.OS.Architecture.Tests.Unit;

// The framework lives in-tree under framework/: os consumes it via ProjectReference (never PackageReference or
// PackageVersion pins). framework/ names no path outside itself and nothing IntoChat, its tests reference nothing
// under the product source tree, no type is declared on both sides of the boundary (beyond the shrinking allowlist),
// and the packable set is the DX-03 list. The DigitalBrain closure reaches no os or host assembly (D-09).
public sealed partial class FrameworkBoundaryFacts
{
    private const string SourceSegment = "s" + "rc";
    // REQUIREMENTS DX-03, the digitalbrain packages.
    private static readonly string[] Dx03Packages =
    [
        "DigitalBrain",
        "DigitalBrain.Analyzers",
        "DigitalBrain.Capabilities.Compute",
        "DigitalBrain.Capabilities.Connections",
        "DigitalBrain.Capabilities.Http",
        "DigitalBrain.Capabilities.Identity",
        "DigitalBrain.Capabilities.Secrets",
        "DigitalBrain.Capabilities.Storage",
        "DigitalBrain.Capabilities.Tools",
        "DigitalBrain.Simulation",
        "DigitalBrain.Graph",
        "DigitalBrain.Identity.Local",
        "DigitalBrain.Runtime.Http",
        "DigitalBrain.Runtime.Orleans",
        "DigitalBrain.Runtime.Orleans.Azure",
        "DigitalBrain.Runtime.Synapses",
        "DigitalBrain.Templates",
        "DigitalBrain.Testing",
        "DigitalBrain.Testing.Silo",
        "DigitalBrain.UI",
    ];

    private static readonly string[] SkippedDirectories = ["bin", "obj", "artifacts", "node_modules", ".git", ".claude", ".dart_tool", "build", ".vs", ".idea"];

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

    private static readonly string[] OsOnlyMarkers =
    [
        "IKernel", "IIntoChatOSBrains", "IMasterKeyCustody", "IntoChatOSNames", "CallerContextStamper", "ICostMeter", "BrainSignals", "OsModule",
        "BrainKey", "GrainId", "GenerateSerializer", "PlainNeuronContracts",
    ];

    // Files whose DigitalBrain 0.12.0-preview.1 copies exist; the originals stay until external module packages stop binding to them.
    private static readonly string[] PendingMoveToDigitalBrain =
    [
    ];

    private static string[] OsFrameworkFiles()
        => new[]
            {
                Path.Combine(ContractsDirectory, "Runtime"),
                Path.Combine(Repository.ProjectNamed("IntoChat.OS.Modules.Identity").Directory, "Core"),
            }
            .Where(Directory.Exists)
            .SelectMany(folder => FilesUnder(folder, name => name.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)))
            .Select(path => Path.GetRelativePath(Repository.SourceRoot, path).Replace(Path.DirectorySeparatorChar, '/')).ToArray();

    // Entries and scanned paths are relative to the contracts project (IntoChat.OS) folder.
    private static string ContractsDirectory => Repository.ProjectNamed("IntoChat.OS").Directory;

    // Generic code belongs in DigitalBrain; what stays in the os framework folders must touch an os-only type or say why it is os-only.
    [Fact]
    public void OsFrameworkFilesReferenceAnOsOnlyTypeOrAwaitTheirMove()
    {
        var files = OsFrameworkFiles();
        Assert.True(files.Length >= 15, "the scan found " + files.Length + " files");
        var offenders = files
            .Where(path => !PendingMoveToDigitalBrain.Contains(path, StringComparer.OrdinalIgnoreCase))
            .Where(path =>
            {
                var text = File.ReadAllText(Path.Combine(Repository.SourceRoot, path));
                return !OsOnlyMarkers.Any(marker => text.Contains(marker, StringComparison.Ordinal))
                    && !text.Split('\n').Any(line => line.TrimStart().StartsWith("// os-only: ", StringComparison.Ordinal) && line.Trim().Length > "// os-only: ".Length);
            })
            .ToArray();
        Assert.True(offenders.Length == 0, "os framework files reference an os-only type or carry '// os-only: <reason>':\n" + string.Join('\n', offenders));
    }

    [Fact]
    public void PendingMoveListNamesOnlyExistingFiles()
    {
        var missing = PendingMoveToDigitalBrain.Where(path => !File.Exists(Path.Combine(ContractsDirectory, path))).ToArray();
        Assert.True(missing.Length == 0, "Remove deleted files from PendingMoveToDigitalBrain:\n" + string.Join('\n', missing));
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

    [GeneratedRegex(@"\b(?:using\s+(?:static\s+|global::|[A-Za-z_]\w*\s*=\s*)*|namespace\s+)IntoChat\b", RegexOptions.CultureInvariant)]
    private static partial Regex FrameworkTestIntoChatDirective();

    [Fact]
    public void FrameworkReferencesNoIntoChat()
    {
        Assert.True(Directory.Exists(Repository.FrameworkRoot), "framework/ must exist at " + Repository.FrameworkRoot);
        var offenders = new List<string>();
        foreach (var project in FrameworkBuildFiles().Where(file => file.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase)))
        {
            var document = XDocument.Load(project);
            var names = document.Descendants("PackageReference").Select(reference => reference.Attribute("Include")?.Value)
                .Concat(document.Descendants("ProjectReference").Select(reference => reference.Attribute("Include")?.Value is { } include
                    ? Path.GetFileNameWithoutExtension(include.Replace('\\', '/')) : null))
                .Concat(document.Descendants("InternalsVisibleTo").Select(attribute => attribute.Attribute("Include")?.Value))
                .OfType<string>();
            offenders.AddRange(names.Where(name => name.StartsWith("IntoChat", StringComparison.OrdinalIgnoreCase)).Select(name => Repository.Relative(project) + " -> " + name));
        }
        Assert.True(offenders.Count == 0, "The framework references nothing IntoChat:\n" + string.Join('\n', offenders));
    }

    [Fact]
    public void FrameworkSourceContainsNoIntoChatNamespace()
    {
        Assert.True(Directory.Exists(Repository.FrameworkRoot), "framework/ must exist at " + Repository.FrameworkRoot);

        var sourceFolders = new[] { SourceSegment, "examples", "templates" }
            .Select(folder => Path.Combine(Repository.FrameworkRoot, folder))
            .Where(Directory.Exists)
            .ToArray();
        Assert.NotEmpty(sourceFolders);

        var sourceOffenders = sourceFolders
            .SelectMany(folder => FilesUnder(folder, name => name.EndsWith(".cs", StringComparison.OrdinalIgnoreCase) && !name.EndsWith(".g.cs", StringComparison.OrdinalIgnoreCase)))
            .Where(file => File.ReadAllText(file).Contains("IntoChat", StringComparison.Ordinal))
            .Select(Repository.Relative)
            .Order(StringComparer.Ordinal)
            .ToArray();
        Assert.True(sourceOffenders.Length == 0, "Framework source, examples, and templates contain no IntoChat references:\n" + string.Join('\n', sourceOffenders));

        var testsFolder = Path.Combine(Repository.FrameworkRoot, "tests");
        var testOffenders = (Directory.Exists(testsFolder)
                ? FilesUnder(testsFolder, name => name.EndsWith(".cs", StringComparison.OrdinalIgnoreCase) && !name.EndsWith(".g.cs", StringComparison.OrdinalIgnoreCase))
                : [])
            .Where(file => FrameworkTestIntoChatDirective().IsMatch(File.ReadAllText(file)))
            .Select(Repository.Relative)
            .Order(StringComparer.Ordinal)
            .ToArray();
        Assert.True(testOffenders.Length == 0, "Framework tests contain no 'using IntoChat' or 'namespace IntoChat':\n" + string.Join('\n', testOffenders));
    }

    [Fact]
    public void FrameworkTestsReferenceNoSrc()
    {
        if (!Directory.Exists(Repository.FrameworkRoot)) { return; }
        var source = Path.Combine(Repository.Root, SourceSegment) + Path.DirectorySeparatorChar;
        var projects = new[] { "tests", "templates" }
            .SelectMany(folder => FilesUnder(Path.Combine(Repository.FrameworkRoot, folder), name => name.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase)))
            .ToArray();
        Assert.True(projects.Length > 10, "the framework test scan found " + projects.Length + " projects");
        var offenders = projects
            .SelectMany(project => ResolvedPaths(project).Where(path => (path.Resolved + Path.DirectorySeparatorChar).StartsWith(source, PathComparison))
                .Select(path => Repository.Relative(project) + ": " + path.Raw))
            .ToArray();
        Assert.True(offenders.Length == 0, "Framework tests and templates reference nothing under " + SourceSegment + "/:\n" + string.Join('\n', offenders));
    }

    [Fact]
    public void ConsumersReferenceFrameworkByProject()
    {
        var files = FilesUnder(Repository.Root, IsBuildFile).Where(file => !InFramework(file)).ToArray();
        Assert.True(files.Length >= 35, "the scan found " + files.Length + " build files");

        static bool IsDigitalBrainId(string? id)
            => id is not null && (id == "DigitalBrain" || id.StartsWith("DigitalBrain.", StringComparison.Ordinal));

        var packageOffenders = files
            .SelectMany(file => XDocument.Load(file).Descendants()
                .Where(element => element.Name.LocalName is "PackageReference" or "PackageVersion")
                .Where(element => IsDigitalBrainId(element.Attribute("Include")?.Value))
                .Select(element => Repository.Relative(file) + ": " + element.Name.LocalName + " " + element.Attribute("Include")!.Value))
            .ToArray();
        Assert.True(packageOffenders.Length == 0, "No build file outside framework/ may reference or pin DigitalBrain* as a NuGet package:\n" + string.Join('\n', packageOffenders));

        var productRoot = Path.Combine(Repository.Root, SourceSegment);
        var frameworkSourceRoot = Path.GetFullPath(Path.Combine(Repository.FrameworkRoot, SourceSegment)) + Path.DirectorySeparatorChar;
        var consumerProjects = FilesUnder(productRoot, name => name.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase))
            .Where(project => ResolvedProjectReferences(project).Any(resolved => resolved.StartsWith(frameworkSourceRoot, PathComparison)))
            .ToArray();
        Assert.True(consumerProjects.Length >= 20, "Expected at least 20 product projects referencing framework/" + SourceSegment + "/, found " + consumerProjects.Length);
    }

    [Fact]
    public void NoTypeIsDeclaredOnBothSidesOfTheBoundary()
    {
        var frameworkSource = Path.Combine(Repository.FrameworkRoot, SourceSegment);
        var productSource = Path.Combine(Repository.Root, SourceSegment);
        Assert.True(Directory.Exists(frameworkSource), "framework source directory must exist at " + frameworkSource);
        Assert.True(Directory.Exists(productSource), "product source directory must exist at " + productSource);

        var frameworkTypes = DeclaredPublicOrInternalTypeNames(frameworkSource);
        var osTypes = DeclaredPublicOrInternalTypeNames(productSource);
        var duplicates = frameworkTypes.Intersect(osTypes, StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();

        var allowlistPath = Path.Combine(Repository.ProjectNamed("IntoChat.OS.Architecture.Tests.Unit").Directory, "FrameworkDuplicateTypesAllowlist.txt");
        Assert.True(File.Exists(allowlistPath), "Missing allowlist: " + allowlistPath);

        var allowlistEntries = File.ReadAllLines(allowlistPath)
            .Select(line => line.Trim())
            .Where(line => line.Length > 0 && !line.StartsWith('#'))
            .ToArray();

        Assert.True(allowlistEntries.Length == 0, $"FrameworkDuplicateTypesAllowlist.txt must be empty, found {allowlistEntries.Length}.");

        var allowlist = new HashSet<string>(StringComparer.Ordinal);
        var duplicateAllowlistEntries = new List<string>();
        foreach (var entry in allowlistEntries)
        {
            if (!allowlist.Add(entry))
            {
                duplicateAllowlistEntries.Add(entry);
            }
        }
        Assert.True(duplicateAllowlistEntries.Count == 0, "Duplicate entries in FrameworkDuplicateTypesAllowlist.txt:\n" + string.Join('\n', duplicateAllowlistEntries));

        var unexpected = duplicates.Except(allowlist, StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        Assert.True(unexpected.Length == 0, "New duplicate type declared in both framework/" + SourceSegment + " and " + SourceSegment + "/:\n" + string.Join('\n', unexpected));

        var stale = allowlist.Except(duplicates, StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        Assert.True(stale.Length == 0, "Type is no longer duplicated; remove it from FrameworkDuplicateTypesAllowlist.txt so the list only shrinks:\n" + string.Join('\n', stale));
    }

    private static IEnumerable<string> ResolvedProjectReferences(string file)
    {
        var directory = Path.GetDirectoryName(file)! + Path.DirectorySeparatorChar;
        foreach (var raw in XDocument.Load(file).Descendants("ProjectReference")
            .Select(element => element.Attribute("Include")?.Value)
            .OfType<string>()
            .SelectMany(value => value.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)))
        {
            var value = raw.Replace("$(MSBuildThisFileDirectory)", directory, StringComparison.Ordinal)
                .Replace("$(MSBuildProjectDirectory)", directory, StringComparison.Ordinal);
            if (value.Contains("$(", StringComparison.Ordinal) || value.Contains("@(", StringComparison.Ordinal) || value.Contains("%(", StringComparison.Ordinal)) { continue; }
            yield return Path.GetFullPath(Path.Combine(directory, value.Replace('\\', '/')));
        }
    }

    private static HashSet<string> DeclaredPublicOrInternalTypeNames(string rootDirectory)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var file in FilesUnder(rootDirectory, name => name.EndsWith(".cs", StringComparison.OrdinalIgnoreCase) && !name.EndsWith(".g.cs", StringComparison.OrdinalIgnoreCase)))
        {
            var root = CSharpSyntaxTree.ParseText(File.ReadAllText(file)).GetRoot();
            foreach (var declaration in root.DescendantNodes().OfType<BaseTypeDeclarationSyntax>())
            {
                if (IsPublicOrInternal(declaration))
                {
                    names.Add(declaration.Identifier.Text);
                }
            }
        }
        return names;
    }

    private static bool IsPublicOrInternal(BaseTypeDeclarationSyntax declaration)
    {
        if (declaration.Modifiers.Any(SyntaxKind.PrivateKeyword)
            || declaration.Modifiers.Any(SyntaxKind.ProtectedKeyword)
            || declaration.Modifiers.Any(SyntaxKind.FileKeyword))
        {
            return false;
        }

        return declaration.Modifiers.Any(SyntaxKind.PublicKeyword)
            || declaration.Modifiers.Any(SyntaxKind.InternalKeyword)
            || declaration.Parent is not BaseTypeDeclarationSyntax;
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

    // Kept from NuGetOnlyConsumptionFacts: restores go through the committed nuget.config.
    [Fact]
    public void WorkflowsRestoreFromTheCommittedConfigOnly()
    {
        var offenders = Directory.EnumerateFiles(Path.Combine(Repository.Root, ".github", "workflows"), "*.yml")
            .Where(path => LocalFeed().IsMatch(File.ReadAllText(path)))
            .Select(Repository.Relative).ToArray();
        Assert.True(offenders.Length == 0, "Workflows restore through the committed nuget.config: " + string.Join(", ", offenders));
    }

    [GeneratedRegex("local" + "-feed|Restore" + "ConfigFile|\\.\\./nuget|\\.nuget-local", RegexOptions.IgnoreCase)]
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
        Assert.NotNull(core.GetType("DigitalBrain.Modules.Module", throwOnError: true));
        Assert.NotNull(core.GetType("DigitalBrain.Modules.ModuleManifest", throwOnError: true));
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
