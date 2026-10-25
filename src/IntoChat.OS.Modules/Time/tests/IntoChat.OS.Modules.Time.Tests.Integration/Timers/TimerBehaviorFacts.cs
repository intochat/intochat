using IntoChat.OS.Testing;
using IntoChat.OS.Modules.Time;
using IntoChat.OS.Modules.Time.Timers.Signals;
using Xunit;

namespace IntoChat.OS.Modules.Time.Tests.Integration;

public sealed class TimerBehaviorFacts(TimeHost host)
{
    [Fact]
    public async Task BehaviorSubscribesBeforeTheTriggerAndOnlyReportsItsTimer()
    {
        var ct = TestContext.Current.CancellationToken;
        var brain = host.Brain;
        var timer = brain.Get<ITimer>("behavior");
        var other = brain.Get<ITimer>("other");
        var result = new TaskCompletionSource<TimerTick>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var run = brain.RunBehavior((handle, token) => TimerReport.RunAsync(handle, "behavior", fact =>
        {
            result.TrySetResult(fact);
            return Task.CompletedTask;
        }, token), ct);
        await run.WaitForSubscriptionAsync<TimerTick>(timer, ct);
        await using var otherTicks = await brain.Observe<TimerTick>(other, ct);
        await other.Start(TimeSpan.Zero);
        await otherTicks.NextAsync(ct: ct);
        Assert.False(result.Task.IsCompleted);
        await timer.Start(TimeSpan.Zero);
        var fact = await result.Task.WaitAsync(TimeSpan.FromSeconds(90), ct);
        Assert.Equal("behavior", fact.TimerId);
        await run.DisposeAsync();
        Assert.True(run.Completion.IsCompleted);
    }
}
