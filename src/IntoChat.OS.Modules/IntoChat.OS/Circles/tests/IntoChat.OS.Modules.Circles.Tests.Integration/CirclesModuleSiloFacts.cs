using DigitalBrain.Testing;

namespace IntoChat.OS.Modules.Circles;

public sealed class CirclesModuleSiloFacts
{
    [Fact]
    public async Task ModuleLoadsOnTheLocalSilo()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await SiloHost.Local(options => options.Modules = [typeof(CirclesModule)], ct);
        Assert.NotNull(host.Services);
    }
}
