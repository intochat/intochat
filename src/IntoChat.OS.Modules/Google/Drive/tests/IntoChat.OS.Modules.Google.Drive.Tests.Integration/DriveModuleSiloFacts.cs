using DigitalBrain.Testing;

namespace IntoChat.OS.Modules.Google.Drive;

public sealed class DriveModuleSiloFacts
{
    [Fact]
    public async Task ModuleLoadsOnTheLocalSilo()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await SiloHost.Local(options => options.Modules = [typeof(DriveModule)], ct);
        Assert.NotNull(host.Services);
    }
}
