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
        {"name":"echo","title":"Echo","description":"Echoes.","runtime":"csharp","operations":[],"settings":[]}
        """;

    [Fact]
    public void AFolderOfAppFoldersLoadsLikeTheEmbeddedSource()
    {
        Write("echo/app.json", Manifest);
        Write("echo/app.cs", "// program\r\nreturn;\r\n");
        Write("echo/behaviors/extra.cs", "// behavior\n");
        var source = new DirectoryShippedAppSource(_root, "vendor");
        var app = Assert.Single(source.Load());
        Assert.Equal("vendor/echo", app.Package.ToString());
        // Line endings normalize exactly as the embedded source does, so content hashes agree
        // across checkouts.
        Assert.Equal("// program\nreturn;\n", app.Content.File(PackageContent.SourcePath));
        Assert.Equal("// behavior\n", app.Content.File("behaviors/extra.cs"));
    }

    [Fact]
    public void AMissingDirectoryIsAnError()
        => Assert.Throws<DirectoryNotFoundException>(() => new DirectoryShippedAppSource(Path.Combine(_root, "absent"), "vendor").Load());

    [Fact]
    public void ConfigurationNamesThePublishersAndPaths()
    {
        Write("echo/app.json", Manifest);
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            [ShippedAppsServiceCollectionExtensions.ConfigurationSection + ":vendor:Path"] = _root,
        }).Build();
        var services = new ServiceCollection().AddShippedApps(configuration);
        using var provider = services.BuildServiceProvider();
        var source = Assert.Single(provider.GetServices<IShippedAppSource>());
        Assert.Equal("vendor", source.Publisher);
        Assert.Equal("vendor/echo", Assert.Single(source.Load()).Package.ToString());
    }
}
