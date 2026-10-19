using IntoChat.OS.AI.Agents.Intents;
using IntoChat.OS.AI.Agents.Intents.Signals;

namespace IntoChat.OS.Kernel.Tests.Unit;

// Memory's feature helper (features/os/Memory/Intents.cs) is compiled into this project.
public sealed class OsMemoryIntendFacts
{
    [Fact]
    public void AMalformedAskIsRefused()
        => Assert.Throws<ArgumentException>(() => global::Intents.ParseAsk("{}"));

    [Fact]
    public void AReplyWithoutABriefIsNull()
        => Assert.Null(global::Intents.ParseReply("I think the user wants…"));

    [Fact]
    public void AReplyParses()
    {
        var id = Guid.NewGuid();
        var reply = global::Intents.ParseReply(
            $$"""{"brief":{"goal":"book a flight","inputs":["Paris"],"constraints":["cheap"],"acceptance":["confirmed"]},"confidence":0.9,"continues":"{{id}}"}""");
        Assert.NotNull(reply);
        Assert.Equal("book a flight", reply.Brief.Goal);
        Assert.Equal(["Paris"], reply.Brief.Inputs);
        Assert.Equal(["cheap"], reply.Brief.Constraints);
        Assert.Equal(["confirmed"], reply.Brief.Acceptance);
        Assert.Equal(0.9, reply.Confidence);
        Assert.Equal(id, reply.Continues);
    }

    [Fact]
    public void RenderingCutsAtTheBudgetOnAnIntentBoundary()
    {
        static Intent Make(string heard, string goal) => new(Guid.NewGuid(), heard, "c", heard, new(goal, [], [], []), 0.5, IntentState.Rephrased, [], [], []);
        var oldest = Make("aaa", "oldest goal");
        var middle = Make("bbb", "middle goal");
        var newest = Make("ccc", "newest goal");
        var two = global::Intents.RenderActive([middle, newest], 10_000);
        var rendered = global::Intents.RenderActive([oldest, middle, newest], two.Length);
        Assert.Contains("newest goal", rendered);
        Assert.Contains("middle goal", rendered);
        Assert.DoesNotContain("oldest goal", rendered);
    }

    [Fact]
    public void OnlyAHeardIntentOfThisRequestIsAbandonedOnFailure()
    {
        static Intent Make(string request, IntentState state) => new(Guid.NewGuid(), "h", "c", request, null, 0, state, [], [], []);
        Assert.True(global::Intents.ShouldCompensate(Make("r1", IntentState.Heard), "r1"));
        Assert.False(global::Intents.ShouldCompensate(Make("r0", IntentState.Heard), "r1"));
        Assert.False(global::Intents.ShouldCompensate(Make("r1", IntentState.Rephrased), "r1"));
        Assert.False(global::Intents.ShouldCompensate(null, "r1"));
    }

    [Fact]
    public void RenderingTheWorkingMemoryEntryCutsAtFourThousand()
    {
        Assert.Equal(4_000, global::Intents.RenderWorking(new string('x', 5_000)).Length);
        Assert.Equal("short", global::Intents.RenderWorking("short"));
    }

    [Fact]
    public void AnActiveIntentShowsItsFirstAcceptanceLine()
    {
        var intent = new Intent(Guid.NewGuid(), "h", "c", "r", new("the goal", [], [], ["first check", "second check"]), 0.9, IntentState.Rephrased, [], [], []);
        var rendered = global::Intents.RenderActive([intent], 6_000);
        Assert.Contains("first check", rendered);
        Assert.DoesNotContain("second check", rendered);
    }

    [Fact]
    public void AContextEntryIsSetOnlyWhenItsTextChangesAndRemovedWhenEmpty()
    {
        Assert.Equal(global::Intents.Change.Skip, global::Intents.Decide("a", "a"));
        Assert.Equal(global::Intents.Change.Set, global::Intents.Decide("a", "b"));
        Assert.Equal(global::Intents.Change.Set, global::Intents.Decide(null, "b"));
        Assert.Equal(global::Intents.Change.Remove, global::Intents.Decide("a", ""));
        Assert.Equal(global::Intents.Change.Skip, global::Intents.Decide(null, ""));
    }

