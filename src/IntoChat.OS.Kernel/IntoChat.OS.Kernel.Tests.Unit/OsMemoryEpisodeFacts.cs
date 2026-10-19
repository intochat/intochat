namespace IntoChat.OS.Kernel.Tests.Unit;

// Memory's episode helper (features/os/Memory/Episodes.cs) is compiled into this project.
public sealed class OsMemoryEpisodeFacts
{
    [Fact]
    public void ASummaryParses()
    {
        var parsed = global::Episodes.ParseSummary("```json\n{\"summary\":\"- wanted a flight\",\"importance\":7}\n```");
        Assert.Equal(("- wanted a flight", 7), parsed);
    }

    [Fact]
    public void ASummaryWithoutImportanceIsNull()
    {
        Assert.Null(global::Episodes.ParseSummary("{\"summary\":\"- x\"}"));
        Assert.Null(global::Episodes.ParseSummary("not json"));
    }

    [Fact]
    public void TheFallbackIsBoundedAndMarked()
    {
        var fallback = global::Episodes.Fallback(new string('u', 500), new string('r', 500));
        Assert.Equal(3, fallback.Importance);
        Assert.True(fallback.IsFallback);
        Assert.Contains(new string('u', 300), fallback.Summary);
        Assert.DoesNotContain(new string('u', 301), fallback.Summary);
        Assert.Contains(new string('r', 300), fallback.Summary);
        Assert.DoesNotContain(new string('r', 301), fallback.Summary);
    }

    [Fact]
    public void AnEpisodeIdIsAgentHashRun()
        => Assert.Equal("chat/a#run1", global::Episodes.Id("chat/a", "run1"));
}

// Memory's consolidation helper (features/os/Memory/Consolidation.cs) is compiled into this project.
public sealed class OsMemoryConsolidationFacts
{
    [Fact]
    public void AVerdictBatchParses()
    {
        var items = global::Consolidation.ParseVerdict("```json\n{\"items\":[{\"disposition\":\"publish\",\"episodeIds\":[\"a#1\",\"a#2\"],"
            + "\"kind\":\"profile\",\"text\":\"Prefers short answers\",\"subject\":\"user\",\"confidence\":0.9,\"importance\":6,"
            + "\"reason\":\"stated twice\",\"contradicts\":null}]}\n```");
        Assert.NotNull(items);
        var item = Assert.Single(items);
        Assert.Equal("publish", item.Disposition);
        Assert.Equal(["a#1", "a#2"], item.EpisodeIds);
        Assert.Equal("profile", item.Kind);
        Assert.Equal("Prefers short answers", item.Text);
        Assert.Equal(0.9, item.Confidence);
        Assert.Equal(6, item.Importance);
        Assert.Null(item.Contradicts);
        Assert.Null(global::Consolidation.ParseVerdict("not json"));
        Assert.Null(global::Consolidation.ParseVerdict("{\"other\":[]}"));
    }

    private static global::Consolidation.Verdict Item(string text, double confidence = 0.9, string disposition = "publish")
        => new(disposition, ["a#1"], "fact", text, "user", confidence, 5, "", null);

    [Fact]
    public void ALowConfidenceItemGoesToReview()
    {
        var decision = global::Consolidation.Classify(Item("Prefers tea", 0.5), 0.75, global::Consolidation.SensitiveClasses);
        Assert.Equal(global::Consolidation.Disposition.Review, decision.Disposition);
    }

    [Fact]
    public void AHealthFactGoesToReview()
    {
        var decision = global::Consolidation.Classify(Item("Was diagnosed with diabetes last year"), 0.75, global::Consolidation.SensitiveClasses);
        Assert.Equal(global::Consolidation.Disposition.Review, decision.Disposition);
        Assert.Contains("health", decision.Reason);
    }

    [Fact]
    public void ACredentialIsDiscardedAsSecret()
    {
        var decision = global::Consolidation.Classify(Item("Their key is sk-live-4f9a8b7c6d5e4f3a2b1c"), 0.75, global::Consolidation.SensitiveClasses);
        Assert.Equal(global::Consolidation.Disposition.Discard, decision.Disposition);
        Assert.Equal("secret", decision.Reason);
        // A secret is dropped even when the model asked for a review.
        Assert.Equal(global::Consolidation.Disposition.Discard,
            global::Consolidation.Classify(Item("password: hunter22", 0.9, "review"), 0.75, global::Consolidation.SensitiveClasses).Disposition);
    }

