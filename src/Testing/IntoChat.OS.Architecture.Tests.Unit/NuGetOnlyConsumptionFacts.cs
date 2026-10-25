using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace IntoChat.OS.Architecture.Tests.Unit;

// os consumes digitalbrain from NuGet only (CORE-07, D-01): no build file reaches outside this repository or into a
// digitalbrain checkout, every committed nuget config maps DigitalBrain* to nuget.org alone, the loaded DigitalBrain
// assemblies are the package bytes, and the pins float only until the os release fixes them (D-02). DigitalBrain pins
// are never compared with the os VERSION: the two repositories release independently (D-05).
public sealed partial class NuGetOnlyConsumptionFacts
{
    private const string NuGetOrg = "https://api.nuget.org/v3/index.json";

    private static readonly string[] SkippedDirectories = ["bin", "obj", "artifacts", "node_modules", ".git", ".dart_tool", "build", ".vs", ".idea"];

    private static readonly StringComparison PathComparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    internal static IEnumerable<string> RepositoryFiles(Func<string, bool> name)
    {
        var pending = new Stack<string>([Repository.Root]);
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

    [Fact]
    public void NoProjectPathLeavesTheRepository()
    {
        var root = Path.GetFullPath(Repository.Root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var files = RepositoryFiles(IsBuildFile).ToArray();
        Assert.True(files.Length > 100, "the scan found " + files.Length + " build files");
        var offenders = new List<string>();
        foreach (var file in files)
        {
            var directory = Path.GetDirectoryName(file)! + Path.DirectorySeparatorChar;
            foreach (var raw in NamedPaths(XDocument.Load(file)).SelectMany(value => value.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)))
            {
                var value = raw.Replace("$(MSBuildThisFileDirectory)", directory, StringComparison.Ordinal)
                    .Replace("$(MSBuildProjectDirectory)", directory, StringComparison.Ordinal);
                if (value.Contains("$(", StringComparison.Ordinal) || value.Contains("@(", StringComparison.Ordinal) || value.Contains("%(", StringComparison.Ordinal)) { continue; }
                var wildcard = value.IndexOfAny(['*', '?']);
                var literal = wildcard < 0 ? value : value[..wildcard];
                var resolved = Path.GetFullPath(Path.Combine(directory, literal.Replace('\\', '/')));
                var segments = Repository.Relative(resolved).Split('/');
                var inside = (resolved + Path.DirectorySeparatorChar).StartsWith(root, PathComparison);
                if (!inside || segments.Any(segment => segment.Contains("digitalbrain", StringComparison.OrdinalIgnoreCase)))
                {
                    offenders.Add(Repository.Relative(file) + ": " + raw);
                }
            }
        }
        Assert.True(offenders.Count == 0, "Build files may only name paths inside the os repository and never a digitalbrain folder (consume the DigitalBrain packages):\n" + string.Join('\n', offenders));
    }

    [Fact]
    public void CommittedNuGetConfigsMapDigitalBrainToNuGetOrgOnly()
    {
        var configs = RepositoryFiles(name => name.Equals("nuget.config", StringComparison.OrdinalIgnoreCase)).ToArray();
        Assert.Contains(configs, path => string.Equals(Path.GetDirectoryName(path), Path.GetFullPath(Repository.Root).TrimEnd(Path.DirectorySeparatorChar), PathComparison));
        var offenders = new List<string>();
        var mentionsDigitalBrain = false;
        foreach (var config in configs)
        {
            var document = XDocument.Load(config);
            var sources = document.Descendants("packageSources").Elements("add")
                .ToDictionary(add => add.Attribute("key")!.Value, add => add.Attribute("value")!.Value, StringComparer.OrdinalIgnoreCase);
            foreach (var (key, value) in sources)
            {
                var remote = Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps;
                if (!remote || value.Contains("local-feed", StringComparison.OrdinalIgnoreCase))
                {
                    offenders.Add(Repository.Relative(config) + ": source " + key + " = " + value + " is not a remote https feed");
                }
            }
            foreach (var mapped in document.Descendants("packageSourceMapping").Elements("packageSource"))
            {
                var key = mapped.Attribute("key")!.Value;
                foreach (var pattern in mapped.Elements("package").Select(package => package.Attribute("pattern")!.Value)
                    .Where(pattern => pattern.StartsWith("DigitalBrain", StringComparison.OrdinalIgnoreCase)))
                {
                    mentionsDigitalBrain |= Path.GetDirectoryName(config)!.Length <= Repository.Root.TrimEnd(Path.DirectorySeparatorChar).Length;
                    if (!sources.TryGetValue(key, out var value) || value != NuGetOrg)
                    {
                        offenders.Add(Repository.Relative(config) + ": " + pattern + " maps to " + key + " (" + (value ?? "undeclared") + ")");
                    }
                }
            }
        }
        Assert.True(mentionsDigitalBrain, "the root nuget.config maps DigitalBrain and DigitalBrain.* to nuget.org explicitly");
        Assert.True(offenders.Count == 0, "Committed nuget configs restore DigitalBrain* from " + NuGetOrg + " only; local feeds go in an out-of-repo config passed with -p:RestoreConfigFile:\n" + string.Join('\n', offenders));
    }

    [Fact]
    public void WorkflowsRestoreFromTheCommittedConfigOnly()
    {
        var offenders = Directory.EnumerateFiles(Path.Combine(Repository.Root, ".github", "workflows"), "*.yml")
            .Where(path => Regex.IsMatch(File.ReadAllText(path), "local-feed|RestoreConfigFile|--configfile", RegexOptions.IgnoreCase))
            .Select(Repository.Relative).ToArray();
        Assert.True(offenders.Length == 0, "Workflows restore through the committed nuget.config: " + string.Join(", ", offenders));
    }

    // The test host copies package assemblies next to itself, so provenance is proven twice: the deps file records a
    // package library, and the loaded file is byte-identical to that package's lib asset in the NuGet packages folder.
    [Theory]
    [InlineData("DigitalBrain")]
    [InlineData("DigitalBrain.Runtime.Orleans")]
    public void DigitalBrainLoadsFromAPackageFolder(string name)
    {
        var assembly = name == "DigitalBrain" ? typeof(DigitalBrain.BrainId).Assembly : Assembly.Load(name);
        var deps = Path.Combine(AppContext.BaseDirectory, Assembly.GetExecutingAssembly().GetName().Name + ".deps.json");
        using var json = JsonDocument.Parse(File.ReadAllText(deps));
        var library = json.RootElement.GetProperty("libraries").EnumerateObject().Single(entry => entry.Name.StartsWith(name + "/", StringComparison.Ordinal));
        Assert.Equal("package", library.Value.GetProperty("type").GetString());
        var packagePath = library.Value.GetProperty("path").GetString()!;
        Assert.StartsWith("digitalbrain", packagePath, StringComparison.Ordinal);
        var asset = json.RootElement.GetProperty("targets").EnumerateObject().First().Value.GetProperty(library.Name).GetProperty("runtime")
            .EnumerateObject().Single(file => file.Name.EndsWith("/" + name + ".dll", StringComparison.Ordinal)).Name;

        var folders = new[] { Environment.GetEnvironmentVariable("NUGET_PACKAGES"), Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".nuget", "packages") }
            .OfType<string>().Where(folder => folder.Length > 0);
        var packaged = folders.Select(folder => Path.Combine(folder, packagePath, asset)).FirstOrDefault(File.Exists);
        Assert.True(packaged is not null, name + " has no package asset " + packagePath + "/" + asset + " in " + string.Join(", ", folders));
        Assert.False(packaged!.StartsWith(Path.GetFullPath(Repository.Root), PathComparison), "the packages folder must not sit inside the os repository");
        Assert.True(File.ReadAllBytes(packaged).AsSpan().SequenceEqual(File.ReadAllBytes(assembly.Location)), assembly.Location + " is not the package asset " + packaged);
    }

