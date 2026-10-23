using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;
using IntoChat.OS.AI.Agents;
using IntoChat.OS.AI.Agents.Context;
using IntoChat.OS.AI.Agents.Facts;
using IntoChat.OS.AI.Models;
using IntoChat.OS.AI.Models.OpenAI;
using IntoChat.OS.Apps;
using IntoChat.OS.Flutter.Text;
using IntoChat.OS.Postgres.Tables;
using IntoChat.OS.Testing.Model;

namespace IntoChat.OS.Kernel.Tests.E2E;

// The memory loop, live: an Ino turn is captured by Memory as an episode, consolidated once the brain is quiet
// into a profile fact (or a review item, when it is sensitive), rendered into the next conversation's context,
// forgotten on the model's request and recalled from the life index with its source. Ino's turn, Memory's capture
// and its consolidation all reach the catalog default model; the fake answers each by its prompt, so a capture or
// consolidation of an earlier fact's turn never takes another's reply. QuietMinutes is "0" (the one-second floor).
[Collection("Router host")]
public sealed class MemoryLoopFacts(RouterHostFixture host) : BrainFact(host)
{
    private const string InoPrompt = "You are the user's assistant";
    private const string IntendPrompt = "Your only job is to rephrase it";
    private const string CapturePrompt = RouterHostFixture.CapturePrompt;
    private const string ConsolidatePrompt = RouterHostFixture.ConsolidatePrompt;
    private const string Prefers = "prefers short answers";
    private const string Allergy = "has a peanut allergy";
    private const string ShortAsk = "keep your answers short from now on";
    private const string ShortNote = "short answers from now on";
    private const string AllergyNote = "peanut allergy";

    private static string ModelId => LLMModel.FindByMarker(typeof(IGpt56Luna))!.Id;

    private string MemoryKey => IApp.Key(WorkspaceId, "memory");

    [Fact(Timeout = 300_000)]
    public Task ATurnBecomesAnEpisodeAndThenAProfileFact() => AssertTurnBecomesAProfileFact(TestContext.Current.CancellationToken);

    [Fact(Timeout = 300_000)]
    public Task ATurnBecomesAProfileFactAfterTheModelWasScriptedAndCleared()
    {
        Host.Model.Script(ModelId, FakeReply.Text("A prior scripted reply."));
        Host.Model.Clear(ModelId);
        return AssertTurnBecomesAProfileFact(TestContext.Current.CancellationToken);
    }

    private async Task AssertTurnBecomesAProfileFact(CancellationToken ct)
    {
        using var caller = User.AsCaller();
        using var model = Answering();
        await QuietNow(ct);
        var known = (await Facts().Read()).Facts.Select(fact => fact.Id).ToHashSet();
        var episodes = (await Episodes()).Select(row => row.Id).ToHashSet(StringComparer.Ordinal);

        await Send("send", ShortAsk, ct);

        var fact = await Waiting(TestWait.UntilAsync(_ => Facts().Current(),
            current => current.Any(fact => !known.Contains(fact.Id) && fact.Kind == FactKind.Profile && fact.Text == Prefers),
            TimeSpan.FromSeconds(120), ct));
        Assert.Contains(fact, fact => !known.Contains(fact.Id) && fact.Kind == FactKind.Profile && fact.Text == Prefers);
        // The turn's episode (agent#run) carries the capture note and is marked consolidated once the fact is written.
        var episode = await Waiting(TestWait.UntilAsync(async _ => (await Episodes()).FirstOrDefault(row => !episodes.Contains(row.Id) && row.Summary.Contains(ShortNote, StringComparison.Ordinal)),
            row => row is { ConsolidatedAt: not null }, TimeSpan.FromSeconds(60), ct));
        Assert.Contains("#", episode!.Id, StringComparison.Ordinal);
        Assert.Equal(6, episode.Importance);
        var source = episode.Id.Split('#', 2);
        var run = Assert.Single((await Brain.Get<IAgent>(source[0]).Read()).Runs, run => run.RunId == source[1]);
        Assert.Equal(AgentRunStatus.Completed, run.Status);
        Assert.Equal(ShortAsk, run.UserText);
    }

