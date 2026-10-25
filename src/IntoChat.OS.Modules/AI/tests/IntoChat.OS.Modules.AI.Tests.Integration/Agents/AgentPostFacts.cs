using IntoChat.OS.Modules.AI.Agents;
using Microsoft.Extensions.AI;

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
        // Posted ids live under `post/`, so they never clash with run ids.
        Assert.Equal(("post/greeting", AgentRunStatus.Completed, true, "Hello! Open apps from the gallery."), (run.RunId, run.Status, run.Posted, run.Reply));
        var message = Assert.Single(await agent.Transcript());
        Assert.Equal("assistant", message.Role);
    }

    [Fact]
    public async Task PostIsRejectedWhileARunIsActive()
    {
        var ct = TestContext.Current.CancellationToken;
        var resume = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var brain = await AgentFacts.Brain(StubChatClient.StreamingOnly((_, _, token) => Stalled(resume.Task, token)), ct);
        var agent = brain.Get<IAgent>(AgentFacts.Key("busy"));
        await agent.Start(AgentTurn.Text("hello"));
        try { await Assert.ThrowsAsync<InvalidOperationException>(() => agent.Post("Hi", "greeting")); }
        finally { resume.TrySetResult(); }
        Assert.DoesNotContain((await agent.Read()).Runs, run => run.Posted);
    }

    private static async IAsyncEnumerable<ChatResponseUpdate> Stalled(Task resume, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        yield return new(ChatRole.Assistant, "Hi");
        await resume.WaitAsync(ct);
    }
}
