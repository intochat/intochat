using IntoChat.OS.Apps.Ino;
using IntoChat.OS.Modules.Apps;
using IntoChat.OS.Modules.Flutter.Chat;

namespace IntoChat.OS.Apps.Tests.Unit.Ino;

public sealed class InoModelFacts
{
    private static readonly DateTimeOffset At = new(2026, 11, 13, 12, 0, 0, TimeSpan.Zero);
    private const string Agent = "local/ino/conversations/main";

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
    public void AProjectionReplacesThePendingMessageWithTheRealOnes()
    {
        var sent = InoModel.Sent(new InoAppState(), "hello", "p1", At);
        var next = InoModel.Projected(sent, Chat(1, ChatRunStatus.Completed, User("hello"), Answer("Hi.")), Agent);
        Assert.Null(next.Pending);
        Assert.Equal([("user", "hello"), ("assistant", "Hi.")], next.Messages!.Select(m => (m.Role, m.Text)));
        Assert.Equal(InoModel.Ready, next.Status);
        Assert.Null(next.ActiveRunId);
        Assert.Equal(1L, next.ChatRevision);
    }

    [Fact]
    public void ARunningReplyKeepsInoWorking()
    {
        var next = InoModel.Projected(new InoAppState(), Chat(1, ChatRunStatus.Running, User("hello"), Answer("Hi", "running")), Agent);
        Assert.Equal((InoModel.Working, "r1"), (next.Status, next.ActiveRunId));
    }

    [Fact]
    public void AnOlderOrRepeatedProjectionChangesNothing()
    {
        var current = InoModel.Projected(new InoAppState(), Chat(2, ChatRunStatus.Completed, User("hello"), Answer("Hi.")), Agent);
        Assert.Same(current, InoModel.Projected(current, Chat(2, ChatRunStatus.Completed, User("hello"), Answer("Hi.")), Agent));
        Assert.Same(current, InoModel.Projected(current, Chat(1, ChatRunStatus.Running, User("hello")), Agent));
    }

    [Fact]
    public void AProjectionOfAnotherConversationChangesNothing()
    {
        var current = new InoAppState();
        Assert.Same(current, InoModel.Projected(current, Chat(5, ChatRunStatus.Completed, User("old"), Answer("old reply")), "local/ino/conversations/earlier"));
    }

    [Fact]
    public void AFailedReplyBecomesAnErrorWithItsCause()
    {
        var next = InoModel.Projected(new InoAppState(), Chat(1, ChatRunStatus.Failed, User("hello"), Answer("", "failed", "no-provider")), Agent);
        Assert.Equal(InoModel.Failed, next.Status);
        Assert.Equal("no-provider", next.Error?.Code);
        Assert.Null(next.ActiveRunId);
    }

    [Fact]
    public void AnInterruptedReplyCanBeTriedAgain()
    {
        var next = InoModel.Projected(new InoAppState(), Chat(1, ChatRunStatus.Interrupted, User("hello"), Answer("Hi", "interrupted")), Agent);
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
    public void ANewConversationKeepsNothingOfTheOld()
    {
        var old = InoModel.Projected(new InoAppState(), Chat(3, ChatRunStatus.Completed, User("hello"), Answer("Hi.")), Agent)
            with { GrantSummaries = ["read · files"] };
        var next = InoModel.NewConversation(old, "c2");
        Assert.Equal(("c2", "New chat", 0L, InoModel.Ready), (next.ConversationId, next.Title, next.ChatRevision, next.Status));
        Assert.Empty(next.Messages ?? []);
        Assert.Equal(["read · files"], next.GrantSummaries!);
    }

    private static ChatState Chat(long revision, ChatRunStatus status, params ChatEntry[] entries) => new()
    {
        Name = "local/ino/chats/main",
        Revision = revision,
        Selection = new ChatSelection(Agent, 1),
        Projection = new ChatProjection(Agent, 1, revision, entries),
        Run = new ChatRunSnapshot(Agent, 1, revision, "r1", status),
    };

    private static ChatEntry User(string text) => new("r1/user", ChatRole.User, text);

    private static ChatEntry Answer(string text, string status = "completed", string? failure = null)
        => new("r1/assistant", ChatRole.Assistant, text)
        {
            Status = status,
            Error = failure,
        };
}
