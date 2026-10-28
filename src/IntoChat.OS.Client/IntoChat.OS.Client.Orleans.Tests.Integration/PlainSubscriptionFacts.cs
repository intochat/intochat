using IntoChat.OS;
using IntoChat.OS.Framework;
using IntoChat.OS.Modules.Time;
using ITimer = IntoChat.OS.Modules.Time.Timers.ITimer;
using IntoChat.OS.Modules.Time.Timers.Signals;
using IntoChat.OS.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace IntoChat.OS.Client.Orleans.Tests.Integration;

// 06-07 (R4-15): an in-process client calls a plain neuron (the migrated Time timer) and hears its [Signal] records through
// the session synapse neuron, with the same brain.Get/brain.On it uses for grains.
[Trait("host", "own")]
public sealed class PlainSubscriptionFacts
{
    private static readonly TimeSpan Within = TimeSpan.FromSeconds(15);

    [Fact]
    public async Task AClientHearsAPlainTimerTickOnceWithItsOsKey()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await TestBrain.Create().WithModule<TimeModule>().StartAsync(ct);
        var brains = host.Services.GetRequiredService<IIntoChatOSBrains>();
        var brain = brains.Open(host.Owner.BrainId);
        var key = host.Owner.Key("plain-tick");
        var timer = brains.Get<ITimer>(key);
        var subscribed = new TaskCompletionSource();
        await using var ticks = brain.On<TimerTick>(timer, () => { subscribed.TrySetResult(); return Task.CompletedTask; }, ct).GetAsyncEnumerator(ct);
        var first = ticks.MoveNextAsync().AsTask();
        await subscribed.Task.WaitAsync(Within, ct);

        await timer.Start(TimeSpan.FromMilliseconds(50));

        Assert.True(await first.WaitAsync(Within, ct));
        Assert.Equal(key, ticks.Current.TimerId);
        // A one-shot timer ticks once: nothing else arrives.
        var second = ticks.MoveNextAsync().AsTask();
        await Assert.ThrowsAsync<TimeoutException>(() => second.WaitAsync(TimeSpan.FromSeconds(1), ct));
    }
}
