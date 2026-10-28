using DigitalBrain.Testing;

namespace IntoChat.OS.Modules.Microsoft.GitHub;

public sealed class GitHubModuleSiloFacts
{
    [Fact]
    public async Task ModuleLoadsOnTheLocalSilo()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await SiloHost.Local(options => options.Modules = [typeof(GitHubModule)], ct);
        Assert.NotNull(host.Services);
    }
}