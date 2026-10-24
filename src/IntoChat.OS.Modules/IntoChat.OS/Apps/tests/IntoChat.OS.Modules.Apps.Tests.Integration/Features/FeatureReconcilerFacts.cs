using IntoChat.OS;
using IntoChat.OS.Apps.Signals;
using IntoChat.OS.Framework.Platform.Identity;
using IntoChat.OS.Modules.Apps.Tests.Integration;
using IntoChat.OS.Modules.Apps.Tests.Integration.Runtimes;
using IntoChat.OS.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;

namespace IntoChat.OS.Apps.Tests.Features;

// Every brain activation brings the brain's unedited features to the features folder's version; a copy the
// person edited, or that came from a person's brain, stays exactly as it is.
public sealed class FeatureReconcilerFacts : IDisposable
{
    private static readonly CancellationToken Ct = TestContext.Current.CancellationToken;
    private readonly string _root = Directory.CreateTempSubdirectory("reconciler-").FullName;

    public FeatureReconcilerFacts()
    {
        Directory.CreateDirectory(Path.Combine(_root, "os", "Greeter"));
        File.WriteAllText(Path.Combine(_root, "os", "Greeter", "app.cs"),
            Declarations.Of("greeter", "1.2.0", "Greeter", "Greets.", [new("ask", "Greet")]));
        File.WriteAllText(Path.Combine(_root, "os", "Greeter", "greet.cs"), "// greet\n");
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    // A host whose writable features folder holds os/greeter, so a test can bump the catalog.
    private Task<ITestBrain> Start(FakeAppRuntime? runtime = null) => TestBrain.Create()
        .WithModule<AppsModule, AppsOptions>(options => options.Features = _root)
        .WithFakeAppRuntime(runtime ?? new())
        // The test host runs as "Testing"; the features folder is writable only in development.
        .ConfigureServices(services => services.AddSingleton<IHostEnvironment>(new Development()))
        .StartAsync(Ct);

    private static async Task<(Person Alice, IApp Copy)> Installed(ITestBrain brain)
    {
        var alice = await brain.PersonAsync("alice", Ct);
        using (alice.AsCaller()) { await brain.InstallLatest("os", "greeter", Ct); }
        return (alice, brain.Get<IApp>(IApp.Key(alice.BrainId, "greeter")));
    }

    // Writes a newer bundle back to the folder and reloads the catalog, so the catalog outruns the copy.
    private static async Task ShipNewerVersion(ITestBrain brain, string version = "2.0.0")
    {
        var catalog = brain.Services.GetRequiredService<FeatureCatalog>();
        var current = (await catalog.Find("os", "greeter"))!;
        await catalog.SaveToSource("os", current with
        {
            Version = version,
            Files = new(current.Files, StringComparer.Ordinal) { ["greet.cs"] = "// greet v2\n" },
        });
    }

    private static IFeatureReconciler Reconciler(ITestBrain brain, Person person)
        => brain.Get<IFeatureReconciler>(IFeatureReconciler.Key(person.BrainId));

    // Activation never widens: a shipped release that asks for more than the copy's consent is not loaded until the person consents.
    [Trait("host", "own")]
    [Fact]
    public async Task ReconcilingNeverWidensAShippedApp()
    {
        await using var brain = await Start();
        var (alice, copy) = await Installed(brain);
        var catalog = brain.Services.GetRequiredService<FeatureCatalog>();
        var current = (await catalog.Find("os", "greeter"))!;
        await catalog.SaveToSource("os", current with
        {
            Version = "2.0.0",
            Manifest = current.Manifest with { Requires = new(Connectors: ["mailbox"]) },
        });

        using (alice.AsCaller()) { await Reconciler(brain, alice).Reconcile(Ct); }

        Assert.Equal("1.2.0", (await copy.Read()).Version);
    }

    [Trait("host", "own")]
    [Fact]
    public async Task ADeletedFeatureAppCanBeReinstalledAndIsAttachedToItsOrigin()
    {
        await using var brain = await Start();
        var (alice, copy) = await Installed(brain);
        using (alice.AsCaller())
        {
            await copy.Delete();
            var again = await brain.InstallLatest("os", "greeter", Ct);
            Assert.Equal(new AppOrigin("os", new AppSource.Shipped(), "greeter", "1.2.0"), again.Origin);
            Assert.False(again.Edited);
        }
    }

    [Trait("host", "own")]
    [Fact]
    public async Task AnEditedShippedAppDeletedThenReinstalledIsFreshAndUnedited()
    {
        await using var brain = await Start();
        var (alice, copy) = await Installed(brain);
        using (alice.AsCaller())
        {
            await copy.WriteBehavior("greet.cs", "// mine\n", Guid.NewGuid());
            Assert.True((await copy.Read()).Edited);
            await copy.Delete();
            Assert.Empty((await copy.Read()).Manifest.Title);

            var again = await brain.InstallLatest("os", "greeter", Ct);

            Assert.False(again.Edited);
            Assert.Equal("// greet\n", again.Files["greet.cs"]);
            Assert.Equal("1.2.0", again.Origin!.Version);
        }
    }

    [Trait("host", "own")]
    [Fact]
    public async Task ReconcilingPutsAnOlderUneditedShippedAppOnTheCatalogVersion()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await Start();
        var (alice, copy) = await Installed(brain);
        await ShipNewerVersion(brain);

        await Reconciler(brain, alice).Reconcile(ct);

        var after = await copy.Read();
        Assert.Equal("2.0.0", after.Version);
        Assert.Equal("// greet v2\n", after.Files["greet.cs"]);
        Assert.Equal("2.0.0", after.Origin!.Version);
        Assert.IsType<AppSource.Shipped>(after.Origin.Source);
        Assert.False(after.Edited);
    }