    private static readonly IntentHeard Heard = new("ino/memory/intents", Guid.NewGuid(), "r1");

    [Fact]
    public void ASignalCueMatchesByAliasAlone()
        => Assert.True(global::Intents.CueMatches(new("signal", "k", "intents.heard", null, null, null), Heard));

    [Fact]
    public void ASignalCueWithAFieldMatchesOnlyThatValue()
    {
        Assert.True(global::Intents.CueMatches(new("signal", "k", "intents.heard", "RequestId", "r1", null), Heard));
        Assert.False(global::Intents.CueMatches(new("signal", "k", "intents.heard", "RequestId", "r2", null), Heard));
        Assert.False(global::Intents.CueMatches(new("signal", "k", "intents.heard", "Missing", "r1", null), Heard));
    }

    [Fact]
    public void ASignalCueMatchesCaseInsensitiveField()
        => Assert.True(global::Intents.CueMatches(new("signal", "k", "intents.heard", "requestid", "r1", null), Heard));

    [Fact]
    public void ANonStringFieldValueNeverMatches()
        => Assert.False(global::Intents.CueMatches(new("signal", "k", "test.counted", "Count", "5", null), new CountedSignal(5)));

    [Fact]
    public void ANullEqualToNeverMatches()
        => Assert.False(global::Intents.CueMatches(new("signal", "k", "intents.heard", "RequestId", null, null), Heard));

    [Fact]
    public void ANonObjectSignalNeverMatches()
        => Assert.False(global::Intents.CueMatches(new("signal", "k", "test.plain", "RequestId", "r1", null), new PlainSignal()));

    [NeuronAlias("test.counted")]
    private sealed record CountedSignal(int Count) : Signal;

    [NeuronAlias("test.plain"), System.Text.Json.Serialization.JsonConverter(typeof(PlainConverter))]
    private sealed record PlainSignal : Signal;

    private sealed class PlainConverter : System.Text.Json.Serialization.JsonConverter<PlainSignal>
    {
        public override PlainSignal Read(ref System.Text.Json.Utf8JsonReader reader, Type type, System.Text.Json.JsonSerializerOptions options) => new();

        public override void Write(System.Text.Json.Utf8JsonWriter writer, PlainSignal value, System.Text.Json.JsonSerializerOptions options) => writer.WriteStringValue("plain");
    }

    [Fact]
    public void ACueTypeCacheMissRefreshesOnce()
    {
        var loads = 0;
        var known = new Dictionary<string, Type> { ["a"] = typeof(string) };
        var cache = new global::Intents.CueTypes(() => { loads++; return new Dictionary<string, Type>(known); });
        Assert.Equal(typeof(string), cache.Resolve("a"));
        Assert.Equal(1, loads);
        Assert.Equal(typeof(string), cache.Resolve("a"));
        Assert.Equal(1, loads);
        Assert.Null(cache.Resolve("missing"));
        Assert.Equal(2, loads);
        known["late"] = typeof(int);
        Assert.Equal(typeof(int), cache.Resolve("late"));
        Assert.Equal(3, loads);
    }

    [Fact]
    public void ADifferentAliasNeverMatches()
        => Assert.False(global::Intents.CueMatches(new("signal", "k", "intents.rephrased", null, null, null), Heard));

    [Fact]
    public void OnlyARevisionConflictIsRetriedAndOnlyThreeTimes()
    {
        var conflict = new RevisionConflictException("stale", 2);
        Assert.True(global::Intents.ShouldRetry(conflict, 1));
        Assert.True(global::Intents.ShouldRetry(conflict, 2));
        Assert.False(global::Intents.ShouldRetry(conflict, 3));
        Assert.False(global::Intents.ShouldRetry(new InvalidOperationException("x"), 1));
    }

    [Fact]
    public void OnlyAWaitingIntentCountsAsWaiting()
    {
        static Intent Make(IntentState state) => new(Guid.NewGuid(), "h", "c", "r", null, 0, state, [], [], []);
        Assert.True(global::Intents.IsWaiting(Make(IntentState.Waiting)));
        Assert.False(global::Intents.IsWaiting(Make(IntentState.Dispatched)));
        Assert.False(global::Intents.IsWaiting(null));
    }

