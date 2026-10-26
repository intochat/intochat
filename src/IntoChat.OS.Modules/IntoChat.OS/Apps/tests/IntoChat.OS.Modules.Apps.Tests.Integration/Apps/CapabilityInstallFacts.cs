using IntoChat.OS.Connections.Platform.Connections;
using IntoChat.OS.Integrations;
using IntoChat.OS.Modules.Apps.Tests.Integration.Runtimes;
using IntoChat.OS.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace IntoChat.OS.Modules.Apps.Tests.Apps;

// 05-10 (D-10, D-14): an app that requires a capability installs only into a brain where a connected connection's
// module provides it. Install, the publication (fork) path and Update all refuse before any state is created.
public sealed class CapabilityInstallFacts
{
    private const string Refusal = "Files needs storage@1, which no connection in this brain provides.";
    private static readonly CancellationToken Ct = TestContext.Current.CancellationToken;
    private static readonly AppRequirements Storage = new(Capabilities: ["storage@1"]);

    private static Task<ITestBrain> Start() => TestBrain.Create()
        .WithModule<AppsModule>()
        .WithModule<FixtureConnectorsModule>()
        .WithFakeAppRuntime(new())
        .StartAsync(Ct);

    private static AppManifest Files(AppRequirements? requires) => new("Files", "browses", [new("open", "open it")], [],
        requires?.Capabilities is { Length: > 0 } ? [new("drive", Capability: "storage@1")] : [], Requires: requires);

    private static async Task<IApp> Publish(ITestBrain brain, Person publisher, AppRequirements? requires)
    {
        var app = brain.Get<IApp>(IApp.Key(publisher.BrainId, "files"));
        using var _ = publisher.AsCaller();
        await app.Create(Files(requires), "## Scenario: Browses\nIt browses.");
        await app.BeginBuild(null);
        await app.CompleteBuild(await app.CurrentBuildAttempt(), new() { ["synapses/files.cs"] = "// v1" }, null);
        await app.Publish();
        return app;
    }

    private static async Task Connect(ITestBrain brain, Person person, string kind, string id, string credential = "fixture-value-0001")
    {
        using var _ = person.AsCaller();
        var connection = brain.Get<IConnection>(ConnectionKeys.For(person.BrainId, kind, id));
        await connection.Connect("api-key", credential);
        await connection.Probe();
    }

    private static AppInstaller Installer(ITestBrain brain) => brain.Services.GetRequiredService<AppInstaller>();

    private static Task<AppSnapshot> Copy(ITestBrain brain, Person person) => brain.Get<IApp>(IApp.Key(person.BrainId, "files")).Read();

    [Trait("host", "own")]
    [Fact]
    public async Task InstallIsRefusedWhenNoConnectionProvidesTheCapability()
    {
        await using var brain = await Start();
        await Publish(brain, await brain.PersonAsync("intochat", Ct), Storage);
        var alice = await brain.PersonAsync("alice", Ct);

        using var _ = alice.AsCaller();
        var refused = await Assert.ThrowsAsync<InvalidOperationException>(() => brain.InstallLatest("intochat", "files", Ct));
        Assert.Equal(Refusal, refused.Message);
        Assert.Equal("", (await Copy(brain, alice)).Manifest.Title);
    }

    [Trait("host", "own")]
    [Fact]
    public async Task AConnectionThatIsNotConnectedSatisfiesNothing()
    {
        await using var brain = await Start();
        await Publish(brain, await brain.PersonAsync("intochat", Ct), Storage);
        var alice = await brain.PersonAsync("alice", Ct);
        await Connect(brain, alice, "drive", "home", "fixture-value-expired");

        using var _ = alice.AsCaller();
        var refused = await Assert.ThrowsAsync<InvalidOperationException>(() => brain.InstallLatest("intochat", "files", Ct));
        Assert.Equal(Refusal, refused.Message);
        Assert.Equal("", (await Copy(brain, alice)).Manifest.Title);
    }

    [Trait("host", "own")]
    [Fact]
    public async Task AConnectedStorageConnectionLetsTheAppInstallAndBindItsSlotWithoutGrants()
    {
        await using var brain = await Start();
        await Publish(brain, await brain.PersonAsync("intochat", Ct), Storage);
        var alice = await brain.PersonAsync("alice", Ct);
        await Connect(brain, alice, "drive", "home");

        using var _ = alice.AsCaller();
        var outcome = await Installer(brain).Install(
            new(new("intochat", "files"), AppConsent.Of(Storage), Guid.Empty, null, new() { ["drive"] = "home" }), Ct);

        Assert.Equal(InstallStatus.Ok, outcome.Status);
        var installed = await Copy(brain, alice);
        Assert.Equal("home", installed.Connections["drive"]);
        Assert.Equal(Storage, installed.Origin!.Consented);
        // A requirement is a need, not a grant.
        Assert.Empty(await UnifiedInstallFacts.Granted(brain, alice, "files"));
    }

