namespace IntoChat.OS.Kernel.Tests.Unit;

// Memory's recall ladder (features/os/Memory/RecallLadder.cs) is compiled into this project.
public sealed class OsMemoryRecallFacts
{
    private static readonly DateTimeOffset Now = new(2026, 10, 19, 12, 0, 0, TimeSpan.Zero);

    private static global::Recall.Candidate Candidate(string id, string tier, double similarity, double ageDays, int importance = 5)
        => new(id, tier, "text of " + id, similarity, Now.AddDays(-ageDays), importance);

    [Fact]
    public void RankingDecaysWithAHalfLifeAndFloors()
    {
        var ranked = global::Recall.Rank(
            [Candidate("fresh", "episode", 0.8, 0), Candidate("half", "episode", 0.8, 14), Candidate("ancient", "episode", 0.8, 3650)], Now, 14);

        var scores = ranked.ToDictionary(item => item.Item.Id, item => item.Score);
        Assert.Equal(0.8 * 1.0 * 1.0 * 1.0, scores["fresh"], 6);
        Assert.Equal(0.8 * 0.5, scores["half"], 6);
        Assert.Equal(0.8 * 0.3, scores["ancient"], 6);
        Assert.Equal(["fresh", "half", "ancient"], ranked.Select(item => item.Item.Id));
    }

    [Fact]
    public void ProfileOutranksAnEpisodeAtEqualSimilarity()
    {
        var ranked = global::Recall.Rank(
            [Candidate("e", "episode", 0.7, 1), Candidate("f", "fact", 0.7, 1), Candidate("i", "insight", 0.7, 1), Candidate("p", "profile", 0.7, 1)], Now, 14);

        Assert.Equal(["p", "i", "f", "e"], ranked.Select(item => item.Item.Id));
        Assert.Equal(2.0 / 1.0, ranked[0].Score / ranked[3].Score, 6);
    }

    [Fact]
    public void ImportanceScalesTheScore()
    {
        var ranked = global::Recall.Rank([Candidate("low", "fact", 0.7, 0, 1), Candidate("high", "fact", 0.7, 0, 10)], Now, 14);
        Assert.Equal("high", ranked[0].Item.Id);
        Assert.Equal(0.7 * 1.2 * 1.5, ranked[0].Score, 6);
    }

    [Fact]
    public void AnAbsentAnswerNamesTheCoverage()
    {
        var coverage = new global::Recall.Coverage(new(2026, 9, 1, 0, 0, 0, TimeSpan.Zero), new(2026, 10, 18, 0, 0, 0, TimeSpan.Zero), 12);

        var absent = global::Recall.Render("the invoice", [], coverage);
        Assert.Contains("Absent", absent);
        Assert.Contains("episodes from 2026-09-01 to 2026-10-18, 12 facts", absent);

        var found = global::Recall.Render("the invoice", global::Recall.Rank([Candidate("fact-1", "fact", 0.9, 1)], Now, 14), coverage);
        Assert.Contains("Found", found);
        Assert.Contains("fact 2026-10-18 fact-1", found);
        Assert.DoesNotContain("episodes from", found);

        var partial = global::Recall.Render("the invoice", global::Recall.Rank([Candidate("ep#1", "episode", 0.4, 1)], Now, 14), coverage);
        Assert.Contains("Partial", partial);
        Assert.Contains("episode 2026-10-18 ep#1", partial);
    }

    [Fact]
    public void ExpandReturnsTheNeighbours()
    {
        global::Recall.Episode[] all =
        [
            new("b#2", "b", Now.AddDays(-2), "b2"), new("a#2", "a", Now.AddDays(-2), "a2"), new("a#1", "a", Now.AddDays(-3), "a1"),
            new("a#3", "a", Now.AddDays(-1), "a3"), new("b#1", "b", Now.AddDays(-3), "b1"),
        ];

        Assert.Equal(["a#1", "a#3"], global::Recall.Expand("a#2", all).Select(episode => episode.Id));
        Assert.Equal(["a#2"], global::Recall.Expand("a#1", all).Select(episode => episode.Id));
        Assert.Equal(["a#2"], global::Recall.Expand("a#3", all).Select(episode => episode.Id));
        Assert.Empty(global::Recall.Expand("missing", all));
    }
}
