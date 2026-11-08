namespace IntoChat.OS.Modules.Apps.Tests.Integration;

public sealed class FeatureCatalogFacts : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("feature-catalog-").FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private void Write(string relative, string content)
    {
        var path = Path.Combine(_root, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    private void WriteEcho(string body = "// echo\n")
    {
        Write("intochat/Echo/app.cs", Declarations.Of("echo", "2.0.0", "Echo", "Echoes."));
        Write("intochat/Echo/echo.cs", body);
    }

    private void WriteGreeter(string owner, string folder = "Greeter")
    {
        Write($"{owner}/{folder}/app.cs", Declarations.Of("greeter", "1.2.0", "Greeter", "Greets."));
        Write($"{owner}/{folder}/greet.cs", "// greet\n");
    }

    private FeatureCatalog Catalog() => new(new FeatureDirectory(_root));

    // One reaction, so it passes the shape and reaches the compiler, which refuses it.
    private const string Uncompilable = "await foreach (var request in brain.On(app, \"ask\", brain.Stopping)) { this is not C#; }\n";

    [Fact]
    public async Task TheCatalogListsEveryOwnersFeaturesAsShipped()
    {
        WriteEcho();
        WriteGreeter("os");
        var catalog = Catalog();

        var listings = await catalog.Listings();

        Assert.Equal([("intochat", "echo", "2.0.0"), ("os", "greeter", "1.2.0")],
            listings.Select(listing => (listing.Publisher, listing.Name, listing.Version)));
        Assert.All(listings, listing => Assert.IsType<AppSource.Shipped>(listing.Source));
        Assert.Equal("// greet\n", (await catalog.Find("os", "greeter"))!.Files["greet.cs"]);
        Assert.Equal("// echo\n", (await catalog.Find("intochat", "echo"))!.Files["echo.cs"]);
        Assert.Null(await catalog.Find("intochat", "greeter"));
        Assert.True(await catalog.IsOwner("os"));
        Assert.False(await catalog.IsOwner("alice"));
    }

    [Fact]
    public async Task TwoFoldersShippingTheSameAppAreBothSkippedAndReportedWhileAThirdLoads()
    {
        WriteGreeter("os");
        WriteGreeter("os", "GreeterAgain");
        WriteEcho();
        var catalog = Catalog();

        Assert.Equal([("intochat", "echo")], (await catalog.Listings()).Select(listing => (listing.Publisher, listing.Name)));
        Assert.Null(await catalog.Find("os", "greeter"));
        var failures = await catalog.Failures();
        Assert.Equal(2, failures.Count);
        Assert.All(failures, failure =>
        {
            Assert.Contains("os/Greeter", failure.Reason, StringComparison.Ordinal);
            Assert.Contains("os/GreeterAgain", failure.Reason, StringComparison.Ordinal);
        });
    }

    [Fact]
    public async Task OneAppNameUnderTwoOwnersIsTwoApps()
    {
        WriteGreeter("os");
        WriteGreeter("intochat");
        Assert.Equal(2, (await Catalog().Listings()).Count);
    }

    [Fact]
    public async Task AnEditOnDiskReachesTheCatalogWhenThatFeatureReloads()
    {
        WriteEcho();
        WriteGreeter("os");
        var catalog = Catalog();
        Assert.Equal("// echo\n", (await catalog.Find("intochat", "echo"))!.Files["echo.cs"]);

        WriteEcho("// echo louder\n");
        Write("intochat/Echo/app.cs", Declarations.Of("echo", "2.0.1", "Echo", "Echoes."));
        var reloaded = await catalog.Reload("intochat", "echo");

        Assert.Equal("2.0.1", reloaded!.Version);
        Assert.Equal("// echo louder\n", (await catalog.Find("intochat", "echo"))!.Files["echo.cs"]);
        Assert.Contains(await catalog.Listings(), listing => listing.Name == "echo" && listing.Version == "2.0.1");
        Assert.Equal("// greet\n", (await catalog.Find("os", "greeter"))!.Files["greet.cs"]);
    }

    [Fact]
    public async Task AHostWithNoFeaturesFolderHasNoFeatures()
        => Assert.Empty(await new FeatureCatalog(null).Listings());

}