    [Fact(Timeout = 300_000)]
    public async Task AProfileFactIsRenderedInTheNextConversation()
    {
        var ct = TestContext.Current.CancellationToken;
        using var caller = User.AsCaller();
        using var model = Answering();
        await QuietNow(ct);
        await EnsureProfile(ct);
        await ProfileEntry(text => text.Contains(Prefers, StringComparison.Ordinal), ct);
        await Send("new", "", ct);

        var before = Host.Model.Requests(ModelId).Count;
        await Send("send", "hello", ct);
        var ino = await InoRequest(before, "hello", ct);
        // Memory's context entry "profile" renders each current profile fact as a line; Ino attaches it to the turn.
        Assert.Contains("- " + Prefers, ino, StringComparison.Ordinal);
        var profile = Assert.Single((await Context().Read()).Entries, entry => entry.Name == "profile");
        Assert.Contains("- " + Prefers, profile.Text, StringComparison.Ordinal);
        Assert.Contains(profile.Text.Trim(), ino, StringComparison.Ordinal);
    }

    [Fact(Timeout = 300_000)]
    public async Task AHealthFactGoesToReviewNotToFacts()
    {
        var ct = TestContext.Current.CancellationToken;
        using var caller = User.AsCaller();
        using var model = Answering();
        await QuietNow(ct);
        var episodes = (await Episodes()).Select(row => row.Id).ToHashSet(StringComparer.Ordinal);

        await Send("send", "I have a peanut allergy, keep that in mind", ct);

        // The consolidation publishes it with confidence 0.95, but Classify holds health back for review.
        var episode = await Waiting(TestWait.UntilAsync(async _ => (await Episodes()).FirstOrDefault(row => !episodes.Contains(row.Id) && row.Summary.Contains(AllergyNote, StringComparison.Ordinal)),
            row => row is { ConsolidatedAt: not null }, TimeSpan.FromSeconds(120), ct));
        var rows = (await Reviews()).Where(row => row.Text == Allergy && row.EpisodeIds.Split(',').Contains(episode!.Id)).ToArray();
        var review = Assert.Single(rows);
        Assert.Equal("pending", review.Status);
        Assert.StartsWith("sensitive: health", review.Reason, StringComparison.Ordinal);
        Assert.DoesNotContain(await Facts().Current(), fact => fact.Text.Contains("peanut", StringComparison.OrdinalIgnoreCase));
    }

    [Fact(Timeout = 300_000)]
    public async Task ForgetHidesTheFactEverywhere()
    {
        var ct = TestContext.Current.CancellationToken;
        using var caller = User.AsCaller();
        await QuietNow(ct);
        var target = await OneProfileFact(ct);
        var revision = (await Facts().Read()).Revision;
        var call = JsonSerializer.Serialize(new
        {
            contract = typeof(IFacts).FullName,
            key = IFacts.Key(WorkspaceId),
            method = nameof(IFacts.Forget),
            arguments = JsonSerializer.Serialize(new object[] { target.Id.ToString("D"), "the user asked to forget it", revision }),
        });
        const string Ask = "forget that I like short answers";
        using var model = Answering(request => RouterHostFixture.Text(request).Contains(Ask, StringComparison.Ordinal)
            ? Called(request) ? FakeReply.Text("Forgotten.") : FakeReply.Tool("neurons_call", call)
            : null);
        await Send("new", "", ct);
        await Send("send", Ask, ct);

        await Waiting(TestWait.UntilAsync(_ => Facts().Current(), current => current.Length == 0, TimeSpan.FromSeconds(60), ct));
        await ProfileEntry(text => !text.Contains(Prefers, StringComparison.Ordinal), ct);
        await Send("new", "", ct);
        var before = Host.Model.Requests(ModelId).Count;
        await Send("send", "hello again", ct);
        Assert.DoesNotContain(Prefers, await InoRequest(before, "hello again", ct), StringComparison.Ordinal);

        var forgotten = Assert.Single((await Facts().Read()).Facts, fact => fact.Id == target.Id);
        Assert.True(forgotten.Hidden);
        Assert.Equal(Prefers, forgotten.Text);
    }

