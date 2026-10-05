using DigitalBrain;
using DigitalBrain.Apps;
using DigitalBrain.Enforcement;
using DigitalBrain.Framework.Enforcement;
using DigitalBrain.Framework.Platform.Identity;
using DigitalBrain.OS;
using DigitalBrain.Testing.Module;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

namespace DigitalBrain.OS.Tests.Unit;

public sealed class OsBootFacts
{
    [Fact(Timeout = 60_000)]
    public async Task EstablishingABrainImportsTheOsAppsAndReactivationUpgradesThem()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await ModuleTest.Create()
            .WithModule<AppsModule>()
            .ConfigureSilo(silo => { silo.Services.RemoveAll<DigitalBrain.Apps.IDeployableRuntime>(); silo.Services.AddOperatingSystemBoot(os => os.Apps.Add(new("hello"))); })
            .StartAsync(ct);
        var os = await RegisterOs(brain, ct);

        var source = await SeedGreen(brain, os, "hello");
        var first = (await source.Read()).Version;

        var scope = BrainScope.Create("acct", "home").Id;
        StampBrainOwner();
        await brain.AuthorizeCallerAsync();
        var neuron = brain.Get<IBrain>(scope);
        await neuron.Establish(new("home", "acct"));
        var app = brain.Get<IApp>(IApp.Key(scope, "hello"));
        var installed = await WaitForAsync(app, snapshot => snapshot.Status == AppStatus.Live, ct);
        Assert.Equal(first, installed.Version);

        // A new green version on the os side re-imports on the next activation, keeping settings.
        await app.Configure(new() { ["Greeting"] = "mine" }, new());
        Orleans.Runtime.RequestContext.Remove(CallerContextStamper.RequestContextKey);
        await source.SaveSpec("## Scenario: Greets\nIt greets twice.", Guid.NewGuid());
        await source.RecordVerdicts([new("Greets", true, "ok")], green: true);
        var next = (await source.Read()).Version;
        Assert.NotEqual(first, next);
        StampBrainOwner();
        await brain.DeactivateAsync(neuron, ct);
        await neuron.Read();
        var upgraded = await WaitForAsync(app, snapshot => snapshot.Version == next, ct);
        Assert.Equal(AppStatus.Live, upgraded.Status);
        Assert.Equal("mine", upgraded.Settings["Greeting"]);
    }

    [Fact(Timeout = 60_000)]
    public async Task AnUnshippedOsAppIsSkippedAndPickedUpOnTheNextActivation()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await ModuleTest.Create()
            .WithModule<AppsModule>()
            .ConfigureSilo(silo =>
            {
                silo.Services.RemoveAll<DigitalBrain.Apps.IDeployableRuntime>();
                silo.Services.AddOperatingSystemBoot(os =>
                {
                    os.Apps.Add(new("hello"));
                    os.Apps.Add(new("later"));
                });
            })
            .StartAsync(ct);
        var os = await RegisterOs(brain, ct);

        await SeedGreen(brain, os, "hello");

        var scope = BrainScope.Create("acct", "home").Id;
        StampBrainOwner();
        await brain.AuthorizeCallerAsync();
        var neuron = brain.Get<IBrain>(scope);
        await neuron.Establish(new("home", "acct"));
        await WaitForAsync(brain.Get<IApp>(IApp.Key(scope, "hello")), snapshot => snapshot.Status == AppStatus.Live, ct);
        Assert.Equal(AppStatus.Drafted, (await brain.Get<IApp>(IApp.Key(scope, "later")).Read()).Status);

        await SeedGreen(brain, os, "later");
        StampBrainOwner();
        await brain.DeactivateAsync(neuron, ct);
        await neuron.Read();
        await WaitForAsync(brain.Get<IApp>(IApp.Key(scope, "later")), snapshot => snapshot.Status == AppStatus.Live, ct);
    }

    private static async Task<PublisherBrain> RegisterOs(ModuleBrain brain, CancellationToken ct)
    {
        await brain.Get<IIdentityDirectory>(IdentityGrains.Directory).RegisterAsync("os", "os-development-password", "DigitalBrain OS", ct);
        return await brain.SiloServices.GetRequiredService<IPublisherDirectory>().Resolve("os", ct);
    }

    // The "os" account's copy: created, built and recorded green so Export is allowed.
    // Seeding plays the platform: an unstamped call rides the trusted internal path.
    private static async Task<IApp> SeedGreen(ModuleBrain brain, PublisherBrain os, string name)
    {
        Orleans.Runtime.RequestContext.Remove(CallerContextStamper.RequestContextKey);
        var source = brain.Get<IApp>(IApp.Key(os.ScopeId, name));
        await source.Create(new("Hello", "Shows hello world", [new("open", "Open")], [new("Greeting", "what to say", "hello")], []),
            "## Scenario: Greets\nIt greets.");
        await source.BeginBuild(null);
        await source.CompleteBuild(await source.CurrentBuildAttempt(), new() { ["tests.cs"] = "// t", ["behaviors/hello.cs"] = "// b" }, null);
        await source.RecordVerdicts([new("Greets", true, "ok")], green: true);
        return source;
    }

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
