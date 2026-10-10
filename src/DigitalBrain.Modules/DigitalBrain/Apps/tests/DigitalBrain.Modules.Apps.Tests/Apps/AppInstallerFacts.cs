using DigitalBrain.Modules.Apps.Tests;
using DigitalBrain.Modules.Apps.Tests.Runtimes;
using DigitalBrain.Testing.Module;
using Microsoft.Extensions.DependencyInjection;

namespace DigitalBrain.Apps.Tests.Apps;

// Install copies a published version into the caller's brain and remembers where it came from;
// Update replaces the program and keeps what the person chose, until the person edits it.
public sealed class AppInstallerFacts
{
    private const string Spec = "## Scenario: Echoes\nIt echoes.";
    private static readonly CancellationToken Ct = TestContext.Current.CancellationToken;

    private static AppManifest Manifest() =>
        new("Echo", "echoes", [new("ask", "ask it")], [new("Text", "what to say", "hi")], [new("gmail", "gmail")]);

    private static Task<ModuleBrain> Start() => ModuleTest.Create().WithModule<AppsModule>().WithFakeAppRuntime(new()).StartAsync(Ct);

    // A host whose features folder (AppsOptions.Features) holds os/greeter 1.2.0.
    private static Task<ModuleBrain> StartShipping() => ModuleTest.Create()
        .WithModule<AppsModule, AppsOptions>(options => options.Features = Path.Combine(AppContext.BaseDirectory, "FeatureFixture"))
        .WithFakeAppRuntime(new())
        .StartAsync(Ct);

    private static AppInstaller Installer(ModuleBrain brain) => brain.SiloServices.GetRequiredService<AppInstaller>();

    private static IApp Mine(ModuleBrain brain, string principal, string name = "echo") => brain.Get<IApp>(IApp.Key(AppOwner.Scope(principal), name));

    private static async Task<IApp> Published(ModuleBrain brain, string publisher = "intochat")
    {
        Caller.Clear();
        await AppOwner.Own(brain, publisher);
        var app = Mine(brain, publisher);
        await app.Create(Manifest(), Spec);
        await app.BeginBuild(null);
        await app.CompleteBuild(await app.CurrentBuildAttempt(), new() { ["synapses/echo.cs"] = "// v1" }, null);
        AppOwner.Stamp(publisher);
        await app.Publish();
        Caller.Clear();
        return app;
    }

    private static async Task<string> PublishNewVersion(IApp app, string publisher = "intochat")
    {
        Caller.Clear();
        await app.WriteBehavior("synapses/echo.cs", "// v2", Guid.NewGuid());
        AppOwner.Stamp(publisher);
        var version = (await app.Publish()).Version;
        Caller.Clear();
        return version;
    }

    [Fact]
    public async Task InstallCopiesThePublishedVersionLiveWithDefaultSettingsAndRecordsItsOrigin()
    {
        await using var brain = await Start();
        var source = await Published(brain);
        await source.Configure(new() { ["Text"] = "publisher's own" }, new() { ["gmail"] = "publisher-gmail" });

        AppOwner.Stamp("alice");
        var installed = await Installer(brain).Install("intochat", "echo", Ct);
        Caller.Clear();

        Assert.Equal(AppStatus.Live, installed.Status);
        Assert.Equal((await source.ExportPublished()).Version, installed.Version);
        Assert.Equal("hi", installed.Settings["Text"]);
        Assert.Empty(installed.Connections);
        Assert.Equal(new AppOrigin("intochat", new AppSource.Brain("account-intochat", "intochat"), "echo", installed.Version), installed.Origin);
        Assert.False(installed.Edited);
        Assert.Equal(installed.Version, (await Mine(brain, "alice").Read()).Version);
    }