    [Fact]
    public void TheAnswerNamesTheIntent()
    {
        var id = Guid.NewGuid();
        Assert.Equal("ok " + id, global::Intents.Ok(id));
    }

    [Fact]
    public void OnlyARephrasedIntentIsConfirmedAfterAConfidentRephrase()
    {
        static Intent Make(IntentState state) => new(Guid.NewGuid(), "h", "c", "r", null, 0.9, state, [], [], []);
        Assert.True(global::Intents.ShouldConfirm(Make(IntentState.Rephrased), 0.9, 0.8));
        Assert.False(global::Intents.ShouldConfirm(Make(IntentState.Rephrased), 0.5, 0.8));
        Assert.False(global::Intents.ShouldConfirm(Make(IntentState.Dispatched), 0.9, 0.8));
        Assert.False(global::Intents.ShouldConfirm(Make(IntentState.Waiting), 0.9, 0.8));
        Assert.False(global::Intents.ShouldConfirm(null, 0.9, 0.8));
    }

    private static Intent Mk(string goal, IntentState state = IntentState.Rephrased, string[]? acceptance = null)
        => new(Guid.NewGuid(), "h", "c", "r", new(goal, [], [], acceptance ?? []), 0.5, state, [], [], []);

    [Fact]
    public void ANewlineInAcceptanceDoesNotBreakTheList()
    {
        var rendered = global::Intents.RenderActive([Mk("g", acceptance: ["line one\r\nline two\tend"])], 10_000);
        Assert.Single(rendered.TrimEnd('\n').Split('\n'));
        Assert.Contains("line one line two end", rendered);
        Assert.Equal("a b c", global::Intents.Line("a\nb\tc"));
    }

    [Fact]
    public void AnOversizedNewestIntentIsTruncatedNotDropped()
    {
        var rendered = global::Intents.RenderActive([Mk(new string('x', 500))], 100);
        Assert.NotEmpty(rendered);
        Assert.True(rendered.Length <= 100);
        Assert.Contains("…", rendered);
    }

    [Fact]
    public void RenderWorkingNeverSplitsASurrogatePair()
    {
        var text = new string('x', 3_999) + "😀";
        Assert.Equal(4_001, text.Length);
        var cut = global::Intents.RenderWorking(text);
        Assert.True(cut.Length <= 4_000);
        Assert.False(char.IsHighSurrogate(cut[^1]));
    }

    [Fact]
    public void RenderActiveOrdersNewestFirst()
    {
        var rendered = global::Intents.RenderActive([Mk("older"), Mk("newer")], 10_000);
        Assert.True(rendered.IndexOf("newer", StringComparison.Ordinal) < rendered.IndexOf("older", StringComparison.Ordinal));
    }

    [Fact]
    public void RenderActiveSkipsDoneAndAbandoned()
    {
        var kept = global::Intents.ActiveOf([Mk("doneGoal", IntentState.Done), Mk("abandonedGoal", IntentState.Abandoned), Mk("waitingGoal", IntentState.Waiting), Mk("heardGoal", IntentState.Heard)]);
        Assert.Equal(["waitingGoal"], kept.Select(intent => intent.Brief!.Goal));
        var rendered = global::Intents.RenderActive(kept, 10_000);
        Assert.DoesNotContain("doneGoal", rendered);
        Assert.DoesNotContain("abandonedGoal", global::Intents.RenderActive([Mk("abandonedGoal", IntentState.Abandoned)], 10_000));
    }

    [Fact]
    public void ParseReplyAcceptsAFencedJsonBlock()
        => Assert.NotNull(global::Intents.ParseReply("```json\n{\"brief\":{\"goal\":\"g\"},\"confidence\":0.7}\n```"));

    [Fact]
    public void ParseReplyWithoutConfidenceIsNull()
        => Assert.Null(global::Intents.ParseReply("{\"brief\":{\"goal\":\"g\"}}"));

    [Fact]
    public void ParseAskRejectsAMissingConversation()
        => Assert.Throws<ArgumentException>(() => global::Intents.ParseAsk("{\"message\":\"m\",\"requestId\":\"r\"}"));
}
