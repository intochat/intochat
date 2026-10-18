using IntoChat.OS.Modules.Apps.Tests.Integration.Runtimes;
using IntoChat.OS.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace IntoChat.OS.Apps.Tests.Apps;

// Install copies a published version into the caller's brain and remembers where it came from;
// Update replaces the program and keeps what the person chose, until the person edits it.
public sealed class AppInstallerFacts
{
    private const string Spec = "## Scenario: Echoes\nIt echoes.";
    private static readonly CancellationToken Ct = TestContext.Current.CancellationToken;

    private static AppManifest Manifest() =>
        new("Echo", "echoes", [new("ask", "ask it")], [new("Text", "what to say", "hi")], [new("gmail", "gmail")]);

    private static Task<ITestBrain> Start() => TestBrain.Create().WithModule<AppsModule>().WithFakeAppRuntime(new()).StartAsync(Ct);

    // A host whose features folder (AppsOptions.Features) holds os/greeter 1.2.0.
    private static Task<ITestBrain> StartShipping() => TestBrain.Create()
        .WithModule<AppsModule, AppsOptions>(options => options.Features = Path.Combine(AppContext.BaseDirectory, "FeatureFixture"))
        .WithFakeAppRuntime(new())
        .StartAsync(Ct);

    private static AppInstaller Installer(ITestBrain brain) => brain.Services.GetRequiredService<AppInstaller>();

    private static IApp Mine(ITestBrain brain, Person person, string name = "echo") => brain.Get<IApp>(IApp.Key(person.BrainId, name));

    private static async Task<(Person Publisher, IApp App)> Published(ITestBrain brain, string publisher = "intochat")
    {
        var person = await brain.PersonAsync(publisher, Ct);
        var app = Mine(brain, person);
        using (person.AsCaller())
        {
            await app.Create(Manifest(), Spec);
            await app.BeginBuild(null);
            await app.CompleteBuild(await app.CurrentBuildAttempt(), new() { ["synapses/echo.cs"] = "// v1" }, null);
            await app.Publish();
        }
        return (person, app);
    }

    private static async Task<string> PublishNewVersion(Person publisher, IApp app)
    {
        using (publisher.AsCaller())
        {
            await app.WriteBehavior("synapses/echo.cs", "// v2", Guid.NewGuid());
            return (await app.Publish()).Version;
        }
    }

    [Trait("host", "own")]
    [Fact]
    public async Task InstallCopiesThePublishedVersionLiveWithDefaultSettingsAndRecordsItsOrigin()
    {
        await using var brain = await Start();
        var (intochat, source) = await Published(brain);
        using (intochat.AsCaller()) { await source.Configure(new() { ["Text"] = "publisher's own" }, new() { ["gmail"] = "publisher-gmail" }); }
        var alice = await brain.PersonAsync("alice", Ct);

        using var _ = alice.AsCaller();
        var installed = await Installer(brain).Install("intochat", "echo", Ct);

        Assert.Equal(AppStatus.Live, installed.Status);
        Assert.Equal((await source.ExportPublished()).Version, installed.Version);
        Assert.Equal("hi", installed.Settings["Text"]);
        Assert.Empty(installed.Connections);
        Assert.Equal(new AppOrigin("intochat", new AppSource.Brain(intochat.AccountId, intochat.BrainId), "echo", installed.Version), installed.Origin);
        Assert.False(installed.Edited);
        Assert.Equal(installed.Version, (await Mine(brain, alice).Read()).Version);
    }

    [Trait("host", "own")]
    [Fact]
    public async Task InstallingAnAppNotInTheMarketplaceIsNotFound()
    {
        await using var brain = await Start();
        var alice = await brain.PersonAsync("alice", Ct);
        using var _ = alice.AsCaller();
        await Assert.ThrowsAsync<KeyNotFoundException>(() => Installer(brain).Install("intochat", "echo", Ct));
    }

    [Trait("host", "own")]
    [Fact]
    public async Task InstallingOverAnAppOfTheSameNameIsRefusedAndLeavesItUntouched()
    {
        await using var brain = await Start();
        await Published(brain);
        var alice = await brain.PersonAsync("alice", Ct);
        using var _ = alice.AsCaller();
        var own = Mine(brain, alice);
        await own.Create(Manifest() with { Title = "Alice's echo" }, Spec);

        await Assert.ThrowsAsync<InvalidOperationException>(() => Installer(brain).Install("intochat", "echo", Ct));
        var after = await own.Read();
        Assert.Equal("Alice's echo", after.Manifest.Title);
        Assert.Null(after.Origin);
    }

