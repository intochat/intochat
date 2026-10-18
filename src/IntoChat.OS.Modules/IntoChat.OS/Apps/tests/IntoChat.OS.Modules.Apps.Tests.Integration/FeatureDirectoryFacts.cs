using IntoChat.OS.Apps;

namespace IntoChat.OS.Modules.Apps.Tests.Integration;

public sealed class FeatureDirectoryFacts : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("features-").FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private void Write(string relative, string content)
    {
        var path = Path.Combine(_root, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    private static readonly string Manifest = Declarations.Of("echo", "1.0.0", "Echo", "Echoes.");

    [Fact]
    public void AFolderOfOwnerFoldersLoadsEachAppUnderItsOwner()
    {
        Write("vendor/echo/app.cs", Manifest);
        Write("vendor/echo/synapses/program.cs", "// program\r\nreturn;\r\n");
        Write("vendor/echo/synapses/extra.cs", "// behavior\n");
        var source = new FeatureDirectory(_root);
        var (owner, app, _) = Assert.Single(source.Load());
        Assert.Equal(("vendor", "echo"), (owner, app.Name));
        // Line endings normalize, so content hashes agree across checkouts.
        Assert.Equal("// program\nreturn;\n", app.Files["synapses/program.cs"]);
        Assert.Equal("// behavior\n", app.Files["synapses/extra.cs"]);
    }

    [Fact]
    public void AMissingDirectoryIsAnError()
        => Assert.Throws<DirectoryNotFoundException>(() => new FeatureDirectory(Path.Combine(_root, "absent")).Load());

    [Fact]
    public void AWrittenBundleReadsBackIdentically()
    {
        Write("vendor/Echo/app.cs", Declarations.Of("echo", "1.0.0", "Echo", "Echoes.",
            [new("ask", "Echo")], [new("Tone", "how", "flat")], "icon.svg", "## Scenario: Echo\nEchoes."));
        Write("vendor/Echo/echo.cs", "// echo\n");
        Write("vendor/Echo/icon.svg", "<svg/>");
        var source = new FeatureDirectory(_root, writable: true);
        var loaded = Assert.Single(source.Load()).Bundle;
        Assert.Equal("icon.svg", loaded.Manifest.Icon);

        var edited = loaded with
        {
            Version = "1.1.0",
            Spec = "## Scenario: Echo\nEchoes twice.",
            Files = new(loaded.Files.Where(file => file.Key != "echo.cs"), StringComparer.Ordinal) { ["twice.cs"] = "// twice\n" },
        };
        source.Write("vendor", edited);

        var reread = Assert.Single(source.Load()).Bundle;
        Assert.Equal("1.1.0", reread.Version);
        Assert.Equal(edited.Spec, reread.Spec);
        Assert.Equal(edited.Manifest with { Operations = [], Settings = [], Slots = [] }, reread.Manifest with { Operations = [], Settings = [], Slots = [] });
        Assert.Equal(edited.Manifest.Operations, reread.Manifest.Operations);
        Assert.Equal(edited.Manifest.Settings, reread.Manifest.Settings);
        Assert.Equal(edited.Files.OrderBy(f => f.Key), reread.Files.OrderBy(f => f.Key));
        Assert.False(File.Exists(Path.Combine(_root, "vendor", "Echo", "synapses", "echo.cs")));
        Assert.Single(Directory.GetDirectories(Path.Combine(_root, "vendor")));
    }

    [Fact]
    public void ANewAppGetsAFolderNamedAfterItsTitle()
    {
        var source = new FeatureDirectory(_root, writable: true);
        source.Write("vendor", new("lead-finder", "1.0.0", new("Lead finder", "Finds leads.", [], [], []), "## Scenario: Finds\nIt finds.\n",
            new() { ["synapses/finds.cs"] = "// t\n" }));
        Assert.True(File.Exists(Path.Combine(_root, "vendor", "LeadFinder", "app.cs")));
        Assert.Equal("lead-finder", Assert.Single(source.Load()).Bundle.Name);
    }

    [Fact]
    public void AReadOnlyFolderRefusesToWrite()
        => Assert.Throws<InvalidOperationException>(() => new FeatureDirectory(_root).Write("vendor",
            new("echo", "1.0.0", new("Echo", "Echoes.", [], [], []), "", [])));

    [Fact]
    public void AnOwnerOrFileOutsideTheFeaturesFolderIsRefused()
    {
        var source = new FeatureDirectory(_root, writable: true);
        var manifest = new AppManifest("Echo", "Echoes.", [], [], []);
        Assert.Throws<ArgumentException>(() => source.Write("..", new("echo", "1.0.0", manifest, "", [])));
        Assert.Throws<ArgumentException>(() => source.Write("vendor", new("echo", "1.0.0", manifest, "",
            new() { ["../../escaped.cs"] = "// out" })));
        Assert.False(File.Exists(Path.Combine(_root, "escaped.cs")));
    }

    [Trait("host", "own")]
    [Fact]
    public void TheRepositoryFeaturesShipTheirIcons()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (!File.Exists(Path.Combine(root!.FullName, "Intochat.OS.slnx"))) { root = root.Parent; }
        var features = Path.Combine(root.FullName, "features");
        var loaded = new FeatureDirectory(features).Load();
        var withIcons = loaded.Where(feature => feature.Owner == "os").Select(feature => feature.Bundle)
            .Where(app => !string.IsNullOrEmpty(app.Manifest.Icon) || app.Files.ContainsKey("icon.svg")).ToArray();
        Assert.NotEmpty(withIcons);
        foreach (var app in withIcons)
        {
            Assert.Equal("icon.svg", app.Manifest.Icon);
            Assert.StartsWith("<svg", app.Files["icon.svg"], StringComparison.Ordinal);
        }
    }
}
