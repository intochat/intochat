using DigitalBrain.Testing;

namespace IntoChat.OS.Modules.Microsoft.Azure;

public sealed class AzureModuleSiloFacts
{
    [Fact]
    public async Task ModuleLoadsOnTheLocalSilo()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await SiloHost.Local(options => options.Modules = [typeof(AzureModule)], ct);
        Assert.NotNull(host.Services);
    }
}
