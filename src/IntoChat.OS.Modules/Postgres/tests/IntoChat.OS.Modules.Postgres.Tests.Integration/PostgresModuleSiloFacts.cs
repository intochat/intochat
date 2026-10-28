using DigitalBrain.Testing;

namespace IntoChat.OS.Modules.Postgres;

public sealed class PostgresModuleSiloFacts
{
    [Fact]
    public async Task ModuleLoadsOnTheLocalSilo()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await SiloHost.Local(options => options.Modules = [typeof(PostgresModule)], ct);
        Assert.NotNull(host.Services);
    }
}