    [Fact]
    public async Task InstallingAnAppNotInTheMarketplaceIsNotFound()
    {
        await using var brain = await Start();
        AppOwner.Stamp("alice");
        await Assert.ThrowsAsync<KeyNotFoundException>(() => Installer(brain).Install("intochat", "echo", Ct));
        Caller.Clear();
    }

    [Fact]
    public async Task InstallingOverAnAppOfTheSameNameIsRefusedAndLeavesItUntouched()
    {
        await using var brain = await Start();
        await Published(brain);
        var own = Mine(brain, "alice");
        await own.Create(Manifest() with { Title = "Alice's echo" }, Spec);

        AppOwner.Stamp("alice");
        await Assert.ThrowsAsync<InvalidOperationException>(() => Installer(brain).Install("intochat", "echo", Ct));
        Caller.Clear();
        var after = await own.Read();
        Assert.Equal("Alice's echo", after.Manifest.Title);
        Assert.Null(after.Origin);
    }

    [Fact]
    public async Task InstallingYourOwnPublishedAppIsRefused()
    {
        await using var brain = await Start();
        await Published(brain);
        AppOwner.Stamp("intochat");
        await Assert.ThrowsAsync<InvalidOperationException>(() => Installer(brain).Install("intochat", "echo", Ct));
        Caller.Clear();
    }

    [Fact]
    public async Task UpdateReplacesTheProgramAndKeepsSettingsAndConnections()
    {
        await using var brain = await Start();
        var source = await Published(brain);
        AppOwner.Stamp("alice");
        await Installer(brain).Install("intochat", "echo", Ct);
        await Mine(brain, "alice").Configure(new() { ["Text"] = "alice's words" }, new() { ["gmail"] = "alice-gmail" });
        var next = await PublishNewVersion(source);

        AppOwner.Stamp("alice");
        var updated = await Installer(brain).Update("echo", Ct);
        Caller.Clear();

        Assert.Equal(next, updated.Version);
        Assert.Equal("// v2", updated.Files["synapses/echo.cs"]);
        Assert.Equal("alice's words", updated.Settings["Text"]);
        Assert.Equal("alice-gmail", updated.Connections["gmail"]);
        Assert.Equal(next, updated.Origin!.Version);
        Assert.False(updated.Edited);
    }

