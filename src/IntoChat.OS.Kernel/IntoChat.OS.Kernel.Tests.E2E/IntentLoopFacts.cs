using System.Net.Http.Json;
using System.Text.Json;
using IntoChat.OS.AI.Agents;
using IntoChat.OS.AI.Agents.Intents;
using IntoChat.OS.Apps;
using IntoChat.OS.AI.Models;
using IntoChat.OS.AI.Models.OpenAI;
using IntoChat.OS.Flutter.Text;
using IntoChat.OS.Testing.Model;

namespace IntoChat.OS.Kernel.Tests.E2E;

// The intent loop, live: Jev is unsure, so Ino hands the message to Memory, Memory's agent rephrases it into
// a brief and records an intent, and Ino's turn acts on the brief (or answers without it when Memory is slow).
// Memory's intend turn and Ino's turn both use the catalog default model, in that order.
[Collection("Router host")]
public sealed class IntentLoopFacts(RouterHostFixture host) : BrainFact(host)
{
    private const string Goal = "Draft the launch email";
    private const string Act = "Act on this brief";
    private const string InoPrompt = "You are the user's assistant";
    private const string MemoryPrompt = "Your only job is to rephrase it";

    // The router host pins the default chat model to this one.
    private static string ModelId => LLMModel.FindByMarker(typeof(IGpt56Luna))!.Id;

    [Fact(Timeout = 300_000)]
    public async Task AVagueAskBecomesABriefTheModelActsOn()
    {
        var ct = TestContext.Current.CancellationToken;
        using var caller = User.AsCaller();
        var previous = Host.Model.SystemOneOverride;
        Host.Model.SystemOneOverride = Unsure;
        try
        {
            var before = Host.Model.Requests(ModelId).Count;
            await DraftLaunchEmail(ct);
            // The intent is Dispatched just before Ino's turn starts, so its model request is waited for.
            var ino = await InoRequest(before, ct);
            Assert.Contains(Host.Model.Requests(ModelId).Skip(before).Select(RouterHostFixture.Text),
                text => text.Contains(MemoryPrompt, StringComparison.Ordinal) && text.Contains("can you get the launch email going", StringComparison.Ordinal));
            Assert.Contains(Act, ino, StringComparison.Ordinal);
            Assert.Contains(Goal, ino, StringComparison.Ordinal);
            await AssertIntendBeforeTurn(before, "can you get the launch email going", null, ct);
        }
        finally { Host.Model.SystemOneOverride = previous; Host.Model.Clear(ModelId); }
    }

    [Fact(Timeout = 300_000)]
    public async Task AnActiveIntentIsRenderedInTheNextConversation()
    {
        var ct = TestContext.Current.CancellationToken;
        using var caller = User.AsCaller();
        var previous = Host.Model.SystemOneOverride;
        Host.Model.SystemOneOverride = Unsure;
        try
        {
            if (!(await Intents().Read()).Intents.Any(intent => intent.State == IntentState.Dispatched && intent.Brief?.Goal == Goal))
            { await DraftLaunchEmail(ct); }
            await Send("new", "", ct);

            var before = Host.Model.Requests(ModelId).Count;
            Host.Model.Script(ModelId, IntendReply("Say hello", 0.3), "Hi.");
            await Send("send", "hello", ct);
            // The active-intents entry renders "`{id}` {State}: {goal}".
            Assert.Contains("Dispatched: " + Goal, await InoRequest(before, ct), StringComparison.Ordinal);
            await AssertIntendBeforeTurn(before, "hello", "Say hello", ct);
        }
        finally { Host.Model.SystemOneOverride = previous; Host.Model.Clear(ModelId); }
    }