    private static XDocument CentralPackages() => XDocument.Load(Path.Combine(Repository.Root, "Directory.Packages.props"));

    private static string Property(XDocument props, string name)
        => props.Descendants(name).Single().Value.Trim();

    private static Dictionary<string, string> DigitalBrainPins(XDocument props)
        => props.Descendants("PackageVersion")
            .Where(pin => pin.Attribute("Include")!.Value.StartsWith("DigitalBrain", StringComparison.Ordinal))
            .ToDictionary(pin => pin.Attribute("Include")!.Value, pin => pin.Attribute("Version")!.Value, StringComparer.Ordinal);

    [Fact]
    public void DigitalBrainPinsAreLiteralAndFloatOnlyBeforeRelease()
    {
        var props = CentralPackages();
        var mode = Property(props, "DigitalBrainPinMode");
        Assert.Contains(mode, new[] { "floating", "exact" });
        var pins = DigitalBrainPins(props);
        Assert.Contains("DigitalBrain", pins.Keys);
        Assert.Contains("DigitalBrain.Runtime.Orleans", pins.Keys);
        foreach (var (package, version) in pins)
        {
            Assert.False(version.Contains("$(", StringComparison.Ordinal), package + " pin must be a literal, not " + version);
            if (mode == "floating") { Assert.Equal(Property(props, "DigitalBrainFloatPrefix"), version); }
            else { Assert.Matches(ExactVersion(), version); }
        }
        var overrides = Repository.Projects.Values.Where(project => XDocument.Load(project.Path).Descendants("PackageReference")
            .Any(reference => reference.Attribute("Include")?.Value.StartsWith("DigitalBrain", StringComparison.Ordinal) == true
                && (reference.Attribute("Version") is not null || reference.Attribute("VersionOverride") is not null)))
            .Select(project => project.Name).ToArray();
        Assert.True(overrides.Length == 0, "DigitalBrain versions are central only: " + string.Join(", ", overrides));
    }

