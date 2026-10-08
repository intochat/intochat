using DigitalBrain.Apps;
using DigitalBrain.Framework.Platform.Connections;
using DigitalBrain.Postgres.Tables;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace DigitalBrain.Modules.Apps.Tests.Runtimes;

public sealed class TablesSlotFacts
{
    private const string Brain = "slot-facts";

    [Fact]
    public async Task ABoundTablesSlotNamesItsPostgresConnection()
    {
        await using var brain = await Start();
        var app = await App(brain, "bound", new("tables", "postgres"), new() { ["tables"] = "c1" });
        Assert.Equal(ConnectionKeys.For(Brain, "postgres", "c1"), await Slot(brain).BoundConnection(new(Brain, app)));
    }

    [Fact]
    public async Task AnUndeclaredOrUnboundTablesSlotNamesNoConnection()
    {
        await using var brain = await Start();
        var unbound = await App(brain, "unbound", new("tables", "postgres"), []);
        var undeclared = await App(brain, "undeclared", null, new() { ["tables"] = "c1" });
        Assert.Null(await Slot(brain).BoundConnection(new(Brain, unbound)));
        Assert.Null(await Slot(brain).BoundConnection(new(Brain, undeclared)));
    }

    [Fact]
    public async Task AnotherPostgresSlotDoesNotRedirectTables()
    {
        await using var brain = await Start();
        var app = await App(brain, "visualizer", new("db", "postgres"), new() { ["db"] = "c1" });
        Assert.Null(await Slot(brain).BoundConnection(new(Brain, app)));
    }

    [Fact]
    public async Task AnAppOfAnotherBrainIsNeverRead()
    {
        await using var brain = await Start();
        var app = await App(brain, "foreign", new("tables", "postgres"), new() { ["tables"] = "c1" });
        Assert.Null(await Slot(brain).BoundConnection(new("other-brain", app)));
    }

    private static ITablesSlot Slot(ModuleBrain brain) => new AppTablesSlot(brain.SiloServices);

    private static async Task<string> App(ModuleBrain brain, string name, AppSlot? slot, Dictionary<string, string> connections)
    {
        var key = IApp.Key(Brain, name);
        var app = brain.Get<IApp>(key);
        await app.Create(new("Slots", "slots", [], [], slot is null ? [] : [slot]), "## Scenario: Stores\nIt stores.");
        await app.Configure([], connections);
        return key;
    }

    private static Task<ModuleBrain> Start()
        => ModuleTest.Create().WithModule<AppsModule>()
            .ConfigureSilo(silo =>
            {
                silo.Services.RemoveAll<IDeployableRuntime>();
                silo.Services.AddSingleton<IDeployableRuntime>(new DeployableRuntimeFacts.FakeRuntime());
            })
            .StartAsync(TestContext.Current.CancellationToken);
}
