using IntoChat.OS.Apps.Ino;
using IntoChat.OS.Host.Primitives;
using IntoChat.OS.Modules.AI.Agents;
using IntoChat.OS.Modules.AI.Models;
using IntoChat.OS.Modules.Flutter.Conversations;
using IntoChat.OS.Modules.Identity.Contracts;
using ConversationEntry = IntoChat.OS.Modules.Flutter.Conversations.Entry;

namespace IntoChat.OS.Apps.Tests.Unit.Ino;

public sealed class InoModelFacts
{
    private static readonly DateTimeOffset At = new(2026, 11, 13, 12, 0, 0, TimeSpan.Zero);
    private static readonly Participant.Person Owner = new(new AccountId("local"));
    private static readonly Participant.Agent Ino = new("ino");

    [Fact]
    public void SendingShowsMyMessageAtOnce()
    {
        var next = InoModel.Sent(new InoAppState(), "hello", "p1", At);
        Assert.Equal(new InoPending("p1", "hello", At), next.Pending);
        Assert.Equal((InoModel.Working, "hello"), (next.Status, next.Title));
    }

    [Fact]
    public void ASecondSendWhileOneIsPendingChangesNothing()
    {
        var first = InoModel.Sent(new InoAppState(), "hello", "p1", At);
        Assert.Same(first, InoModel.Sent(first, "again", "p2", At));
    }

    [Fact]
    public void ASendWhileARunIsActiveChangesNothing()
    {
        var running = new InoAppState() with { ActiveRunId = "r1" };
        Assert.Same(running, InoModel.Sent(running, "again", "p2", At));
    }

    [Fact]
    public void AMessageOver32000CharactersIsNotSent()
    {
        var next = InoModel.Sent(new InoAppState(), new string('x', 32_001), "p1", At);
        Assert.Null(next.Pending);
        Assert.Equal("too-long", next.Error?.Code);
    }

    [Fact]
    public void FromConversationClearsPendingAndNeverWritesMessages()
    {
        var sent = InoModel.Sent(new InoAppState(), "hello", "p1", At);
        var view = View("main", 1, null, [new AgentRun("r1", AgentRunStatus.Completed, "hello", At, Reply: "Hi.")], User("hello"), Answer("Hi."));
        var next = InoModel.FromConversation(sent, view);

        Assert.Null(next.Pending);
        Assert.Null(next.Messages);
        Assert.Equal(InoModel.Ready, next.Status);
        Assert.Null(next.ActiveRunId);
    }

    [Fact]
    public void ARunningReplyKeepsInoWorking()
    {
        var active = new AgentRun("r1", AgentRunStatus.Running, "hello", At, Reply: "Hi");
        var next = InoModel.FromConversation(new InoAppState(), View("main", 1, active, [], User("hello")));
        Assert.Equal((InoModel.Working, "r1"), (next.Status, next.ActiveRunId));
    }

    [Fact]
    public void AViewOfAnotherConversationChangesNothing()
    {
        var current = new InoAppState();
        var other = View("earlier", 5, null, [new AgentRun("r1", AgentRunStatus.Completed, "old", At, Reply: "old reply")], User("old"), Answer("old reply"));
        Assert.Same(current, InoModel.FromConversation(current, other));
    }

    [Fact]
    public void AFailedReplyBecomesAnErrorWithItsCause()
    {
        var failed = new AgentRun("r1", AgentRunStatus.Failed, "hello", At, Error: "opaque") { Failure = AgentFailure.NoProvider };
        var next = InoModel.FromConversation(new InoAppState(), View("main", 1, null, [failed], User("hello")));
        Assert.Equal(InoModel.Failed, next.Status);
        Assert.Equal("no-provider", next.Error?.Code);
        Assert.Null(next.ActiveRunId);
    }

