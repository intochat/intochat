using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Text.Json;
using DigitalBrain.Apps.Packaging;
using IntoChat.OS.Modules.Microsoft.CSharp.Catalog;

namespace IntoChat.OS.BuiltinApps.Tests.Unit;

// The built-in packages are built once per run (and a second time to prove the bytes repeat); every fact reads them.
public sealed class BuiltinPackageFacts : IClassFixture<BuiltinPackageFacts.Packages>
{
    public sealed class Packages : IDisposable
    {
        public string Features { get; } = System.IO.Path.Combine(Root(), "features", "os");
        public string First { get; } = Directory.CreateTempSubdirectory("builtin-a-").FullName;
        public string Second { get; } = Directory.CreateTempSubdirectory("builtin-b-").FullName;
        public IReadOnlyList<BuiltinPackage> Built { get; }
        public IReadOnlyList<BuiltinPackage> Rebuilt { get; }

        public Packages()
        {
            Built = BuiltinPacker.PackAll(Features, First, null, CancellationToken.None).GetAwaiter().GetResult();
            Rebuilt = BuiltinPacker.PackAll(Features, Second, null, CancellationToken.None).GetAwaiter().GetResult();
        }

        public void Dispose()
        {
            Directory.Delete(First, true);
            Directory.Delete(Second, true);
        }

        private static string Root()
        {
            for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            {
                if (File.Exists(System.IO.Path.Combine(directory.FullName, "Intochat.OS.slnx"))) { return directory.FullName; }
            }
            throw new DirectoryNotFoundException("Repository root not found.");
        }
    }

