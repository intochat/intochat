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

    private const string Manifest = """
        {"name":"echo","title":"Echo","description":"Echoes.","version":"1.0.0","operations":[],"settings":[]}
        """;

    [Fact]
    public void AFolderOfAppFoldersLoadsLikeTheEmbeddedSource()
    {
        Write("echo/app.json", Manifest);
        Write("echo/synapses/app.cs", "// program\r\nreturn;\r\n");
        Write("echo/synapses/extra.cs", "// behavior\n");
        var source = new DirectoryShippedAppSource(_root, "vendor");
        var app = Assert.Single(source.Load());
        Assert.Equal("echo", app.Name);
        // Line endings normalize exactly as the embedded source does, so content hashes agree
        // across checkouts.
        Assert.Equal("// program\nreturn;\n", app.Files["synapses/app.cs"]);
        Assert.Equal("// behavior\n", app.Files["synapses/extra.cs"]);
    }

    [Fact]
    public void AMissingDirectoryIsAnError()
        => Assert.Throws<DirectoryNotFoundException>(() => new DirectoryShippedAppSource(Path.Combine(_root, "absent"), "vendor").Load());

    [Fact]
    public void ConfigurationNamesTheSourcesAccountsAndPaths()
    {
        Write("echo/app.json", Manifest);
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            [ShippedAppsServiceCollectionExtensions.ConfigurationSection + ":vendor:Path"] = _root,
            [ShippedAppsServiceCollectionExtensions.ConfigurationSection + ":vendor:Account"] = "vendor-account",
        }).Build();
        var services = new ServiceCollection().AddShippedApps(configuration);
        using var provider = services.BuildServiceProvider();
        var source = Assert.Single(provider.GetServices<IShippedAppSource>());
        Assert.Equal("vendor-account", source.Account);
        Assert.Equal("echo", Assert.Single(source.Load()).Name);
    }

    [Fact]
    public void ASourceWithoutAnAccountIsAConfigurationError()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            [ShippedAppsServiceCollectionExtensions.ConfigurationSection + ":vendor:Path"] = _root,
        }).Build();
        Assert.Throws<InvalidOperationException>(() => new ServiceCollection().AddShippedApps(configuration));
    }

    [Fact]
    public void AWrittenBundleReadsBackIdentically()
    {
        Write("Echo/app.json", """
            {"name":"echo","title":"Echo","description":"Echoes.","version":"1.0.0","icon":"icon.svg","operations":[{"name":"ask","description":"Echo"}],"settings":[{"name":"Tone","description":"how","default":"flat"}]}
            """);
        Write("Echo/app.spec.md", "## Scenario: Echo\nEchoes.\n");
        Write("Echo/tests.cs", "// tests\n");
        Write("Echo/synapses/echo.cs", "// echo\n");
        Write("Echo/icon.svg", "<svg/>");
        var source = new DirectoryShippedAppSource(_root, "vendor", writable: true);
        var loaded = Assert.Single(source.Load());
        Assert.Equal("icon.svg", loaded.Manifest.Icon);

        var edited = loaded with
        {
            Version = "1.1.0",
            Spec = "## Scenario: Echo\nEchoes twice.\n",
            Files = new(loaded.Files.Where(file => file.Key != "synapses/echo.cs"), StringComparer.Ordinal) { ["synapses/twice.cs"] = "// twice\n" },
        };
        source.Write(edited);

        var reread = Assert.Single(source.Load());
        Assert.Equal("1.1.0", reread.Version);
        Assert.Equal(edited.Spec, reread.Spec);
        Assert.Equal(edited.Manifest with { Operations = [], Settings = [], Slots = [] }, reread.Manifest with { Operations = [], Settings = [], Slots = [] });
        Assert.Equal(edited.Manifest.Operations, reread.Manifest.Operations);
        Assert.Equal(edited.Manifest.Settings, reread.Manifest.Settings);
        Assert.Equal(edited.Files.OrderBy(f => f.Key), reread.Files.OrderBy(f => f.Key));
        Assert.False(File.Exists(Path.Combine(_root, "Echo", "behaviors", "echo.cs")));
        Assert.Single(Directory.GetDirectories(_root));
    }

    [Fact]
    public void ANewAppGetsAFolderNamedAfterItsTitle()
    {
        var source = new DirectoryShippedAppSource(_root, "vendor", writable: true);
        source.Write(new("lead-finder", "1.0.0", new("Lead finder", "Finds leads.", [], [], []), "## Scenario: Finds\nIt finds.\n",
            new() { [AppFiles.TestsPath] = "// t\n" }));
        Assert.True(File.Exists(Path.Combine(_root, "LeadFinder", "app.json")));
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
