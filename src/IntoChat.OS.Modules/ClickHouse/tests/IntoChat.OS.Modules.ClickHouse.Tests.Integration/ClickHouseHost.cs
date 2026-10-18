using IntoChat.OS.ClickHouse;

[assembly: AssemblyFixture(typeof(IntoChat.OS.Modules.ClickHouse.Tests.Integration.ClickHouseHost))]

namespace IntoChat.OS.Modules.ClickHouse.Tests.Integration;

// The shared host composes ClickHouse with its deployment integration pointed at one fake server started for the
// assembly. Facts read the server's recorded databases in sequence within their class; users connect in fresh persons.
public sealed class ClickHouseHost : BrainHostFixture
{
    private FakeClickHouseServer? _server;

    internal FakeClickHouseServer Server => _server ?? throw new InvalidOperationException("The host starts in InitializeAsync.");

    protected override TestBrain Configure(TestBrain brain)
        => brain.WithModule<ClickHouseModule>()
            .WithIntegration(ClickHouseModule.Integration, "ConnectionString", Server.ConnectionString("deployment"));

    public override async ValueTask InitializeAsync()
    {
        _server = await FakeClickHouseServer.StartAsync(TestContext.Current.CancellationToken);
        await base.InitializeAsync();
    }

    public override async ValueTask DisposeAsync()
    {
        await base.DisposeAsync();
        if (_server is { } server) { await server.DisposeAsync(); }
    }
}
