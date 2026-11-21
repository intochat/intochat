using IntoChat.OS.Modules.AI.Conversations;
using IntoChat.OS.Modules.AI.Inference;
using IntoChat.OS.Modules.Flutter.Conversations;
using IntoChat.OS.Modules.Identity.Contracts;

namespace IntoChat.OS.Tests.Unit.Modules.AI.Conversations;

public sealed class ModelContextFacts
{
    private static readonly DateTimeOffset At = new(2026, 11, 20, 12, 0, 0, TimeSpan.Zero);
    private static readonly Participant Person = new Participant.Person(new AccountId("a1"));
    private static readonly Participant Agent = new Participant.Agent("ino");

    private static Entry Said(string runId, string text) => new Said(EntryIds.Said(runId), Person, At, text);
    private static Entry Reply(string runId, string text) => new Said(EntryIds.Reply(runId), Agent, At, text);
    private static Entry ReplyReplacing(string runId, string text, EntryId replaces) => new Said(EntryIds.Reply(runId), Agent, At, text, Replaces: replaces);
    private static Entry Call(string runId, string callId, string tool) => new Called(EntryIds.Call(runId, callId), Agent, At, tool, "{}");
    private static Entry Result(string runId, string callId, string json) => new Returned(EntryIds.Result(runId, callId), Agent, At, "t", json, true, null);
    private static Entry Summary(string runId, string summary, EntryId upTo) => new Summarized(EntryIds.Summary(runId), Agent, At, summary, upTo);
    private static LoggedEntry[] Log(params Entry[] entries) => ConversationLog.Append([], 1, entries);

    [Fact]
    public void CallsAndResultsPairByCallId()
    {
        var window = ModelContext.From(Log(Said("r1", "hi"), Call("r1", "c1", "files_read"), Result("r1", "c1", "{}"), Reply("r1", "done")));
        Assert.Equal(["user", "assistant", "tool", "assistant"], window.Messages.Select(m => m.Role));
        Assert.Equal("c1", Assert.IsType<AiToolCall>(window.Messages[1].Content[0]).CallId);
        Assert.Equal("c1", Assert.IsType<AiToolResult>(window.Messages[2].Content[0]).CallId);
        Assert.Equal(window.Messages.Length, window.Sources.Length);
    }

    [Fact]
    public void ASupersededReplyAndItsCallsAreLeftOut()
    {
        var window = ModelContext.From(Log(
            Said("r1", "hi"),
            Call("r1", "c1", "t"),
            Result("r1", "c1", "{}"),
            Reply("r1", "old"),
            ReplyReplacing("r2", "new", EntryIds.Reply("r1"))));
        Assert.DoesNotContain(window.Messages, m => m.Content.OfType<AiText>().Any(t => t.Text == "old"));
        Assert.DoesNotContain(window.Messages, m => m.Role == "tool");
        Assert.Equal(["user", "assistant"], window.Messages.Select(m => m.Role));
        Assert.Equal(["hi", "new"], window.Messages.Select(m => m.Content.OfType<AiText>().Single().Text));
    }

    [Fact]
    public void ASummaryReplacesWhatItCovers()
    {
        var window = ModelContext.From(Log(
            Said("r1", "a"),
            Reply("r1", "b"),
            Summary("r2", "earlier", EntryIds.Reply("r1")),
            Said("r2", "c")));
        Assert.Equal("earlier", window.Summary);
        Assert.Equal(["c"], window.Messages.Select(m => m.Content.OfType<AiText>().Single().Text));
    }
}
