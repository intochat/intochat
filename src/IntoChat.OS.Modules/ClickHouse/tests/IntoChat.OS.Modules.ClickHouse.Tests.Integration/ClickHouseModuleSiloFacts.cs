using DigitalBrain.Testing;

namespace IntoChat.OS.Modules.ClickHouse;

public sealed class ClickHouseModuleSiloFacts
{
    [Fact]
    public async Task ModuleLoadsOnTheLocalSilo()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await SiloHost.Local(options => options.Modules = [typeof(ClickHouseModule)], ct);
        Assert.NotNull(host.Services);
    }
}
