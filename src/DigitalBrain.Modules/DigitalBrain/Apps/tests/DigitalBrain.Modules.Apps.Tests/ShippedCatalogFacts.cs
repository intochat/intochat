using DigitalBrain.Apps;

namespace DigitalBrain.Modules.Apps.Tests;

public sealed class ShippedCatalogFacts : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("shipped-catalog-").FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private void Write(string relative, string content)
    {
        var path = Path.Combine(_root, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    private void WriteEcho(string body = "// echo\n")
    {
        Write("Echo/app.json", """
            {"name":"echo","title":"Echo","description":"Echoes.","version":"2.0.0","operations":[],"settings":[]}
            """);
        Write("Echo/synapses/echo.cs", body);
    }

    private EmbeddedShippedAppSource Embedded(string publisher)
        => new(typeof(ShippedCatalogFacts).Assembly, "ShippedFixture/", publisher);

    [Fact]
    public async Task TheCatalogListsEmbeddedAndDirectoryAppsAsShipped()
    {
        WriteEcho();
        var catalog = new ShippedCatalog([Embedded("os"), new DirectoryShippedAppSource(_root, "intochat")], new Sandbox(success: true));

        var listings = await catalog.Listings();

        Assert.Equal([("intochat", "echo", "2.0.0"), ("os", "greeter", "1.2.0")],
            listings.Select(listing => (listing.Publisher, listing.Name, listing.Version)));
        Assert.All(listings, listing => Assert.IsType<AppSource.Shipped>(listing.Source));
        Assert.Equal("// greet\n", (await catalog.Find("os", "greeter"))!.Files["synapses/greet.cs"]);
        Assert.Equal("// echo\n", (await catalog.Find("intochat", "echo"))!.Files["synapses/echo.cs"]);
        Assert.Null(await catalog.Find("intochat", "greeter"));
    }

    [Fact]
    public async Task TheSameAppShippedTwiceUnderOnePublisherIsRefused()
    {
        var catalog = new ShippedCatalog([Embedded("os"), Embedded("os")]);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(catalog.Listings);
        Assert.Contains("os/greeter", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task OneAppNameUnderTwoPublishersIsTwoApps()
    {
        var catalog = new ShippedCatalog([Embedded("os"), Embedded("intochat")]);
        Assert.Equal(2, (await catalog.Listings()).Count);
    }

    [Fact]
    public async Task ABundleThatDoesNotCompileNeverReachesTheCatalog()
    {
        WriteEcho("this is not C#");
        var catalog = new ShippedCatalog([new DirectoryShippedAppSource(_root, "intochat")], new Sandbox(success: false));
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => catalog.Find("intochat", "echo"));
        Assert.Contains("intochat/echo 2.0.0 does not compile", error.Message, StringComparison.Ordinal);
    }

    private sealed class Sandbox(bool success) : IScriptSandbox
    {
        public bool CanRun => true;
        public string AuthoringDescription => "";
        public Task<ScriptContractCatalog> ReadContracts(IReadOnlyList<string> modules, CancellationToken cancellationToken)
            => throw new NotSupportedException();
        public Task<ScriptCompilationCheck> Compile(IReadOnlyDictionary<string, string> files, CancellationToken cancellationToken)
            => Task.FromResult(success
                ? new ScriptCompilationCheck(true, [])
                : new ScriptCompilationCheck(false, [.. files.Keys.Select(file => new ScriptCompilationDiagnostic(file, "CS1002", 1, "; expected"))]));
    }
}
