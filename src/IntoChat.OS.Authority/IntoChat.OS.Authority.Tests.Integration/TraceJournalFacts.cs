using IntoChat.OS.Authority.Compute;
using IntoChat.OS.Framework.Compute;
using Microsoft.Extensions.Options;

namespace IntoChat.OS.Authority.Tests.Integration;

public sealed class TraceJournalFacts(AuthorityHost host)
{
    [Fact]
    public async Task TraceIdentityReplayAfterExpiryStartsAFreshJournalGeneration()
    {
        var journal = Journal();
        await journal.Append(Cost("old-a", ExternalCallState.Settled, 1), 1);
        await journal.Append(Cost("old-b", ExternalCallState.Settled, 2), 2);
        var oldCursor = (await journal.Read(1, null)).ContinuationToken!;
        await journal.Expire();
        await journal.Append(Cost("fresh", ExternalCallState.Settled, 3), 3);
        var fresh = await journal.Read(100, null);
        Assert.Equal(TraceDetailStatus.Complete, fresh.Status);
        Assert.Equal("fresh", Assert.Single(fresh.Rows).CostId);
        await Assert.ThrowsAsync<ArgumentException>(() => journal.Read(1, oldCursor));
    }

    [Fact]
    public async Task ContinuationKeepsPendingSnapshotWhenTheAttemptSettlesBetweenPages()
    {
        var journal = Journal();
        var first = Cost("a", ExternalCallState.Reserved, 0);
        var second = Cost("b", ExternalCallState.Reserved, 0);
        await journal.Append(first, 1);
        await journal.Append(second, 2);
        var page = await journal.Read(1, null);
        Assert.Equal("a", Assert.Single(page.Rows).CostId);
        Assert.Equal(2, page.PendingCount);
        Assert.NotNull(page.ContinuationToken);

        await journal.Append(second with { AttemptState = ExternalCallState.Settled, Micros = 125, ListPriceMicros = 100 }, 3);
        await journal.Append(Cost("c", ExternalCallState.Settled, 50), 4);
        var next = await journal.Read(1, page.ContinuationToken);
        Assert.Equal("b", Assert.Single(next.Rows).CostId);
        Assert.Equal(ExternalCallState.Reserved, next.Rows[0].AttemptState);
        Assert.Equal(0, next.ChargedMicros);
        Assert.Equal(2, next.PendingCount);
        Assert.Null(next.ContinuationToken);
        var fresh = await journal.Read(500, null);
        Assert.Equal(175, fresh.ChargedMicros);
        Assert.Equal(3, fresh.Rows.Length);
    }

    [Fact]
    public async Task RepeatedRevisionDoesNotAddAnAttemptAndConflictingReplayFails()
    {
        var journal = Journal();
        var cost = Cost("a", ExternalCallState.Settled, 100);
        await journal.Append(cost, 1);
        await journal.Append(cost, 1);
        Assert.Single((await journal.Read(100, null)).Rows);
        await Assert.ThrowsAsync<InvalidOperationException>(() => journal.Append(cost with { Micros = 101 }, 1));
    }

    [Fact]
    public async Task BoundedJournalReportsIncompleteAndExpiredInsteadOfCompleteDetail()
    {
        var clock = new JournalClock();
        var journal = Journal(new ComputeOptions { MaxTraceRevisions = 2 }, clock);
        await journal.Append(Cost("a", ExternalCallState.Settled, 1), 1);
        await journal.Append(Cost("b", ExternalCallState.Settled, 2), 2);
        await journal.Append(Cost("c", ExternalCallState.Settled, 3), 3);
        Assert.Equal(TraceDetailStatus.Incomplete, (await journal.Read(100, null)).Status);
        clock.Now = clock.Now.AddDays(31);
        Assert.Equal(TraceDetailStatus.Expired, (await journal.Read(100, null)).Status);
    }

    [Fact]
    public async Task ForeignOrModifiedContinuationIsRejected()
    {
        var first = Journal();
        await first.Append(Cost("a", ExternalCallState.Settled, 1), 1);
        await first.Append(Cost("b", ExternalCallState.Settled, 2), 2);
        var token = (await first.Read(1, null)).ContinuationToken!;
        await Assert.ThrowsAsync<ArgumentException>(() => first.Read(1, token + "x"));
        await Assert.ThrowsAsync<ArgumentException>(() => Journal().Read(1, token));
    }

    [Fact]
    public async Task PendingAttemptsStayVisiblePastSettledRetention()
    {
        var clock = new JournalClock();
        var journal = Journal(clock: clock);
        await journal.Append(Cost("pending", ExternalCallState.Uncertain, 0), 1);
        clock.Now = clock.Now.AddDays(31);
        var page = await journal.Read(100, null);
        Assert.Equal(1, page.PendingCount);
        Assert.Equal("pending", Assert.Single(page.Rows).CostId);
    }

    [Fact]
    public async Task ExpiryClearsStoredDetailAndAStaleCursorCannotRestartTheSnapshot()
    {
        var ct = TestContext.Current.CancellationToken;
        var journal = host.Brain.Platform.TraceJournal(AuthorityHost.Unique("journal"), "trace");
        await journal.Append(Cost("a", ExternalCallState.Settled, 1), 1);
        await journal.Append(Cost("b", ExternalCallState.Settled, 2), 2);
        var token = (await journal.Read(1, null)).ContinuationToken!;
        await journal.Expire();
        await host.Brain.DeactivateAsync(journal, ct);
        Assert.Equal(TraceDetailStatus.DetailUnavailable, (await journal.Read(100, null)).Status);
        await Assert.ThrowsAsync<ArgumentException>(() => journal.Read(100, token));
    }

    private static TraceJournalGrain Journal(ComputeOptions? options = null, TimeProvider? clock = null)
        => new(new ComputeStore<TraceJournalState>(), Options.Create(options ?? new()), clock ?? new JournalClock());

    private static Cost Cost(string id, ExternalCallState state, long micros)
        => ComputeReservationFacts.Cost(new("trace", id), micros) with
        {
            AttemptState = state,
            PricingStatus = PricingStatus.Known,
            FundingKind = FundingKind.Integration,
            ListPriceMicros = state == ExternalCallState.Settled ? micros : null,
        };

    private sealed class JournalClock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 10, 22, 0, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }
}
