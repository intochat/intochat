using DigitalBrain.UI;
using IntoChat.OS.Apps.Ino;
using IntoChat.OS.Host.Primitives;
using IntoChat.OS.Modules.AI.Agents;
using IntoChat.OS.Modules.AI.Models;
using IntoChat.OS.Modules.Apps;
using IntoChat.OS.Modules.Flutter.Conversations;
using IntoChat.OS.Modules.Identity.Contracts;

namespace IntoChat.OS.Apps.Tests.Unit.Ino;

public sealed class InoThreadFacts
{
    private static readonly DateTimeOffset At = new(2026, 11, 20, 12, 0, 0, TimeSpan.Zero);
    private static readonly Participant.Human Owner = new(new AccountId("owner"));
    private static readonly Participant.Assistant Ino = new("ino");
    private static readonly IReadOnlyDictionary<string, AppManifest?> NoApps = new Dictionary<string, AppManifest?>(StringComparer.Ordinal);

    private static ConversationView View(params ConversationEntry[] entries) =>
        new(ConversationId.Parse("main"), 1, [Owner, Ino], entries, null, [], false, []);

    private static Said Said(string runId, string text) =>
        new(EntryIds.Said(runId), Owner, At, text);

    private static Said Reply(string runId, string text, ModelId? model = null) =>
        new(EntryIds.Reply(runId), Ino, At, text, model);

    private static Said ReplyReplacing(string runId, string text, EntryId replaces, ModelId? model = null) =>
        new(EntryIds.Reply(runId), Ino, At, text, model, replaces);

    private static AgentRun Running(string runId, string partialText) =>
        new(runId, AgentRunStatus.Running, "hi", At, Reply: partialText);

    [Fact]
    public void ASupersededReplyIsNotDrawn() =>
        Assert.Equal(
            ["hi", "new"],
            InoThread.Messages(
                View(Said("r1", "hi"), Reply("r1", "old"), ReplyReplacing("r2", "new", EntryIds.Reply("r1"))),
                NoApps,
                null).Select(m => m.Text));

    [Fact]
    public void TheActiveRunIsARunningBubbleUnderItsReplyId()
    {
        var last = InoThread.Messages(View(Said("r1", "hi")) with { Active = Running("r1", "par") }, NoApps, null)[^1];
        Assert.Equal((EntryIds.Reply("r1").Value, "running", "par"), (last.Id, last.Status, last.Text));
    }

    [Fact]
    public void AFailedRunDrawsItsReasonAfterItsPrompt()
    {
        var failed = new AgentRun("r1", AgentRunStatus.Failed, "hi", At, Error: "503") { Failure = AgentFailure.ProviderUnavailable };
        var view = View(Said("r1", "hi")) with { Runs = [failed] };

        var messages = InoThread.Messages(view, NoApps, null);

        Assert.Equal(2, messages.Count);
        Assert.Equal(("user", "hi"), (messages[0].Role, messages[0].Text));
        Assert.Equal((EntryIds.Reply("r1").Value, "assistant", "failed", "The model provider did not answer."),
            (messages[1].Id, messages[1].Role, messages[1].Status, messages[1].Failure));
    }

    [Fact]
    public void ACancelledRunDrawsPartialTextAndStoppedReasonAfterItsPrompt()
    {
        var cancelled = new AgentRun("r1", AgentRunStatus.Cancelled, "hi", At, Reply: "partial ans", Error: "Cancelled");
        var view = View(Said("r1", "hi")) with { Runs = [cancelled] };

        var messages = InoThread.Messages(view, NoApps, null);

        Assert.Equal(2, messages.Count);
        Assert.Equal((EntryIds.Reply("r1").Value, "assistant", "cancelled", "partial ans", "Stopped."),
            (messages[1].Id, messages[1].Role, messages[1].Status, messages[1].Text, messages[1].Failure));
        Assert.True(messages[1].IsFailed);
    }

    [Fact]
    public void ANodeCardIsDrawnFromTheReturnedEntry()
    {
        const string nodeJson = """{"kind":"column","children":[{"kind":"text","value":"Apple"}]}""";
        var returned = new Returned(
            EntryIds.Result("r1", "c1"),
            Ino,
            At,
            "focus_show",
            """{"text":"Shown.","ok":true}""",
            Ok: true,
            Card: new CardEntry("node", "Fruit", nodeJson));
        var view = View(Said("r1", "show fruit"), returned, Reply("r1", "Here you go."));

        var assistant = InoThread.Messages(view, NoApps, null)[^1];

        var card = Assert.IsType<Card>(Assert.Single(assistant.Cards));
        Assert.Equal("Fruit", card.Title);
        var column = Assert.IsType<Column>(Assert.Single(card.Children));
        var text = Assert.IsType<Text>(Assert.Single(column.Children));
        Assert.Equal("Apple", text.Value);
    }

    [Fact]
    public void AnOpenedAppBecomesACard()
    {
        var call = new AgentToolCall(
            "c1",
            "apps_app2_request",
            """{"key":"local/apps/notes","operation":"open","input":"page-7"}""",
            Result: """{"ok":true}""");
        var returned = new Returned(
            EntryIds.Result("r1", "c1"),
            Ino,
            At,
            "apps_app2_request",
            """{"ok":true}""",
            Ok: true,
            Card: null);
        var run = new AgentRun("r1", AgentRunStatus.Completed, "open notes", At, Reply: "Opened.") { ToolCalls = [call] };
        var apps = new Dictionary<string, AppManifest?>(StringComparer.Ordinal)
        {
            ["notes"] = new AppManifest("Notes", "Take notes", [], [], [], "notebook"),
        };
        var view = View(Said("r1", "open notes"), returned, Reply("r1", "Opened.")) with { Runs = [run] };

        var assistant = InoThread.Messages(view, apps, null)[^1];

        var card = Assert.IsType<RecordCardNode>(Assert.Single(assistant.Cards));
        Assert.Equal("Notes", card.Name);
        Assert.Equal("page-7", card.Subtitle);
        Assert.NotNull(card.Open);

        var appCard = InoThread.Opened(returned, call.Arguments);
        Assert.Equal(new InoLinkCard("notes", "notes", "", true, "page-7"), appCard);
        Assert.Equal(
            new InoLinkCard("notes", "Notes", "notebook", true, "page-7"),
            InoThread.Named(appCard!, apps["notes"]));
    }
}