    [Fact]
    public void APlainPreferenceIsPublished()
        => Assert.Equal(global::Consolidation.Disposition.Publish,
            global::Consolidation.Classify(Item("Prefers answers in Ukrainian"), 0.75, global::Consolidation.SensitiveClasses).Disposition);

    [Fact]
    public void ReflectionTriggersAtThirtyImportance()
    {
        Assert.False(global::Consolidation.Reflect([10, 10, 9]));
        Assert.True(global::Consolidation.Reflect([10, 10, 10]));
    }

    [Fact]
    public void AnExpiredClaimIsReclaimable()
    {
        var now = DateTimeOffset.Parse("2026-10-19T10:00:00Z");
        Assert.True(global::Consolidation.Claimable(null, null, null, now));
        Assert.True(global::Consolidation.Claimable(null, "run1", now.AddSeconds(-1), now));
        Assert.False(global::Consolidation.Claimable(null, "run1", now.AddMinutes(4), now));
        Assert.False(global::Consolidation.Claimable(now.AddHours(-1), null, null, now));
    }

    private static global::Consolidation.Disposition Classified(string text)
        => global::Consolidation.Classify(Item(text), 0.75, global::Consolidation.SensitiveClasses).Disposition;

    [Theory]
    [InlineData("Wife is called Anna")]
    [InlineData("Anna, a colleague, runs the Kyiv office")]
    [InlineData("Has back pain most mornings")]
    [InlineData("Earns $4000 a month")]
    [InlineData("Works at the Lviv hub")]
    public void APhraseThatSlippedThroughNowGoesToReview(string text)
        => Assert.Equal(global::Consolidation.Disposition.Review, Classified(text));

    [Theory]
    [InlineData("Session token eyJhbGciOiJIUzI1NiJ9.eyJzdWIiOiIxIn0.c2lnbmF0dXJl")]
    [InlineData("Authorization: Bearer abcdefghijklmnop1234")]
    [InlineData("Stored 3f9a8b7c6d5e4f3a2b1c0d9e8f7a6b5c4d3e2f1a for later")]
    [InlineData("The api key abc123def456 works")]
    public void MoreCredentialsAreDiscardedAsSecret(string text)
    {
        var decision = global::Consolidation.Classify(Item(text), 0.75, global::Consolidation.SensitiveClasses);
        Assert.Equal(new global::Consolidation.Decision(global::Consolidation.Disposition.Discard, "secret"), decision);
    }

    [Fact]
    public void AStoredVerdictIsReplayedNotReasked()
    {
        global::Consolidation.Candidate[] candidates =
            [new("a#1", "old", true), new("a#2", "old", true), new("a#3", null, true), new("a#4", "live", false)];
        var replay = global::Consolidation.Next(candidates, new HashSet<string> { "old" }, "new");
        Assert.NotNull(replay);
        Assert.True(replay.Replay);
        Assert.Equal("old", replay.BatchId);
        Assert.Equal(["a#1", "a#2"], replay.EpisodeIds);

        var fresh = global::Consolidation.Next(candidates, new HashSet<string>(), "new");
        Assert.NotNull(fresh);
        Assert.False(fresh.Replay);
        Assert.Equal("new", fresh.BatchId);
        Assert.Equal(["a#1", "a#2", "a#3"], fresh.EpisodeIds);
        Assert.Null(global::Consolidation.Next([new("a#4", "live", false)], new HashSet<string>(), "new"));
    }

    [Fact]
    public void TheDedupKeyIsStableAcrossOrderAndWhitespace()
    {
        var one = new global::Consolidation.Verdict("publish", ["a#2", "a#1"], "fact", "Prefers  tea", "User", 0.9, 5, "", null);
        var two = new global::Consolidation.Verdict("review", ["a#1", "a#2"], "Fact", " prefers tea ", "user", 0.4, 2, "x", null);
        Assert.Equal(global::Consolidation.DedupKey(one), global::Consolidation.DedupKey(two));
        Assert.Matches("^[0-9a-f]{24}$", global::Consolidation.DedupKey(one));
        Assert.NotEqual(global::Consolidation.DedupKey(one), global::Consolidation.DedupKey(one with { Text = "Prefers coffee" }));
    }

    [Fact]
    public void TwoIntentsFromOneEpisodeGetDistinctRequestIds()
    {
        var first = new global::Consolidation.Verdict("publish", ["a#1"], "intent", "Book a flight", "travel", 0.9, 5, "", null);
        var second = first with { Text = "Renew the passport" };
        Assert.NotEqual(global::Consolidation.IntentRequestId(first), global::Consolidation.IntentRequestId(second));
        Assert.StartsWith("consolidation:", global::Consolidation.IntentRequestId(first));
    }
}
