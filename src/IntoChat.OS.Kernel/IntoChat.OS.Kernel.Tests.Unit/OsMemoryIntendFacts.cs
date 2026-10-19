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
}