    [Fact(Timeout = 300_000)]
    public async Task RecallAnswersFromTheLifeIndexWithSources()
    {
        var ct = TestContext.Current.CancellationToken;
        using var caller = User.AsCaller();
        using var model = Answering();
        await QuietNow(ct);
        var fact = await OneProfileFact(ct);

        var request = await Brain.Get<IApp>(MemoryKey).Request("recall-life", "what do I prefer");
        var answer = await Waiting(TestWait.UntilAsync(_ => Brain.Get<IText>(MemoryKey + "/recalls/" + request.ToString("D")).Read(),
            text => text.Markdown.Length > 0, TimeSpan.FromSeconds(60), ct));

        // The fact line: its subject and text, then its source (its kind, the day it became valid, its id).
        Assert.True(answer.Markdown.Contains("Found", StringComparison.Ordinal), answer.Markdown + " life index: " + await LifeIndex());
        Assert.Contains(Prefers, answer.Markdown, StringComparison.Ordinal);
        Assert.Contains("source: profile " + fact.ValidFrom.UtcDateTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + " fact:" + fact.Id.ToString("D"),
            answer.Markdown, StringComparison.Ordinal);
    }

    // What the loop got to, for a wait that times out.
    private async Task<T> Waiting<T>(Task<T> wait)
    {
        try { return await wait; }
        catch (TimeoutException error)
        {
            var texts = Host.Model.Requests(ModelId).Select(RouterHostFixture.Text).ToList();
            string Count(string marker) => marker + "=" + texts.Count(text => text.Contains(marker, StringComparison.Ordinal));
            string episodes;
            try { episodes = string.Join(" | ", (await Episodes()).Select(row => row.Id + " " + row.Importance + " " + (row.ConsolidatedAt is null ? "open" : "done") + " " + row.Summary.ReplaceLineEndings(" "))); }
            catch (Exception failure) { episodes = "unreadable: " + failure.Message; }
            var lines = new[]
            {
                error.Message,
                "requests: " + string.Join(", ", Count(InoPrompt), Count(IntendPrompt), Count(CapturePrompt), Count(ConsolidatePrompt)),
                "memory status: " + (await Brain.Get<IText>(MemoryKey + "/status").Read()).Markdown,
                "ino status: " + (await Brain.Get<IText>(WorkspaceId + "/apps/ino/status").Read()).Markdown,
                "settings: " + string.Join(",", (await Brain.Get<IApp>(MemoryKey).Read()).Settings.Select(pair => pair.Key + "=" + pair.Value)),
                "episodes: " + episodes,
                "facts: " + string.Join(" | ", (await Facts().Read()).Facts.Select(fact => fact.Kind + " " + fact.Text + (fact.Hidden ? " hidden" : ""))),
            };
            throw new TimeoutException(string.Join(Environment.NewLine, lines), error);
        }
    }

    private async Task<string> LifeIndex()
    {
        try
        {
            var index = Brain.Get<IntoChat.OS.AI.Embeddings.IVectorIndex>(IntoChat.OS.AI.Embeddings.IVectorIndex.Key(WorkspaceId, "life"));
            var snapshot = await index.Read();
            var near = await index.Nearest(FakeModelServer.WordHash("what do I prefer"), 20);
            return snapshot.Count + " entries, " + snapshot.Dimensions + " dims; nearest: "
                + string.Join(" | ", near.Select(match => match.Id + "=" + match.Score.ToString("0.000", CultureInfo.InvariantCulture) + " " + match.Payload.GetValueOrDefault("text")));
        }
        catch (Exception error) { return "unreadable: " + error.Message; }
    }

    // Exactly one current fact, the profile fact: one is made when there is none, and any other is forgotten.
    private async Task<Fact> OneProfileFact(CancellationToken ct)
    {
        using (Answering()) { await EnsureProfile(ct); }
        var current = await Facts().Current();
        var keep = current.Where(fact => fact.Kind == FactKind.Profile && fact.Text == Prefers).OrderByDescending(fact => fact.Recorded).First();
        foreach (var other in current.Where(fact => fact.Id != keep.Id))
        { await Facts().Forget(other.Id, "test setup: one profile fact", (await Facts().Read()).Revision); }
        return keep;
    }

    private async Task EnsureProfile(CancellationToken ct)
    {
        if ((await Facts().Current()).Any(fact => fact.Kind == FactKind.Profile && fact.Text == Prefers)) { return; }
        await Send("send", ShortAsk, ct);
        await Waiting(TestWait.UntilAsync(_ => Facts().Current(),
            current => current.Any(fact => fact.Kind == FactKind.Profile && fact.Text == Prefers), TimeSpan.FromSeconds(120), ct));
    }

