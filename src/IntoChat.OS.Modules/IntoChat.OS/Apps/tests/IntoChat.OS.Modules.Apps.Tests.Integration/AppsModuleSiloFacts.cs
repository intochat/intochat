using DigitalBrain.Testing;

namespace IntoChat.OS.Modules.Apps;

public sealed class AppsModuleSiloFacts
{
    [Fact]
    public async Task ModuleLoadsOnTheLocalSilo()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await SiloHost.Local(options => options.Modules = [typeof(AppsModule)], ct);
        Assert.NotNull(host.Services);
    }
}