    // The reader stages read-only files.
    private static void Clean(string directory)
    {
        foreach (var file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories)) { File.SetAttributes(file, FileAttributes.Normal); }
        Directory.Delete(directory, true);
    }

    private readonly Packages packages;

    public BuiltinPackageFacts(Packages packages) => this.packages = packages;

    [Fact]
    public void EveryBuiltInAppHasAPackage()
    {
        var expected = Directory.EnumerateDirectories(packages.Features).Where(folder => File.Exists(System.IO.Path.Combine(folder, "app.cs")))
            .Select(folder => Declaration.Read(System.IO.Path.GetFileName(folder), File.ReadAllText(System.IO.Path.Combine(folder, "app.cs")).Replace("\r\n", "\n", StringComparison.Ordinal)).Name)
            .Order(StringComparer.Ordinal).ToArray();
        Assert.Equal(19, expected.Length);
        Assert.Equal(expected, packages.Built.Select(package => package.Name).Order(StringComparer.Ordinal));
        Assert.All(packages.Built, package => Assert.True(File.Exists(package.Path)));
    }

    [Fact]
    public async Task EveryPackagePassesTheReader()
    {
        foreach (var package in packages.Built)
        {
            await using var stream = File.OpenRead(package.Path);
            var staging = Directory.CreateTempSubdirectory("builtin-read-").FullName;
            try
            {
                var read = await new AppPackageReader().Read(stream, staging, CancellationToken.None);
                Assert.Equal(package.Digest, read.Digest);
                Assert.Equal("os/" + package.Name, read.Descriptor.Id);
                Assert.DoesNotContain(read.Descriptor.Payload, item => item.Path.EndsWith(".pdb", StringComparison.OrdinalIgnoreCase) || item.Path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase));
                Assert.Contains(read.Descriptor.Payload, item => item.Path == "files/app.cs" + BuiltinPacker.SourceSuffix);
                var icon = read.Descriptor.Presentation["icon"];
                if (icon.Length > 0) { Assert.Contains(read.Descriptor.Payload, item => item.Path == icon); }
            }
            finally { Clean(staging); }
        }
    }

    [Fact]
    public void RebuildingRepeatsEveryDigest()
        => Assert.Equal(packages.Built.Select(package => (package.Name, package.Digest)), packages.Rebuilt.Select(package => (package.Name, package.Digest)));

    [Fact]
    public async Task EverySynapseAssemblyIsWhatTheSourceRuntimeEmitsFromThePackagedSources()
    {
        foreach (var package in packages.Built)
        {
            var staging = Directory.CreateTempSubdirectory("builtin-src-").FullName;
            try
            {
                await using var stream = File.OpenRead(package.Path);
                var read = await new AppPackageReader().Read(stream, staging, CancellationToken.None);
                // The runtime compiles the program files of the carried sources with a fresh compiler.
                var files = read.Descriptor.Payload.Where(item => item.Path.StartsWith("files/", StringComparison.Ordinal) && item.Path.EndsWith(BuiltinPacker.SourceSuffix, StringComparison.Ordinal))
                    .ToDictionary(item => item.Path["files/".Length..^BuiltinPacker.SourceSuffix.Length], item => File.ReadAllText(System.IO.Path.Combine(read.DirectoryPath, item.Path)), StringComparer.Ordinal);
                files.Remove("app.cs");
                var compiled = new CSharpScriptCheck().Compile(files);
                Assert.True(compiled.Success, package.Name);
                var synapses = JsonSerializer.Deserialize<JsonElement>(read.Descriptor.HostMetadata["synapses"]).EnumerateArray().Select(item => item.GetProperty("assembly").GetString()!).Order(StringComparer.Ordinal).ToArray();
                Assert.Equal(compiled.Assemblies.Keys.Select(BuiltinPacker.AssemblyPath).Order(StringComparer.Ordinal), synapses);
                foreach (var (program, behavior) in compiled.Assemblies)
                {
                    var packed = File.ReadAllBytes(System.IO.Path.Combine(read.DirectoryPath, BuiltinPacker.AssemblyPath(program)));
                    Assert.True(behavior.Assembly.AsSpan().SequenceEqual(packed), $"{package.Name}/{program} differs from a fresh compile of its packaged sources.");
                }
            }
            finally { Clean(staging); }
        }
    }

    // Module contracts projects, found through the solution rather than a directory walk.
    private static IEnumerable<string> ContractsProjects()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(System.IO.Path.Combine(root.FullName, "Intochat.OS.slnx"))) { root = root.Parent; }
        if (root is null) { throw new DirectoryNotFoundException("Repository root not found."); }
        return System.Xml.Linq.XDocument.Load(System.IO.Path.Combine(root.FullName, "Intochat.OS.slnx")).Descendants("Project")
            .Select(element => element.Attribute("Path")!.Value)
            .Where(path => System.IO.Path.GetFileName(path).StartsWith("IntoChat.OS.Modules.", StringComparison.Ordinal)
                && path.EndsWith(".Contracts.csproj", StringComparison.Ordinal));
    }

    [Fact]
    public async Task EverySynapseAssemblyResolvesAgainstTheCarriedReferenceSet()
    {
        var carried = CSharpScriptCheck.ReferenceNames().ToHashSet(StringComparer.OrdinalIgnoreCase);
        // The host's modules: every module contracts project and every contracts-hosting module is carried.
        foreach (var contracts in ContractsProjects())
        {
            Assert.Contains(System.IO.Path.GetFileNameWithoutExtension(contracts), carried);
        }
        Assert.Contains("IntoChat.OS.Client", carried);
        Assert.Contains("DigitalBrain.UI", carried);
        foreach (var package in packages.Built)
        {
            var staging = Directory.CreateTempSubdirectory("builtin-refs-").FullName;
            try
            {
                await using var stream = File.OpenRead(package.Path);
                var read = await new AppPackageReader().Read(stream, staging, CancellationToken.None);
                Assert.Equal(carried.Order(StringComparer.Ordinal), JsonSerializer.Deserialize<string[]>(read.Descriptor.HostMetadata["references"])!.Order(StringComparer.Ordinal));
                foreach (var assembly in read.Descriptor.Payload.Where(item => item.Path.StartsWith("assemblies/", StringComparison.Ordinal)))
                {
                    using var image = new PEReader(File.OpenRead(System.IO.Path.Combine(read.DirectoryPath, assembly.Path)));
                    var metadata = image.GetMetadataReader();
                    foreach (var handle in metadata.AssemblyReferences)
                    {
                        var name = metadata.GetString(metadata.GetAssemblyReference(handle).Name);
                        Assert.True(carried.Contains(name), $"{package.Name}/{assembly.Path} references {name}, which the sandbox does not carry.");
                    }
                }
            }
            finally { Clean(staging); }
        }
    }
}