    [Trait("host", "own")]
    [Fact]
    public async Task InstallingYourOwnPublishedAppIsRefused()
    {
        await using var brain = await Start();
        var (intochat, _) = await Published(brain);
        using var caller = intochat.AsCaller();
        await Assert.ThrowsAsync<InvalidOperationException>(() => Installer(brain).Install("intochat", "echo", Ct));
    }

    [Trait("host", "own")]
    [Fact]
    public async Task UpdateReplacesTheProgramAndKeepsSettingsAndConnections()
    {
        await using var brain = await Start();
        var (intochat, source) = await Published(brain);
        var alice = await brain.PersonAsync("alice", Ct);
        using (alice.AsCaller())
        {
            await Installer(brain).Install("intochat", "echo", Ct);
            await Mine(brain, alice).Configure(new() { ["Text"] = "alice's words" }, new() { ["gmail"] = "alice-gmail" });
        }
        var next = await PublishNewVersion(intochat, source);

        using var _ = alice.AsCaller();
        var updated = await Installer(brain).Update("echo", Ct);

        Assert.Equal(next, updated.Version);
        Assert.Equal("// v2", updated.Files["synapses/echo.cs"]);
        Assert.Equal("alice's words", updated.Settings["Text"]);
        Assert.Equal("alice-gmail", updated.Connections["gmail"]);
        Assert.Equal(next, updated.Origin!.Version);
        Assert.False(updated.Edited);
    }

