using DigitalBrain.Invocation;
using IntoChat.OS.Modules.AI.Agents;
using IntoChat.OS.Modules.AI.Conversations;
using IntoChat.OS.Modules.AI.Inference;
using IntoChat.OS.Modules.Flutter.Conversations;
using IntoChat.OS.Modules.Identity.Contracts;

namespace IntoChat.OS.Tests.Unit.Modules.AI.Conversations;

public sealed class LegacyImportFacts
{
    private static readonly string LegacyDir = Path.Combine(AppContext.BaseDirectory, "Modules", "AI", "Conversations", "Legacy");

    public static TheoryData<string> Captured => new()
    {
        "main",
        "conv1",
        "conv2",
        "edge",
    };

    private static (AgentState State, AiMessage[] Transcript) LoadLegacy(string name)
    {
        var stateBytes = File.ReadAllBytes(Path.Combine(LegacyDir, $"{name}.agent-state.json"));
        var transcriptBytes = File.ReadAllBytes(Path.Combine(LegacyDir, $"{name}.transcript.json"));
        var state = (AgentState)RuntimeProtocol.Deserialize(stateBytes, typeof(AgentState))!;
        var transcript = (AiMessage[])RuntimeProtocol.Deserialize(transcriptBytes, typeof(AiMessage[]))!;
        return (state, transcript);
    }

    [Theory, MemberData(nameof(Captured))]
    public void EveryRunBecomesEntriesInOrder(string name)
    {
        var (state, transcript) = LoadLegacy(name);
        var (entries, runs) = LegacyImport.From(state, transcript, new AccountId("a1"), "ino");
        Assert.Equal(state.Runs.Count(r => !r.Posted), entries.OfType<Said>().Count(s => s.By is Participant.Human));
        Assert.Equal(state.Runs.Count(r => r.Status == AgentRunStatus.Completed && !r.Posted), entries.OfType<Said>().Count(s => s.Id.Value.EndsWith("/reply", StringComparison.Ordinal)));
        Assert.Equal(state.Runs.Length, runs.Length);
        Assert.True(entries.Select(e => e.At).SequenceEqual(entries.Select(e => e.At).Order()));
    }

    [Fact]
    public void TheEdgeFileKeepsLinksFailuresAndTheSummary()
    {
        var (state, transcript) = LoadLegacy("edge");
        var (entries, runs) = LegacyImport.From(state, transcript, new AccountId("a1"), "ino");
        Assert.Contains(entries, e => e.Id.Value.StartsWith("post/link-", StringComparison.Ordinal));
        Assert.Contains(runs, r => r.Status == AgentRunStatus.Failed && r.Failure != AgentFailure.Unknown);
        Assert.IsType<Summarized>(entries[0]);
        Assert.Contains(entries.OfType<Returned>(), r => r.ResultJson.Contains("\"truncated\":true", StringComparison.Ordinal));
    }

    [Fact]
    public void ConversationCardsParsesTopLevelCardObject()
    {
        var card = ConversationCards.FromResult("""{"summary":"Shown.","card":{"kind":"node","title":"Fruit","node":"{\"kind\":\"text\",\"value\":\"Apple\"}"}}""");
        Assert.NotNull(card);
        Assert.Equal("node", card!.Kind);
        Assert.Equal("Fruit", card.Title);
        Assert.Equal("""{"kind":"text","value":"Apple"}""", card.NodeJson);
        Assert.Null(ConversationCards.FromResult("""{"summary":"No card here."}"""));
        Assert.Null(ConversationCards.FromResult("not json"));
    }
}
