using System.Globalization;
using System.Text.Json;
using IntoChat.OS.Modules.AI.Agents;
using IntoChat.OS.Modules.AI.Agents.Facts;
using IntoChat.OS.Modules.AI.Embeddings;
using IntoChat.OS.Modules.Apps;
using IntoChat.OS.Modules.Flutter.Text;
using IntoChat.OS.Modules.Postgres.Tables;

// "recall-life" answers a question from the user's life: facts and episodes embedded in the brain's "life" index,
// ranked by similarity x recency (HalfLifeDays) x authority x importance. Every search is limited to this brain.
// A partial answer climbs the ladder: the neighbouring episodes of the best episode, then its transcript. The
// answer is written under the request id and as the latest answer; a request already answered is skipped.
await using var brain = await IntoChatOSClient.ConnectAsync(args);
var appKey = brain.Setting("App")!;
var app = brain.Get<IApp>(appKey);
var scope = Memory.Scope(brain);
var episodes = brain.Get<ITable>(Episodes.TableKey(appKey));
var life = brain.Get<IVectorIndex>(Recall.LifeKey(scope));
var facts = brain.Get<IFacts>(IFacts.Key(scope));

await foreach (var request in brain.On(app, "recall-life", brain.Stopping))
{
    var answered = brain.Get<IText>(appKey + "/recalls/" + request.RequestId);
    string answer;
    try
    {
        if ((await answered.Read()).Markdown.Length > 0) { continue; }
        answer = await Answer(request.Input);
    }
    catch (Exception error) when (!brain.Stopping.IsCancellationRequested)
    { answer = "# Recall: " + request.Input.Trim() + "\n\nMemory could not answer: " + error.Message; }
    await answered.Set(answer);
    await brain.Get<IText>(appKey + "/recall").Set(answer);
}

async Task<string> Answer(string question)
{
    await episodes.Define(Episodes.Definition);
    var settings = (await app.Read()).Settings;
    var halfLife = double.TryParse(settings.GetValueOrDefault("HalfLifeDays", "14"), CultureInfo.InvariantCulture, out var days) && days > 0 ? days : 14;
    var stored = await EpisodeRows();
    var current = await facts.Current();
    var coverage = new Recall.Coverage(stored.Count == 0 ? null : stored.Min(e => e.At), stored.Count == 0 ? null : stored.Max(e => e.At), current.Length);
    var ranked = new List<Recall.Ranked>();
    if (question.Trim().Length > 0)
    {
        var query = (await Memory.Embed(brain, [question]))[0];
        var nearest = await life.Nearest(query, 20, Recall.MustMatch(scope));
        var live = current.Select(fact => fact.Id.ToString("D")).ToHashSet(StringComparer.Ordinal);
        ranked = Recall.Rank(Recall.Current(nearest.Select(Recall.ToCandidate).OfType<Recall.Candidate>(), live), DateTimeOffset.UtcNow, halfLife);
    }
    var text = Recall.Render(question, ranked, coverage);
    if (Recall.Verdict([.. ranked.Select(item => item.Item.Similarity)]) != "Partial"
        || ranked.FirstOrDefault(item => item.Item.Tier == "episode") is not { } top) { return text; }
    var id = top.Item.Id["episode:".Length..];
    var rungTwo = Recall.Expand(id, stored);
    if (rungTwo.Count > 0)
    { text += "\nNeighbouring episodes:\n" + string.Concat(rungTwo.Select(e => "- " + e.Summary.Trim().Replace("\n", " ") + "\n  source: episode " + e.At.UtcDateTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + " " + e.Id + "\n")); }
    var hash = id.LastIndexOf('#');
    if (hash > 0 && await Recall.Transcript(brain.Get<IAgent>(id[..hash]), id[(hash + 1)..]) is { Length: > 0 } transcript)
    { text += "\nTranscript around the run:\n\n" + transcript; }
    return text;
}

async Task<List<Recall.Episode>> EpisodeRows()
{
    var result = new List<Recall.Episode>();
    for (var offset = 0; ; offset += 200)
    {
        var page = await episodes.Page(offset, 200);
        foreach (var cells in page)
        {
            string Cell(string name)
            {
                if (cells.FirstOrDefault(c => c.Column == name)?.Json is not { } json) { return ""; }
                using var document = JsonDocument.Parse(json);
                return document.RootElement.ValueKind == JsonValueKind.String ? document.RootElement.GetString() ?? "" : "";
            }
            if (DateTimeOffset.TryParse(Cell("at"), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var at))
            { result.Add(new(Cell("id"), Cell("agent"), at, Cell("summary"))); }
        }
        if (page.Length < 200) { return result; }
    }
}
