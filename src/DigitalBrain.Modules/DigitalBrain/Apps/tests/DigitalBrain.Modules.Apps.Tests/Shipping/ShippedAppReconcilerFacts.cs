using DigitalBrain;
using DigitalBrain.Apps.Signals;
using DigitalBrain.Modules.Apps.Tests;
using DigitalBrain.Modules.Apps.Tests.Runtimes;
using DigitalBrain.Testing.Module;
using Microsoft.Extensions.DependencyInjection;

namespace DigitalBrain.Apps.Tests.Shipping;

// Every brain activation brings the brain's unedited shipped apps to the catalog's version; a copy the
// person edited, or that came from a person's brain, stays exactly as it is.
public sealed class ShippedAppReconcilerFacts : IDisposable
{
    private static readonly CancellationToken Ct = TestContext.Current.CancellationToken;
    private readonly string _root = Directory.CreateTempSubdirectory("reconciler-").FullName;

    public ShippedAppReconcilerFacts()
    {
        Directory.CreateDirectory(Path.Combine(_root, "Greeter", "synapses"));
        File.WriteAllText(Path.Combine(_root, "Greeter", "app.json"),
            """{"name":"greeter","title":"Greeter","description":"Greets.","version":"1.2.0","operations":[{"name":"ask","description":"Greet"}],"settings":[]}""");
        File.WriteAllText(Path.Combine(_root, "Greeter", "synapses", "greet.cs"), "// greet\n");
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    // A host that ships os/greeter from a writable development folder, so a test can bump the catalog.
    private Task<ModuleBrain> Start() => ModuleTest.Create().WithModule<AppsModule>()
        .WithFakeAppRuntime(new())
        .ConfigureSilo(silo =>
        {
            silo.Services.AddSingleton<IShippedAppSource>(new DirectoryShippedAppSource(_root, "os", development: true));
            silo.Services.AddSingleton<ShippedCatalog>();
        })
        .StartAsync(Ct);

    private static async Task<IApp> Installed(ModuleBrain brain)
    {
        AppOwner.Stamp("alice");
        await brain.SiloServices.GetRequiredService<AppInstaller>().Install("os", "greeter", Ct);
        Caller.Clear();
        return brain.Get<IApp>(IApp.Key(AppOwner.Scope("alice"), "greeter"));
    }

    // Writes a newer bundle back to the folder and reloads the catalog, so the catalog outruns the copy.
    private static async Task ShipNewerVersion(ModuleBrain brain, string version = "2.0.0")
    {
        var catalog = brain.SiloServices.GetRequiredService<ShippedCatalog>();
        var current = (await catalog.Find("os", "greeter"))!;
        await catalog.SaveToSource("os", current with
        {
            Version = version,
            Files = new(current.Files, StringComparer.Ordinal) { ["synapses/greet.cs"] = "// greet v2\n" },
        });
    }

    private static IShippedAppReconciler Reconciler(ModuleBrain brain)
        => brain.Get<IShippedAppReconciler>(IShippedAppReconciler.Key(AppOwner.Scope("alice")));

    [Fact(Timeout = 60_000)]
    public async Task ReconcilingPutsAnOlderUneditedShippedAppOnTheCatalogVersion()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await Start();
        var copy = await Installed(brain);
        await ShipNewerVersion(brain);

        await Reconciler(brain).Reconcile(ct);

        var after = await copy.Read();
        Assert.Equal("2.0.0", after.Version);
        Assert.Equal("// greet v2\n", after.Files["synapses/greet.cs"]);
        Assert.Equal("2.0.0", after.Origin!.Version);
        Assert.IsType<AppSource.Shipped>(after.Origin.Source);
        Assert.False(after.Edited);
    }

    [Fact(Timeout = 60_000)]
    public async Task ReconcilingLeavesAnEditedCopyAlone()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await Start();
        var copy = await Installed(brain);
        await copy.WriteFile("notes.md", "mine now", Guid.NewGuid());
        await ShipNewerVersion(brain);

        await Reconciler(brain).Reconcile(ct);

        var after = await copy.Read();
        Assert.True(after.Edited);
        Assert.Equal("1.2.1", after.Version);
        Assert.Equal("// greet\n", after.Files["synapses/greet.cs"]);
        Assert.Equal("1.2.0", after.Origin!.Version);
    }

    [Fact(Timeout = 60_000)]
    public async Task ReconcilingLeavesAThirdPartyOriginAlone()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await Start();
        var app = brain.Get<IApp>(IApp.Key(AppOwner.Scope("alice"), "from-bob"));
        await app.Create(new AppManifest("FromBob", "bob's", [new("ask", "ask")], [], []), "## Scenario: Answers\nIt answers.");
        await app.RecordOrigin(new AppOrigin("bob", new AppSource.Brain("account-bob", "bob"), "from-bob", "1.0.0"));
        await ShipNewerVersion(brain);
        var before = (await app.Read()).Version;

        await Reconciler(brain).Reconcile(ct);

        var after = await app.Read();
        Assert.Equal(before, after.Version);
        Assert.IsType<AppSource.Brain>(after.Origin!.Source);
    }

    [Fact(Timeout = 60_000)]
    public async Task ReconcilingPublishesAppReconciledNamingTheVersions()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await Start();
        var copy = await Installed(brain);
        await ShipNewerVersion(brain);
        var reconciler = Reconciler(brain);
        await using var reconciled = await brain.SubscribeAsync<AppReconciled>(reconciler, ct);
        await using var signals = reconciled.ReadAllAsync(ct).GetAsyncEnumerator(ct);

        await reconciler.Reconcile(ct);

        Assert.True(await signals.MoveNextAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10), ct));
        Assert.Equal(("greeter", "1.2.0", "2.0.0"), (signals.Current.Name, signals.Current.From, signals.Current.To));
    }

    [Fact(Timeout = 60_000)]
    public async Task EveryBrainActivationReconcilesTheBrain()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await Start();
        var kernel = brain.Get<IKernel>(AppOwner.Scope("alice"));
        await kernel.Create(new Owner("account-alice"));
        var copy = await Installed(brain);
        await ShipNewerVersion(brain);

        await brain.DeactivateAsync(kernel, ct);
        await kernel.Read();

        while ((await copy.Read()).Version != "2.0.0") { await Task.Delay(50, ct); }
        Assert.Equal("2.0.0", (await copy.Read()).Version);
    }
}
