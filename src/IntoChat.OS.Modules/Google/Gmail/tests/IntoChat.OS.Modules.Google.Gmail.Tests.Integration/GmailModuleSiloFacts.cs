using DigitalBrain.Testing;

namespace IntoChat.OS.Modules.Google.Gmail;

public sealed class GmailModuleSiloFacts
{
    [Fact]
    public async Task ModuleLoadsOnTheLocalSilo()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await SiloHost.Local(options => options.Modules = [typeof(GmailModule)], ct);
        Assert.NotNull(host.Services);
    }
}
