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
    [InlineData("Stored Xq7Lm2Np9Rt4Vw8Yz3Bc6Df1Gh5Jk0Ms2Qa for later")]
    [InlineData("password is hunter2hunter2")]
    public void MoreCredentialsAreDiscardedAsSecret(string text)
    {
        var decision = global::Consolidation.Classify(Item(text), 0.75, global::Consolidation.SensitiveClasses);
        Assert.Equal(new global::Consolidation.Decision(global::Consolidation.Disposition.Discard, "secret"), decision);
    }

    [Theory]
    [InlineData("Merged commit 3f9a8b7c6d5e4f3a2b1c0d9e8f7a6b5c4d3e2f1a into main")]
    [InlineData("The intent 6f1c2b3a-4d5e-4f60-8a9b-0c1d2e3f4a5b was dispatched")]
    [InlineData("Plans live in ino-memory-remaining-plan-2026-10-19")]
    [InlineData("Edits features/os/Memory/ConsolidationHelpersForTheBrainRuntime2026.cs often")]
    [InlineData("Token rotation is scheduled for Fridays in the release plan")]
    [InlineData("The user prefers their coffee black")]
    [InlineData("Drinks tea, a habit since school")]
    public void WorkMemoriesThatLookTechnicalArePublished(string text)
        => Assert.Equal(global::Consolidation.Disposition.Publish, Classified(text));

    [Theory]
    [InlineData("Their api key abc123def456 works")]
    [InlineData("The token 9f8e7d6c5b4a lives in the vault")]
    public void AKeywordFollowedByAValueGoesToReview(string text)
        => Assert.Equal(new global::Consolidation.Decision(global::Consolidation.Disposition.Review, "possible credential"),
            global::Consolidation.Classify(Item(text), 0.75, global::Consolidation.SensitiveClasses));

    [Theory]
    [InlineData("The door passcode: 4821")]
    [InlineData("Their card pin is 1234")]
    [InlineData("pin=0000")]
    public void APasscodeOrPinWithAValueIsDiscarded(string text)
        => Assert.Equal(new global::Consolidation.Decision(global::Consolidation.Disposition.Discard, "secret"),
            global::Consolidation.Classify(Item(text), 0.75, global::Consolidation.SensitiveClasses));

    [Fact]
    public void ANamedSecretWithAValueGoesToReview()
    {
        var decision = global::Consolidation.Classify(Item("secret = hunter2hunter2"), 0.75, global::Consolidation.SensitiveClasses);
        Assert.Equal(global::Consolidation.Disposition.Review, decision.Disposition);
    }

    [Fact]
    public void TheStoredReflectFlagReadsLeniently()
    {
        Assert.True(global::Consolidation.Flag("true"));
        Assert.True(global::Consolidation.Flag("True"));
        Assert.True(global::Consolidation.Flag("1"));
        Assert.True(global::Consolidation.Flag("\"true\""));
        Assert.False(global::Consolidation.Flag("false"));
        Assert.False(global::Consolidation.Flag(null));
    }

    [Fact]
    public void TheCapRefusalIsRecognisedByTheFactsNeuronsWording()
    {
        // The exact message FactsNeuron.Assert throws at the cap; its integration fact pins the same prefix.
        var refusal = new InvalidOperationException($"At most {(global::IntoChat.OS.AI.Agents.Facts.IFacts.Cap)} facts can be current; invalidate or forget one first.");
        Assert.True(global::Consolidation.IsCapRefusal(refusal));
        Assert.False(global::Consolidation.IsCapRefusal(new InvalidOperationException("something else")));
    }

    [Theory]
    [InlineData("Session token eyJhbGciOiJIUzI1NiJ9.eyJzdWIiOiIxIn0.c2lnbmF0dXJl")]
    [InlineData("Authorization: Bearer abcdefghijklmnop1234")]
    public void TokensAreDiscardedAsSecret(string text)
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

// Credentials never reach an episode: Consolidation.Redact runs over the note and the fallback before capture stores them.
public sealed class OsMemoryRedactionFacts
{
    [Fact]
    public void TheFallbackRedactsASecret()
    {
        var fallback = global::Episodes.Fallback("my key is sk-live-4f9a8b7c6d5e4f3a2b1c and password: hunter22", "Noted, Bearer abcdefghijklmnop1234");
        Assert.DoesNotContain("sk-live-4f9a8b7c6d5e4f3a2b1c", fallback.Summary);
        Assert.DoesNotContain("hunter22", fallback.Summary);
        Assert.DoesNotContain("abcdefghijklmnop1234", fallback.Summary);
        Assert.Contains(global::Consolidation.RedactedSecret, fallback.Summary);
    }

