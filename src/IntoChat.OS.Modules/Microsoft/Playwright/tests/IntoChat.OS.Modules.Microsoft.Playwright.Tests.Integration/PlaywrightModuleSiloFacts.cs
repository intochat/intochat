using DigitalBrain.Testing;

namespace IntoChat.OS.Modules.Microsoft.Playwright;

public sealed class PlaywrightModuleSiloFacts
{
    [Fact]
    public async Task ModuleLoadsOnTheLocalSilo()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await SiloHost.Local(options => options.Modules = [typeof(PlaywrightModule)], ct);
        Assert.NotNull(host.Services);
    }
}