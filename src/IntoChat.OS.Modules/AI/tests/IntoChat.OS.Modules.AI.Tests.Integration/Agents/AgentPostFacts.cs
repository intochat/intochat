using IntoChat.OS.AI.Agents;

namespace IntoChat.OS.Modules.AI.Tests.Integration.Agents;

// Post records an assistant-only entry without calling the model; the same id posts once.
public sealed class AgentPostFacts
{
    [Fact]
    public async Task PostRecordsOneCompletedAssistantEntryWithoutTheModelAndIsIdempotentById()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await AgentFacts.Brain(StubChatClient.Failing("the model must not be called"), ct);
        var agent = brain.Get<IAgent>(AgentFacts.Key("greeter"));

        await agent.Post("Hello! Open apps from the gallery.", "greeting");
        await agent.Post("Hello! Open apps from the gallery.", "greeting");

        var run = Assert.Single((await agent.Read()).Runs);
        Assert.Equal(("greeting", AgentRunStatus.Completed, "", "Hello! Open apps from the gallery."), (run.RunId, run.Status, run.UserText, run.Reply));
        var message = Assert.Single(await agent.Transcript());
        Assert.Equal("assistant", message.Role);
    }
}
