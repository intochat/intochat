using DigitalBrain.Testing;
using DigitalBrain.Modules.Apps.Tests.Runtimes;

namespace DigitalBrain.Apps.Tests.Apps;

// The dock renders a manifest's toolbar; a manifest names only what its app can answer.
public sealed class AppManifestFacts
{
    private static AppManifest Manifest(params AppToolbarEntry[] toolbar)
        => new AppManifest("Player", "plays", [new AppOperation("play", "Play"), new AppOperation("volume", "Set the volume")], [], [], Toolbar: toolbar);

    [Fact]
    public async Task AnEntrySurvivesGrainStorageExportAndInstallInAnotherBrain()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await TestBrain.Create().WithModule<AppsModule>().WithFakeAppRuntime(new()).StartAsync(ct);
        var source = brain.Get<IApp>(IApp.Key("entry-source", "report"));
        await source.Create(new("Report", "", [], [], [], Entry: new("text", "documents/answer")), "spec");
        var bundle = await source.Export();
        var installed = brain.Get<IApp>(IApp.Key("entry-target", "report"));
        await installed.Import(bundle);
        Assert.Equal(new DigitalBrain.Flutter.UiChildRef("text", "documents/answer"), (await installed.Read()).Manifest.Entry);
        Assert.Equal(bundle.Manifest.Entry, AppDeclaration.Read(FeatureFolder.ToFiles(await installed.Export())["app.cs"]).Manifest.Entry);
    }

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
        await using var brain = await TestBrain.Create().WithModule<AppsModule>().StartAsync(ct);
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
            Directory.CreateDirectory(Path.Combine(root, "vendor", "player"));
            File.WriteAllText(Path.Combine(root, "vendor", "player", "app.cs"), """
                using DigitalBrain.Apps;
                static class App
                {
                    public const string Name = "player";
                    public const string Version = "1.0.0";
                    public static readonly AppManifest Manifest = new(
                        Title: "Player", Description: "plays",
                        Operations: [new("play", "Play"), new("volume", "Volume")],
                        Settings: [], Slots: [],
                        Toolbar: [new("play", Icon: "play", Label: "Play"),
                                  new("volume", Kind: AppToolbarKinds.Slider, Icon: "volume", Min: 0, Max: 100)]);
                }
                """);
            var bundle = Assert.Single(new FeatureDirectory(root).Load()).Bundle;

            Assert.Equal([("play", "button", null), ("volume", "slider", 100d)],
                bundle.Manifest.Toolbar!.Select(entry => (entry.Operation, entry.Kind, entry.Max)));
            Assert.Contains("Toolbar:", FeatureFolder.ToFiles(bundle)["app.cs"], StringComparison.Ordinal);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void AShippedFolderWithABadToolbarFailsToLoad()
    {
        var root = Directory.CreateTempSubdirectory("toolbar-bad-").FullName;
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "vendor", "player"));
            File.WriteAllText(Path.Combine(root, "vendor", "player", "app.cs"), """
                using DigitalBrain.Apps;
                static class App
                {
                    public const string Name = "player";
                    public const string Version = "1.0.0";
                    public static readonly AppManifest Manifest = new("Player", "plays", [], [], [], Toolbar: [new("play")]);
                }
                """);
            Assert.Throws<InvalidDataException>(() => new FeatureDirectory(root).Load());
        }
        finally { Directory.Delete(root, recursive: true); }
    }
}
