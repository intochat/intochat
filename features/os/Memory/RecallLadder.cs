using System.Globalization;
using System.Text;
using IntoChat.OS;
using IntoChat.OS.AI.Agents;
using IntoChat.OS.Client;
using IntoChat.OS.AI.Embeddings;
using IntoChat.OS.AI.Inference;
using IntoChat.OS.Microsoft.CSharp.Runtime;

// Shared: recall over the user's life. Facts and episodes are embedded into the brain's "life" vector index
// (every entry carries tier and brain, and every search is limited to the brain), ranked by
// similarity x recency x authority x importance, and answered as Found, Partial or Absent with their sources.
// The ladder: rung 1 is the ranked search, rung 2 Expand the neighbouring episodes of the same agent, rung 3
// Transcript the agent's own words around the run.
internal static class Recall
{
    public const double Floor = 0.3;
    public const double FoundSimilarity = 0.5;
    public const double PartialSimilarity = 0.3;
    private const int TextLimit = 2000;

    public sealed record Candidate(string Id, string Tier, string Text, double Similarity, DateTimeOffset At, int Importance);

    public sealed record Ranked(Candidate Item, double Score);

    public sealed record Coverage(DateTimeOffset? First, DateTimeOffset? Last, int Facts);

    public sealed record Episode(string Id, string Agent, DateTimeOffset At, string Summary);

    public static string LifeKey(string scope) => IVectorIndex.Key(scope, "life");

    public static double Authority(string tier) => tier switch { "profile" => 2.0, "insight" => 1.5, "fact" => 1.2, _ => 1.0 };

    public static double Recency(DateTimeOffset at, DateTimeOffset now, double halfLifeDays, double floor)
    {
        var age = Math.Max(0, (now - at).TotalDays);
        return halfLifeDays <= 0 ? floor : Math.Max(floor, Math.Pow(0.5, age / halfLifeDays));
    }

    public static List<Ranked> Rank(IEnumerable<Candidate> candidates, DateTimeOffset now, double halfLifeDays, double floor = Floor)
        => [.. candidates
            .Select(item => new Ranked(item, item.Similarity * Recency(item.At, now, halfLifeDays, floor) * Authority(item.Tier)
                * (Math.Clamp(item.Importance, 0, 10) / 10.0 + 0.5)))
            .OrderByDescending(ranked => ranked.Score).ThenBy(ranked => ranked.Item.Id, StringComparer.Ordinal)];

    public static string Render(string query, IReadOnlyList<Ranked> ranked, Coverage coverage)
    {
        var best = ranked.Count == 0 ? 0 : ranked.Max(item => item.Item.Similarity);
        var verdict = best >= FoundSimilarity ? "Found" : best >= PartialSimilarity ? "Partial" : "Absent";
        var text = new StringBuilder("# Recall: " + query.Trim() + "\n\n" + verdict + "\n");
        if (verdict == "Absent")
        {
            text.Append("\nNothing in memory answers this. Coverage: episodes from " + Day(coverage.First) + " to " + Day(coverage.Last)
                + ", " + coverage.Facts + " facts.\n");
            return text.ToString();
        }
        text.Append(verdict == "Partial" ? "\nOnly part of an answer; the nearest memories:\n\n" : "\n");
        foreach (var item in ranked.Take(5))
        {
            text.Append("- " + item.Item.Text.Trim().Replace("\n", " ") + "\n  source: " + item.Item.Tier + " " + Day(item.Item.At) + " " + item.Item.Id + "\n");
        }
        return text.ToString();
    }

    private static string Day(DateTimeOffset? at) => at is { } value ? value.UtcDateTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) : "n/a";

    // Rung 2: the previous and the next episode of the same agent, by time.
    public static List<Episode> Expand(string episodeId, IReadOnlyList<Episode> all)
    {
        var found = all.FirstOrDefault(episode => episode.Id == episodeId);
        if (found is null) { return []; }
        var line = all.Where(episode => episode.Agent == found.Agent).OrderBy(episode => episode.At).ThenBy(episode => episode.Id, StringComparer.Ordinal).ToList();
        var index = line.FindIndex(episode => episode.Id == episodeId);
        var neighbours = new List<Episode>();
        if (index > 0) { neighbours.Add(line[index - 1]); }
        if (index < line.Count - 1) { neighbours.Add(line[index + 1]); }
        return neighbours;
    }

    // Rung 3: the run's own words from the agent's transcript, from its user message up to the next one.
    public static async Task<string> Transcript(IAgent agent, string runId)
    {
        var state = await agent.Read();
        var run = state.Runs.FirstOrDefault(candidate => candidate.RunId == runId);
        if (run is null) { return ""; }
        var messages = await agent.Transcript();
        var start = Array.FindIndex(messages, message => message.Role == "user" && Words(message) == run.UserText);
        if (start < 0) { return Clip("user: " + run.UserText + "\nassistant: " + run.Reply); }
        var text = new StringBuilder();
        for (var at = start; at < messages.Length && (at == start || messages[at].Role != "user"); at++)
        {
            text.Append(messages[at].Role + ": " + Words(messages[at]) + "\n");
        }
        return Clip(text.ToString());
    }

    private static string Words(AiMessage message) => string.Concat(message.Content.OfType<AiText>().Select(part => part.Text));

    private static string Clip(string text) => text.Length <= TextLimit ? text : text[..TextLimit];

    public static VectorEntry Entry(string scope, string id, string tier, string text, DateTimeOffset at, int importance, float[] vector, string? kind = null)
        => new(id, vector, new()
        {
            ["tier"] = tier, ["kind"] = kind ?? tier, ["brain"] = scope, ["text"] = Clip(text), ["at"] = at.UtcDateTime.ToString("O", CultureInfo.InvariantCulture),
            ["importance"] = importance.ToString(CultureInfo.InvariantCulture),
        });

    public static Dictionary<string, string> MustMatch(string scope) => new() { ["brain"] = scope };

    // Best effort: a memory that cannot be embedded is still stored, only not searchable by meaning.
    public static async Task Index(IntoChatOSConnection brain, string id, string tier, string text, DateTimeOffset at, int importance, string? kind = null)
    {
        try
        {
            var scope = Memory.Scope(brain);
            var vector = (await Memory.Embed(brain, [text]))[0];
            await brain.Get<IVectorIndex>(LifeKey(scope)).Upsert([Entry(scope, id, tier, text, at, importance, vector, kind)]);
        }
        catch (Exception) when (!brain.Stopping.IsCancellationRequested) { }
    }

    public static Candidate? ToCandidate(VectorMatch match)
        => match.Payload.TryGetValue("text", out var text) && match.Payload.TryGetValue("tier", out var tier)
            && DateTimeOffset.TryParse(match.Payload.GetValueOrDefault("at"), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var at)
            ? new(match.Id, match.Payload.GetValueOrDefault("kind", tier), text, match.Score, at,
                int.TryParse(match.Payload.GetValueOrDefault("importance"), CultureInfo.InvariantCulture, out var importance) ? importance : 5)
            : null;
}