    [Fact]
    public async Task AnEditedCopyRefusesUpdates()
    {
        await using var brain = await Start();
        var source = await Published(brain);
        AppOwner.Stamp("alice");
        await Installer(brain).Install("intochat", "echo", Ct);
        Caller.Clear();
        await Mine(brain, "alice").WriteFile("notes.md", "mine now", Guid.NewGuid());
        await PublishNewVersion(source);

        AppOwner.Stamp("alice");
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => Installer(brain).Update("echo", Ct));
        Caller.Clear();
        Assert.Contains("edits", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task UpdatingWithNothingNewerIsANoOp()
    {
        await using var brain = await Start();
        await Published(brain);
        AppOwner.Stamp("alice");
        var installed = await Installer(brain).Install("intochat", "echo", Ct);
        var again = await Installer(brain).Update("echo", Ct);
        Caller.Clear();
        Assert.Equal(installed.Version, again.Version);
        Assert.Equal(installed.Versions.Length, again.Versions.Length);
    }

    [Fact]
    public async Task UpdatingAnAppThatWasNotInstalledIsRefused()
    {
        await using var brain = await Start();
        await Mine(brain, "alice").Create(Manifest(), Spec);
        AppOwner.Stamp("alice");
        await Assert.ThrowsAsync<InvalidOperationException>(() => Installer(brain).Update("echo", Ct));
        Caller.Clear();
    }

    [Fact]
    public async Task AnEntryShowsInstalledAndAnUpdateOnlyForAnUneditedOlderCopy()
    {
        await using var brain = await Start();
        var source = await Published(brain);
        var marketplace = brain.Get<IMarketplace>(IMarketplace.Key);

        AppOwner.Stamp("alice");
        var before = await Installer(brain).Entry((await marketplace.Read("intochat", "echo"))!, Ct);
        Assert.False(before.Installed);
        Assert.False(before.UpdateAvailable);

        await Installer(brain).Install("intochat", "echo", Ct);
        var current = await Installer(brain).Entry((await marketplace.Read("intochat", "echo"))!, Ct);
        Assert.True(current.Installed);
        Assert.False(current.UpdateAvailable);

        await PublishNewVersion(source);
        AppOwner.Stamp("alice");
        Assert.True((await Installer(brain).Entry((await marketplace.Read("intochat", "echo"))!, Ct)).UpdateAvailable);

        Caller.Clear();
        await Mine(brain, "alice").WriteFile("notes.md", "mine now", Guid.NewGuid());
        AppOwner.Stamp("alice");
        var edited = await Installer(brain).Entry((await marketplace.Read("intochat", "echo"))!, Ct);
        Assert.True(edited.Installed);
        Assert.False(edited.UpdateAvailable);

        // The publisher sees its own listing as installed: the app is theirs.
        AppOwner.Stamp("intochat");
        Assert.True((await Installer(brain).Entry((await marketplace.Read("intochat", "echo"))!, Ct)).Installed);
        Caller.Clear();
    }

    [Fact]
    public async Task AnInstalledCopyKeepsWorkingAfterThePublisherDeletesTheApp()
    {
        await using var brain = await Start();
        var source = await Published(brain);
        AppOwner.Stamp("alice");
        await Installer(brain).Install("intochat", "echo", Ct);
        Caller.Clear();
        await source.Delete();

        Assert.Equal(AppStatus.Live, (await Mine(brain, "alice").Read()).Status);
        AppOwner.Stamp("alice");
        await Assert.ThrowsAsync<KeyNotFoundException>(() => Installer(brain).Update("echo", Ct));
        Caller.Clear();
    }

    [Fact]
    public async Task InstallCopiesAShippedAppFromTheCatalogAndRecordsAShippedOrigin()
    {
        await using var brain = await StartShipping();
        AppOwner.Stamp("alice");
        var installed = await Installer(brain).Install("os", "greeter", Ct);
        var entry = await Installer(brain).Entry((await brain.SiloServices.GetRequiredService<MarketplaceView>().Read("os", "greeter", Ct))!, Ct);
        Caller.Clear();

        Assert.Equal("1.2.0", installed.Version);
        Assert.Equal("// greet\n", installed.Files["greet.cs"]);
        Assert.Equal(new AppOrigin("os", new AppSource.Shipped(), "greeter", "1.2.0"), installed.Origin);
        Assert.True(entry.Installed);
        Assert.False(entry.UpdateAvailable);
        Assert.Null(await brain.Get<IMarketplace>(IMarketplace.Key).Read("os", "greeter"));
    }

    [Fact]
    public async Task TheMarketplaceListsShippedAppsBeforePeoplesListings()
    {
        await using var brain = await StartShipping();
        await Published(brain);

        var listings = await brain.SiloServices.GetRequiredService<MarketplaceView>().List(Ct);

        Assert.Equal([("os", "greeter"), ("intochat", "echo")], listings.Select(listing => (listing.Publisher, listing.Name)));
        Assert.IsType<AppSource.Shipped>(listings[0].Source);
        Assert.IsType<AppSource.Brain>(listings[1].Source);
    }

    [Fact]
    public async Task UpdatingACopyWhoseShippedAppIsGoneSaysItNoLongerShips()
    {
        await using var brain = await StartShipping();
        var copy = Mine(brain, "alice", "retired");
        await copy.Create(Manifest(), Spec);
        await copy.RecordOrigin(new AppOrigin("os", new AppSource.Shipped(), "retired", "1.0.0"));

        AppOwner.Stamp("alice");
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => Installer(brain).Update("retired", Ct));
        Caller.Clear();
        Assert.Contains("no longer ships", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ABundleNoLongerInTheCatalogSaysItNoLongerShips()
    {
        await using var brain = await StartShipping();
        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => Installer(brain).Bundle(new AppSource.Shipped(), "os", "retired", Ct));
        Assert.Contains("no longer ships", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task NoOnePublishesUnderACatalogVendorName()
    {
        await using var brain = await StartShipping();
        var app = Mine(brain, "os");
        await app.Create(Manifest(), Spec);
        await app.BeginBuild(null);
        await app.CompleteBuild(await app.CurrentBuildAttempt(), new() { ["synapses/echo.cs"] = "// v1" }, null);

        AppOwner.Stamp("os");
        await Assert.ThrowsAsync<UnauthorizedAccessException>(app.Publish);
        Caller.Clear();
        Assert.Null(await brain.Get<IMarketplace>(IMarketplace.Key).Read("os", "echo"));
    }

    [Fact]
    public async Task AnInstalledFeatureIsLoadedIntoTheKernelUnderItsOwner()
    {
        await using var brain = await StartShipping();
        AppOwner.Stamp("alice");
        await Installer(brain).Install("os", "greeter", Ct);
        Caller.Clear();

        var kernel = brain.SiloServices.GetRequiredService<DigitalBrain.Framework.IKernel>();
        Assert.Equal(new DigitalBrain.Framework.Feature("greeter", "1.2.0", "os"), Assert.Single(await kernel.Features(AppOwner.Scope("alice"), Ct)));

        await Mine(brain, "alice", "greeter").Delete();
        Assert.Empty(await kernel.Features(AppOwner.Scope("alice"), Ct));
    }

    [Fact]
    public async Task ASignalAFeatureDeclaresInASharedFileIsAKernelType()
    {
        await using var brain = await Start();
        var app = Mine(brain, "alice");
        await app.Create(Manifest(), Spec);
        await app.BeginBuild(null);
        await app.CompleteBuild(await app.CurrentBuildAttempt(), new()
        {
            ["echo.cs"] = "// v1",
            ["echoed.cs"] = "namespace Echo;\npublic sealed record Echoed(string Text) : DigitalBrain.Signal;\n",
        }, null);

        AppOwner.Stamp("alice");
        var types = await brain.SiloServices.GetRequiredService<DigitalBrain.Framework.IKernel>().Types(Ct);
        Caller.Clear();
        var echoed = Assert.Single(types, type => type.Name == "Echo.Echoed");
        Assert.Equal((TypeKind.Signal, $"feature:{AppOwner.Scope("alice")}/echo"), (echoed.Kind, echoed.Provider));
        Assert.Equal(["string Text"], echoed.Shape);
    }

    [Fact]
    public async Task ATypeTwoFeaturesDeclareStaysListedOnceUntilBothAreGone()
    {
        await using var brain = await Start();
        const string Shared = "namespace Echo;\npublic sealed record Echoed(string Text) : DigitalBrain.Signal;\n";
        var first = Mine(brain, "alice", "echo");
        var second = Mine(brain, "alice", "echo-two");
        foreach (var app in new[] { first, second })
        {
            await app.Create(Manifest(), Spec);
            await app.BeginBuild(null);
            await app.CompleteBuild(await app.CurrentBuildAttempt(), new() { ["echo.cs"] = "// v1", ["echoed.cs"] = Shared }, null);
        }
        var kernel = brain.SiloServices.GetRequiredService<DigitalBrain.Framework.IKernel>();
        async Task<int> Echoed()
        {
            AppOwner.Stamp("alice");
            var count = (await kernel.Types(Ct)).Count(type => type.Name == "Echo.Echoed");
            Caller.Clear();
            return count;
        }

        Assert.Equal(1, await Echoed());
        await second.Delete();
        Assert.Equal(1, await Echoed());
        await first.Delete();
        Assert.Equal(0, await Echoed());
    }
}
