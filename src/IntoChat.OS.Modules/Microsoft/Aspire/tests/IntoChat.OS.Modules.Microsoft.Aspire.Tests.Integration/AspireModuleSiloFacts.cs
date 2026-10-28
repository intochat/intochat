using DigitalBrain.Testing;

namespace IntoChat.OS.Modules.Microsoft.Aspire;

public sealed class AspireModuleSiloFacts
{
    [Fact]
    public async Task ModuleLoadsOnTheLocalSilo()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await SiloHost.Local(options => options.Modules = [typeof(AspireModule)], ct);
        Assert.NotNull(host.Services);
    }
}