using DigitalBrain.Modules.Apps.Tests.Runtimes;
using DigitalBrain.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace DigitalBrain.Apps.Tests.Apps;

public sealed class ExactAppInstallFacts
{
    private static readonly CancellationToken Ct = TestContext.Current.CancellationToken;
    private static Task<ITestBrain> Start() => TestBrain.Create().WithModule<AppsModule>().WithFakeAppRuntime(new()).StartAsync(Ct);
    private static async Task<AppPublicationRef> Publish(ITestBrain brain, Person person)
    {
        using var caller = person.AsCaller();
        var app = brain.Get<IApp>(IApp.Key(person.BrainId, "echo"));
        await app.Import(new("echo", "1.0.0", new("Echo", "A", [], [new("text", "Text", "default")], [new("mail", "gmail")]), "spec", new() { ["preview.txt"] = "A" }));
        await app.Configure(new() { ["text"] = "private" }, new() { ["mail"] = "private-mail" });
        await app.Publish();
        var reference = Assert.Single(await app.PublishedReleases()).Reference;
        await app.WriteFile("preview.txt", "B", Guid.NewGuid());
        await app.Publish();
        return reference;
    }

    [Fact]
    public async Task ExactForkKeepsOldCodeAndOwnConfigurationAndRetriesDoNotOverwriteEdits()
    {
        await using var brain = await Start();
        var publisher = await brain.PersonAsync("vlad", Ct);
        var reference = await Publish(brain, publisher);
        var reader = await brain.PersonAsync("reader", Ct);
        using var caller = reader.AsCaller();
        var installer = brain.Services.GetRequiredService<AppInstaller>();
        var operation = Guid.NewGuid();
        var copy = await installer.InstallPublication(reference, "mine", operation, Ct);
        Assert.Equal("1.0.0", copy.Version);
        Assert.Equal("A", copy.Files["preview.txt"]);
        Assert.Equal("default", copy.Settings["text"]);
        Assert.Empty(copy.Connections);
        Assert.Equal("vlad", copy.Origin!.Publisher);
        Assert.Equal(reference.Digest, copy.Origin.PublicationDigest);
        Assert.Equal(reader.AccountId, copy.Origin.InstalledByAccount);
        var app = brain.Get<IApp>(IApp.Key(reader.BrainId, "mine"));
        await app.WriteFile("preview.txt", "my edit", Guid.NewGuid());
        var retry = await installer.InstallPublication(reference, "mine", operation, Ct);
        Assert.Equal("my edit", retry.Files["preview.txt"]);
        await Assert.ThrowsAsync<InvalidOperationException>(() => installer.InstallPublication(reference, "mine", Guid.NewGuid(), Ct));
    }

    [Fact]
    public async Task ConcurrentForksCannotTurnACollisionIntoReplacement()
    {
        await using var brain = await Start();
        var publisher = await brain.PersonAsync("vlad", Ct);
        var reference = await Publish(brain, publisher);
        var reader = await brain.PersonAsync("reader", Ct);
        using var caller = reader.AsCaller();
        var installer = brain.Services.GetRequiredService<AppInstaller>();
        async Task<bool> Attempt()
        {
            try { await installer.InstallPublication(reference, "mine", Guid.NewGuid(), Ct); return true; }
            catch (InvalidOperationException) { return false; }
        }
        var results = await Task.WhenAll(Attempt(), Attempt());
        Assert.Single(results, success => success);
        Assert.Equal("A", (await brain.Get<IApp>(IApp.Key(reader.BrainId, "mine")).Read()).Files["preview.txt"]);
    }

    [Fact]
    public async Task InvalidReferenceNeverCreatesATarget()
    {
        await using var brain = await Start();
        var publisher = await brain.PersonAsync("vlad", Ct);
        var reference = await Publish(brain, publisher);
        var reader = await brain.PersonAsync("reader", Ct);
        using var caller = reader.AsCaller();
        var installer = brain.Services.GetRequiredService<AppInstaller>();
        await Assert.ThrowsAsync<KeyNotFoundException>(() => installer.InstallPublication(reference with { Digest = new string('0', 64) }, "mine", Guid.NewGuid(), Ct));
        await Assert.ThrowsAsync<KeyNotFoundException>(() => installer.InstallPublication(reference with { Source = new(reader.AccountId, reader.BrainId) }, "mine", Guid.NewGuid(), Ct));
        Assert.Equal("", (await brain.Get<IApp>(IApp.Key(reader.BrainId, "mine")).Read()).Manifest.Title);
    }
}
