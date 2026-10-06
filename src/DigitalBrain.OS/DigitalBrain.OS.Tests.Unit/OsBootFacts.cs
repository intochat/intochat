using DigitalBrain;
using DigitalBrain.Apps;
using DigitalBrain.Framework.Enforcement;
using DigitalBrain.Framework.Platform.Identity;
using DigitalBrain.OS;
using DigitalBrain.Testing.Module;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Xunit;

namespace DigitalBrain.OS.Tests.Unit;

public sealed class OsBootFacts
{
    private static readonly string OsScope = BrainScope.Create("account-os", "os").Id;

    [Fact(Timeout = 60_000)]
    public async Task AnEstablishedBrainAlreadyHasThePublishedDefaultsWithTheirOrigin()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await Boot(ct, new OsApp("os", "hello"));
        var released = await SeedPublished(brain, "hello");

        var scope = await Establish(brain);
        var installed = await brain.Get<IApp>(IApp.Key(scope, "hello")).Read();
        Assert.Equal(AppStatus.Live, installed.Status);
        Assert.Equal(released, installed.Version);
        Assert.Equal("os", installed.Origin!.Publisher);
    }

    [Fact(Timeout = 60_000)]
    public async Task ANewPublishedVersionIsOfferedNotForcedOnReactivation()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await Boot(ct, new OsApp("os", "hello"));
        var source = brain.Get<IApp>(IApp.Key(OsScope, "hello"));
        var released = await SeedPublished(brain, "hello");
        var scope = await Establish(brain);
        var app = brain.Get<IApp>(IApp.Key(scope, "hello"));
        await WaitForAsync(app, snapshot => snapshot.Status == AppStatus.Live, ct);

        Orleans.Runtime.RequestContext.Remove(CallerContextStamper.RequestContextKey);
        await source.SaveSpec("## Scenario: Greets\nIt greets twice.", Guid.NewGuid());
        await source.RecordVerdicts([new("Greets", true, "ok")], green: true);
        StampOs();
        await source.Publish();

        StampBrainOwner();
        var neuron = brain.Get<IBrain>(scope);
        await brain.DeactivateAsync(neuron, ct);
        await neuron.Read();
        await Task.Delay(500, ct);
        Assert.Equal(released, (await app.Read()).Version);
    }

    [Fact(Timeout = 60_000)]
    public async Task ADefaultMissingFromTheMarketplaceIsSkippedAndInstalledOnALaterActivation()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await Boot(ct, new OsApp("os", "hello"), new OsApp("os", "later"));
        await SeedPublished(brain, "hello");

        var scope = await Establish(brain);
        await WaitForAsync(brain.Get<IApp>(IApp.Key(scope, "hello")), snapshot => snapshot.Status == AppStatus.Live, ct);
        Assert.Equal(AppStatus.Drafted, (await brain.Get<IApp>(IApp.Key(scope, "later")).Read()).Status);

        await SeedPublished(brain, "later");
        StampBrainOwner();
        var neuron = brain.Get<IBrain>(scope);
        await brain.DeactivateAsync(neuron, ct);
        await neuron.Read();
        await WaitForAsync(brain.Get<IApp>(IApp.Key(scope, "later")), snapshot => snapshot.Status == AppStatus.Live, ct);
    }

    // A fresh deployment activates brains while its default apps are still passing their gates;
    // a default published after activation reaches the brain without waiting for a reactivation.
    [Fact(Timeout = 60_000)]
    public async Task ADefaultPublishedAfterTheBrainActivatedIsInstalledWithoutAReactivation()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await Boot(ct, new OsApp("os", "late"));
        var scope = await Establish(brain);
        Assert.Equal(AppStatus.Drafted, (await brain.Get<IApp>(IApp.Key(scope, "late")).Read()).Status);

        await SeedPublished(brain, "late");

        await WaitForAsync(brain.Get<IApp>(IApp.Key(scope, "late")), snapshot => snapshot.Status == AppStatus.Live, ct);
    }

    // A default is offered once: a person who deletes it keeps it deleted.
    [Fact(Timeout = 60_000)]
    public async Task ADeletedDefaultStaysDeleted()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await Boot(ct, new OsApp("os", "hello"));
        await SeedPublished(brain, "hello");
        var scope = await Establish(brain);
        var app = brain.Get<IApp>(IApp.Key(scope, "hello"));
        await WaitForAsync(app, snapshot => snapshot.Status == AppStatus.Live, ct);

        StampBrainOwner();
        await app.Delete();
        var neuron = brain.Get<IBrain>(scope);
        await brain.DeactivateAsync(neuron, ct);
        await neuron.Read();
        await Task.Delay(1000, ct);

        Assert.Equal(AppStatus.Drafted, (await app.Read()).Status);
    }

    [Fact(Timeout = 60_000)]
    public async Task ADefaultThePersonAlreadyHasIsLeftAlone()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await Boot(ct, new OsApp("os", "hello"));
        await SeedPublished(brain, "hello");
        var scope = BrainScope.Create("acct", "home").Id;
        Orleans.Runtime.RequestContext.Remove(CallerContextStamper.RequestContextKey);
        var own = brain.Get<IApp>(IApp.Key(scope, "hello"));
        await own.Create(new("My hello", "mine", [], [], []), "## Scenario: Mine\nMine.");

        await Establish(brain);
        await Task.Delay(500, ct);
        var after = await own.Read();
        Assert.Equal("My hello", after.Manifest.Title);
        Assert.Null(after.Origin);
    }

    [Fact(Timeout = 60_000)]
    public async Task TheHostIsReadyOnlyOnceEveryDefaultIsPublished()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await Boot(ct, new OsApp("os", "hello"));
        var health = brain.SiloServices.GetRequiredService<HealthCheckService>();

        Assert.Equal(HealthStatus.Unhealthy, (await health.CheckHealthAsync(ct)).Entries["os-defaults"].Status);
        await SeedPublished(brain, "hello");
        Assert.Equal(HealthStatus.Healthy, (await health.CheckHealthAsync(ct)).Entries["os-defaults"].Status);
    }

    private static Task<ModuleBrain> Boot(CancellationToken ct, params OsApp[] defaults) => ModuleTest.Create()
        .WithModule<AppsModule>()
        .ConfigureSilo(silo =>
        {
            silo.Services.RemoveAll<DigitalBrain.Apps.IDeployableRuntime>();
            silo.Services.AddOperatingSystemBoot(os => { foreach (var app in defaults) { os.Apps.Add(app); } });
        })
        .StartAsync(ct);

    private static async Task<string> Establish(ModuleBrain brain)
    {
        var scope = BrainScope.Create("acct", "home").Id;
        StampBrainOwner();
        await brain.AuthorizeCallerAsync();
        await brain.Get<IBrain>(scope).Establish(new("home", "acct"));
        return scope;
    }

    // The os account's copy: created, built, recorded green and published. Building plays the
    // platform (unstamped); publishing speaks for the os account.
    private static async Task<string> SeedPublished(ModuleBrain brain, string name)
    {
        Orleans.Runtime.RequestContext.Remove(CallerContextStamper.RequestContextKey);
        var source = brain.Get<IApp>(IApp.Key(OsScope, name));
        await source.Create(new("Hello", "Shows hello world", [new("open", "Open")], [new("Greeting", "what to say", "hello")], []),
            "## Scenario: Greets\nIt greets.");
        await source.BeginBuild(null);
        await source.CompleteBuild(await source.CurrentBuildAttempt(), new() { ["tests.cs"] = "// t", ["behaviors/hello.cs"] = "// b" }, null);
        await source.RecordVerdicts([new("Greets", true, "ok")], green: true);
        StampOs();
        var published = await source.Publish();
        Orleans.Runtime.RequestContext.Remove(CallerContextStamper.RequestContextKey);
        return published.Version;
    }

    private static void StampOs() => CallerContextStamper.Stamp(new()
    {
        PrincipalId = "os",
        AccountId = "account-os",
        BrainId = "os",
        Kind = CallerKind.Platform,
        StampedBy = TrustedEdge.Platform,
    });

    private static void StampBrainOwner() => CallerContextStamper.Stamp(new()
    {
        PrincipalId = "acct",
        AccountId = "acct",
        BrainId = "home",
        Kind = CallerKind.User,
        StampedBy = TrustedEdge.AuthenticatedHttp,
    });

    // The boot behavior reacts to a signal, so the facts wait for the state it converges on.
    private static async Task<AppSnapshot> WaitForAsync(IApp app, Func<AppSnapshot, bool> done, CancellationToken ct)
    {
        var snapshot = await app.Read();
        var started = TimeProvider.System.GetTimestamp();
        while (!done(snapshot) && TimeProvider.System.GetElapsedTime(started) < TimeSpan.FromSeconds(30))
        {
            await Task.Delay(100, ct);
            snapshot = await app.Read();
        }
        Assert.True(done(snapshot), $"The app did not converge; status {snapshot.Status}, version {snapshot.Version}.");
        return snapshot;
    }
}
