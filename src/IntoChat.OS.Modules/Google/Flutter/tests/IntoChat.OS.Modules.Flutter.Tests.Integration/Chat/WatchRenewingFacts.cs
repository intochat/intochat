using IntoChat.OS.Modules.Flutter.Chat;
using Microsoft.Extensions.DependencyInjection;

namespace IntoChat.OS.Modules.Flutter.Tests.Integration.Chat;

// Chat and Composer follow their source through durable synapses (06-05b; they held a renewed lease before).
public sealed class WatchRenewingFacts
{
    [Trait("host", "own")]
    [Fact]
    public async Task AComposerFollowsAChatThatReactivatedAfterItWatched()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await TestBrain.Create().WithModule<FlutterModule>().WithNeuronModule<ChatSourceModule>()
            .ConfigureServices(services => services.AddSingleton<IChatAgentSource, TestChatAgentSource>()).StartAsync(ct);
        const string key = "owner/a/apps/chat";
        var chat = brain.Get<IChat>(key + "/transcript");
        var composer = brain.Get<IComposer>(key + "/composer");
        var a = key + "/agent-a";
        var b = key + "/agent-b";
        var first = await chat.SelectAgent(a, 0, Guid.NewGuid());
        await composer.Follow(key + "/transcript");
        await TestWait.UntilAsync(token => composer.Read(), state => state.Context == a && state.Generation == first.Revision, TimeSpan.FromSeconds(30), ct);

        // The composer's synapse outlives the chat's activation: the reactivated chat still feeds it.
        await brain.DeactivateAsync(chat, ct);
        var second = await chat.SelectAgent(b, first.Revision, Guid.NewGuid());

        var followed = await TestWait.UntilAsync(token => composer.Read(), state => state.Context == b && state.Generation == second.Revision, TimeSpan.FromSeconds(30), ct);
        Assert.Equal(b, followed.Context);
    }
}
