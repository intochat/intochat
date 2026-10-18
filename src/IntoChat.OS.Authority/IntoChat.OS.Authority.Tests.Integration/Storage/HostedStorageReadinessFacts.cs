using IntoChat.OS.Authority.Storage;
using Microsoft.Extensions.DependencyInjection;

namespace IntoChat.OS.Authority.Tests.Integration.Storage;

[Trait("host", "own")]
public sealed class HostedStorageReadinessFacts
{
    [Fact]
    public async Task ReadinessProbesTheOwnedStoreAndFailsAfterDisposal()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await TestBrain.Create().StartAsync(ct);
        var logs = brain.Services.GetRequiredService<BrainLogs>();
        Assert.True(await logs.CheckStorageAsync("owner", ct));
        await logs.DisposeAsync();
        Assert.False(await logs.CheckStorageAsync("owner", ct));
    }
}
