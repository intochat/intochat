using DigitalBrain.Apps;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace DigitalBrain.Modules.Apps.Tests;

public sealed class DirectoryShippedAppSourceFacts : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("shipped-apps-").FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private void Write(string relative, string content)
    {
        var path = Path.Combine(_root, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    private static readonly string Manifest = Declarations.Of("echo", "1.0.0", "Echo", "Echoes.");

    [Fact]
    public void AFolderOfAppFoldersLoadsLikeTheEmbeddedSource()
    {
        Write("echo/app.cs", Manifest);
        Write("echo/synapses/program.cs", "// program\r\nreturn;\r\n");
        Write("echo/synapses/extra.cs", "// behavior\n");
        var source = new DirectoryShippedAppSource(_root, "vendor");
        var app = Assert.Single(source.Load());
        Assert.Equal("echo", app.Name);
        // Line endings normalize exactly as the embedded source does, so content hashes agree
        // across checkouts.
        Assert.Equal("// program\nreturn;\n", app.Files["synapses/program.cs"]);
        Assert.Equal("// behavior\n", app.Files["synapses/extra.cs"]);
    }

    [Fact]
    public void AMissingDirectoryIsAnError()
        => Assert.Throws<DirectoryNotFoundException>(() => new DirectoryShippedAppSource(Path.Combine(_root, "absent"), "vendor").Load());

    [Fact]
    public void ConfigurationNamesEachVendorAndItsPath()
    {
        Write("echo/app.cs", Manifest);
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            [ShippedAppsServiceCollectionExtensions.ConfigurationSection + ":vendor:Path"] = _root,
        }).Build();
        var services = new ServiceCollection().AddShippedApps(configuration);
        using var provider = services.BuildServiceProvider();
        var source = Assert.Single(provider.GetServices<IShippedAppSource>());
        Assert.Equal("vendor", source.Publisher);
        Assert.Equal("echo", Assert.Single(source.Load()).Name);
    }

    [Fact]
    public void ASourceWithoutAPathIsAConfigurationError()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            [ShippedAppsServiceCollectionExtensions.ConfigurationSection + ":vendor:Name"] = "unused",
        }).Build();
        Assert.Throws<InvalidOperationException>(() => new ServiceCollection().AddShippedApps(configuration));
    }

    [Fact]
    public void AWrittenBundleReadsBackIdentically()
    {
        Write("Echo/app.cs", Declarations.Of("echo", "1.0.0", "Echo", "Echoes.",
            [new("ask", "Echo")], [new("Tone", "how", "flat")], "icon.svg", "## Scenario: Echo\nEchoes."));
        Write("Echo/echo.cs", "// echo\n");
        Write("Echo/icon.svg", "<svg/>");
        var source = new DirectoryShippedAppSource(_root, "vendor", development: true);
        var loaded = Assert.Single(source.Load());
        Assert.Equal("icon.svg", loaded.Manifest.Icon);

        var edited = loaded with
        {
            Version = "1.1.0",
            Spec = "## Scenario: Echo\nEchoes twice.",
            Files = new(loaded.Files.Where(file => file.Key != "echo.cs"), StringComparer.Ordinal) { ["twice.cs"] = "// twice\n" },
        };
        source.Write(edited);

        var reread = Assert.Single(source.Load());
        Assert.Equal("1.1.0", reread.Version);
        Assert.Equal(edited.Spec, reread.Spec);
        Assert.Equal(edited.Manifest with { Operations = [], Settings = [], Slots = [] }, reread.Manifest with { Operations = [], Settings = [], Slots = [] });
        Assert.Equal(edited.Manifest.Operations, reread.Manifest.Operations);
        Assert.Equal(edited.Manifest.Settings, reread.Manifest.Settings);
        Assert.Equal(edited.Files.OrderBy(f => f.Key), reread.Files.OrderBy(f => f.Key));
        Assert.False(File.Exists(Path.Combine(_root, "Echo", "synapses", "echo.cs")));
        Assert.Single(Directory.GetDirectories(_root));
    }

    [Fact]
    public void ANewAppGetsAFolderNamedAfterItsTitle()
    {
        var source = new DirectoryShippedAppSource(_root, "vendor", development: true);
        source.Write(new("lead-finder", "1.0.0", new("Lead finder", "Finds leads.", [], [], []), "## Scenario: Finds\nIt finds.\n",
            new() { ["synapses/finds.cs"] = "// t\n" }));
        Assert.True(File.Exists(Path.Combine(_root, "LeadFinder", "app.cs")));
        Assert.Equal("lead-finder", Assert.Single(source.Load()).Name);
    }

    [Fact]
    public void AReadOnlySourceRefusesToWrite()
        => Assert.Throws<InvalidOperationException>(() => new DirectoryShippedAppSource(_root, "vendor").Write(
            new("echo", "1.0.0", new("Echo", "Echoes.", [], [], []), "", [])));

    [Fact]
    public void TheIntoChatAppsShipTheirIcons()
    {
        var apps = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "..", "..", "..", "IntoChat", "Apps"));
        var bundles = new DirectoryShippedAppSource(apps, "intochat").Load();
        Assert.NotEmpty(bundles);
        foreach (var app in bundles)
        {
            Assert.Equal("icon.svg", app.Manifest.Icon);
            Assert.StartsWith("<svg", app.Files["icon.svg"], StringComparison.Ordinal);
        }
    }
}