    [Fact]
    public void FromConversationReadsTypedRunFailureEvenWithoutMatchingErrorText()
    {
        var failed = new AgentRun("r1", AgentRunStatus.Failed, "hello", At, Error: "completely opaque vendor payload 9999")
        {
            Failure = AgentFailure.ProviderRejectedKey,
        };
        var next = InoModel.FromConversation(new InoAppState(), View("main", 1, null, [failed], User("hello")));
        Assert.Equal(InoModel.Failed, next.Status);
        Assert.Equal("provider-rejected-key", next.Error?.Code);
    }

    [Fact]
    public void AnInterruptedReplyCanBeTriedAgain()
    {
        var interrupted = new AgentRun("r1", AgentRunStatus.Interrupted, "hello", At, Reply: "Hi");
        var next = InoModel.FromConversation(new InoAppState(), View("main", 1, null, [interrupted], User("hello")));
        Assert.Equal(InoModel.Failed, next.Status);
        Assert.Equal("interrupted", next.Error?.Code);
        Assert.Null(next.ActiveRunId);
    }

    [Fact]
    public void ANewSendClearsTheLastError()
    {
        var failed = new InoAppState() with { Error = new InoError("unknown"), Status = InoModel.Failed };
        Assert.Null(InoModel.Sent(failed, "again", "p2", At).Error);
    }

    [Fact]
    public void AFailedStartDropsThePendingMessageAndShowsWhy()
    {
        var next = InoModel.StartFailed(InoModel.Sent(new InoAppState(), "hello", "p1", At), new InoError("paused", "An interrupt holds the agent."));
        Assert.Null(next.Pending);
        Assert.Equal("paused", next.Error?.Code);
        Assert.Equal(InoModel.Paused, next.Status);
    }

    [Fact]
    public void APauseShowsAndResumingClearsIt()
    {
        var paused = InoModel.Pause(new InoAppState(), "An interrupt holds the agent.");
        Assert.Equal((InoModel.Paused, "paused", true), (paused.Status, paused.Error?.Code, paused.Blocked));
        var resumed = InoModel.Resume(paused);
        Assert.Equal(InoModel.Ready, resumed.Status);
        Assert.Null(resumed.Error);
        Assert.False(resumed.Blocked);
    }

    [Fact]
    public void TheModelInInosHeaderIsTheModelTheTurnSelected()
    {
        var opened = new InoAppState();
        var started = InoModel.Started(opened, "r1", "qwen3.5:4b");
        Assert.Equal("qwen3.5:4b", started.SelectedModel);

        var view = View("main", 1, null,
            [new AgentRun("r1", AgentRunStatus.Completed, "hello", At, Reply: "Answered on Qwen 3 14B.")],
            User("hello"),
            Answer("Answered on Qwen 3 14B.", new ModelId("qwen3:14b")));
        var projected = InoModel.FromConversation(started, view);
        Assert.Equal("qwen3:14b", projected.SelectedModel);
    }

    [Fact]
    public void ANewConversationKeepsNothingOfTheOld()
    {
        var old = InoModel.FromConversation(
            new InoAppState(),
            View("main", 3, null, [new AgentRun("r1", AgentRunStatus.Completed, "hello", At, Reply: "Hi.")], User("hello"), Answer("Hi.")))
            with
        { GrantSummaries = ["read · files"] };
        var next = InoModel.NewConversation(old, "c2");
        Assert.Equal(("c2", "New chat", 0L, InoModel.Ready), (next.ConversationId, next.Title, next.ChatRevision, next.Status));
        Assert.Null(next.Messages);
        Assert.Equal(["read · files"], next.GrantSummaries!);
    }

    private static ConversationView View(string id, long revision, AgentRun? active, AgentRun[] runs, params ConversationEntry[] entries)
        => new(ConversationId.Parse(id), revision, [Owner, Ino], entries, active, runs, false, []);

    private static Said User(string text) => new(EntryIds.Said("r1"), Owner, At, text);

    private static Said Answer(string text, ModelId? model = null) => new(EntryIds.Reply("r1"), Ino, At, text, model);
}