    [Trait("host", "own")]
    [Fact]
    public async Task AnEditedCopyRefusesUpdates()
    {
        await using var brain = await Start();
        var (intochat, source) = await Published(brain);
        var alice = await brain.PersonAsync("alice", Ct);
        using (alice.AsCaller())
        {
            await Installer(brain).Install("intochat", "echo", Ct);
            await Mine(brain, alice).WriteFile("notes.md", "mine now", Guid.NewGuid());
        }
        await PublishNewVersion(intochat, source);

        using var _ = alice.AsCaller();
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => Installer(brain).Update("echo", Ct));
        Assert.Contains("edits", error.Message, StringComparison.Ordinal);
    }

    [Trait("host", "own")]
    [Fact]
    public async Task UpdatingWithNothingNewerIsANoOp()
    {
        await using var brain = await Start();
        await Published(brain);
        var alice = await brain.PersonAsync("alice", Ct);
        using var _ = alice.AsCaller();
        var installed = await Installer(brain).Install("intochat", "echo", Ct);
        var again = await Installer(brain).Update("echo", Ct);
        Assert.Equal(installed.Version, again.Version);
        Assert.Equal(installed.Versions.Length, again.Versions.Length);
    }

    [Trait("host", "own")]
    [Fact]
    public async Task AnUpdateRetriedAtTheSameVersionAddsNoSecondHistoryEntry()
    {
        await using var brain = await Start();
        var (intochat, source) = await Published(brain);
        var alice = await brain.PersonAsync("alice", Ct);
        using var _ = alice.AsCaller();
        await Installer(brain).Install("intochat", "echo", Ct);
        var next = await PublishNewVersion(intochat, source);
        var updated = await Installer(brain).Update("echo", Ct);

        var retried = await Mine(brain, alice).Replace((await source.ExportPublished()) with { Name = "echo" });

        Assert.Equal(next, updated.Version);
        Assert.Equal(updated.Versions.Length, retried.Versions.Length);
        Assert.Single(retried.Versions, entry => entry.Version == next);
    }

    [Trait("host", "own")]
    [Fact]
    public async Task UpdatingAnAppThatWasNotInstalledIsRefused()
    {
        await using var brain = await Start();
        var alice = await brain.PersonAsync("alice", Ct);
        using var _ = alice.AsCaller();
        await Mine(brain, alice).Create(Manifest(), Spec);
        await Assert.ThrowsAsync<InvalidOperationException>(() => Installer(brain).Update("echo", Ct));
    }

    [Trait("host", "own")]
    [Fact]
    public async Task AnEntryShowsInstalledAndAnUpdateOnlyForAnUneditedOlderCopy()
    {
        await using var brain = await Start();
        var (intochat, source) = await Published(brain);
        var alice = await brain.PersonAsync("alice", Ct);
        var marketplace = brain.Get<IMarketplace>(IMarketplace.Key);
        async Task<MarketplaceEntry> Entry(Person person)
        {
            using (person.AsCaller()) { return await Installer(brain).Entry((await marketplace.Read("intochat", "echo"))!, Ct); }
        }

        var before = await Entry(alice);
        Assert.False(before.Installed);
        Assert.False(before.UpdateAvailable);

        using (alice.AsCaller()) { await Installer(brain).Install("intochat", "echo", Ct); }
        var current = await Entry(alice);
        Assert.True(current.Installed);
        Assert.False(current.UpdateAvailable);

        await PublishNewVersion(intochat, source);
        Assert.True((await Entry(alice)).UpdateAvailable);

        using (alice.AsCaller()) { await Mine(brain, alice).WriteFile("notes.md", "mine now", Guid.NewGuid()); }
        var edited = await Entry(alice);
        Assert.True(edited.Installed);
        Assert.False(edited.UpdateAvailable);

        // The publisher sees its own listing as installed: the app is theirs.
        Assert.True((await Entry(intochat)).Installed);
    }

    [Trait("host", "own")]
    [Fact]
    public async Task AnInstalledCopyKeepsWorkingAfterThePublisherDeletesTheApp()
    {
        await using var brain = await Start();
        var (intochat, source) = await Published(brain);
        var alice = await brain.PersonAsync("alice", Ct);
        using (alice.AsCaller()) { await Installer(brain).Install("intochat", "echo", Ct); }
        using (intochat.AsCaller()) { await source.Delete(); }

        using var _ = alice.AsCaller();
        Assert.Equal(AppStatus.Live, (await Mine(brain, alice).Read()).Status);
        await Assert.ThrowsAsync<KeyNotFoundException>(() => Installer(brain).Update("echo", Ct));
    }

    [Trait("host", "own")]
    [Fact]
    public async Task InstallCopiesAShippedAppFromTheCatalogAndRecordsAShippedOrigin()
    {
        await using var brain = await StartShipping();
        var alice = await brain.PersonAsync("alice", Ct);
        using var _ = alice.AsCaller();
        var installed = await Installer(brain).Install("os", "greeter", Ct);
        var entry = await Installer(brain).Entry((await brain.Services.GetRequiredService<MarketplaceView>().Read("os", "greeter", Ct))!, Ct);

        Assert.Equal("1.2.0", installed.Version);
        Assert.Equal("// greet\n", installed.Files["greet.cs"]);
        Assert.Equal(new AppOrigin("os", new AppSource.Shipped(), "greeter", "1.2.0"), installed.Origin);
        Assert.True(entry.Installed);
        Assert.False(entry.UpdateAvailable);
        Assert.Null(await brain.Get<IMarketplace>(IMarketplace.Key).Read("os", "greeter"));
    }

    [Trait("host", "own")]
    [Fact]
    public async Task TheMarketplaceListsShippedAppsBeforePeoplesListings()
    {
        await using var brain = await StartShipping();
        await Published(brain);

        var listings = await brain.Services.GetRequiredService<MarketplaceView>().List(Ct);

        Assert.Equal([("os", "greeter"), ("intochat", "echo")], listings.Select(listing => (listing.Publisher, listing.Name)));
        Assert.IsType<AppSource.Shipped>(listings[0].Source);
        Assert.IsType<AppSource.Brain>(listings[1].Source);
    }

    [Trait("host", "own")]
    [Fact]
    public async Task UpdatingACopyWhoseShippedAppIsGoneSaysItNoLongerShips()
    {
        await using var brain = await StartShipping();
        var alice = await brain.PersonAsync("alice", Ct);
        using var _ = alice.AsCaller();
        var copy = Mine(brain, alice, "retired");
        await copy.Create(Manifest(), Spec);
        await copy.RecordOrigin(new AppOrigin("os", new AppSource.Shipped(), "retired", "1.0.0"));

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => Installer(brain).Update("retired", Ct));
        Assert.Contains("no longer ships", error.Message, StringComparison.Ordinal);
    }

    [Trait("host", "own")]
    [Fact]
    public async Task ABundleNoLongerInTheCatalogSaysItNoLongerShips()
    {
        await using var brain = await StartShipping();
        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => Installer(brain).Bundle(new AppSource.Shipped(), "os", "retired", Ct));
        Assert.Contains("no longer ships", error.Message, StringComparison.Ordinal);
    }

    [Trait("host", "own")]
    [Fact]
    public async Task NoOnePublishesUnderACatalogVendorName()
    {
        await using var brain = await StartShipping();
        var os = await brain.PersonAsync("os", Ct);
        var app = Mine(brain, os);
        using (os.AsCaller())
        {
            await app.Create(Manifest(), Spec);
            await app.BeginBuild(null);
            await app.CompleteBuild(await app.CurrentBuildAttempt(), new() { ["synapses/echo.cs"] = "// v1" }, null);
            await Assert.ThrowsAsync<UnauthorizedAccessException>(app.Publish);
        }
        Assert.Null(await brain.Get<IMarketplace>(IMarketplace.Key).Read("os", "echo"));
    }

    [Trait("host", "own")]
    [Fact]
    public async Task AnInstalledFeatureIsLoadedIntoTheKernelUnderItsOwner()
    {
        await using var brain = await StartShipping();
        var alice = await brain.PersonAsync("alice", Ct);
        var kernel = brain.Services.GetRequiredService<IntoChat.OS.Framework.IKernel>();
        using var _ = alice.AsCaller();
        await Installer(brain).Install("os", "greeter", Ct);

        Assert.Equal(new IntoChat.OS.Framework.Feature("greeter", "1.2.0", "os"), Assert.Single(await kernel.Features(alice.BrainId, Ct)));

        await Mine(brain, alice, "greeter").Delete();
        Assert.Empty(await kernel.Features(alice.BrainId, Ct));
    }

    [Trait("host", "own")]
    [Fact]
    public async Task ASignalAFeatureDeclaresInASharedFileIsAKernelType()
    {
        await using var brain = await Start();
        var alice = await brain.PersonAsync("alice", Ct);
        using var _ = alice.AsCaller();
        var app = Mine(brain, alice);
        await app.Create(Manifest(), Spec);
        await app.BeginBuild(null);
        await app.CompleteBuild(await app.CurrentBuildAttempt(), new()
        {
            ["echo.cs"] = "// v1",
            ["echoed.cs"] = "namespace Echo;\npublic sealed record Echoed(string Text) : IntoChat.OS.Signal;\n",
        }, null);

        var types = await brain.Services.GetRequiredService<IntoChat.OS.Framework.IKernel>().Types(Ct);
        var echoed = Assert.Single(types, type => type.Name == "Echo.Echoed");
        Assert.Equal((TypeKind.Signal, $"feature:{alice.BrainId}/echo"), (echoed.Kind, echoed.Provider));
        Assert.Equal(["string Text"], echoed.Shape);
    }

    [Trait("host", "own")]
    [Fact]
    public async Task ATypeTwoFeaturesDeclareStaysListedOnceUntilBothAreGone()
    {
        await using var brain = await Start();
        var alice = await brain.PersonAsync("alice", Ct);
        using var _ = alice.AsCaller();
        const string Shared = "namespace Echo;\npublic sealed record Echoed(string Text) : IntoChat.OS.Signal;\n";
        var first = Mine(brain, alice, "echo");
        var second = Mine(brain, alice, "echo-two");
        foreach (var app in new[] { first, second })
        {
            await app.Create(Manifest(), Spec);
            await app.BeginBuild(null);
            await app.CompleteBuild(await app.CurrentBuildAttempt(), new() { ["echo.cs"] = "// v1", ["echoed.cs"] = Shared }, null);
        }
        var kernel = brain.Services.GetRequiredService<IntoChat.OS.Framework.IKernel>();
        async Task<int> Echoed() => (await kernel.Types(Ct)).Count(type => type.Name == "Echo.Echoed");

        Assert.Equal(1, await Echoed());
        await second.Delete();
        Assert.Equal(1, await Echoed());
        await first.Delete();
        Assert.Equal(0, await Echoed());
    }
}
