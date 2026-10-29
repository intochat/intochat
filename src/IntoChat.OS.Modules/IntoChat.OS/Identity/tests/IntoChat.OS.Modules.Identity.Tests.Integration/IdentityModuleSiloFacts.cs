using DigitalBrain.Testing;

namespace IntoChat.OS.Authority;

public sealed class IdentityModuleSiloFacts
{
    [Fact]
    public async Task ModuleLoadsOnTheLocalSilo()
    {
        var ct = TestContext.Current.CancellationToken;
        var home = Directory.CreateTempSubdirectory("identity-silo").FullName;
        await using var host = await SiloHost.Local(options =>
        {
            options.Modules = [typeof(IdentityModule)];
            options.Home = home;
        }, ct);
        Assert.NotNull(host.Services);
    }
}