    [Fact(Timeout = 300_000)]
    public async Task TheModelCanDeclareAnIntentExplicitly()
    {
        var ct = TestContext.Current.CancellationToken;
        using var caller = User.AsCaller();
        var scope = WorkspaceId;
        var previous = Host.Model.SystemOneOverride;
        Host.Model.SystemOneOverride = Unsure;
        try
        {
            var draft = JsonSerializer.Serialize(new[] { new { requestId = "explicit-1", heard = "remind me to call Ann", conversation = scope + "/apps/ino/conversations/main" } });
            var call = JsonSerializer.Serialize(new { contract = typeof(IIntents).FullName, key = IIntents.Key(scope), method = nameof(IIntents.Declare), arguments = draft });
            var before = Host.Model.Requests(ModelId).Count;
            Host.Model.Script(ModelId, IntendReply("Remind the user to call Ann", 0.3), FakeReply.Tool("neurons_call", call), "Noted.");
            await Send("send", "remind me to call Ann", ct);
            await TestWait.UntilAsync(_ => Intents().Read(),
                snapshot => snapshot.Intents.Any(intent => intent.Heard == "remind me to call Ann" && intent.RequestId == "explicit-1"),
                TimeSpan.FromSeconds(60), ct);
            await AssertIntendBeforeTurn(before, "remind me to call Ann", null, ct);
        }
        finally { Host.Model.SystemOneOverride = previous; Host.Model.Clear(ModelId); }
    }

    [Fact(Timeout = 300_000)]
    public async Task ASlowIntendStillAnswers()
    {
        var ct = TestContext.Current.CancellationToken;
        using var caller = User.AsCaller();
        var previous = Host.Model.SystemOneOverride;
        Host.Model.SystemOneOverride = Unsure;
        try
        {
            var before = Host.Model.Requests(ModelId).Count;
            // Memory's intend turn is answered only well after IntendGate (8 s), and its late reply is not a brief,
            // so nothing is declared when it lands; Ino's turn takes the next reply.
            Host.Model.Script(ModelId, FakeReply.Delayed(TimeSpan.FromSeconds(12), "not a brief"), "Answered.");
            await Send("send", "what is on today", ct);
            var status = Brain.Get<IText>(WorkspaceId + "/apps/ino/status");
            await TestWait.UntilAsync(_ => status.Read(),
                text => text.Markdown.Contains("answering without a brief", StringComparison.Ordinal), TimeSpan.FromSeconds(15), ct);
            Assert.DoesNotContain(Act, await InoRequest(before, ct), StringComparison.Ordinal);
            await AssertIntendBeforeTurn(before, "what is on today", null, ct);
        }
        finally
        {
            Host.Model.SystemOneOverride = previous;
            Host.Model.Clear(ModelId);
            // Memory's intend turn is still waiting on its delayed reply: let it end, so the next fact finds the agent idle.
            var agent = Brain.Get<IAgent>(IApp.Key(WorkspaceId, "memory") + "/agents/intend");
            await TestWait.UntilAsync(_ => agent.Read(), state => state.ActiveRun is null, TimeSpan.FromSeconds(60), ct);
        }
    }

    [Fact(Timeout = 300_000)]
    public async Task AContinuedMessageRebriefsTheSameIntent()
    {
        var ct = TestContext.Current.CancellationToken;
        using var caller = User.AsCaller();
        var previous = Host.Model.SystemOneOverride;
        Host.Model.SystemOneOverride = Unsure;
        try
        {
            if (!(await Intents().Read()).Intents.Any(intent => intent.State == IntentState.Dispatched && intent.Brief?.Goal == Goal))
            { await DraftLaunchEmail(ct); }
            var first = (await Intents().Read()).Intents.Last(intent => intent.State == IntentState.Dispatched && intent.Brief?.Goal == Goal);
            const string French = "Draft the launch email in French";
            var status = Brain.Get<IText>(WorkspaceId + "/apps/ino/status");
            await status.Set("");

            var before = Host.Model.Requests(ModelId).Count;
            Host.Model.Script(ModelId, IntendReply(French, 0.95, first.Id), "Here it is in French.");
            await Send("send", "make it French", ct);
            // The first message's own Ino turn may still land after `before`, so the turn with the new brief is waited for.
            var ino = await TestWait.UntilAsync(_ => Task.FromResult(Host.Model.Requests(ModelId).Skip(before).Select(RouterHostFixture.Text)
                    .FirstOrDefault(text => text.Contains(InoPrompt, StringComparison.Ordinal) && text.Contains(French, StringComparison.Ordinal))),
                text => text is not null, TimeSpan.FromSeconds(60), ct);

            // routed.cs indexes its pending requests, newest last; Memory's answer to this one names the continued intent.
            var request = (await Brain.Get<IText>(WorkspaceId + "/apps/ino/pending/index-routed").Read()).Markdown.Split('\n')[^1];
            Assert.Equal("ok " + first.Id, (await Brain.Get<IText>(IApp.Key(WorkspaceId, "memory") + "/intents/" + request).Read()).Markdown);
            // The continued intent keeps its first request id; Memory names it in its answer, so Ino finds its new brief.
            // It is Dispatched, so the rephrase kept that state and the brief is shown rather than acted on.
            Assert.Contains("Show this brief to the user", ino, StringComparison.Ordinal);
            var after = await Intents().Read();
            var continued = Assert.Single(after.Intents, intent => intent.Id == first.Id);
            Assert.Equal(French, continued.Brief?.Goal);
            Assert.Equal(IntentState.Dispatched, continued.State);
            Assert.DoesNotContain(after.Intents, intent => intent.Heard == "make it French");
            Assert.DoesNotContain("Memory could not intend", (await status.Read()).Markdown, StringComparison.Ordinal);
            await AssertIntendBeforeTurn(before, "make it French", French, ct);
        }
        finally { Host.Model.SystemOneOverride = previous; Host.Model.Clear(ModelId); }
    }

