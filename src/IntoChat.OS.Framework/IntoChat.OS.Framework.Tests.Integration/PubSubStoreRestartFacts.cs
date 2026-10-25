using IntoChat.OS.Contracts.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Runtime;
using Orleans.Storage;

namespace IntoChat.OS.Framework.Tests.Integration;

// D-25 / TOPO-02: on Topology=local the platform store and the stream PubSubStore are digitalbrain file grain
// stores under the TestHome, so what they hold before a silo restart is read back after it.
[Trait("host", "own")]
public sealed class PubSubStoreRestartFacts
{
    private const string PubSubStore = "PubSubStore";

    [Fact]
    public async Task PlatformStateAndPubSubRecordsSurviveARestart()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await TestBrain.Create().WithoutHttp().StartAsync(ct);
        var platformId = GrainId.Create("restart-platform", "owner/restart");
        var pubSubId = GrainId.Create("pubsubrendezvous", "signals/owner/restart");
        await Store(brain, IntoChatOSNames.PlatformGrainStorage).WriteStateAsync("state", platformId, new GrainState<CounterState>(new CounterState { Value = 41 }));
        await Store(brain, PubSubStore).WriteStateAsync("state", pubSubId, new GrainState<CounterState>(new CounterState { Value = 7 }));

        await brain.RestartAsync(ct);

        var platform = new GrainState<CounterState>();
        await Store(brain, IntoChatOSNames.PlatformGrainStorage).ReadStateAsync("state", platformId, platform);
        var pubSub = new GrainState<CounterState>();
        await Store(brain, PubSubStore).ReadStateAsync("state", pubSubId, pubSub);
        Assert.True(platform.RecordExists);
        Assert.Equal(41, platform.State!.Value);
        Assert.True(pubSub.RecordExists);
        Assert.Equal(7, pubSub.State!.Value);
    }

    private static IGrainStorage Store(ITestBrain brain, string name) => brain.Services.GetRequiredKeyedService<IGrainStorage>(name);
}
