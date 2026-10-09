namespace DigitalBrain.Apps.Tests.Apps;

// The dock renders a manifest's toolbar; a manifest names only what its app can answer.
public sealed class AppManifestFacts
{
    private static AppManifest Manifest(params AppToolbarEntry[] toolbar)
        => new AppManifest("Player", "plays", [new AppOperation("play", "Play"), new AppOperation("volume", "Set the volume")], [], [], Toolbar: toolbar);

    [Fact]
    public void AToolbarEntryMustNameADeclaredOperation()
    {
        var error = Assert.Throws<InvalidDataException>(() => Manifest(new AppToolbarEntry("stop")).Validated());
        Assert.Contains("stop", error.Message, StringComparison.Ordinal);
        Assert.Contains("play", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AKindIsOneOfTheFourNamed()
    {
        Assert.Throws<InvalidDataException>(() => Manifest(new AppToolbarEntry("play", Kind: "knob")).Validated());
        foreach (var kind in new[] { "button", "toggle", "text" })
        {
            var manifest = Manifest(new AppToolbarEntry("play", Kind: kind));
            Assert.Same(manifest, manifest.Validated());
        }
    }

    [Fact]
    public void ASliderNeedsMinBelowMax()
    {
        Assert.NotNull(Manifest(new AppToolbarEntry("volume", Kind: "slider", Min: 0, Max: 100)).Validated());
        Assert.Throws<InvalidDataException>(() => Manifest(new AppToolbarEntry("volume", Kind: "slider", Min: 100, Max: 0)).Validated());
        Assert.Throws<InvalidDataException>(() => Manifest(new AppToolbarEntry("volume", Kind: "slider")).Validated());
    }

    [Fact]
    public void AManifestWithoutAToolbarIsValid()
        => Assert.Null(new AppManifest("Echo", "echoes", [new AppOperation("ask", "")], [], []).Validated().Toolbar);

    [Fact]
    public async Task AnAppRefusesAManifestWhoseToolbarNamesAnUnknownOperation()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await ModuleTest.Create().WithModule<AppsModule>().StartAsync(ct);
        var app = brain.Get<IApp>(IApp.Key("manifest-facts", "player"));
        await app.Create(Manifest(new AppToolbarEntry("play", Icon: "play", Label: "Play")), "spec");

        await Assert.ThrowsAsync<InvalidDataException>(() => app.SaveManifest(Manifest(new AppToolbarEntry("stop")), Guid.NewGuid()));
        await Assert.ThrowsAsync<InvalidDataException>(() => brain.Get<IApp>(IApp.Key("manifest-facts", "other")).Create(Manifest(new AppToolbarEntry("stop")), "spec"));

        Assert.Equal("play", Assert.Single((await app.Read()).Manifest.Toolbar!).Operation);
    }

    [Fact]
    public void AShippedFolderCarriesItsToolbarThroughLoadAndSave()
    {
        var root = Directory.CreateTempSubdirectory("toolbar-manifest-").FullName;
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "player"));
            File.WriteAllText(Path.Combine(root, "player", "app.json"), """
                {"name":"player","title":"Player","description":"plays","version":"1.0.0",
                 "operations":[{"name":"play","description":"Play"},{"name":"volume","description":"Volume"}],
                 "settings":[],
                 "toolbar":[{"operation":"play","icon":"play","label":"Play"},
                            {"operation":"volume","kind":"slider","icon":"volume","min":0,"max":100}]}
                """);
            var bundle = Assert.Single(new DirectoryShippedAppSource(root, "vendor").Load());

            Assert.Equal([("play", "button", null), ("volume", "slider", 100d)],
                bundle.Manifest.Toolbar!.Select(entry => (entry.Operation, entry.Kind, entry.Max)));
            Assert.Contains("\"toolbar\"", ShippedAppFolder.ToFiles(bundle)["app.json"], StringComparison.Ordinal);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void AShippedFolderWithABadToolbarFailsToLoad()
    {
        var root = Directory.CreateTempSubdirectory("toolbar-bad-").FullName;
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "player"));
            File.WriteAllText(Path.Combine(root, "player", "app.json"), """
                {"name":"player","title":"Player","description":"plays","version":"1.0.0","operations":[],"settings":[],
                 "toolbar":[{"operation":"play"}]}
                """);
            Assert.Throws<InvalidDataException>(() => new DirectoryShippedAppSource(root, "vendor").Load());
        }
        finally { Directory.Delete(root, recursive: true); }
    }
}