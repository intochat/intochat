using IntoChat.OS.Modules.AI;
using IntoChat.OS.Modules.AI.Agents;
using IntoChat.OS.Modules.Flutter;
using IntoChat.OS.Modules.Flutter.Chat;
using Microsoft.Extensions.DependencyInjection;

namespace IntoChat.OS.Kernel.Tests.Integration;

// 06-05b: the chat is a plain neuron and the agent is still a grain. The chat follows the agent through a synapse its stand-in
// admits, and hears the agent's journalled signals as their plain change records, so an answer the agent posts reaches the
// chat's projection without a watch or a renewal.
public sealed class AgentChatFollowFacts
{
    [Trait("host", "own")]
    [Fact]
    public async Task APlainChatFollowsALegacyAgentsPostedAnswer()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await TestBrain.Create().WithModule<AIModule>().WithModule<FlutterModule>()
            .ConfigureServices(services => services.AddSingleton<IChatAgentSource, AgentChatSource>()).StartAsync(ct);
        using var caller = brain.Owner.AsCaller();
        var agentKey = brain.Owner.Key("apps/ino-data/conversations/main");
        var chat = brain.Get<IChat>(brain.Owner.Key("apps/ino/transcript"));

        var selection = await chat.SelectAgent(agentKey, 0, Guid.NewGuid());
        await brain.Get<IAgent>(agentKey).Post("Hello from the agent.", "greeting");

        var followed = await TestWait.UntilAsync(_ => chat.Read(),
            state => state.Projection?.Messages.Any(message => message.Text == "Hello from the agent.") == true, TimeSpan.FromSeconds(30), ct);
        Assert.Equal((agentKey, selection.Revision), (followed.Projection!.Source, followed.Projection.Generation));
    }
}