    private static string Unsure(JsonElement request) => RouterHostFixture.Choose(request, "router.ask", "ask", 0.2);

    // Memory's intend request (it carries the message) comes before Ino's turn request in the model's request log.
    // Both are waited for: a previous message's late turn can still be landing after `before`.
    private async Task AssertIntendBeforeTurn(int before, string message, string? briefText, CancellationToken ct)
    {
        (int Intend, int Turn) Find()
        {
            // Only requests since `before` are read: earlier ones are not this message's.
            var texts = Host.Model.Requests(ModelId).Skip(before).Select(RouterHostFixture.Text).ToList();
            return (texts.FindIndex(text => text.Contains(MemoryPrompt, StringComparison.Ordinal) && text.Contains(message, StringComparison.Ordinal)),
                texts.FindIndex(text => text.Contains(InoPrompt, StringComparison.Ordinal)
                    && text.Contains(message, StringComparison.Ordinal)
                    && (briefText is null || text.Contains(briefText, StringComparison.Ordinal))));
        }
        var (intend, turn) = await TestWait.UntilAsync(_ => Task.FromResult(Find()), found => found.Intend >= 0 && found.Turn >= 0, TimeSpan.FromSeconds(60), ct);
        Assert.True(intend < turn, $"Memory's intend request ({intend}) came after Ino's turn request ({turn}).");
    }

    private IIntents Intents() => Brain.Get<IIntents>(IIntents.Key(WorkspaceId));

    private async Task DraftLaunchEmail(CancellationToken ct)
    {
        Host.Model.Script(ModelId, IntendReply(Goal, 0.95), "Drafted.");
        await Send("send", "can you get the launch email going", ct);
        await TestWait.UntilAsync(_ => Intents().Read(),
            snapshot => snapshot.Intents.Any(intent => intent.State is IntentState.Dispatched or IntentState.Done && intent.Brief?.Goal == Goal),
            TimeSpan.FromSeconds(60), ct);
    }

    private async Task Send(string operation, string input, CancellationToken ct)
    {
        using var sent = await User.Http.PostAsJsonAsync($"/brains/{WorkspaceId}/apps/ino/request", new { operation, input }, ct);
        sent.EnsureSuccessStatusCode();
    }

    // The first request of Ino's turn since `before`, waited for: the turn starts after Memory answers.
    private async Task<string> InoRequest(int before, CancellationToken ct)
        => (await TestWait.UntilAsync(_ => Task.FromResult(Host.Model.Requests(ModelId).Skip(before).Select(RouterHostFixture.Text)
                .FirstOrDefault(text => text.Contains(InoPrompt, StringComparison.Ordinal))),
            text => text is not null, TimeSpan.FromSeconds(60), ct))!;

    private static FakeReply IntendReply(string goal, double confidence, Guid? continues = null)
        => FakeReply.Text(JsonSerializer.Serialize(new
        {
            brief = new { goal, inputs = Array.Empty<string>(), constraints = Array.Empty<string>(), acceptance = new[] { goal + " is done" } },
            confidence,
            continues = continues?.ToString(),
        }));
}
