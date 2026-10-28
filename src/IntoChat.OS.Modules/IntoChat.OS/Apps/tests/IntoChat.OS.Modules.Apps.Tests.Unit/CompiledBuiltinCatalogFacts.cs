using System.Collections.Immutable;
using System.Text.Json;
using DigitalBrain.Apps.Packaging;
using IntoChat.OS.Testing;

namespace IntoChat.OS.Modules.Apps.Tests.Unit;

public sealed class CompiledBuiltinCatalogFacts : IDisposable
{
    private readonly TestHome home = TestHome.Create();
    private readonly CompiledAppPackages packages;
    private string Inventory => Path.Combine(home.Path, "compiled-apps");
    public CompiledBuiltinCatalogFacts() => packages = new(Path.Combine(home.Path, "cache"), new Dictionary<string, string>());

    [Fact]
    public async Task SourceFreePackagesListWithCompletePresentationWithoutLoadingTheirEntryType()
    {
        await Write("os", "counter", "1");
        var catalog = new CompiledBuiltinCatalog(Inventory, packages);
        var found = Assert.Single(await catalog.All(TestContext.Current.CancellationToken));
        Assert.Equal("os", found.Publisher);
        Assert.Equal("counter", found.Bundle.Name);
        Assert.Equal("1", found.Bundle.Version);
        Assert.Equal("Counter specification", found.Bundle.Spec);
        Assert.Equal("<svg />", found.Bundle.Files["icon.svg"]);
        Assert.DoesNotContain(found.Bundle.Files.Keys, path => path.EndsWith(".cs", StringComparison.Ordinal));
        Assert.Equal(["private.key"], found.Bundle.Manifest.Requires!.Secrets!);
        Assert.True(File.Exists(Path.Combine(found.Package.DirectoryPath, found.Package.Descriptor.EntryAssembly)));
        Assert.Empty(await catalog.Failures(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task InvalidReplacementKeepsTheLastValidPackageWhileOtherAppsAdvance()
    {
        var path = await Write("os", "counter", "1");
        await Write("os", "other", "1");
        var catalog = new CompiledBuiltinCatalog(Inventory, packages);
        var original = (await catalog.Find("os", "counter", TestContext.Current.CancellationToken))!;
        await File.WriteAllTextAsync(path, "broken archive", TestContext.Current.CancellationToken);
        await Write("os", "other", "2");
        await catalog.Reload(TestContext.Current.CancellationToken);
        Assert.Equal(original.Package.Digest, (await catalog.Find("os", "counter", TestContext.Current.CancellationToken))!.Package.Digest);
        Assert.Equal("2", (await catalog.Find("os", "other", TestContext.Current.CancellationToken))!.Bundle.Version);
        var failure = Assert.Single(await catalog.Failures(TestContext.Current.CancellationToken));
        Assert.Equal(("os", "counter"), (failure.Owner, failure.Name));
    }

    [Fact]
    public async Task DuplicateIdentityIsRefusedWhileUnrelatedPackagesLoad()
    {
        var path = await Write("os", "counter", "1");
        File.Copy(path, Path.Combine(Inventory, "os", "again.intochat-app"));
        await Write("os", "other", "1");
        var catalog = new CompiledBuiltinCatalog(Inventory, packages);
        Assert.Equal("other", Assert.Single(await catalog.All(TestContext.Current.CancellationToken)).Bundle.Name);
        Assert.Null(await catalog.Find("os", "counter", TestContext.Current.CancellationToken));
        Assert.Equal(2, (await catalog.Failures(TestContext.Current.CancellationToken)).Count);
    }

    [Fact]
    public async Task OneNameCanExistUnderDifferentPublishersAndAnUploadIsNeverShipped()
    {
        await Write("os", "counter", "1");
        await Write("intochat", "counter", "1");
        var uploaded = await Write("temporary", "uploaded", "1");
        await using (var stream = File.OpenRead(uploaded)) { await packages.Stage(stream, TestContext.Current.CancellationToken); }
        File.Delete(uploaded);
        var catalog = new CompiledBuiltinCatalog(Inventory, packages);
        Assert.Equal(["intochat", "os"], (await catalog.All(TestContext.Current.CancellationToken)).Select(app => app.Publisher));
        Assert.Null(await catalog.Find("temporary", "uploaded", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task MutablePresentationCannotChangeTheNextConsentReview()
    {
        await Write("os", "counter", "1");
        var catalog = new CompiledBuiltinCatalog(Inventory, packages);
        var first = (await catalog.Find("os", "counter", TestContext.Current.CancellationToken))!;
        first.Bundle.Manifest.Requires!.Secrets![0] = "changed";
        var second = (await catalog.Find("os", "counter", TestContext.Current.CancellationToken))!;
        Assert.Equal("private.key", Assert.Single(second.Bundle.Manifest.Requires!.Secrets!));
    }

    [Fact]
    public async Task FeatureListingsUseCompiledPackagesWithoutAnySourceDirectory()
    {
        await Write("os", "counter", "1");
        using var compiled = new CompiledBuiltinCatalog(Inventory, packages);
        var catalog = new FeatureCatalog(null, null, compiled);
        Assert.Equal("counter", Assert.Single(await catalog.Listings()).Name);
        Assert.IsType<AppSource.Shipped>(Assert.Single(await catalog.Listings()).Source);
        Assert.NotNull(await catalog.FindCompiled("os", "counter", TestContext.Current.CancellationToken));
        Assert.Empty(await catalog.Failures());
        Assert.Null(catalog.Directory);
    }

    [Fact]
    public async Task CompiledInventoryReplacesOnlyItsOwnLegacySourceIdentity()
    {
        await Write("os", "counter", "1");
        var source = new FeatureDirectory(Path.Combine(home.Path, "sources"), writable: true);
        source.Write("os", new("counter", "0", new("Legacy counter", "", [new("open", "Open")], [], []), "", new() { ["bad.cs"] = "invalid script must never compile" }));
        source.Write("os", new("source-only", "1", new("Source only", "", [], [], []), "", []));
        using var compiled = new CompiledBuiltinCatalog(Inventory, packages);
        var catalog = new FeatureCatalog(source, null, compiled);
        Assert.Equal(["counter", "source-only"], (await catalog.Listings()).Select(item => item.Name));
        Assert.Equal("Counter", (await catalog.Find("os", "counter"))!.Manifest.Title);
        Assert.Empty(await catalog.Failures());
        var bundle = (await compiled.Find("os", "counter", TestContext.Current.CancellationToken))!.Bundle;
        await Assert.ThrowsAsync<InvalidOperationException>(() => catalog.SaveToSource("os", bundle));
    }

    [Fact]
    public async Task AnAbsentInventoryIsEmptyAndRemovalDoesNotDeleteTheInstalledCache()
    {
        var catalog = new CompiledBuiltinCatalog(Inventory, packages);
        Assert.Empty(await catalog.All(TestContext.Current.CancellationToken));
        var path = await Write("os", "counter", "1");
        await catalog.Reload(TestContext.Current.CancellationToken);
        var original = (await catalog.Find("os", "counter", TestContext.Current.CancellationToken))!;
        File.Delete(path);
        await catalog.Reload(TestContext.Current.CancellationToken);
        Assert.Empty(await catalog.All(TestContext.Current.CancellationToken));
        Assert.Equal(original.Package.Digest, (await packages.Read(original.Package.Digest, TestContext.Current.CancellationToken)).Digest);
    }

    [Fact]
    public async Task ProgramPackagesListFromTheirDeclarationAndCarryEditableSources()
    {
        var path = await WriteProgram("os", "counter", "2.0.0");
        var catalog = new CompiledBuiltinCatalog(Inventory, packages);
        var found = Assert.Single(await catalog.All(TestContext.Current.CancellationToken));
        Assert.Equal("counter", found.Bundle.Name);
        Assert.Equal("2.0.0", found.Bundle.Version);
        Assert.Equal("Counter", found.Bundle.Manifest.Title);
        Assert.Equal("Counts things.", found.Bundle.Spec.Trim());
        Assert.Equal("// open\n", found.Bundle.Files["open.cs"]);
        Assert.Equal("<svg />", found.Bundle.Files["icon.svg"]);
        Assert.DoesNotContain(found.Bundle.Files.Keys, name => name.EndsWith(".src", StringComparison.Ordinal) || name == "app.cs");
        Assert.Equal(found.Package.Digest, (await catalog.Find("os", "counter", TestContext.Current.CancellationToken))!.Package.Digest);
        Assert.True(File.Exists(path));
        Assert.Empty(await catalog.Failures(TestContext.Current.CancellationToken));
    }

    private async Task<string> WriteProgram(string publisher, string name, string version)
    {
        var build = Directory.CreateDirectory(Path.Combine(home.Path, "build", Guid.NewGuid().ToString("N"))).FullName;
        Directory.CreateDirectory(Path.Combine(build, "files"));
        Directory.CreateDirectory(Path.Combine(build, "assemblies"));
        File.Copy(typeof(CompiledBuiltinCatalogFacts).Assembly.Location, Path.Combine(build, "assemblies", "open.dll"));
        await File.WriteAllTextAsync(Path.Combine(build, "files", "icon.svg"), "<svg />", TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(Path.Combine(build, "files", "open.cs.src"), "// open\n", TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(Path.Combine(build, "files", "app.cs.src"), $$"""
            /*
            Counts things.
            */

            using IntoChat.OS.Modules.Apps;

            static class App
            {
                public const string Name = "{{name}}";
                public const string Version = "{{version}}";

                public static readonly AppManifest Manifest = new(
                    Title: "Counter",
                    Description: "Counts",
                    Operations: [new("open", "Open")],
                    Settings: [],
                    Slots: [],
                    Icon: "icon.svg");
            }
            """, TestContext.Current.CancellationToken);
        var descriptor = new CompiledAppDescriptor
        {
            Id = publisher + "/" + name,
            Name = "Counter",
            Version = version,
            EntryAssembly = "assemblies/open.dll",
            EntryType = "Program",
            Presentation = ImmutableSortedDictionary<string, string>.Empty.Add("icon", "files/icon.svg"),
            HostMetadata = ImmutableSortedDictionary<string, string>.Empty.Add("kind", "program")
                .Add("synapses", JsonSerializer.Serialize(new[] { new { source = "files/open.cs.src", assembly = "assemblies/open.dll" } })),
        };
        var folder = Directory.CreateDirectory(Path.Combine(Inventory, publisher)).FullName;
        var path = Path.Combine(folder, name + ".intochat-app");
        await using var archive = File.Create(path);
        await new AppPackageWriter().Write(descriptor, build, archive, TestContext.Current.CancellationToken);
        return path;
    }

    private async Task<string> Write(string publisher, string name, string version)
    {
        var build = Directory.CreateDirectory(Path.Combine(home.Path, "build", Guid.NewGuid().ToString("N"))).FullName;
        var entry = Path.GetFileName(typeof(CompiledBuiltinCatalogFacts).Assembly.Location);
        File.Copy(typeof(CompiledBuiltinCatalogFacts).Assembly.Location, Path.Combine(build, entry));
        await File.WriteAllTextAsync(Path.Combine(build, "icon.svg"), "<svg />", TestContext.Current.CancellationToken);
        var manifest = new AppManifest("Counter", "Counts", [new("open", "Open")], [], [], Icon: "icon.svg", Requires: new(Secrets: ["private.key"]));
        var descriptor = new CompiledAppDescriptor
        {
            Id = name,
            Name = "Counter",
            Version = version,
            EntryAssembly = entry,
            EntryType = "ThisTypeDoesNotExistAndMustNeverBeLoaded",
            Presentation = ImmutableSortedDictionary<string, string>.Empty.Add("operations", "[\"open\"]").Add("spec", "Counter specification"),
            HostMetadata = ImmutableSortedDictionary<string, string>.Empty.Add("intochat.os.v1", JsonSerializer.Serialize(manifest, new JsonSerializerOptions(JsonSerializerDefaults.Web))),
        };
        var folder = Directory.CreateDirectory(Path.Combine(Inventory, publisher)).FullName;
        var path = Path.Combine(folder, name + ".intochat-app");
        await using var archive = File.Create(path);
        await new AppPackageWriter().Write(descriptor, build, archive, TestContext.Current.CancellationToken);
        return path;
    }

    public void Dispose()
    {
        packages.Dispose();
        foreach (var file in Directory.EnumerateFiles(home.Path, "*", SearchOption.AllDirectories)) { File.SetAttributes(file, FileAttributes.Normal); }
        home.Dispose();
    }
}
