using DigitalBrain.Apps;

namespace DigitalBrain.Modules.Apps.Tests;

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

    private FeatureCatalog Catalog(IScriptSandbox? sandbox = null) => new(new FeatureDirectory(_root), sandbox);

    [Fact]
    public async Task TheCatalogListsEveryOwnersFeaturesAsShipped()
    {
        WriteEcho();
        WriteGreeter("os");
        var catalog = Catalog(new Sandbox(success: true));

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
    public async Task TheSameAppTwiceUnderOneOwnerIsRefused()
    {
        WriteGreeter("os");
        WriteGreeter("os", "GreeterAgain");
        var error = await Assert.ThrowsAsync<InvalidOperationException>(Catalog().Listings);
        Assert.Contains("os/greeter", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task OneAppNameUnderTwoOwnersIsTwoApps()
    {
        WriteGreeter("os");
        WriteGreeter("intochat");
        Assert.Equal(2, (await Catalog().Listings()).Count);
    }

    [Fact]
    public async Task ABundleThatDoesNotCompileNeverReachesTheCatalog()
    {
        WriteEcho("this is not C#");
        var catalog = Catalog(new Sandbox(success: false));
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => catalog.Find("intochat", "echo"));
        Assert.Contains("intochat/echo 2.0.0 does not compile", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AHostWithNoFeaturesFolderHasNoFeatures()
        => Assert.Empty(await new FeatureCatalog(null).Listings());

    private sealed class Sandbox(bool success) : IScriptSandbox
    {
        public Task<Verdict[]> Judge(string brain, IReadOnlyDictionary<string, string> bundle, Signal signal, CancellationToken cancellationToken)
            => Task.FromResult<Verdict[]>([]);
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
