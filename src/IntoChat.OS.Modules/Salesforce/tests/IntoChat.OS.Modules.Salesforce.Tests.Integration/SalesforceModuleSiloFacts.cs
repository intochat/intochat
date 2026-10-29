using DigitalBrain.Testing;

namespace IntoChat.OS.Modules.Salesforce;

public sealed class SalesforceModuleSiloFacts
{
    [Fact]
    public async Task ModuleLoadsOnTheLocalSilo()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await SiloHost.Local(options => options.Modules = [typeof(SalesforceModule)], ct);
        Assert.NotNull(host.Services);
    }
}