    [GeneratedRegex(@"^\d+\.\d+\.\d+(-[0-9A-Za-z.-]+)?$")]
    private static partial Regex ExactVersion();

    [Fact]
    public void CentralFloatingFlagMatchesPins()
    {
        var props = CentralPackages();
        var floating = Property(props, "DigitalBrainPinMode") == "floating";
        var flag = props.Descendants("CentralPackageFloatingVersionsEnabled").SingleOrDefault()?.Value.Trim();
        Assert.Equal(floating, flag == "true");
        if (floating) { Assert.EndsWith("*", Property(props, "DigitalBrainFloatPrefix"), StringComparison.Ordinal); }
    }

    [Fact]
    public void NoLockFiles()
    {
        var locks = RepositoryFiles(name => name.Equals("packages.lock.json", StringComparison.OrdinalIgnoreCase)).Select(Repository.Relative).ToArray();
        Assert.True(locks.Length == 0, "Lock files would freeze a floating DigitalBrain pin: " + string.Join(", ", locks));
        Assert.DoesNotContain(RepositoryFiles(IsBuildFile), file => File.ReadAllText(file).Contains("RestorePackagesWithLockFile", StringComparison.Ordinal));
    }

    // The cloud profile reads its Key Vault from configuration; only tests may replace that source.
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

    // D-02: the os release ships exact DigitalBrain pins. The guard runs before anything is restored, packed or pushed.
    [Fact]
    public void ReleaseRefusesToPublishWhileFloating()
    {
        var release = File.ReadAllText(Path.Combine(Repository.Root, ".github", "workflows", "release.yml"));
        var guard = release.IndexOf("- name: Guard DigitalBrain pins are exact", StringComparison.Ordinal);
        Assert.True(guard > 0, "release.yml has no DigitalBrainPinMode guard");
        Assert.Contains("DigitalBrainPinMode", release[guard..release.IndexOf("- uses: actions/setup-dotnet", StringComparison.Ordinal)], StringComparison.Ordinal);
        Assert.True(guard < release.IndexOf("dotnet restore", StringComparison.Ordinal));
        Assert.True(guard < release.IndexOf("dotnet pack", StringComparison.Ordinal));
        Assert.True(guard < release.IndexOf("dotnet nuget push", StringComparison.Ordinal));
    }
}