    [Trait("host", "own")]
    [Fact]
    public async Task AModuleWithoutTheCapabilityCannotBindACapabilitySlot()
    {
        await using var brain = await Start();
        await Publish(brain, await brain.PersonAsync("intochat", Ct), Storage);
        var alice = await brain.PersonAsync("alice", Ct);
        await Connect(brain, alice, "drive", "home");
        await Connect(brain, alice, "mailbox", "inbox");

        using var _ = alice.AsCaller();
        var outcome = await Installer(brain).Install(
            new(new("intochat", "files"), AppConsent.Of(Storage), Guid.Empty, null, new() { ["drive"] = "inbox" }), Ct);

        Assert.Equal(InstallStatus.Refused, outcome.Status);
        Assert.Equal("Choose a storage@1 connection for drive.", outcome.Message);
    }

    [Trait("host", "own")]
    [Fact]
    public async Task ThePublicationPathRefusesBeforeTheCopyIsCreated()
    {
        await using var brain = await Start();
        var publisher = await brain.PersonAsync("intochat", Ct);
        var app = await Publish(brain, publisher, Storage);
        AppPublicationRef reference;
        using (publisher.AsCaller()) { reference = Assert.Single(await app.PublishedReleases()).Reference; }
        var alice = await brain.PersonAsync("alice", Ct);

        using var _ = alice.AsCaller();
        var refused = await Assert.ThrowsAsync<InvalidOperationException>(() => brain.InstallRelease(reference, "files", Guid.NewGuid(), Ct));
        Assert.Equal(Refusal, refused.Message);
        Assert.Equal("", (await Copy(brain, alice)).Manifest.Title);
    }

    [Trait("host", "own")]
    [Fact]
    public async Task AnUpdateThatAddsAnUnsatisfiedCapabilityKeepsTheInstalledVersion()
    {
        await using var brain = await Start();
        var publisher = await brain.PersonAsync("intochat", Ct);
        var app = await Publish(brain, publisher, null);
        var alice = await brain.PersonAsync("alice", Ct);
        string before;
        using (alice.AsCaller()) { before = (await brain.InstallLatest("intochat", "files", Ct)).Version; }

        using (publisher.AsCaller())
        {
            await app.SaveManifest(Files(Storage), Guid.NewGuid());
            await app.Publish();
        }

        using var _ = alice.AsCaller();
        var refused = await Assert.ThrowsAsync<InvalidOperationException>(() => Installer(brain).Update(new("files", AppConsent.Of(Storage)), Ct));
        Assert.Equal(Refusal, refused.Message);
        var kept = await Copy(brain, alice);
        Assert.Equal(before, kept.Version);
        Assert.Null(kept.Manifest.Requires);
    }

    [Trait("host", "own")]
    [Fact]
    public async Task AnAppRequiringNothingInstallsAsBefore()
    {
        await using var brain = await Start();
        await Publish(brain, await brain.PersonAsync("intochat", Ct), null);
        var alice = await brain.PersonAsync("alice", Ct);

        using var _ = alice.AsCaller();
        Assert.Equal(AppStatus.Live, (await brain.InstallLatest("intochat", "files", Ct)).Status);
    }

    [Fact]
    public void OnlyAConnectedConnectionWhoseModuleDeclaresTheCapabilitySatisfiesIt()
    {
        static ConnectionSummary Summary(string status, params string[] capabilities)
            => new("drive", "home", "key", status, null, null) { Provides = capabilities };
        Assert.True(ConnectedModuleSatisfaction.Satisfies([Summary("connected", "storage")], "storage@1"));
        Assert.True(ConnectedModuleSatisfaction.Satisfies([Summary("connected", "storage@1")], "storage@1"));
        Assert.False(ConnectedModuleSatisfaction.Satisfies([Summary("connected", "storage")], "storage@2"));
        Assert.False(ConnectedModuleSatisfaction.Satisfies([Summary("pending", "storage")], "storage@1"));
        Assert.False(ConnectedModuleSatisfaction.Satisfies([Summary("connected")], "storage@1"));
        Assert.False(ConnectedModuleSatisfaction.Satisfies([], "storage@1"));
    }
}
