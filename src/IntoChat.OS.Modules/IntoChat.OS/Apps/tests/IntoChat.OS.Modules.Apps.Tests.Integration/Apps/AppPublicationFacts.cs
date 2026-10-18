using IntoChat.OS.Modules.Apps.Tests.Integration.Runtimes;
using IntoChat.OS.Testing;

namespace IntoChat.OS.Apps.Tests.Apps;

public sealed class AppPublicationFacts
{
    private static readonly CancellationToken Ct = TestContext.Current.CancellationToken;
    private static Task<ITestBrain> Start() => TestBrain.Create().WithModule<AppsModule>().WithFakeAppRuntime(new()).StartAsync(Ct);
    private static AppBundle Bundle() => new("echo", "1.0.0", new("Echo", "A", [], [], []), "spec A", new() { ["preview.txt"] = "sample A" });

    [Trait("host", "own")]
    [Fact]
    public async Task PublishedReleaseSurvivesMoreThanTwentyDraftEdits()
    {
        await using var brain = await Start();
        var person = await brain.PersonAsync("vlad", Ct);
        using var caller = person.AsCaller();
        var app = brain.Get<IApp>(IApp.Key(person.BrainId, "echo"));
        await app.Import(Bundle());
        await app.Publish();
        var first = Assert.Single(await app.PublishedReleases());
        for (var i = 0; i < 25; i++) { await app.WriteFile("draft.txt", "draft " + i, Guid.NewGuid()); }
        var draft = (await app.Read()).Version;
        Assert.Null(await app.ExportVersion("1.0.0"));
        await Assert.ThrowsAsync<KeyNotFoundException>(() => app.ExportRelease(draft, first.Reference.Digest));
        await app.Publish();
        Assert.Equal(2, (await app.PublishedReleases()).Length);
        var retained = await app.ExportRelease("1.0.0", first.Reference.Digest);
        Assert.Equal("spec A", retained.Spec);
        Assert.Equal("A", retained.Manifest.Description);
        Assert.Equal("sample A", retained.Files["preview.txt"]);
        Assert.False(retained.Files.ContainsKey("draft.txt"));
        var reader = await brain.PersonAsync("reader", Ct);
        using var other = reader.AsCaller();
        Assert.Equal("1.0.0", (await app.ExportRelease("1.0.0", first.Reference.Digest)).Version);
    }

    [Trait("host", "own")]
    [Fact]
    public async Task RepublishingIdenticalContentKeepsItsTimeAndDigest()
    {
        await using var brain = await Start();
        var person = await brain.PersonAsync("vlad", Ct);
        using var caller = person.AsCaller();
        var app = brain.Get<IApp>(IApp.Key(person.BrainId, "echo"));
        await app.Import(Bundle());
        await app.Publish();
        var first = Assert.Single(await app.PublishedReleases());
        await app.Publish();
        var again = Assert.Single(await app.PublishedReleases());
        Assert.Equal(first.Reference, again.Reference);
        Assert.Equal(first.PublishedAt, again.PublishedAt);
    }

    [Trait("host", "own")]
    [Fact]
    public async Task ChangedContentCannotReuseAPublishedVersion()
    {
        await using var brain = await Start();
        var person = await brain.PersonAsync("vlad", Ct);
        using var caller = person.AsCaller();
        var app = brain.Get<IApp>(IApp.Key(person.BrainId, "echo"));
        await app.Import(Bundle());
        await app.Publish();
        await app.Replace(Bundle() with { Spec = "different" });
        await Assert.ThrowsAsync<InvalidOperationException>(app.Publish);
        Assert.Equal("sample A", Assert.Single(await app.PublishedReleases()).Preview);
    }

    [Trait("host", "own")]
    [Fact]
    public async Task RecreatedAppCannotSatisfyAnOldDigest()
    {
        await using var brain = await Start();
        var person = await brain.PersonAsync("vlad", Ct);
        using var caller = person.AsCaller();
        var app = brain.Get<IApp>(IApp.Key(person.BrainId, "echo"));
        await app.Import(Bundle());
        await app.Publish();
        var first = Assert.Single(await app.PublishedReleases());
        await app.Delete();
        Assert.Empty(await app.PublishedReleases());
        await app.Import(Bundle() with { Files = new() { ["preview.txt"] = "different" } });
        await app.Publish();
        await Assert.ThrowsAsync<KeyNotFoundException>(() => app.ExportRelease("1.0.0", first.Reference.Digest));
    }

    [Fact]
    public void DigestIsIndependentOfDictionaryInsertionOrderButCoversAllPublishedContent()
    {
        var bundle = Bundle() with { Files = new() { ["a"] = "one", ["b"] = "two" } };
        var digest = AppPublicationDigest.Of(bundle);
        Assert.Equal(digest, AppPublicationDigest.Of(bundle with { Files = new() { ["b"] = "two", ["a"] = "one" } }));
        Assert.NotEqual(digest, AppPublicationDigest.Of(bundle with { Spec = "changed" }));
        Assert.NotEqual(digest, AppPublicationDigest.Of(bundle with { Manifest = bundle.Manifest with { Description = "changed" } }));
        Assert.NotEqual(digest, AppPublicationDigest.Of(bundle with { Files = new() { ["a"] = "one", ["b"] = "changed" } }));
    }
}
