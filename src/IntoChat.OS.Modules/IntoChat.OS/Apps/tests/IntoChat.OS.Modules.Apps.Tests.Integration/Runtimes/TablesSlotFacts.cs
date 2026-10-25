using IntoChat.OS.Connections.Platform.Connections;
using IntoChat.OS.Modules.Postgres;
using IntoChat.OS.Testing;

namespace IntoChat.OS.Modules.Apps.Tests.Integration.Runtimes;

public sealed class TablesSlotFacts
{
    private const string Brain = "slot-facts";

    [Trait("host", "own")]
    [Fact]
    public async Task ABoundTablesSlotNamesItsPostgresConnection()
    {
        await using var brain = await Start();
        var app = await App(brain, "bound", new("tables", "postgres"), new() { ["tables"] = "c1" });
        Assert.Equal(ConnectionKeys.For(Brain, "postgres", "c1"), await TablesSlot.BoundConnection(brain.Services, new(Brain, app)));
    }

    [Trait("host", "own")]
    [Fact]
    public async Task AnUndeclaredOrUnboundTablesSlotNamesNoConnection()
    {
        await using var brain = await Start();
        var unbound = await App(brain, "unbound", new("tables", "postgres"), []);
        var undeclared = await App(brain, "undeclared", null, new() { ["tables"] = "c1" });
        Assert.Null(await TablesSlot.BoundConnection(brain.Services, new(Brain, unbound)));
        Assert.Null(await TablesSlot.BoundConnection(brain.Services, new(Brain, undeclared)));
    }

    [Trait("host", "own")]
    [Fact]
    public async Task AnotherPostgresSlotDoesNotRedirectTables()
    {
        await using var brain = await Start();
        var app = await App(brain, "visualizer", new("db", "postgres"), new() { ["db"] = "c1" });
        Assert.Null(await TablesSlot.BoundConnection(brain.Services, new(Brain, app)));
    }

    [Trait("host", "own")]
    [Fact]
    public async Task AnAppOfAnotherBrainIsNeverRead()
    {
        await using var brain = await Start();
        var app = await App(brain, "foreign", new("tables", "postgres"), new() { ["tables"] = "c1" });
        Assert.Null(await TablesSlot.BoundConnection(brain.Services, new("other-brain", app)));
    }

    private static async Task<string> App(ITestBrain brain, string name, AppSlot? slot, Dictionary<string, string> connections)
    {
        var key = IApp.Key(Brain, name);
        var app = brain.Get<IApp>(key);
        await app.Create(new("Slots", "slots", [], [], slot is null ? [] : [slot]), "## Scenario: Stores\nIt stores.");
        await app.Configure([], connections);
        return key;
    }

    private static Task<ITestBrain> Start()
        => TestBrain.Create().WithModule<AppsModule>().WithFakeAppRuntime(new())
            .StartAsync(TestContext.Current.CancellationToken);
}
