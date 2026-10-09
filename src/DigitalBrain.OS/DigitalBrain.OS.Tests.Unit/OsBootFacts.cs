using DigitalBrain.Apps;
using DigitalBrain.Framework;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace DigitalBrain.OS.Tests.Unit;

// A new brain is born with the OS defaults, installed as its owner from the shipped catalog.
public sealed class OsBootFacts
{
    [Fact(Timeout = 60_000)]
    public async Task AFreshBrainHasTheOsAppsInstalledFromTheShippedCatalog()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await ModuleTest.Create()
            .WithModule<AppsModule>()
            .ConfigureSilo(silo =>
            {
                silo.Services.RemoveAll<IAppRuntime>();
                silo.Services.AddOperatingSystemApps();
            })
            .StartAsync(ct);

        var scope = await Establish(brain);

        foreach (var name in new[] { "settings", "assistant", "router", "calculator", "datavisualizer" })
        {
            var installed = await brain.Get<IApp>(IApp.Key(scope, name)).Read();
            Assert.Equal(AppStatus.Live, installed.Status);
            Assert.Equal("os", installed.Origin!.Publisher);
            Assert.IsType<AppSource.Shipped>(installed.Origin.Source);
        }
    }

    [Fact(Timeout = 60_000)]
    public async Task ADefaultMissingFromTheCatalogDoesNotKeepTheOthersFromInstalling()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await Boot(ct, new OsApp("os", "missing"), new OsApp("os", "hello"));

        var scope = await Establish(brain);

        Assert.Equal(AppStatus.Live, (await brain.Get<IApp>(IApp.Key(scope, "hello")).Read()).Status);
        Assert.Equal(AppStatus.Drafted, (await brain.Get<IApp>(IApp.Key(scope, "missing")).Read()).Status);
    }

    [Fact(Timeout = 60_000)]
    public async Task ADefaultThePersonAlreadyHasIsLeftAlone()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await Boot(ct, new OsApp("os", "hello"));
        var scope = BrainScope.Create("acct", "home").Id;
        Orleans.Runtime.RequestContext.Remove(CallerContextStamper.RequestContextKey);
        var own = brain.Get<IApp>(IApp.Key(scope, "hello"));
        await own.Create(new("My hello", "mine", [], [], []), "## Scenario: Mine\nMine.");

        await Establish(brain);

        var after = await own.Read();
        Assert.Equal("My hello", after.Manifest.Title);
        Assert.Null(after.Origin);
    }

    private static Task<ModuleBrain> Boot(CancellationToken ct, params OsApp[] defaults) => ModuleTest.Create()
        .WithModule<AppsModule>()
        .ConfigureSilo(silo =>
        {
            silo.Services.RemoveAll<IAppRuntime>();
            silo.Services.AddSingleton<IShippedAppSource>(new HelloSource());
            silo.Services.AddSingleton<ShippedCatalog>();
            silo.Services.AddOperatingSystemBoot(os => { foreach (var app in defaults) { os.Apps.Add(app); } });
        })
        .StartAsync(ct);

    private static async Task<string> Establish(ModuleBrain brain)
    {
        var scope = BrainScope.Create("acct", "home").Id;
        CallerContextStamper.Stamp(new()
        {
            PrincipalId = "acct",
            AccountId = "acct",
            BrainId = "home",
            Kind = CallerKind.User,
            StampedBy = TrustedEdge.AuthenticatedHttp,
        });
        await brain.AuthorizeCallerAsync();
        await brain.Get<IKernel>(scope).Create(new Owner("acct"));
        return scope;
    }

    private sealed class HelloSource : IShippedAppSource
    {
        public string Publisher => "os";
        public IReadOnlyList<AppBundle> Load() =>
        [new("hello", "1.0.0", new("Hello", "Shows hello world", [new("open", "Open")], [new("Greeting", "what to say", "hello")], []),
            "## Scenario: Greets\nIt greets.", new() { ["synapses/hello.cs"] = "// b" })];
    }
}
