using IntoChat.OS.AI.Agents.Intents;

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
}