    // Consolidation runs on the first tick after a capture: QuietMinutes is set to "0" once for the host.
    private async Task QuietNow(CancellationToken ct)
    {
        var memory = Brain.Get<IApp>(MemoryKey);
        if ((await memory.Read()).Settings.GetValueOrDefault("QuietMinutes") == "0") { return; }
        await memory.Configure(new() { ["QuietMinutes"] = "0" }, []).WaitAsync(ct);
    }

    private async Task<string> ProfileEntry(Func<string, bool> holds, CancellationToken ct)
        => await Waiting(TestWait.UntilAsync(async _ => (await Context().Read()).Entries.FirstOrDefault(entry => entry.Name == "profile")?.Text ?? "",
            holds, TimeSpan.FromSeconds(60), ct));

    private IFacts Facts() => Brain.Get<IFacts>(IFacts.Key(WorkspaceId));

    private IAgentContext Context() => Brain.Get<IAgentContext>(MemoryKey + "/context");

    private async Task<List<EpisodeRow>> Episodes()
    {
        var rows = await Rows(MemoryKey + "/episodes");
        return [.. rows.Select(cells => new EpisodeRow(Cell(cells, "id"), Cell(cells, "summary"),
            int.TryParse(cells.GetValueOrDefault("importance"), CultureInfo.InvariantCulture, out var importance) ? importance : 0,
            DateTimeOffset.TryParse(Cell(cells, "consolidated_at"), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var at) ? at : null))];
    }

    private async Task<List<ReviewRow>> Reviews()
    {
        var rows = await Rows(MemoryKey + "/review-items");
        return [.. rows.Select(cells => new ReviewRow(Cell(cells, "id"), Cell(cells, "text"), Cell(cells, "episode_ids"), Cell(cells, "reason"), Cell(cells, "status")))];
    }

    // Memory's tables answer only Memory: the read is made as the app.
    private async Task<List<Dictionary<string, string>>> Rows(string key)
    {
        using var app = User.AsApp(MemoryKey);
        var table = Brain.Get<ITable>(key);
        var rows = new List<Dictionary<string, string>>();
        for (var offset = 0; ; offset += 200)
        {
            TableValue[][] page;
            try { page = await table.Page(offset, 200); }
            catch (Exception) when (offset == 0) { return rows; } // not defined yet
            rows.AddRange(page.Select(cells => cells.ToDictionary(cell => cell.Column, cell => cell.Json, StringComparer.Ordinal)));
            if (page.Length < 200) { return rows; }
        }
    }

    private static string Cell(Dictionary<string, string> cells, string column)
    {
        if (!cells.TryGetValue(column, out var json)) { return ""; }
        using var document = JsonDocument.Parse(json);
        return document.RootElement.ValueKind == JsonValueKind.String ? document.RootElement.GetString() ?? "" : "";
    }

    private async Task Send(string operation, string input, CancellationToken ct)
    {
        using var sent = await User.Http.PostAsJsonAsync($"/brains/{WorkspaceId}/apps/ino/request", new { operation, input }, ct);
        sent.EnsureSuccessStatusCode();
    }

    // The first request of Ino's turn on this message since `before`.
    private async Task<string> InoRequest(int before, string message, CancellationToken ct)
        => (await Waiting(TestWait.UntilAsync(_ => Task.FromResult(Host.Model.Requests(ModelId).Skip(before).Select(RouterHostFixture.Text)
                .FirstOrDefault(text => text.Contains(InoPrompt, StringComparison.Ordinal) && text.Contains(message, StringComparison.Ordinal))),
            text => text is not null, TimeSpan.FromSeconds(60), ct)))!;

    // The fake model for these facts: Jev is unsure, so Memory's intend agent writes a low-confidence brief, Ino's turn
    // answers (through `ino` when it has an answer), and Memory's capture and consolidation get replies keyed by the
    // turn's own words. Restored when disposed.
    private IDisposable Answering(Func<JsonElement, FakeReply?>? ino = null)
    {
        var fallback = Host.Model.ChatFallback;
        var intercept = Host.Model.Intercept;
        var route = Host.Model.SystemOneOverride;
        Host.Model.Clear(ModelId);
        Host.Model.SystemOneOverride = request => RouterHostFixture.Choose(request, "router.ask", "ask", 0.2);
        Host.Model.ChatFallback = request =>
        {
            var text = RouterHostFixture.Text(request);
            if (text.Contains(IntendPrompt, StringComparison.Ordinal)) { return Intend(); }
            if (text.Contains(InoPrompt, StringComparison.Ordinal)) { return ino?.Invoke(request) ?? FakeReply.Text("Understood."); }
            return fallback?.Invoke(request);
        };
        Host.Model.Intercept = request =>
        {
            var text = RouterHostFixture.Text(request);
            return text.Contains(CapturePrompt, StringComparison.Ordinal) ? Capture(LastUser(request))
                : text.Contains(ConsolidatePrompt, StringComparison.Ordinal) ? Consolidate(LastUser(request))
                : null;
        };
        return new Restore(() => { Host.Model.ChatFallback = fallback; Host.Model.Intercept = intercept; Host.Model.SystemOneOverride = route; });
    }