    [Trait("host", "own")]
    [Fact]
    public async Task ReconcilingLeavesAnEditedCopyAlone()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await Start();
        var (alice, copy) = await Installed(brain);
        using (alice.AsCaller()) { await copy.WriteFile("notes.md", "mine now", Guid.NewGuid()); }
        await ShipNewerVersion(brain);

        await Reconciler(brain, alice).Reconcile(ct);

        var after = await copy.Read();
        Assert.True(after.Edited);
        Assert.Equal("1.2.1", after.Version);
        Assert.Equal("// greet\n", after.Files["greet.cs"]);
        Assert.Equal("1.2.0", after.Origin!.Version);
    }

    [Trait("host", "own")]
    [Fact]
    public async Task ReconcilingLeavesAThirdPartyOriginAlone()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await Start();
        var alice = await brain.PersonAsync("alice", ct);
        var app = brain.Get<IApp>(IApp.Key(alice.BrainId, "from-bob"));
        await app.Create(new AppManifest("FromBob", "bob's", [new("ask", "ask")], [], []), "## Scenario: Answers\nIt answers.");
        await app.RecordOrigin(new AppOrigin("bob", new AppSource.Brain("account-bob", "bob"), "from-bob", "1.0.0"));
        await ShipNewerVersion(brain);
        var before = (await app.Read()).Version;

        await Reconciler(brain, alice).Reconcile(ct);

        var after = await app.Read();
        Assert.Equal(before, after.Version);
        Assert.IsType<AppSource.Brain>(after.Origin!.Source);
    }

    [Trait("host", "own")]
    [Fact]
    public async Task ReconcilingPublishesAppReconciledNamingTheVersions()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await Start();
        var (alice, _) = await Installed(brain);
        await ShipNewerVersion(brain);
        var reconciler = Reconciler(brain, alice);
        await using var reconciled = await brain.Observe<AppReconciled>(reconciler, ct);

        await reconciler.Reconcile(ct);

        var signal = await reconciled.NextAsync(ct: ct);
        Assert.Equal(("greeter", "1.2.0", "2.0.0"), (signal.Name, signal.From, signal.To));
    }

    // A circle brain gets no OS default, but the room it was born with is a feature like any other (R125, T26-13).
    [Trait("host", "own")]
    [Fact]
    public async Task ReconcilingPutsACircleBrainsShippedAppOnTheCatalogVersion()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await Start();
        var alice = await brain.PersonAsync("alice", ct);
        var circle = (await brain.Services.GetRequiredService<IIdentityClient>()
            .CreateBrain(alice.AccountId, alice.PrincipalId, Guid.NewGuid().ToString("N"), "friends", BrainKind.Circle)).Member.BrainId;
        var shipped = (await brain.Services.GetRequiredService<FeatureCatalog>().Find("os", "greeter"))!;
        await brain.Services.GetRequiredService<FeatureLoader>()
            .Load(circle, "greeter", shipped, new AppOrigin("os", new AppSource.Shipped(), "greeter", shipped.Version), ct);
        await ShipNewerVersion(brain);

        await brain.Get<IFeatureReconciler>(IFeatureReconciler.Key(circle)).Reconcile(ct);

        var after = await brain.Get<IApp>(IApp.Key(circle, "greeter")).Read();
        Assert.Equal(("2.0.0", "2.0.0"), (after.Version, after.Origin!.Version));
        Assert.Equal("// greet v2\n", after.Files["greet.cs"]);
    }

    [Trait("host", "own")]
    [Fact]
    public async Task EveryBrainActivationReconcilesTheBrain()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await Start();
        var (alice, copy) = await Installed(brain);
        var kernel = brain.Get<IBrain>(alice.BrainId);
        await ShipNewerVersion(brain);
        await using var changes = await brain.Observe<AppChanged>(copy, ct);

        await brain.DeactivateAsync(kernel, ct);
        await kernel.Read();

        await changes.NextAsync(change => change.Version == "2.0.0", ct);
    }

    private static readonly AppOrigin Shipped = new("os", new AppSource.Shipped(), "greeter", "1.2.0");

    // The runtime outlasting the caller (Orleans' response timeout inside Assign) and the runtime
    // refusing look the same to the app: the deploy did not confirm.
    private static async Task<(Person Alice, IApp Copy)> InstalledWhileTheRuntimeTimesOut(ITestBrain brain, FakeAppRuntime runtime)
    {
        var alice = await brain.PersonAsync("alice", Ct);
        runtime.Refuse = _ => new TimeoutException("Response did not arrive on time in 00:00:30");
        using (alice.AsCaller())
        { await Assert.ThrowsAsync<TimeoutException>(() => brain.InstallLatest("os", "greeter", Ct)); }
        runtime.Refuse = null;
        return (alice, brain.Get<IApp>(IApp.Key(alice.BrainId, "greeter")));
    }

    [Trait("host", "own")]
    [Fact]
    public async Task AnInstallWhoseDeployTimesOutKeepsItsOriginAndIsNotLive()
    {
        var runtime = new FakeAppRuntime();
        await using var brain = await Start(runtime);
        var (alice, copy) = await InstalledWhileTheRuntimeTimesOut(brain, runtime);

        using (alice.AsCaller())
        {
            var held = await copy.Read();
            Assert.Equal(Shipped, held.Origin);
            Assert.Equal("1.2.0", held.Version);
            Assert.False(held.Edited);
            Assert.Equal(AppStatus.Failed, held.Status);
            Assert.Contains(held.Activity, line => line.StartsWith("deploy failed:", StringComparison.Ordinal));
        }
    }

    [Trait("host", "own")]
    [Fact]
    public async Task TheNextUpdateAfterATimedOutDeployCompletesIt()
    {
        var runtime = new FakeAppRuntime();
        await using var brain = await Start(runtime);
        var (alice, copy) = await InstalledWhileTheRuntimeTimesOut(brain, runtime);

        using (alice.AsCaller())
        {
            var updated = await brain.UpdateApp("greeter", Ct);
            Assert.Equal(AppStatus.Live, updated.Status);
            Assert.Equal(Shipped, updated.Origin);
        }
        Assert.Single(runtime.Deployed);
    }

    [Trait("host", "own")]
    [Fact]
    public async Task AReconcileRecoversADefaultWhoseInstallTimedOut()
    {
        var runtime = new FakeAppRuntime();
        await using var brain = await Start(runtime);
        var (alice, copy) = await InstalledWhileTheRuntimeTimesOut(brain, runtime);

        await Reconciler(brain, alice).Reconcile(Ct);

        using (alice.AsCaller())
        {
            var reconciled = await copy.Read();
            Assert.Equal(AppStatus.Live, reconciled.Status);
            Assert.Equal(Shipped, reconciled.Origin);
            Assert.False(reconciled.Edited);
        }
        Assert.Single(runtime.Deployed);
    }

    private sealed class Development : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = Environments.Development;
        public string ApplicationName { get; set; } = "tests";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
