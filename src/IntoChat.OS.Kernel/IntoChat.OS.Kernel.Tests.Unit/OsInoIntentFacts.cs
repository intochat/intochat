using IntoChat.OS.AI.Agents.Intents;
using Xunit;

namespace IntoChat.OS.Kernel.Tests.Unit;

public sealed class OsInoIntentFacts
{
    private static readonly DateTimeOffset Now = new(2026, 10, 19, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void PendingRoundTrips()
    {
        var pending = new Pending("hello", "\n\nhint", "brain/apps/ino/conversations/main", Now, false);
        Assert.Equal(pending, Pending.Parse(pending.Serialize()));
        Assert.Null(Pending.Parse(""));
        Assert.Null(Pending.Parse("not json"));
    }

    [Fact]
    public void OlderFindsOnlyStaleUnstartedEntries()
    {
        var stale = Guid.NewGuid();
        var started = Guid.NewGuid();
        var fresh = Guid.NewGuid();
        var entries = new Dictionary<Guid, Pending>
        {
            [stale] = new("a", "", "c", Now.AddSeconds(-9), false),
            [started] = new("b", "", "c", Now.AddSeconds(-9), true),
            [fresh] = new("c", "", "c", Now.AddSeconds(-2), false),
        };
        Assert.Equal([stale], PendingStore.Older(entries, Now, TimeSpan.FromSeconds(8)));
    }

    [Fact]
    public void AConfirmedBriefRendersAnActInstruction()
    {
        var text = Ino.BriefInstructions(new IntentBrief("Add two numbers", ["17", "25"], ["use the calculator"], ["the sum is shown"]), true);
        Assert.Contains("Act on this brief:", text);
        Assert.Contains("Add two numbers", text);
        Assert.Contains("17", text);
        Assert.Contains("use the calculator", text);
        Assert.Contains("the sum is shown", text);
        Assert.DoesNotContain("do not act on it yet", text);
    }

    [Fact]
    public void AnUnconfirmedBriefRendersAnAskInstruction()
    {
        var text = Ino.BriefInstructions(new IntentBrief("Add two numbers", [], [], []), false);
        Assert.Contains("Show this brief to the user, ask whether it is right, and do not act on it yet", text);
        Assert.Contains("Add two numbers", text);
        Assert.DoesNotContain("Act on this brief:", text);
    }

    [Theory]
    [InlineData(AnswerOutcome.Started, true)]
    [InlineData(AnswerOutcome.KeptInDraft, true)]
    [InlineData(AnswerOutcome.Failed, false)]
    [InlineData(AnswerOutcome.NoModel, true)]
    public void OnlyAFailedAnswerReleasesTheClaim(AnswerOutcome outcome, bool kept)
        => Assert.Equal(kept, PendingStore.KeepsClaim(outcome));

    [Theory]
    [InlineData("ok", MemoryAnswer.Ok)]
    [InlineData("ok 6f1c2a4e-0d3b-4c1e-9a55-1b2c3d4e5f60", MemoryAnswer.Ok)]
    [InlineData("okay", MemoryAnswer.None)]
    [InlineData("failed: the agent did not answer in 30 seconds", MemoryAnswer.Failed)]
    [InlineData("", MemoryAnswer.None)]
    [InlineData("working", MemoryAnswer.None)]
    public void MemorysAnswerTextIsClassified(string text, MemoryAnswer expected)
        => Assert.Equal(expected, PendingStore.Classify(text));

    [Fact]
    public void ExpiredFindsEntriesOlderThanAnHour()
    {
        var old = Guid.NewGuid();
        var entries = new Dictionary<Guid, Pending>
        {
            [old] = new("a", "", "c", Now.AddHours(-2), true),
            [Guid.NewGuid()] = new("b", "", "c", Now.AddMinutes(-30), false),
        };
        Assert.Equal([old], PendingStore.Expired(entries, Now));
    }

    [Fact]
    public void AnOkAnswerNamesItsIntent()
    {
        var id = Guid.NewGuid();
        Assert.Equal(id, PendingStore.IntentId("ok " + id));
        Assert.Null(PendingStore.IntentId("ok"));
        Assert.Null(PendingStore.IntentId("ok not-a-guid"));
        Assert.Null(PendingStore.IntentId("failed: " + id));
    }

    [Fact]
    public void AContinuedIntentIsFoundByTheIdInTheAnswerNotByTheRequestId()
    {
        var requestId = Guid.NewGuid();
        static Intent Make(Guid id, string request, string goal) => new(id, "h", "c", request, new(goal, [], [], []), 0.9, IntentState.Dispatched, [], [], []);
        var continued = Make(Guid.NewGuid(), Guid.NewGuid().ToString("N"), "the continued goal");
        var other = Make(Guid.NewGuid(), requestId.ToString("N"), "another");
        Intent[] intents = [continued, other];
        Assert.Equal(continued, PendingStore.Find(intents, "ok " + continued.Id, requestId));
        Assert.Equal(other, PendingStore.Find(intents, "ok", requestId));
        Assert.Null(PendingStore.Find([continued], "ok", requestId));
        Assert.Null(PendingStore.Find(intents, "ok " + Guid.NewGuid(), requestId));
    }

    [Fact]
    public void AReleaseRecordsTheAttemptAndGivesUpAtTheCap()
    {
        var claimed = new Pending("a", "", "c", Now.AddSeconds(-20), true);
        var (first, retryFirst) = PendingStore.Released(claimed, Now);
        Assert.True(retryFirst);
        Assert.Equal((false, 1, (DateTimeOffset?)Now), (first.Started, first.Attempts, first.ReleasedAt));
        var (second, retrySecond) = PendingStore.Released(first with { Started = true }, Now.AddSeconds(3));
        Assert.True(retrySecond);
        Assert.Equal(2, second.Attempts);
        var (third, retryThird) = PendingStore.Released(second with { Started = true }, Now.AddSeconds(8));
        Assert.False(retryThird);
        Assert.Equal((true, PendingStore.MaxAttempts), (third.Started, third.Attempts));
    }

    [Theory]
    [InlineData(1, 2)]
    [InlineData(2, 4)]
    [InlineData(3, 8)]
    public void TheBackoffDoubles(int attempts, int seconds)
        => Assert.Equal(TimeSpan.FromSeconds(seconds), PendingStore.Backoff(attempts));

    [Fact]
    public void TheScanWatchesAnswersRetriesAfterTheBackoffAndLeavesStartedEntries()
    {
        var gate = TimeSpan.FromSeconds(8);
        Assert.Equal(PendingStep.Watch, PendingStore.Decide(new("a", "", "c", Now.AddSeconds(-2), false), Now, gate));
        Assert.Equal(PendingStep.Answer, PendingStore.Decide(new("a", "", "c", Now.AddSeconds(-9), false), Now, gate));
        Assert.Equal(PendingStep.Wait, PendingStore.Decide(new("a", "", "c", Now.AddSeconds(-9), true), Now, gate));
        // A released request is not "slow": it waits out its backoff from the release, not from when it was handed over.
        var released = new Pending("a", "", "c", Now.AddMinutes(-5), false, 1, Now.AddSeconds(-1));
        Assert.Equal(PendingStep.Wait, PendingStore.Decide(released, Now, gate));
        Assert.Equal(PendingStep.Answer, PendingStore.Decide(released, Now.AddSeconds(1), gate));
        Assert.Equal(PendingStep.Wait, PendingStore.Decide(released with { Attempts = 2 }, Now.AddSeconds(2), gate));
        Assert.Equal(PendingStep.Answer, PendingStore.Decide(released with { Attempts = 2 }, Now.AddSeconds(3), gate));
        // A capped entry stays claimed and is never tried again.
        Assert.Equal(PendingStep.Wait, PendingStore.Decide(released with { Started = true, Attempts = 3 }, Now.AddMinutes(1), gate));
    }

    [Fact]
    public void AReleasedEntryIsNotOlder()
    {
        var released = Guid.NewGuid();
        var entries = new Dictionary<Guid, Pending> { [released] = new("a", "", "c", Now.AddMinutes(-5), false, 1, Now.AddSeconds(-10)) };
        Assert.Empty(PendingStore.Older(entries, Now, TimeSpan.FromSeconds(8)));
    }

    private static Intent IntentIn(IntentState state, Guid requestId)
        => new(Guid.NewGuid(), "heard", "", requestId.ToString("N"), null, 0, state, [], [], []);

    [Fact]
    public void ADispatchedIntentOfThisRequestIsActedOnRetry()
    {
        var request = Guid.NewGuid();
        Assert.True(Ino.ConfirmedFor(IntentIn(IntentState.Confirmed, request), request));
        Assert.True(Ino.ConfirmedFor(IntentIn(IntentState.Dispatched, request), request));
        Assert.False(Ino.ConfirmedFor(IntentIn(IntentState.Dispatched, Guid.NewGuid()), request));
        Assert.False(Ino.ConfirmedFor(IntentIn(IntentState.Rephrased, request), request));
        Assert.False(Ino.ConfirmedFor(null, request));
    }

    [Fact]
    public void ReleasedThenRetriedKeepsOneWatchPerRequest()
    {
        var request = Guid.NewGuid();
        using var first = new CancellationTokenSource();
        using var second = new CancellationTokenSource();
        var watches = new System.Collections.Concurrent.ConcurrentDictionary<Guid, CancellationTokenSource>();
        watches[request] = first;
        watches[request] = second;
        Assert.False(watches.TryRemove(KeyValuePair.Create(request, first)));
        Assert.Same(second, watches[request]);
        Assert.True(watches.TryRemove(KeyValuePair.Create(request, second)));
    }

    [Fact]
    public void DataKeyFallsBackToTheAppKey()
    {
        Assert.Equal("chat", Ino.DataKey("chat", "app"));
        Assert.Equal("app", Ino.DataKey("", "app"));
        Assert.Equal("app", Ino.DataKey(null, "app"));
    }
}