    private static FakeReply Intend()
        => FakeReply.Text(JsonSerializer.Serialize(new
        {
            brief = new { goal = "Reply to the user's message", inputs = Array.Empty<string>(), constraints = Array.Empty<string>(), acceptance = new[] { "The user has a reply" } },
            confidence = 0.3,
            continues = (string?)null,
        }));

    // The capture input is JSON {user, tools, reply}: the note follows what the user said.
    private static FakeReply Capture(string input)
    {
        var user = "";
        try { using var document = JsonDocument.Parse(input); user = document.RootElement.GetProperty("user").GetString() ?? ""; }
        catch (Exception error) when (error is JsonException or KeyNotFoundException or InvalidOperationException) { }
        var (summary, importance) = user.Contains(ShortAsk, StringComparison.Ordinal)
            ? ("- The user asked for " + ShortNote + ".\n- The assistant agreed.", 6)
            : user.Contains("peanut", StringComparison.Ordinal)
                ? ("- The user mentioned a " + AllergyNote + ".\n- The assistant noted it.", 7)
                : ("- The user sent a message.\n- The assistant replied.", 3);
        return FakeReply.Text(JsonSerializer.Serialize(new { summary, importance }));
    }

    // The consolidation input is JSON with the batch's episodes: a short-answers note becomes a profile fact, an
    // allergy note is published as a fact (Classify sends it to review), anything else yields nothing.
    private static FakeReply Consolidate(string input)
    {
        var items = new List<object>();
        try
        {
            using var document = JsonDocument.Parse(input);
            foreach (var episode in document.RootElement.GetProperty("episodes").EnumerateArray())
            {
                var id = episode.GetProperty("id").GetString() ?? "";
                var summary = episode.GetProperty("summary").GetString() ?? "";
                if (summary.Contains(ShortNote, StringComparison.Ordinal))
                {
                    items.Add(new { disposition = "publish", episodeIds = new[] { id }, kind = "profile", text = Prefers, subject = "user", confidence = 0.9, importance = 6, reason = "stated preference", contradicts = (string?)null });
                }
                else if (summary.Contains(AllergyNote, StringComparison.Ordinal))
                {
                    items.Add(new { disposition = "publish", episodeIds = new[] { id }, kind = "fact", text = Allergy, subject = "food", confidence = 0.95, importance = 8, reason = "stated", contradicts = (string?)null });
                }
            }
        }
        catch (Exception error) when (error is JsonException or KeyNotFoundException or InvalidOperationException) { }
        return FakeReply.Text(JsonSerializer.Serialize(new { items }));
    }

    private static string LastUser(JsonElement request)
    {
        var user = request.GetProperty("messages").EnumerateArray().LastOrDefault(message => message.GetProperty("role").GetString() == "user");
        if (user.ValueKind != JsonValueKind.Object) { return ""; }
        var content = user.GetProperty("content");
        return content.ValueKind == JsonValueKind.String ? content.GetString() ?? ""
            : string.Concat(content.EnumerateArray().Where(part => part.TryGetProperty("text", out _)).Select(part => part.GetProperty("text").GetString()));
    }

    // The model already called the tool in this turn when the request carries its result.
    private static bool Called(JsonElement request)
        => request.GetProperty("messages").EnumerateArray().Any(message => message.GetProperty("role").GetString() == "tool");

    private sealed record EpisodeRow(string Id, string Summary, int Importance, DateTimeOffset? ConsolidatedAt);

    private sealed record ReviewRow(string Id, string Text, string EpisodeIds, string Reason, string Status);

    private sealed class Restore(Action undo) : IDisposable
    {
        public void Dispose() => undo();
    }
}