    [Fact]
    public void ASummaryRedactsABearerToken()
    {
        var note = global::Episodes.Redacted(new("- The user shared Bearer abcdefghijklmnop1234 for the API\n- Their api key abc123def456 works", 5, false));
        Assert.DoesNotContain("abcdefghijklmnop1234", note.Summary);
        Assert.DoesNotContain("abc123def456", note.Summary);
        Assert.Contains(global::Consolidation.RedactedSecret, note.Summary);
        Assert.Contains(global::Consolidation.RedactedHint, note.Summary);
        Assert.Equal((5, false), (note.Importance, note.IsFallback));
    }

    [Fact]
    public void PlainTextIsNotRedacted()
        => Assert.Equal("- Prefers short answers; token rotation is on Fridays",
            global::Consolidation.Redact("- Prefers short answers; token rotation is on Fridays"));

    [Fact]
    public void CaptureRowsMatchTheTableDefinition()
    {
        var row = global::Episodes.Row("ino", "run-1", DateTimeOffset.Parse("2026-10-19T10:00:00Z"), new("- x", 4, false));
        var columns = global::Episodes.Definition.Columns.Select(column => column.Name).Except(global::Episodes.Definition.PrimaryKey).Order(StringComparer.Ordinal);
        Assert.Equal(columns, row.Select(value => value.Column).Order(StringComparer.Ordinal));
        Assert.Equal(row.Length, row.Select(value => value.Column).Distinct().Count());
    }
}

// A batch that keeps failing parks its episodes after three attempts, so the episodes behind it proceed.
public sealed class OsMemoryParkingFacts
{
    [Fact]
    public void APoisonBatchIsParkedAfterThreeAttempts()
    {
        Assert.Equal(0, global::Consolidation.AttemptsOf(null));
        Assert.Equal(1, global::Consolidation.AttemptsOf("consolidate-old"));
        var claim = "consolidate-a";
        for (var attempt = 1; attempt <= global::Consolidation.MaxAttempts; attempt++)
        {
            Assert.False(global::Consolidation.Parks(claim));
            claim = global::Consolidation.Claim("consolidate-a", global::Consolidation.AttemptsOf(claim == "consolidate-a" ? null : claim) + 1);
            Assert.Equal("consolidate-a", global::Consolidation.BatchOf(claim));
            Assert.Equal(attempt, global::Consolidation.AttemptsOf(claim));
        }
        Assert.True(global::Consolidation.Parks(claim));
        Assert.Equal("parked:3 failed attempts", global::Consolidation.Parked("3 failed attempts"));
    }

    [Fact]
    public void AReplayFindsItsVerdictThroughACountedClaim()
    {
        var claim = global::Consolidation.Claim("old", 2);
        var plan = global::Consolidation.Next([new("a#1", global::Consolidation.BatchOf(claim), true)], new HashSet<string> { "old" }, "new");
        Assert.Equal(("old", true), (plan!.BatchId, plan.Replay));
    }
}

// Memory's review helper (features/os/Memory/Reviews.cs) is compiled into this project.
public sealed class OsMemoryReviewFacts
{
    [Fact]
    public void AcceptingAReviewItemAssertsOnce()
    {
        Assert.Equal(global::Reviews.Outcome.Accept, global::Reviews.Decide("pending", global::Reviews.Operation.Accept));
        Assert.Equal(global::Reviews.Outcome.AlreadyDone, global::Reviews.Decide("accepted", global::Reviews.Operation.Accept));
        Assert.Equal(global::Reviews.Outcome.Reject, global::Reviews.Decide("pending", global::Reviews.Operation.Reject));
        Assert.Equal(global::Reviews.Outcome.AlreadyDone, global::Reviews.Decide("rejected", global::Reviews.Operation.Accept));
        Assert.Equal(global::Reviews.Outcome.Unknown, global::Reviews.Decide(null, global::Reviews.Operation.Reject));
    }

    [Fact]
    public void TheCountRendersOnlyWhenPositive()
    {
        Assert.Equal("", global::Reviews.Render(0));
        Assert.Equal("1 memory awaits review", global::Reviews.Render(1));
        Assert.Equal("3 memories await review", global::Reviews.Render(3));
    }

    [Fact]
    public void AnUnexpectedStatusIsDistinct()
        => Assert.Equal(global::Reviews.Outcome.Unexpected, global::Reviews.Decide("weird", global::Reviews.Operation.Accept));

    [Fact]
    public void AnAcceptedRowIsRoutedByItsKind()
    {
        Assert.Equal(global::Reviews.Route.Fact, global::Reviews.RouteOf("profile"));
        Assert.Equal(global::Reviews.Route.Fact, global::Reviews.RouteOf("fact"));
        Assert.Equal(global::Reviews.Route.Fact, global::Reviews.RouteOf("insight"));
        Assert.Equal(global::Reviews.Route.Intent, global::Reviews.RouteOf("intent"));
        Assert.Equal(global::Reviews.Route.Working, global::Reviews.RouteOf("working"));
        Assert.Equal(global::Reviews.Route.Unknown, global::Reviews.RouteOf("weird"));
        Assert.Equal("review:abc", global::Reviews.IntentRequestId("abc"));
    }
}
