using DigitalBrain.Testing;

namespace IntoChat.OS.Modules.Microsoft.Windows;

public sealed class WindowsModuleSiloFacts
{
    [Fact]
    public async Task ModuleLoadsOnTheLocalSilo()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await SiloHost.Local(options => options.Modules = [typeof(WindowsModule)], ct);
        Assert.NotNull(host.Services);
    }
}