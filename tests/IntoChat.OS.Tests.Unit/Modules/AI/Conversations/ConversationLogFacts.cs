using System.Text.Json;
using IntoChat.OS.Modules.AI.Conversations;
using IntoChat.OS.Modules.Flutter.Conversations;
using IntoChat.OS.Modules.Identity.Contracts;

namespace IntoChat.OS.Tests.Unit.Modules.AI.Conversations;

public sealed class ConversationLogFacts
{
    private static readonly DateTimeOffset At = new(2026, 11, 20, 12, 0, 0, TimeSpan.Zero);
    private static readonly Participant Person = new Participant.Human(new AccountId("a1"));
    private static readonly Participant Agent = new Participant.Assistant("ino");

    private static ConversationEntry Said(string runId, string text) => new Said(EntryIds.Said(runId), Person, At, text);
    private static ConversationEntry Reply(string runId, string text) => new Said(EntryIds.Reply(runId), Agent, At, text);
    private static ConversationEntry Call(string runId, string callId, string tool, string args = "{}") => new Called(EntryIds.Call(runId, callId), Agent, At, tool, args);
    private static ConversationEntry Result(string runId, string callId, string tool, string resultJson) => new Returned(EntryIds.Result(runId, callId), Agent, At, tool, resultJson, true, null);

    private static LoggedEntry[] Log(params ConversationEntry[] entries) => ConversationLog.Append([], 1, entries);

    private static LoggedEntry[] LogAt(params (long Rev, ConversationEntry Entry)[] items)
    {
        LoggedEntry[] log = [];
        foreach (var (rev, entry) in items)
        {
            log = ConversationLog.Append(log, rev, entry);
        }
        return log;
    }

    [Fact]
    public void OthersSeeOnlyEveryoneEntries() =>
        Assert.DoesNotContain(ConversationLog.Visible(Log(Call("r1", "c1", "t")), null, owner: false), e => e is Called);

    [Fact]
    public void ReadSinceReturnsOnlyLaterEntries() =>
        Assert.Single(ConversationLog.Visible(LogAt((1, Said("r1", "a")), (2, Reply("r1", "b"))), 1, owner: true));

    [Fact]
    public void TheLogKeepsTheNewest1024() =>
        Assert.Equal(1_024, ConversationLog.Append([], 1, [.. Enumerable.Range(0, 1_100).Select(i => Said($"r{i}", "x"))]).Length);

    [Fact]
    public void DuplicateEntryIdsAreRefused()
    {
        var log = Log(Said("r1", "a"));
        Assert.Throws<ArgumentException>(() => ConversationLog.Append(log, 2, Said("r1", "b")));
    }

    [Fact]
    public void CoveredPayloadsShrinkToPreviews()
    {
        var hugeJson = """{"data":" """ + new string('a', 4_000) + """ "}""";
        var log = LogAt(
            (1, Said("r1", "go")),
            (2, Call("r1", "c1", "files_open", hugeJson)),
            (3, Result("r1", "c1", "files_open", hugeJson)),
            (4, Reply("r1", "done")),
            (5, new Summarized(EntryIds.Summary("r2"), Agent, At, "earlier", EntryIds.Reply("r1"))));

        var returned = Assert.Single(log.Select(static x => x.Entry).OfType<Returned>());
        Assert.True(returned.ResultJson.Length <= ConversationLog.CoveredPreview, $"Expected <= {ConversationLog.CoveredPreview}, got {returned.ResultJson.Length}");
        using var doc = JsonDocument.Parse(returned.ResultJson);
        Assert.True(doc.RootElement.GetProperty("truncated").GetBoolean());

        var called = Assert.Single(log.Select(static x => x.Entry).OfType<Called>());
        Assert.True(called.ArgsJson.Length <= ConversationLog.CoveredPreview, $"Expected <= {ConversationLog.CoveredPreview}, got {called.ArgsJson.Length}");
        using var callDoc = JsonDocument.Parse(called.ArgsJson);
        Assert.True(callDoc.RootElement.GetProperty("truncated").GetBoolean());
    }
}
